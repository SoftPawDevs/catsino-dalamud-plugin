using Catsino.Plugin.Payout;

namespace Catsino.Plugin.Tests;

public sealed class TradeCompletionDetectorTests
{
    private const long ExpectedAmount = 1000;
    private const long GilBefore = 5000;

    [Fact]
    public void CompletesWhenAllProofPresentAndExactGilDebited()
    {
        var detector = new TradeCompletionDetector(ExpectedAmount);
        Assert.Equal(TradeObservationResult.InProgress, detector.Observe(InProgress(confirmed: true)));

        var closed = Closed(GilBefore - ExpectedAmount, confirmed: true);
        Assert.Equal(TradeObservationResult.Completed, detector.Observe(closed));
    }

    [Fact]
    public void CleanCancelWhenWindowClosesWithNoGilMovedAndNoConfirmation()
    {
        var detector = new TradeCompletionDetector(ExpectedAmount);
        Assert.Equal(TradeObservationResult.InProgress, detector.Observe(InProgress(confirmed: false)));

        // Either party closed the trade window; the dealer's gil never changed.
        var closed = Closed(GilBefore, confirmed: false);
        Assert.Equal(TradeObservationResult.Cancelled, detector.Observe(closed));
    }

    [Fact]
    public void CleanCancelWhenBothSidesLockedButFinalConfirmationNeverHappened()
    {
        var detector = new TradeCompletionDetector(ExpectedAmount);
        Assert.Equal(TradeObservationResult.InProgress, detector.Observe(new TradeStateSnapshot(
            TradeConditionOpen: true,
            TradeAddonReady: true,
            ExactPartnerVerified: true,
            ExactAmountSubmitted: true,
            LocalTradeLocked: true,
            PartnerTradeLocked: true,
            ConfirmationAccepted: false,
            GilBefore: GilBefore,
            GilCurrent: GilBefore,
            DefiniteCancellation: false,
            DefiniteFailure: false)));

        var closed = Closed(GilBefore, confirmed: false);
        Assert.Equal(TradeObservationResult.Cancelled, detector.Observe(closed));
    }

    [Fact]
    public void ReconciliationWhenConfirmedButGilDidNotDebitExactly()
    {
        var detector = new TradeCompletionDetector(ExpectedAmount);
        Assert.Equal(TradeObservationResult.InProgress, detector.Observe(InProgress(confirmed: true)));

        // Confirmation was accepted but the gil delta is not the exact expected debit.
        var closed = Closed(GilBefore - 1, confirmed: true);
        Assert.Equal(TradeObservationResult.ReconciliationRequired, detector.Observe(closed));
    }

    private static TradeStateSnapshot InProgress(bool confirmed) => new(
        TradeConditionOpen: true,
        TradeAddonReady: true,
        ExactPartnerVerified: true,
        ExactAmountSubmitted: true,
        LocalTradeLocked: confirmed,
        PartnerTradeLocked: confirmed,
        ConfirmationAccepted: confirmed,
        GilBefore: GilBefore,
        GilCurrent: GilBefore,
        DefiniteCancellation: false,
        DefiniteFailure: false);

    private static TradeStateSnapshot Closed(long gilCurrent, bool confirmed) => new(
        TradeConditionOpen: false,
        TradeAddonReady: false,
        ExactPartnerVerified: confirmed,
        ExactAmountSubmitted: confirmed,
        LocalTradeLocked: confirmed,
        PartnerTradeLocked: confirmed,
        ConfirmationAccepted: false,
        GilBefore: GilBefore,
        GilCurrent: gilCurrent,
        DefiniteCancellation: false,
        DefiniteFailure: false);
}
