using Catsino.Dropbox.Contracts;
using Catsino.Plugin.Contracts;
using Catsino.Plugin.Dropbox;
using Catsino.Plugin.Payout;

namespace Catsino.Plugin.Tests;

public sealed class PayoutCoordinatorTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "catsino-coordinator-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AllowsOneOperationAndNeverAutomaticallyRetriesTerminalFailure()
    {
        var fakeDropbox = new FakeDropbox();
        var outbox = new PersistentPayoutOutbox(directory);
        var transport = new FakeTransport();
        using var coordinator = new PayoutCoordinator(fakeDropbox, outbox, transport, () => true, _ => { });
        var leg = TestData.Leg();

        await coordinator.StartBackendLegAsync(leg);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartBackendLegAsync(TestData.Leg()));
        Assert.Equal(1, fakeDropbox.QueueCount);

        fakeDropbox.Emit(TestData.DropboxEvent(leg, DropboxTradeEventType.TradeFailed, ambiguous: false));
        await WaitUntilAsync(() => coordinator.ActiveOperation?.State == PayoutOperationState.Failed);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartBackendLegAsync(leg));
        Assert.Equal(1, fakeDropbox.QueueCount);
    }

    [Fact]
    public async Task AmbiguousOutcomeRequiresReconciliationAndWaitsForExactAck()
    {
        var fakeDropbox = new FakeDropbox();
        var outbox = new PersistentPayoutOutbox(directory);
        var transport = new FakeTransport { ReturnWrongAck = true };
        using var coordinator = new PayoutCoordinator(fakeDropbox, outbox, transport, () => true, _ => { });
        var leg = TestData.Leg();
        await coordinator.StartBackendLegAsync(leg);

        fakeDropbox.Emit(TestData.DropboxEvent(leg, DropboxTradeEventType.TradeFailed, ambiguous: true));
        await WaitUntilAsync(() => coordinator.ActiveOperation?.State == PayoutOperationState.ReconciliationRequired);
        Assert.Equal(1, await outbox.CountAsync());

        transport.ReturnWrongAck = false;
        await coordinator.ReplayOutboxAsync();
        Assert.Equal(0, await outbox.CountAsync());
        Assert.Equal(1, fakeDropbox.QueueCount);
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

    private sealed class FakeDropbox : IDropboxPayoutClient
    {
        public event Action<DropboxTradeEvent>? TradeEventReceived;

        public int QueueCount { get; private set; }

        public DropboxCompatibility Probe() => TestData.CompatibleDropbox();

        public bool EnablePayoutMode(Guid sessionId) => true;

        public bool DisablePayoutMode(Guid sessionId) => true;

        public bool QueueOutgoingGilTrade(Guid operationId, string characterName, string homeWorld, long amountGil)
        {
            QueueCount++;
            return true;
        }

        public bool CancelOutgoingTrade(Guid operationId) => true;

        public DropboxTradeOperation? GetTradeOperation(Guid operationId) => null;

        public void Emit(DropboxTradeEvent tradeEvent) => TradeEventReceived?.Invoke(tradeEvent);

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
