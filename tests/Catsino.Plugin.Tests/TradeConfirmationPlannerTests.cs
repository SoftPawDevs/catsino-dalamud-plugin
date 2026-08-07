using Catsino.Plugin.Payout;

namespace Catsino.Plugin.Tests;

public sealed class TradeConfirmationPlannerTests
{
    [Fact]
    public void NoActionBeforeAmountSubmitted()
    {
        Assert.Equal(
            TradeConfirmationAction.None,
            TradeConfirmationPlanner.Plan(
                tradeOpen: true,
                throttleElapsed: true,
                amountSubmitted: false,
                exactPartnerVerified: true,
                exactAmountSubmitted: true,
                bothSidesLocked: false,
                lockButtonEnabled: true,
                selectYesNoReady: false,
                yesButtonEnabled: false));
    }

    [Fact]
    public void NoActionWhenTradeClosed()
    {
        Assert.Equal(
            TradeConfirmationAction.None,
            TradeConfirmationPlanner.Plan(
                tradeOpen: false,
                throttleElapsed: true,
                amountSubmitted: true,
                exactPartnerVerified: true,
                exactAmountSubmitted: true,
                bothSidesLocked: false,
                lockButtonEnabled: true,
                selectYesNoReady: true,
                yesButtonEnabled: true));
    }

    [Fact]
    public void NoActionWhenThrottled()
    {
        Assert.Equal(
            TradeConfirmationAction.None,
            TradeConfirmationPlanner.Plan(
                tradeOpen: true,
                throttleElapsed: false,
                amountSubmitted: true,
                exactPartnerVerified: true,
                exactAmountSubmitted: true,
                bothSidesLocked: false,
                lockButtonEnabled: true,
                selectYesNoReady: true,
                yesButtonEnabled: true));
    }

    [Fact]
    public void LocksWhenButtonEnabledAndPartnerVerifiedAndAmountSubmitted()
    {
        Assert.Equal(
            TradeConfirmationAction.Lock,
            TradeConfirmationPlanner.Plan(
                tradeOpen: true,
                throttleElapsed: true,
                amountSubmitted: true,
                exactPartnerVerified: true,
                exactAmountSubmitted: true,
                bothSidesLocked: false,
                lockButtonEnabled: true,
                selectYesNoReady: false,
                yesButtonEnabled: false));
    }

    [Fact]
    public void KeepsReturningLockWhileNotBothLocked()
    {
        // Retry semantics: the planner is pure, so calling it repeatedly before both sides are
        // locked keeps requesting the lock press every throttle tick.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(
                TradeConfirmationAction.Lock,
                TradeConfirmationPlanner.Plan(
                    tradeOpen: true,
                    throttleElapsed: true,
                    amountSubmitted: true,
                    exactPartnerVerified: true,
                    exactAmountSubmitted: true,
                    bothSidesLocked: false,
                    lockButtonEnabled: true,
                    selectYesNoReady: false,
                    yesButtonEnabled: false));
        }
    }

    [Fact]
    public void DoesNotLockWhenPartnerNotVerified()
    {
        Assert.Equal(
            TradeConfirmationAction.None,
            TradeConfirmationPlanner.Plan(
                tradeOpen: true,
                throttleElapsed: true,
                amountSubmitted: true,
                exactPartnerVerified: false,
                exactAmountSubmitted: true,
                bothSidesLocked: false,
                lockButtonEnabled: true,
                selectYesNoReady: false,
                yesButtonEnabled: false));
    }

    [Fact]
    public void DoesNotLockWhenButtonDisabled()
    {
        Assert.Equal(
            TradeConfirmationAction.None,
            TradeConfirmationPlanner.Plan(
                tradeOpen: true,
                throttleElapsed: true,
                amountSubmitted: true,
                exactPartnerVerified: true,
                exactAmountSubmitted: true,
                bothSidesLocked: false,
                lockButtonEnabled: false,
                selectYesNoReady: false,
                yesButtonEnabled: false));
    }

    [Fact]
    public void NeverConfirmsBeforeBothSidesLockedEvenIfDialogPresent()
    {
        // A stray SelectYesno must not be accepted while the trade is not yet locked: the
        // confirmation phase is strictly gated behind both-sides-locked.
        var action = TradeConfirmationPlanner.Plan(
            tradeOpen: true,
            throttleElapsed: true,
            amountSubmitted: true,
            exactPartnerVerified: true,
            exactAmountSubmitted: true,
            bothSidesLocked: false,
            lockButtonEnabled: true,
            selectYesNoReady: true,
            yesButtonEnabled: true);

        Assert.NotEqual(TradeConfirmationAction.ConfirmYes, action);
        Assert.NotEqual(TradeConfirmationAction.SummonConfirm, action);
        Assert.Equal(TradeConfirmationAction.Lock, action);
    }

    [Fact]
    public void SummonsConfirmWhenLockedButDialogAbsent()
    {
        Assert.Equal(
            TradeConfirmationAction.SummonConfirm,
            TradeConfirmationPlanner.Plan(
                tradeOpen: true,
                throttleElapsed: true,
                amountSubmitted: true,
                exactPartnerVerified: true,
                exactAmountSubmitted: true,
                bothSidesLocked: true,
                lockButtonEnabled: true,
                selectYesNoReady: false,
                yesButtonEnabled: false));
    }

    [Fact]
    public void ConfirmsYesWhenLockedAndDialogReady()
    {
        Assert.Equal(
            TradeConfirmationAction.ConfirmYes,
            TradeConfirmationPlanner.Plan(
                tradeOpen: true,
                throttleElapsed: true,
                amountSubmitted: true,
                exactPartnerVerified: true,
                exactAmountSubmitted: true,
                bothSidesLocked: true,
                lockButtonEnabled: true,
                selectYesNoReady: true,
                yesButtonEnabled: true));
    }

    [Fact]
    public void ConfirmYesTakesPrecedenceOverSummonWhenDialogReady()
    {
        Assert.Equal(
            TradeConfirmationAction.ConfirmYes,
            TradeConfirmationPlanner.Plan(
                tradeOpen: true,
                throttleElapsed: true,
                amountSubmitted: true,
                exactPartnerVerified: true,
                exactAmountSubmitted: true,
                bothSidesLocked: true,
                lockButtonEnabled: false,
                selectYesNoReady: true,
                yesButtonEnabled: true));
    }

    [Fact]
    public void SummonsConfirmWhenDialogPresentButYesNotYetEnabled()
    {
        // Dialog is opening but its Yes button is not clickable yet: keep the trade button
        // pressed to hold/raise the confirmation rather than doing nothing.
        Assert.Equal(
            TradeConfirmationAction.SummonConfirm,
            TradeConfirmationPlanner.Plan(
                tradeOpen: true,
                throttleElapsed: true,
                amountSubmitted: true,
                exactPartnerVerified: true,
                exactAmountSubmitted: true,
                bothSidesLocked: true,
                lockButtonEnabled: true,
                selectYesNoReady: true,
                yesButtonEnabled: false));
    }
}
