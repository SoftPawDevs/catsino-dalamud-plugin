using Catsino.Plugin.Contracts;
using Catsino.Plugin.Payout;

namespace Catsino.Plugin.Tests;

public sealed class PayoutCoordinatorTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "catsino-coordinator-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AllowsOneOperationAndNeverAutomaticallyRetriesTerminalFailure()
    {
        var fakeExecutor = new FakeExecutor();
        var outbox = new PersistentPayoutOutbox(directory);
        var transport = new FakeTransport();
        using var coordinator = new PayoutCoordinator(fakeExecutor, outbox, transport, () => true, _ => { });
        var leg = TestData.Leg();

        await coordinator.StartBackendLegAsync(leg);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartBackendLegAsync(TestData.Leg()));
        Assert.Equal(1, fakeExecutor.StartCount);

        fakeExecutor.Emit(TestData.TradeEvent(leg, PayoutTradeEventType.TradeFailed, ambiguous: false));
        await WaitUntilAsync(() => coordinator.ActiveOperation?.State == PayoutOperationState.Failed);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartBackendLegAsync(leg));
        Assert.Equal(1, fakeExecutor.StartCount);
    }

    [Fact]
    public async Task SelfHealsStaleActiveWhenExecutorAlreadyIdle()
    {
        var fakeExecutor = new FakeExecutor();
        var outbox = new PersistentPayoutOutbox(directory);
        using var coordinator = new PayoutCoordinator(fakeExecutor, outbox, new FakeTransport(), () => true, _ => { });

        await coordinator.StartBackendLegAsync(TestData.Leg());
        Assert.Equal(1, fakeExecutor.StartCount);

        // The executor finished the previous operation but its terminal event was dropped before
        // the coordinator could clear state. A new cash-out must not be blocked forever.
        fakeExecutor.GoIdleWithoutEvent();

        await coordinator.StartBackendLegAsync(TestData.Leg());
        Assert.Equal(2, fakeExecutor.StartCount);
        Assert.True(coordinator.HasActiveOperation);
    }

    [Fact]
    public async Task StillRefusesSecondOperationWhileExecutorIsBusy()
    {
        var fakeExecutor = new FakeExecutor();
        var outbox = new PersistentPayoutOutbox(directory);
        using var coordinator = new PayoutCoordinator(fakeExecutor, outbox, new FakeTransport(), () => true, _ => { });

        await coordinator.StartBackendLegAsync(TestData.Leg());
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartBackendLegAsync(TestData.Leg()));
        Assert.Equal(1, fakeExecutor.StartCount);
    }

    [Fact]
    public async Task StartsNextLegOnlyAfterThePreviousLegCompletes()
    {
        // Locks in sequential multi-leg execution: a batch's next leg (a fresh backend
        // QueuePayoutLeg) is refused while the current leg runs and starts once it completes.
        var fakeExecutor = new FakeExecutor();
        var outbox = new PersistentPayoutOutbox(directory);
        using var coordinator = new PayoutCoordinator(fakeExecutor, outbox, new FakeTransport(), () => true, _ => { });
        var firstLeg = TestData.Leg();

        await coordinator.StartBackendLegAsync(firstLeg);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartBackendLegAsync(TestData.Leg()));

        fakeExecutor.Emit(TestData.TradeEvent(firstLeg, PayoutTradeEventType.TradeCompleted, ambiguous: false));
        await WaitUntilAsync(() => !coordinator.HasActiveOperation);

        var secondLeg = TestData.Leg();
        await coordinator.StartBackendLegAsync(secondLeg);
        Assert.Equal(2, fakeExecutor.StartCount);
        Assert.Equal(secondLeg.OperationId, coordinator.ActiveOperation?.OperationId);
    }

    [Fact]
    public async Task ResumeStartsQueuedNextLegWhenTheRealtimePushIsMissed()
    {
        // The core fix: after leg 1 completes, the backend has queued leg 2 but its QueuePayoutLeg
        // push never arrives. The backend-recovery path must still START leg 2 (not just re-attach).
        var fakeExecutor = new FakeExecutor();
        var outbox = new PersistentPayoutOutbox(directory);
        using var coordinator = new PayoutCoordinator(fakeExecutor, outbox, new FakeTransport(), () => true, _ => { });
        var firstLeg = TestData.Leg();

        await coordinator.StartBackendLegAsync(firstLeg);
        fakeExecutor.Emit(TestData.TradeEvent(firstLeg, PayoutTradeEventType.TradeCompleted, ambiguous: false));
        await WaitUntilAsync(() => !coordinator.HasActiveOperation);

        var secondLeg = TestData.Leg();
        var resumed = await coordinator.ResumeBackendOperationAsync(TestData.OpenOperation(secondLeg, PayoutOperationState.Queued));

        Assert.Equal(PayoutResumeOutcome.Started, resumed);
        Assert.Equal(2, fakeExecutor.StartCount);
        Assert.Equal(secondLeg.OperationId, coordinator.ActiveOperation?.OperationId);
    }

    [Fact]
    public async Task ResumeIsNoOpWhileALegIsStillActive()
    {
        var fakeExecutor = new FakeExecutor();
        var outbox = new PersistentPayoutOutbox(directory);
        using var coordinator = new PayoutCoordinator(fakeExecutor, outbox, new FakeTransport(), () => true, _ => { });

        await coordinator.StartBackendLegAsync(TestData.Leg());
        var resumed = await coordinator.ResumeBackendOperationAsync(TestData.OpenOperation(TestData.Leg(), PayoutOperationState.Queued));

        Assert.Equal(PayoutResumeOutcome.Skipped, resumed);
        Assert.Equal(1, fakeExecutor.StartCount);
    }

    [Fact]
    public async Task ResumeHandsAPhysicallyOpenedTradeToReconciliation()
    {
        // A trade that physically opened (gil may have moved) but is no longer driven locally must never
        // be restarted; recovery reports NeedsReconcile so the caller routes it to backend reconciliation.
        var fakeExecutor = new FakeExecutor();
        var outbox = new PersistentPayoutOutbox(directory);
        using var coordinator = new PayoutCoordinator(fakeExecutor, outbox, new FakeTransport(), () => true, _ => { });

        var outcome = await coordinator.ResumeBackendOperationAsync(TestData.OpenOperation(TestData.Leg(), PayoutOperationState.TradeLocked));

        Assert.Equal(PayoutResumeOutcome.NeedsReconcile, outcome);
        Assert.Equal(0, fakeExecutor.StartCount);
    }

    [Fact]
    public async Task ResumeNeverRestartsAnAlreadySettledLeg()
    {
        var fakeExecutor = new FakeExecutor();
        var outbox = new PersistentPayoutOutbox(directory);
        using var coordinator = new PayoutCoordinator(fakeExecutor, outbox, new FakeTransport(), () => true, _ => { });
        var leg = TestData.Leg();

        await coordinator.StartBackendLegAsync(leg);
        fakeExecutor.Emit(TestData.TradeEvent(leg, PayoutTradeEventType.TradeFailed, ambiguous: false));
        await WaitUntilAsync(() => coordinator.ActiveOperation?.State == PayoutOperationState.Failed);

        // The executor is idle again, but this operation already reached a terminal outcome; recovery
        // must never re-trade it even if the backend still momentarily reports it as open.
        fakeExecutor.GoIdleWithoutEvent();
        var resumed = await coordinator.ResumeBackendOperationAsync(TestData.OpenOperation(leg, PayoutOperationState.Queued));

        Assert.Equal(PayoutResumeOutcome.Skipped, resumed);
        Assert.Equal(1, fakeExecutor.StartCount);
    }

    [Fact]
    public async Task ResumeDoesNotDoubleStartALegTheRealtimePushAlreadyStarted()
    {
        var fakeExecutor = new FakeExecutor();
        var outbox = new PersistentPayoutOutbox(directory);
        using var coordinator = new PayoutCoordinator(fakeExecutor, outbox, new FakeTransport(), () => true, _ => { });
        var firstLeg = TestData.Leg();

        await coordinator.StartBackendLegAsync(firstLeg);
        fakeExecutor.Emit(TestData.TradeEvent(firstLeg, PayoutTradeEventType.TradeCompleted, ambiguous: false));
        await WaitUntilAsync(() => !coordinator.HasActiveOperation);

        // The real-time push wins the race and starts leg 2.
        var secondLeg = TestData.Leg();
        await coordinator.StartBackendLegAsync(secondLeg);
        Assert.Equal(2, fakeExecutor.StartCount);

        // The recovery path then also observes leg 2 as open: it must be a safe no-op, not a re-start.
        var resumed = await coordinator.ResumeBackendOperationAsync(TestData.OpenOperation(secondLeg, PayoutOperationState.WaitingForPlayer));

        Assert.Equal(PayoutResumeOutcome.Skipped, resumed);
        Assert.Equal(2, fakeExecutor.StartCount);
        Assert.Equal(secondLeg.OperationId, coordinator.ActiveOperation?.OperationId);
    }

    [Fact]
    public async Task RefusesToStartALegWhoseDurableEventsAreStillUndelivered()
    {
        // Cross-restart double-payment guard: a leg whose completion sits in the durable outbox (the
        // backend has not been told) must never be physically re-traded, even though the in-memory
        // guards are empty after a restart.
        var fakeExecutor = new FakeExecutor();
        var outbox = new PersistentPayoutOutbox(directory);
        var leg = TestData.Leg();
        await outbox.EnqueueAsync(new PayoutEventDto(leg.OperationId, leg.LegId, 3, Guid.NewGuid(),
            PayoutEventType.TradeCompleted, leg.CharacterName, leg.HomeWorld, leg.AmountGil,
            DateTimeOffset.UtcNow, null, null, false));
        using var coordinator = new PayoutCoordinator(fakeExecutor, outbox, new FakeTransport(), () => true, _ => { });

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartBackendLegAsync(leg));
        var resumed = await coordinator.ResumeBackendOperationAsync(TestData.OpenOperation(leg, PayoutOperationState.Queued));

        Assert.Equal(PayoutResumeOutcome.Skipped, resumed);
        Assert.Equal(0, fakeExecutor.StartCount);
    }

    [Fact]
    public async Task HasPendingForOperationMatchesOnlyThatOperation()
    {
        var outbox = new PersistentPayoutOutbox(directory);
        var leg = TestData.Leg();
        await outbox.EnqueueAsync(new PayoutEventDto(leg.OperationId, leg.LegId, 1, Guid.NewGuid(),
            PayoutEventType.TradeOpened, leg.CharacterName, leg.HomeWorld, leg.AmountGil,
            DateTimeOffset.UtcNow, null, null, false));

        Assert.True(await outbox.HasPendingForOperationAsync(leg.OperationId));
        Assert.False(await outbox.HasPendingForOperationAsync(Guid.NewGuid()));

        Assert.True(await outbox.AcknowledgeAsync(leg.OperationId, 1));
        Assert.False(await outbox.HasPendingForOperationAsync(leg.OperationId));
    }

    [Fact]
    public async Task PersistingTradeOpenedReleasesTheExecutorToConfirm()
    {
        // Fix 3 wiring: the executor is told to move gil only AFTER the TradeOpened event is durably
        // persisted. (The executor-internal ConfirmTrade gate is exercised in-game.)
        var fakeExecutor = new FakeExecutor();
        var outbox = new PersistentPayoutOutbox(directory);
        using var coordinator = new PayoutCoordinator(fakeExecutor, outbox, new FakeTransport(), () => true, _ => { });
        var leg = TestData.Leg();
        await coordinator.StartBackendLegAsync(leg);
        Assert.Equal(Guid.Empty, fakeExecutor.OpenPersistedOperationId);

        fakeExecutor.Emit(TestData.TradeEvent(leg, PayoutTradeEventType.TradeOpened, ambiguous: false));
        await WaitUntilAsync(() => fakeExecutor.OpenPersistedOperationId == leg.OperationId);
    }

    [Fact]
    public async Task AmbiguousOutcomeIsTreatedAsFailedAndWaitsForExactAck()
    {
        var fakeExecutor = new FakeExecutor();
        var outbox = new PersistentPayoutOutbox(directory);
        var transport = new FakeTransport { ReturnWrongAck = true };
        using var coordinator = new PayoutCoordinator(fakeExecutor, outbox, transport, () => true, _ => { });
        var leg = TestData.Leg();
        await coordinator.StartBackendLegAsync(leg);

        fakeExecutor.Emit(TestData.TradeEvent(leg, PayoutTradeEventType.TradeFailed, ambiguous: true));
        await WaitUntilAsync(() => coordinator.ActiveOperation?.State == PayoutOperationState.Failed);
        Assert.Equal(1, await outbox.CountAsync());

        transport.ReturnWrongAck = false;
        await coordinator.ReplayOutboxAsync();
        Assert.Equal(0, await outbox.CountAsync());
        Assert.Equal(1, fakeExecutor.StartCount);
    }

    [Theory]
    [InlineData("Wrong Name", "Ragnarok", 900)]
    [InlineData("Exact Player", "Wrong World", 900)]
    [InlineData("Exact Player", "Ragnarok", 901)]
    public async Task RejectsExecutorEventsThatDoNotMatchExactIdentityOrAmount(string characterName, string homeWorld, long amountGil)
    {
        var fakeExecutor = new FakeExecutor();
        var outbox = new PersistentPayoutOutbox(directory);
        using var coordinator = new PayoutCoordinator(fakeExecutor, outbox, new FakeTransport(), () => true, _ => { });
        var leg = TestData.Leg();

        await coordinator.StartBackendLegAsync(leg);

        fakeExecutor.Emit(TestData.TradeEvent(leg, PayoutTradeEventType.TradeCompleted, ambiguous: false) with
        {
            CharacterName = characterName,
            HomeWorld = homeWorld,
            AmountGil = amountGil,
        });

        await Task.Delay(50);

        Assert.Equal(PayoutOperationState.WaitingForPlayer, coordinator.ActiveOperation?.State);
        Assert.Equal(0, await outbox.CountAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition() && DateTimeOffset.UtcNow < timeout)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class FakeExecutor : IPayoutTradeExecutor
    {
        public event Action<PayoutTradeEvent>? TradeEventReceived;

        public int StartCount { get; private set; }

        public PayoutTradeOperation? ActiveOperation { get; private set; }

        public PayoutExecutorReadiness Probe() => TestData.ReadyExecutor() with { ActiveOperation = ActiveOperation };

        public bool StartOperation(PayoutLegDto leg)
        {
            StartCount++;
            ActiveOperation = new PayoutTradeOperation(
                leg.OperationId,
                leg.SessionId,
                leg.CharacterName,
                leg.HomeWorld,
                leg.AmountGil,
                PayoutTradeState.WaitingForPlayer,
                TestData.ExecutorInstanceId,
                0,
                DateTimeOffset.UtcNow,
                null,
                null,
                false);
            return true;
        }

        public bool CancelOperation(Guid operationId) => true;

        // Simulates the executor finishing (going idle) without a terminal event reaching the
        // coordinator, e.g. the durable outbox write threw after the trade completed.
        public void GoIdleWithoutEvent() => ActiveOperation = null;

        public PayoutTradeOperation? GetOperation(Guid operationId) => ActiveOperation?.OperationId == operationId ? ActiveOperation : null;

        public Guid OpenPersistedOperationId { get; private set; }

        public void MarkOpenEventPersisted(Guid operationId) => OpenPersistedOperationId = operationId;

        public void Emit(PayoutTradeEvent tradeEvent)
        {
            ActiveOperation = ActiveOperation is null
                ? null
                : ActiveOperation with
                {
                    State = tradeEvent.EventType switch
                    {
                        PayoutTradeEventType.PlayerDetected => PayoutTradeState.PlayerDetected,
                        PayoutTradeEventType.TradeOpened => PayoutTradeState.TradeOpened,
                        PayoutTradeEventType.TradeLocked => PayoutTradeState.TradeLocked,
                        PayoutTradeEventType.TradeCompleted => PayoutTradeState.Completed,
                        PayoutTradeEventType.TradeCancelled => PayoutTradeState.Cancelled,
                        _ => tradeEvent.IsAmbiguous ? PayoutTradeState.ReconciliationRequired : PayoutTradeState.Failed,
                    },
                    LastSequenceNumber = tradeEvent.SequenceNumber,
                    UpdatedAt = tradeEvent.OccurredAt,
                    ErrorCode = tradeEvent.ErrorCode,
                    ErrorMessage = tradeEvent.ErrorMessage,
                    IsAmbiguous = tradeEvent.IsAmbiguous,
                };
            TradeEventReceived?.Invoke(tradeEvent);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeTransport : IPayoutEventTransport
    {
        public bool ReturnWrongAck { get; set; }

        public Task<PayoutEventAckDto> SendAsync(PayoutEventDto payoutEvent, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PayoutEventAckDto(
                ReturnWrongAck ? Guid.NewGuid() : payoutEvent.OperationId,
                payoutEvent.SequenceNumber,
                DateTimeOffset.UtcNow));
    }
}
