using Catsino.Plugin.Payout;

namespace Catsino.Plugin.Tests;

public sealed class PersistentPayoutOutboxTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "catsino-outbox-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SurvivesRestartDeduplicatesAndRemovesOnlyExactAck()
    {
        var payoutEvent = TestData.PayoutEvent();
        var firstProcess = new PersistentPayoutOutbox(directory);
        await firstProcess.EnqueueAsync(payoutEvent);

        var afterCrash = new PersistentPayoutOutbox(directory);
        Assert.Equal(payoutEvent, Assert.Single(await afterCrash.ReadPendingAsync()));
        await afterCrash.EnqueueAsync(payoutEvent);
        Assert.Equal(1, await afterCrash.CountAsync());

        Assert.False(await afterCrash.AcknowledgeAsync(Guid.NewGuid(), payoutEvent.SequenceNumber));
        Assert.Equal(1, await afterCrash.CountAsync());
        Assert.True(await afterCrash.AcknowledgeAsync(payoutEvent.OperationId, payoutEvent.SequenceNumber));
        Assert.Equal(0, await afterCrash.CountAsync());
    }

    [Fact]
    public async Task SameIdentityWithDifferentPayloadIsRejected()
    {
        var payoutEvent = TestData.PayoutEvent();
        var outbox = new PersistentPayoutOutbox(directory);
        await outbox.EnqueueAsync(payoutEvent);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            outbox.EnqueueAsync(payoutEvent with { AmountGil = payoutEvent.AmountGil + 1 }));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
