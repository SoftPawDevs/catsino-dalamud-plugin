using Catsino.Plugin.Backend;
using Catsino.Plugin.Contracts;

namespace Catsino.Plugin.Payout;

// Result of trying to resume a backend-open payout operation during recovery.
public enum PayoutResumeOutcome
{
    // Nothing to do (already running elsewhere, undelivered durable progress, or a benign race).
    Skipped,
    // Re-attached to a leg the executor was already driving (crash recovery).
    Reattached,
    // A never-started leg was started.
    Started,
    // The trade physically opened but is no longer driven locally; hand it to backend reconciliation.
    NeedsReconcile,
}

public interface IPayoutEventTransport
{
    Task<PayoutEventAckDto> SendAsync(PayoutEventDto payoutEvent, CancellationToken cancellationToken = default);
}

public sealed class BackendPayoutEventTransport(CatsinoApiClient api) : IPayoutEventTransport
{
    public Task<PayoutEventAckDto> SendAsync(PayoutEventDto payoutEvent, CancellationToken cancellationToken = default) =>
        api.ReportPayoutEventAsync(payoutEvent, cancellationToken);
}

public sealed class PayoutCoordinator : IDisposable, IAsyncDisposable
{
    private readonly IPayoutTradeExecutor executor;
    private readonly IPayoutOutbox outbox;
    private readonly IPayoutEventTransport transport;
    private readonly Func<bool> backendConnected;
    private readonly Action<string> reportStatus;
    private readonly Action? onLegSettled;
    private readonly SemaphoreSlim eventGate = new(1, 1);
    private readonly SemaphoreSlim drainGate = new(1, 1);
    private readonly HashSet<Guid> terminalOperations = [];
    private readonly object backgroundSync = new();
    private readonly HashSet<Task> backgroundTasks = [];
    private readonly CancellationTokenSource shutdown = new();
    private ActivePayout? active;
    private Task? disposeTask;
    private bool disposed;

    public PayoutCoordinator(
        IPayoutTradeExecutor executor,
        IPayoutOutbox outbox,
        IPayoutEventTransport transport,
        Func<bool> backendConnected,
        Action<string> reportStatus,
        Action? onLegSettled = null)
    {
        this.executor = executor;
        this.outbox = outbox;
        this.transport = transport;
        this.backendConnected = backendConnected;
        this.reportStatus = reportStatus;
        this.onLegSettled = onLegSettled;
        executor.TradeEventReceived += OnTradeEventReceived;
    }

    public PayoutOperationDto? ActiveOperation { get; private set; }

    public bool HasActiveOperation => active is not null;

    public async Task StartBackendLegAsync(PayoutLegDto leg, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await eventGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (active is not null)
            {
                // Self-heal a stale active operation: if the executor is no longer driving the
                // previous operation (it finished but its terminal event was dropped before we
                // could clear state), abandon the stale slot so a new cash-out is not blocked
                // forever. Only refuse when the executor is genuinely still driving a payout.
                if (executor.GetOperation(active.Leg.OperationId) is not null && executor.Probe().ActiveOperation is not null)
                {
                    throw new InvalidOperationException("Only one payout operation can be active.");
                }

                reportStatus("Cleared a stale payout operation whose executor had already finished; starting the new leg.");
                active = null;
                ActiveOperation = null;
            }

            if (terminalOperations.Contains(leg.OperationId))
            {
                throw new InvalidOperationException("A terminal payout operation is never automatically retried.");
            }

            // Durable, cross-restart safety: if the outbox still holds any unsent event for this
            // operation, the trade already progressed locally (at least opened, possibly completed) and
            // the backend has not been told yet. Restarting would risk a duplicate physical payout, so
            // refuse until the durable events drain. Unlike terminalOperations/usedOperationIds (memory
            // only), this survives a plugin restart.
            if (await outbox.HasPendingForOperationAsync(leg.OperationId, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("A payout operation with undelivered durable events is never restarted; its outbox must drain first.");
            }

            var readiness = executor.Probe();
            var error = PayoutExecutionPolicy.Validate(leg, backendConnected(), readiness);
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }

            active = new ActivePayout(leg, readiness.ExecutorInstanceId);
            ActiveOperation = ToOperation(leg, PayoutOperationState.WaitingForPlayer, null, null);

            if (!executor.StartOperation(leg))
            {
                active = null;
                ActiveOperation = null;
                throw new InvalidOperationException("The built-in payout executor rejected the outgoing payout leg.");
            }

            reportStatus($"Waiting for {leg.CharacterName}@{leg.HomeWorld}; waiting has no timeout.");
        }
        finally
        {
            eventGate.Release();
        }
    }

    public async Task CancelFromBackendAsync(CancelPayoutOperationDto cancellation, CancellationToken cancellationToken = default)
    {
        await eventGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (active?.Leg.OperationId != cancellation.OperationId)
            {
                return;
            }

            if (!executor.CancelOperation(cancellation.OperationId))
            {
                throw new InvalidOperationException("The built-in payout executor could not definitively cancel the payout operation.");
            }
        }
        finally
        {
            eventGate.Release();
        }
    }

    public async Task<bool> AbortActiveOperationAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await eventGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (active is null)
            {
                return false;
            }

            if (!executor.CancelOperation(active.Leg.OperationId))
            {
                throw new InvalidOperationException(
                    "The payout trade is already open in-game and must be resolved through the trade window, not aborted.");
            }

            return true;
        }
        finally
        {
            eventGate.Release();
        }
    }

    public async Task<bool> RecoverBackendOperationAsync(PayoutOperationDto backendOperation, CancellationToken cancellationToken = default)
    {
        await eventGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (active is not null || backendOperation.State is
                (PayoutOperationState.Completed or PayoutOperationState.Cancelled or PayoutOperationState.Failed))
            {
                return false;
            }

            var executorOperation = executor.GetOperation(backendOperation.OperationId);
            var readiness = executor.Probe();
            if (executorOperation is null ||
                executorOperation.OperationId != backendOperation.OperationId ||
                executorOperation.SessionId != backendOperation.SessionId ||
                !string.Equals(executorOperation.CharacterName, backendOperation.CharacterName, StringComparison.Ordinal) ||
                !string.Equals(executorOperation.HomeWorld, backendOperation.HomeWorld, StringComparison.Ordinal) ||
                executorOperation.AmountGil != backendOperation.AmountGil)
            {
                return false;
            }

            var leg = new PayoutLegDto(
                backendOperation.OperationId,
                backendOperation.LegId,
                backendOperation.SessionId,
                backendOperation.CharacterName,
                backendOperation.HomeWorld,
                backendOperation.AmountGil,
                backendOperation.UpdatedAt);
            if (PayoutExecutionPolicy.Validate(leg, backendConnected(), readiness with { ActiveOperation = null }) is not null)
            {
                return false;
            }

            active = new ActivePayout(leg, readiness.ExecutorInstanceId);
            ActiveOperation = backendOperation;
            reportStatus("Recovered the exact active payout operation from local executor and backend state.");
            return true;
        }
        finally
        {
            eventGate.Release();
        }
    }

    // Resumes a payout operation the backend still considers open. Unlike RecoverBackendOperationAsync
    // (which only re-attaches to a leg the executor is already driving, for crash recovery), this also
    // STARTS a leg that has never begun locally. That closes the gap where the backend has queued the
    // next leg of a multi-leg cash-out but the real-time QueuePayoutLeg push was missed (e.g. a hub
    // reconnect on the leg boundary): the poll and reconnect paths call this so the leg still starts.
    //
    // Safety: a physically-opened trade (TradeOpened/TradeLocked) whose executor is gone is NEVER
    // restarted; it is reported as NeedsReconcile so the caller hands it to backend reconciliation.
    public async Task<PayoutResumeOutcome> ResumeBackendOperationAsync(PayoutOperationDto backendOperation, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        // Already driven by the executor -> this is a crash-recovery re-attach, not a fresh start.
        if (executor.GetOperation(backendOperation.OperationId) is not null)
        {
            return await RecoverBackendOperationAsync(backendOperation, cancellationToken).ConfigureAwait(false)
                ? PayoutResumeOutcome.Reattached
                : PayoutResumeOutcome.Skipped;
        }

        if (HasActiveOperation)
        {
            return PayoutResumeOutcome.Skipped;
        }

        // The trade physically opened but the executor is no longer driving it (e.g. plugin restart):
        // the gil may already have moved. Never re-trade; route it to reconciliation.
        if (backendOperation.State is PayoutOperationState.TradeOpened or PayoutOperationState.TradeLocked)
        {
            return PayoutResumeOutcome.NeedsReconcile;
        }

        // Only a leg that never began trading may be started. (After Fix 3, a Queued/WaitingForPlayer
        // operation with no durable event provably never moved gil.)
        if (backendOperation.State is not (PayoutOperationState.Queued or PayoutOperationState.WaitingForPlayer))
        {
            return PayoutResumeOutcome.Skipped;
        }

        // Undelivered durable progress for this operation: do not start; let the outbox drain first.
        if (await outbox.HasPendingForOperationAsync(backendOperation.OperationId, cancellationToken).ConfigureAwait(false))
        {
            return PayoutResumeOutcome.Skipped;
        }

        var leg = new PayoutLegDto(
            backendOperation.OperationId,
            backendOperation.LegId,
            backendOperation.SessionId,
            backendOperation.CharacterName,
            backendOperation.HomeWorld,
            backendOperation.AmountGil,
            backendOperation.UpdatedAt);
        try
        {
            await StartBackendLegAsync(leg, cancellationToken).ConfigureAwait(false);
            return PayoutResumeOutcome.Started;
        }
        catch (InvalidOperationException)
        {
            // Benign race: the real-time push already started this leg, it just became terminal, or
            // the executor is busy. Recovery is best-effort, so treat all of these as a safe no-op.
            return PayoutResumeOutcome.Skipped;
        }
    }

    public Task ReplayOutboxAsync(CancellationToken cancellationToken = default) => DrainOutboxAsync(cancellationToken);

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

    private void OnTradeEventReceived(PayoutTradeEvent tradeEvent)
    {
        lock (backgroundSync)
        {
            if (disposed)
            {
                return;
            }

            var task = PersistAndSendAsync(tradeEvent, shutdown.Token);
            backgroundTasks.Add(task);
            _ = ObserveBackgroundAsync(task);
        }
    }

    private async Task PersistAndSendAsync(PayoutTradeEvent tradeEvent, CancellationToken cancellationToken)
    {
        var entered = false;
        try
        {
            await eventGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            var execution = active;
            if (execution is null || !MatchesExactly(execution, tradeEvent))
            {
                reportStatus("Rejected a payout event whose operation or player identity did not match exactly.");
                return;
            }

            var payoutEvent = new PayoutEventDto(
                tradeEvent.OperationId,
                execution.Leg.LegId,
                tradeEvent.SequenceNumber,
                tradeEvent.ExecutorInstanceId,
                MapEventType(tradeEvent.EventType),
                tradeEvent.CharacterName,
                tradeEvent.HomeWorld,
                tradeEvent.AmountGil,
                tradeEvent.OccurredAt.ToUniversalTime(),
                tradeEvent.ErrorCode,
                tradeEvent.ErrorMessage,
                tradeEvent.IsAmbiguous);

            // The durable atomic write always precedes any network send.
            await outbox.EnqueueAsync(payoutEvent, cancellationToken).ConfigureAwait(false);

            // Once the TradeOpened event is durable, release the executor to move gil. Before this point
            // the executor holds off ConfirmTrade so a crash can never leave a completed trade untraced.
            if (tradeEvent.EventType == PayoutTradeEventType.TradeOpened)
            {
                executor.MarkOpenEventPersisted(tradeEvent.OperationId);
            }

            UpdateOperation(execution.Leg, tradeEvent);

            if (IsTerminal(tradeEvent))
            {
                terminalOperations.Add(tradeEvent.OperationId);
                active = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            reportStatus($"Payout event remains local: {exception.Message}");
            return;
        }
        finally
        {
            if (entered)
            {
                eventGate.Release();
            }
        }

        await DrainOutboxAsync(cancellationToken).ConfigureAwait(false);

        // Reaching here means a matched event was persisted for the active leg. If it was terminal,
        // the leg just settled: signal the host so it can promptly start the batch's next leg without
        // depending solely on the backend's real-time QueuePayoutLeg push.
        if (IsTerminal(tradeEvent))
        {
            onLegSettled?.Invoke();
        }
    }

    private async Task ObserveBackgroundAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Expected during plugin shutdown.
        }
        catch (Exception exception)
        {
            reportStatus($"Payout event processing stopped: {exception.Message}");
        }
        finally
        {
            lock (backgroundSync)
            {
                backgroundTasks.Remove(task);
            }
        }
    }

    private async Task FinishDisposeAsync(Task[] pendingTasks)
    {
        try
        {
            await Task.WhenAll(pendingTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Cancellation is how pending HTTP and file work is stopped safely.
        }
        catch (Exception exception)
        {
            reportStatus($"Payout shutdown completed after a background error: {exception.Message}");
        }
        finally
        {
            eventGate.Dispose();
            drainGate.Dispose();
            shutdown.Dispose();
        }
    }

    private async Task DrainOutboxAsync(CancellationToken cancellationToken = default)
    {
        if (!backendConnected())
        {
            return;
        }

        await drainGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var payoutEvent in await outbox.ReadPendingAsync(cancellationToken).ConfigureAwait(false))
            {
                PayoutEventAckDto acknowledgment;
                try
                {
                    acknowledgment = await transport.SendAsync(payoutEvent, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    reportStatus($"Outbox replay paused: {exception.Message}");
                    break;
                }

                if (acknowledgment.OperationId != payoutEvent.OperationId || acknowledgment.SequenceNumber != payoutEvent.SequenceNumber)
                {
                    reportStatus("Outbox replay paused because the backend acknowledgment did not match exactly.");
                    break;
                }

                await outbox.AcknowledgeAsync(acknowledgment.OperationId, acknowledgment.SequenceNumber, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            drainGate.Release();
        }
    }

    private static bool MatchesExactly(ActivePayout execution, PayoutTradeEvent tradeEvent) =>
        tradeEvent.OperationId == execution.Leg.OperationId &&
        tradeEvent.SessionId == execution.Leg.SessionId &&
        tradeEvent.ExecutorInstanceId == execution.ExecutorInstanceId &&
        string.Equals(tradeEvent.CharacterName, execution.Leg.CharacterName, StringComparison.Ordinal) &&
        string.Equals(tradeEvent.HomeWorld, execution.Leg.HomeWorld, StringComparison.Ordinal) &&
        tradeEvent.AmountGil == execution.Leg.AmountGil &&
        tradeEvent.SequenceNumber > 0;

    private void UpdateOperation(PayoutLegDto leg, PayoutTradeEvent tradeEvent)
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
                _ => throw new ArgumentOutOfRangeException(nameof(tradeEvent)),
            };
        ActiveOperation = ToOperation(leg, state, tradeEvent.ErrorCode, tradeEvent.ErrorMessage);
        reportStatus(state == PayoutOperationState.Failed && tradeEvent.IsAmbiguous
            ? "Payout outcome was ambiguous and was treated as failed. The remaining unpaid amount can be cashed out again."
            : $"Payout state: {state}.");
    }

    private static PayoutOperationDto ToOperation(PayoutLegDto leg, PayoutOperationState state, string? errorCode, string? errorMessage) =>
        new(
            leg.OperationId,
            leg.LegId,
            leg.SessionId,
            leg.CharacterName,
            leg.HomeWorld,
            leg.AmountGil,
            state,
            errorCode,
            errorMessage,
            DateTimeOffset.UtcNow);

    private static bool IsTerminal(PayoutTradeEvent tradeEvent) =>
        tradeEvent.IsAmbiguous || tradeEvent.EventType is
            (PayoutTradeEventType.TradeCompleted or PayoutTradeEventType.TradeCancelled or PayoutTradeEventType.TradeFailed or PayoutTradeEventType.TradeTimedOut);

    private static PayoutEventType MapEventType(PayoutTradeEventType eventType) => eventType switch
    {
        PayoutTradeEventType.PlayerDetected => PayoutEventType.PlayerDetected,
        PayoutTradeEventType.TradeOpened => PayoutEventType.TradeOpened,
        PayoutTradeEventType.TradeLocked => PayoutEventType.TradeLocked,
        PayoutTradeEventType.TradeCompleted => PayoutEventType.TradeCompleted,
        PayoutTradeEventType.TradeCancelled => PayoutEventType.TradeCancelled,
        PayoutTradeEventType.TradeFailed => PayoutEventType.TradeFailed,
        PayoutTradeEventType.TradeTimedOut => PayoutEventType.TradeTimedOut,
        _ => throw new ArgumentOutOfRangeException(nameof(eventType)),
    };

    private sealed record ActivePayout(PayoutLegDto Leg, Guid ExecutorInstanceId);
}
