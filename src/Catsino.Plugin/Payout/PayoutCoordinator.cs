using Catsino.Dropbox.Contracts;
using Catsino.Plugin.Backend;
using Catsino.Plugin.Contracts;
using Catsino.Plugin.Dropbox;

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
    private readonly IDropboxPayoutClient dropbox;
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
        IDropboxPayoutClient dropbox,
        IPayoutOutbox outbox,
        IPayoutEventTransport transport,
        Func<bool> backendConnected,
        Action<string> reportStatus)
    {
        this.dropbox = dropbox;
        this.outbox = outbox;
        this.transport = transport;
        this.backendConnected = backendConnected;
        this.reportStatus = reportStatus;
        dropbox.TradeEventReceived += OnTradeEventReceived;
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
                throw new InvalidOperationException("Only one payout operation can be active.");
            }

            if (terminalOperations.Contains(leg.OperationId))
            {
                throw new InvalidOperationException("A terminal payout operation is never automatically retried.");
            }

            var compatibility = dropbox.Probe();
            var error = PayoutExecutionPolicy.Validate(leg, backendConnected(), compatibility);
            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }

            active = new ActivePayout(leg, compatibility.Version!.PluginInstanceId);
            ActiveOperation = ToOperation(leg, PayoutOperationState.WaitingForPlayer, null, null);

            if (!dropbox.EnablePayoutMode(leg.SessionId))
            {
                active = null;
                ActiveOperation = null;
                throw new InvalidOperationException("Dropbox rejected payout mode.");
            }

            if (!dropbox.QueueOutgoingGilTrade(leg.OperationId, leg.CharacterName, leg.HomeWorld, leg.AmountGil))
            {
                dropbox.DisablePayoutMode(leg.SessionId);
                active = null;
                ActiveOperation = null;
                throw new InvalidOperationException("Dropbox rejected the outgoing payout leg.");
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

            if (!dropbox.CancelOutgoingTrade(cancellation.OperationId))
            {
                throw new InvalidOperationException("Dropbox could not definitively cancel the payout operation.");
            }
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

            var dropboxOperation = dropbox.GetTradeOperation(backendOperation.OperationId);
            var compatibility = dropbox.Probe();
            if (dropboxOperation is null || compatibility.Version is null ||
                dropboxOperation.OperationId != backendOperation.OperationId ||
                dropboxOperation.SessionId != backendOperation.SessionId ||
                !string.Equals(dropboxOperation.CharacterName, backendOperation.CharacterName, StringComparison.Ordinal) ||
                !string.Equals(dropboxOperation.HomeWorld, backendOperation.HomeWorld, StringComparison.Ordinal) ||
                dropboxOperation.AmountGil != backendOperation.AmountGil)
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
                DropboxPayoutContract.IpcVersion,
                DropboxPayoutContract.SupportedBuildVersion,
                backendOperation.UpdatedAt);
            if (PayoutExecutionPolicy.Validate(leg, backendConnected(), compatibility with { ActiveOperation = null }) is not null)
            {
                return false;
            }

            active = new ActivePayout(leg, compatibility.Version.PluginInstanceId);
            ActiveOperation = backendOperation;
            reportStatus("Recovered the exact active payout operation from Dropbox and backend state.");
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
                dropbox.TradeEventReceived -= OnTradeEventReceived;
                shutdown.Cancel();
                disposeTask = FinishDisposeAsync(backgroundTasks.ToArray());
            }

            return new ValueTask(disposeTask);
        }
    }

    private void OnTradeEventReceived(DropboxTradeEvent tradeEvent)
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

    private async Task PersistAndSendAsync(DropboxTradeEvent tradeEvent, CancellationToken cancellationToken)
    {
        var entered = false;
        try
        {
            await eventGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            var execution = active;
            if (execution is null || !MatchesExactly(execution, tradeEvent))
            {
                reportStatus("Rejected a Dropbox event whose operation or player identity did not match exactly.");
                return;
            }

            var payoutEvent = new PayoutEventDto(
                tradeEvent.OperationId,
                execution.Leg.LegId,
                tradeEvent.SequenceNumber,
                tradeEvent.PluginInstanceId,
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
                dropbox.DisablePayoutMode(execution.Leg.SessionId);
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

    private static bool MatchesExactly(ActivePayout execution, DropboxTradeEvent tradeEvent) =>
        tradeEvent.OperationId == execution.Leg.OperationId &&
        tradeEvent.SessionId == execution.Leg.SessionId &&
        tradeEvent.PluginInstanceId == execution.DropboxPluginInstanceId &&
        string.Equals(tradeEvent.CharacterName, execution.Leg.CharacterName, StringComparison.Ordinal) &&
        string.Equals(tradeEvent.HomeWorld, execution.Leg.HomeWorld, StringComparison.Ordinal) &&
        tradeEvent.AmountGil == execution.Leg.AmountGil &&
        tradeEvent.SequenceNumber > 0;

    private void UpdateOperation(PayoutLegDto leg, DropboxTradeEvent tradeEvent)
    {
        var state = tradeEvent.IsAmbiguous
            ? PayoutOperationState.Failed
            : tradeEvent.EventType switch
            {
                DropboxTradeEventType.PlayerDetected => PayoutOperationState.WaitingForPlayer,
                DropboxTradeEventType.TradeOpened => PayoutOperationState.TradeOpened,
                DropboxTradeEventType.TradeLocked => PayoutOperationState.TradeLocked,
                DropboxTradeEventType.TradeCompleted => PayoutOperationState.Completed,
                DropboxTradeEventType.TradeCancelled => PayoutOperationState.Cancelled,
                DropboxTradeEventType.TradeFailed or DropboxTradeEventType.TradeTimedOut => PayoutOperationState.Failed,
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

    private static bool IsTerminal(DropboxTradeEvent tradeEvent) =>
        tradeEvent.IsAmbiguous || tradeEvent.EventType is
            (DropboxTradeEventType.TradeCompleted or DropboxTradeEventType.TradeCancelled or DropboxTradeEventType.TradeFailed or DropboxTradeEventType.TradeTimedOut);

    private static PayoutEventType MapEventType(DropboxTradeEventType eventType) => eventType switch
    {
        DropboxTradeEventType.PlayerDetected => PayoutEventType.PlayerDetected,
        DropboxTradeEventType.TradeOpened => PayoutEventType.TradeOpened,
        DropboxTradeEventType.TradeLocked => PayoutEventType.TradeLocked,
        DropboxTradeEventType.TradeCompleted => PayoutEventType.TradeCompleted,
        DropboxTradeEventType.TradeCancelled => PayoutEventType.TradeCancelled,
        DropboxTradeEventType.TradeFailed => PayoutEventType.TradeFailed,
        DropboxTradeEventType.TradeTimedOut => PayoutEventType.TradeTimedOut,
        _ => throw new ArgumentOutOfRangeException(nameof(eventType)),
    };

    private sealed record ActivePayout(PayoutLegDto Leg, Guid DropboxPluginInstanceId);
}
