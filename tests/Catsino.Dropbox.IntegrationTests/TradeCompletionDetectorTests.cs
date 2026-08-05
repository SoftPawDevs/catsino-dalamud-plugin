using Catsino.Dropbox.Contracts;

namespace Catsino.Dropbox.IntegrationTests;

public sealed class TradeCompletionDetectorTests
{
    [Theory]
    [InlineData("English")]
    [InlineData("German")]
    [InlineData("French")]
    [InlineData("Japanese")]
    [InlineData("unknown")]
    public void CompletionUsesOnlyStructuredStateForEveryLanguage(string _)
    {
        const long amount = 1_000_000;
        var detector = new TradeCompletionDetector(amount);

        Assert.Equal(TradeObservationResult.InProgress, detector.Observe(OpenSnapshot(amount)));
        Assert.Equal(TradeObservationResult.Completed, detector.Observe(ClosedSnapshot(amount, amount)));
    }

    [Fact]
    public void GenericWindowCloseIsAmbiguous()
    {
        const long amount = 500;
        var detector = new TradeCompletionDetector(amount);
        detector.Observe(OpenSnapshot(amount) with { PartnerTradeLocked = false, ConfirmationAccepted = false });

        var result = detector.Observe(ClosedSnapshot(amount, 0));

        Assert.Equal(TradeObservationResult.ReconciliationRequired, result);
        Assert.NotEqual(TradeObservationResult.Completed, result);
    }

    [Fact]
    public void ExactDebitWithoutLockAndConfirmationProofIsStillAmbiguous()
    {
        const long amount = 100;
        var detector = new TradeCompletionDetector(amount);
        detector.Observe(OpenSnapshot(amount) with { LocalTradeLocked = false, PartnerTradeLocked = false, ConfirmationAccepted = false });

        Assert.Equal(TradeObservationResult.ReconciliationRequired, detector.Observe(ClosedSnapshot(amount, amount)));
    }

    [Theory]
    [InlineData("exactPartner")]
    [InlineData("exactAmount")]
    [InlineData("localLock")]
    [InlineData("partnerLock")]
    [InlineData("confirmation")]
    public void CompletionRequiresEveryStructuredProof(string missingProof)
    {
        const long amount = 100;
        var detector = new TradeCompletionDetector(amount);
        var opened = OpenSnapshot(amount) with
        {
            ExactPartnerVerified = missingProof != "exactPartner",
            ExactAmountSubmitted = missingProof != "exactAmount",
            LocalTradeLocked = missingProof != "localLock",
            PartnerTradeLocked = missingProof != "partnerLock",
            ConfirmationAccepted = missingProof != "confirmation",
        };
        detector.Observe(opened);

        Assert.Equal(TradeObservationResult.ReconciliationRequired, detector.Observe(ClosedSnapshot(amount, amount)));
    }

    [Fact]
    public void DefiniteCancellationIsNotCompletion()
    {
        var detector = new TradeCompletionDetector(1);
        var snapshot = OpenSnapshot(1) with { DefiniteCancellation = true };

        Assert.Equal(TradeObservationResult.Cancelled, detector.Observe(snapshot));
    }

    private static TradeStateSnapshot OpenSnapshot(long amount) => new(
        true,
        true,
        true,
        true,
        true,
        true,
        true,
        amount + 5_000,
        amount + 5_000,
        false,
        false);

    private static TradeStateSnapshot ClosedSnapshot(long amount, long debit) => new(
        false,
        false,
        true,
        true,
        true,
        true,
        true,
        amount + 5_000,
        amount + 5_000 - debit,
        false,
        false);
}
