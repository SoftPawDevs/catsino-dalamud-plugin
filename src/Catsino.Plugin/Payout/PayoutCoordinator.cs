using Catsino.Plugin.Backend;
using Catsino.Plugin.Contracts;

namespace Catsino.Plugin.Payout;

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
        Action<string> reportStatus)
    {
        this.executor = executor;
        this.outbox = outbox;
        this.transport = transport;
        this.backendConnected = backendConnected;
        this.reportStatus = reportStatus;
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
