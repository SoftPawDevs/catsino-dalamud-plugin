using System.Security.Cryptography;
using System.Text;
using Catsino.Plugin.Contracts;

namespace Catsino.Plugin.Payout;

// Client-driven cash-out orchestration. Given the backend's leg plan (from the cash-out response), it runs
// every leg locally and sequentially through the built-in trade executor — never waiting on a backend push
// to advance — and reports the whole batch to the backend ONCE, at the end (or when a leg fails), via the
// settle endpoint.
//
// Crash-safety: the durable batch store is the authoritative record of which legs have physically traded.
// A leg is marked Trading (durable) before the executor is released to move gil (the ConfirmTrade barrier),
// so after any crash a Trading leg is treated as ambiguous (quarantined, never re-traded), and a Completed
// leg is never re-run. Settlement is idempotent on the backend, so a resent settle never double-books.
public sealed class PayoutBatchCoordinator : IAsyncDisposable, IDisposable
{
    private readonly IPayoutTradeExecutor executor;
    private readonly ICashOutBatchStore store;
    private readonly IPayoutSettlementTransport settlement;
    private readonly Func<bool> backendConnected;
    private readonly Action<string> reportStatus;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object backgroundSync = new();
    private readonly HashSet<Task> backgroundTasks = [];
    private readonly CancellationTokenSource shutdown = new();
    private CashOutBatchState? active;
    private int activeLegNumber;
    private Guid activeOperationId;
    private Task? disposeTask;
    private bool disposed;

    public PayoutBatchCoordinator(
        IPayoutTradeExecutor executor,
        ICashOutBatchStore store,
        IPayoutSettlementTransport settlement,
        Func<bool> backendConnected,
        Action<string> reportStatus)
    {
        this.executor = executor;
        this.store = store;
        this.settlement = settlement;
        this.backendConnected = backendConnected;
        this.reportStatus = reportStatus;
        executor.TradeEventReceived += OnTradeEventReceived;
    }

    public PayoutOperationDto? ActiveOperation { get; private set; }

    public bool HasActiveBatch => active is not null;

    // Starts a fresh batch. The plan is persisted durably before any trade, then the first leg is started.
    public async Task StartBatchAsync(CashOutBatchPlan plan, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (plan.Legs.Count == 0)
            throw new InvalidOperationException("A cash-out batch must have at least one leg.");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (active is not null)
                throw new InvalidOperationException("A cash-out batch is already running.");
            var state = CashOutBatchState.FromPlan(plan);
            await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            active = state;
            await AdvanceAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    // Reloads durable batches after a restart/reconnect: quarantines any leg caught mid-trade, settles any
    // fully-resolved batch, and resumes the next pending leg of one in-progress batch.
    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (active is not null)
            {
                if (active.Legs.All(x => x.Progress is not (CashOutLegProgress.Pending or CashOutLegProgress.Trading)))
                    await SettleAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            foreach (var batch in await store.LoadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var quarantined = false;
                foreach (var leg in batch.Legs.Where(x => x.Progress == CashOutLegProgress.Trading))
                {
                    // A crash struck while this leg's trade was open: the gil may have moved, so never
                    // re-trade it — quarantine as ambiguous (the backend keeps it deducted for review).
                    leg.Progress = CashOutLegProgress.Ambiguous;
                    leg.ErrorCode = "reconciliationRequired";
                    quarantined = true;
                }

                if (quarantined)
                    await store.SaveAsync(batch, cancellationToken).ConfigureAwait(false);

                var hasFailure = batch.Legs.Any(x => x.Progress is CashOutLegProgress.Failed or CashOutLegProgress.Ambiguous);
                var allCompleted = batch.Legs.All(x => x.Progress == CashOutLegProgress.Completed);
                if (hasFailure || allCompleted)
                {
                    active = batch;
                    await SettleAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (active is null)
                {
                    active = batch;
                    reportStatus($"Resuming cash-out {batch.CashOutId:D} after recovery.");
                    await AdvanceAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    // Aborts the current leg if it has not opened a trade yet (no gil at risk). Refused once the trade is open.
    public async Task<bool> AbortActiveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (active is null)
                return false;
            if (!executor.CancelOperation(activeOperationId))
                throw new InvalidOperationException("The payout trade is already open in-game and must be resolved through the trade window, not aborted.");
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task AdvanceAsync(CancellationToken cancellationToken)
    {
        var next = active!.Legs.FirstOrDefault(x => x.Progress == CashOutLegProgress.Pending);
        if (next is null)
        {
            await SettleAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        activeLegNumber = next.Number;
        activeOperationId = Deterministic("op", active.CashOutId, next.Number);
        var leg = new PayoutLegDto(
            activeOperationId,
            Deterministic("leg", active.CashOutId, next.Number),
            active.SessionId,
            active.CharacterName,
            active.HomeWorld,
            next.Net,
            DateTimeOffset.UtcNow);

        var readiness = executor.Probe();
        var error = PayoutExecutionPolicy.Validate(leg, backendConnected(), readiness);
        if (error is not null)
        {
            ActiveOperation = ToOperation(leg, PayoutOperationState.Queued, error);
            reportStatus($"Payout leg {next.Number} cannot start yet: {error}");
            return;
        }

        ActiveOperation = ToOperation(leg, PayoutOperationState.WaitingForPlayer, null);
        if (!executor.StartOperation(leg))
        {
            // Pre-trade rejection (e.g. exact identity/amount check): no gil moved. Treat as a clean
            // failure and settle so the remaining gross is released.
            next.Progress = CashOutLegProgress.Failed;
            next.ErrorCode = "executorRejected";
            await store.SaveAsync(active, cancellationToken).ConfigureAwait(false);
            reportStatus($"Payout leg {next.Number} was rejected by the executor; releasing the remainder.");
            await SettleAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        reportStatus($"Paying out leg {next.Number} to {active.CharacterName}@{active.HomeWorld}; waiting has no timeout.");
    }

    private async Task SettleAsync(CancellationToken cancellationToken)
    {
        var batch = active;
        if (batch is null)
            return;
        var outcomes = batch.Legs
            .Where(x => x.Progress is CashOutLegProgress.Completed or CashOutLegProgress.Failed or CashOutLegProgress.Ambiguous)
            .OrderBy(x => x.Number)
            .Select(x => new CashOutLegOutcome(x.Number, OutcomeText(x.Progress), x.ErrorCode))
            .ToList();
        if (outcomes.Count == 0)
            return;

        if (!backendConnected())
        {
            reportStatus("Cash-out settlement is waiting for the backend connection; it will retry.");
            return;
        }

        try
        {
            await settlement.SettleAsync(batch.CashOutId, new CashOutSettlementRequest(batch.CashOutId, outcomes), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            reportStatus($"Cash-out settlement will be retried: {exception.Message}");
            return;
        }

        await store.DeleteAsync(batch.CashOutId, cancellationToken).ConfigureAwait(false);
        active = null;
        activeLegNumber = 0;
        activeOperationId = Guid.Empty;
        ActiveOperation = null;
        reportStatus("Cash-out settled with the backend.");
    }

    private void OnTradeEventReceived(PayoutTradeEvent tradeEvent)
    {
        lock (backgroundSync)
        {
            if (disposed)
                return;
            var task = ProcessEventAsync(tradeEvent, shutdown.Token);
            backgroundTasks.Add(task);
            _ = ObserveAsync(task);
        }
    }

    private async Task ProcessEventAsync(PayoutTradeEvent tradeEvent, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (active is null || tradeEvent.OperationId != activeOperationId)
                return;
            var leg = active.Legs.FirstOrDefault(x => x.Number == activeLegNumber);
            if (leg is null)
                return;

            if (tradeEvent.IsAmbiguous)
            {
                leg.Progress = CashOutLegProgress.Ambiguous;
                leg.ErrorCode = tradeEvent.ErrorCode ?? "reconciliationRequired";
                await store.SaveAsync(active, cancellationToken).ConfigureAwait(false);
                UpdateActiveOperation(tradeEvent);
                await SettleAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            switch (tradeEvent.EventType)
            {
                case PayoutTradeEventType.PlayerDetected:
                case PayoutTradeEventType.TradeLocked:
                    UpdateActiveOperation(tradeEvent);
                    break;
                case PayoutTradeEventType.TradeOpened:
                    // Durable "opened" marker BEFORE the executor is released to move gil, so a crash here
                    // is recoverable as ambiguous and never re-traded.
                    leg.Progress = CashOutLegProgress.Trading;
                    await store.SaveAsync(active, cancellationToken).ConfigureAwait(false);
                    executor.MarkOpenEventPersisted(tradeEvent.OperationId);
                    UpdateActiveOperation(tradeEvent);
                    break;
                case PayoutTradeEventType.TradeCompleted:
                    leg.Progress = CashOutLegProgress.Completed;
                    await store.SaveAsync(active, cancellationToken).ConfigureAwait(false);
                    UpdateActiveOperation(tradeEvent);
                    await AdvanceAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case PayoutTradeEventType.TradeCancelled:
                case PayoutTradeEventType.TradeFailed:
                case PayoutTradeEventType.TradeTimedOut:
                    leg.Progress = CashOutLegProgress.Failed;
                    leg.ErrorCode = tradeEvent.ErrorCode ?? tradeEvent.EventType.ToString();
                    await store.SaveAsync(active, cancellationToken).ConfigureAwait(false);
                    UpdateActiveOperation(tradeEvent);
                    await SettleAsync(cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            reportStatus($"Payout batch step failed: {exception.Message}");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            reportStatus($"Payout batch processing stopped: {exception.Message}");
        }
        finally
        {
            lock (backgroundSync)
            {
                backgroundTasks.Remove(task);
            }
        }
    }

    private void UpdateActiveOperation(PayoutTradeEvent tradeEvent)
    {
        var state = tradeEvent.IsAmbiguous
            ? PayoutOperationState.Failed
            : tradeEvent.EventType switch
            {
                PayoutTradeEventType.PlayerDetected => PayoutOperationState.WaitingForPlayer,
                PayoutTradeEventType.TradeOpened => PayoutOperationState.TradeOpened,
                PayoutTradeEventType.TradeLocked => PayoutOperationState.TradeLocked,
                PayoutTradeEventType.TradeCompleted => PayoutOperationState.Completed,
                PayoutTradeEventType.TradeCancelled => PayoutOperationState.Cancelled,
                PayoutTradeEventType.TradeFailed or PayoutTradeEventType.TradeTimedOut => PayoutOperationState.Failed,
                _ => PayoutOperationState.WaitingForPlayer,
            };
        ActiveOperation = new PayoutOperationDto(
            tradeEvent.OperationId,
            ActiveOperation?.LegId ?? Guid.Empty,
            tradeEvent.SessionId,
            tradeEvent.CharacterName,
            tradeEvent.HomeWorld,
            tradeEvent.AmountGil,
            state,
            tradeEvent.ErrorCode,
            tradeEvent.ErrorMessage,
            DateTimeOffset.UtcNow);
    }

    private static PayoutOperationDto ToOperation(PayoutLegDto leg, PayoutOperationState state, string? errorMessage) =>
        new(leg.OperationId, leg.LegId, leg.SessionId, leg.CharacterName, leg.HomeWorld, leg.AmountGil, state, null, errorMessage, DateTimeOffset.UtcNow);

    private static string OutcomeText(CashOutLegProgress progress) => progress switch
    {
        CashOutLegProgress.Completed => "completed",
        CashOutLegProgress.Ambiguous => "ambiguous",
        _ => "failed",
    };

    private static Guid Deterministic(string purpose, Guid cashOutId, int legNumber)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"Catsino.Plugin.v1:{purpose}:{cashOutId:D}:{legNumber}"), hash);
        var uuid = hash[..16];
        uuid[6] = (byte)((uuid[6] & 0x0f) | 0x80);
        uuid[8] = (byte)((uuid[8] & 0x3f) | 0x80);
        return new Guid(uuid, bigEndian: true);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (backgroundSync)
        {
            if (disposeTask is null)
            {
                disposed = true;
                executor.TradeEventReceived -= OnTradeEventReceived;
                shutdown.Cancel();
                disposeTask = FinishDisposeAsync(backgroundTasks.ToArray());
            }

            return new ValueTask(disposeTask);
        }
    }

    private async Task FinishDisposeAsync(Task[] pending)
    {
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch
        {
            // Cancellation/shutdown errors are expected while tearing down.
        }
        finally
        {
            gate.Dispose();
            shutdown.Dispose();
        }
    }
}
