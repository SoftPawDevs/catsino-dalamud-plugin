using Catsino.Plugin.Payout;

namespace Catsino.Plugin.Tests;

public sealed class PlayerWaitPlannerTests
{
    [Fact]
    public void KeepsWaitingWhenTradeWindowAlreadyOpen()
    {
        // Once the window is open the waiting phase is over; even a due resend/timeout is ignored.
        Assert.Equal(
            PlayerWaitAction.KeepWaiting,
            PlayerWaitPlanner.Plan(tradeWindowOpen: true, waitTimedOut: true, playerReadyToTrade: true, resendThrottleElapsed: true));
    }

    [Fact]
    public void TimesOutWhenDeadlineElapsedEvenIfPlayerNeverAppeared()
    {
        Assert.Equal(
            PlayerWaitAction.TimedOut,
            PlayerWaitPlanner.Plan(tradeWindowOpen: false, waitTimedOut: true, playerReadyToTrade: false, resendThrottleElapsed: false));
    }

    [Fact]
    public void TimeoutTakesPrecedenceOverResend()
    {
        Assert.Equal(
            PlayerWaitAction.TimedOut,
            PlayerWaitPlanner.Plan(tradeWindowOpen: false, waitTimedOut: true, playerReadyToTrade: true, resendThrottleElapsed: true));
    }

    [Fact]
    public void ResendsWhenPlayerReadyAndThrottleElapsed()
    {
        Assert.Equal(
            PlayerWaitAction.ResendTradeRequest,
            PlayerWaitPlanner.Plan(tradeWindowOpen: false, waitTimedOut: false, playerReadyToTrade: true, resendThrottleElapsed: true));
    }

    [Fact]
    public void KeepsWaitingWhenThrottleNotElapsed()
    {
        Assert.Equal(
            PlayerWaitAction.KeepWaiting,
            PlayerWaitPlanner.Plan(tradeWindowOpen: false, waitTimedOut: false, playerReadyToTrade: true, resendThrottleElapsed: false));
    }

    [Fact]
    public void KeepsWaitingWhenPlayerNotYetReady()
    {
        Assert.Equal(
            PlayerWaitAction.KeepWaiting,
            PlayerWaitPlanner.Plan(tradeWindowOpen: false, waitTimedOut: false, playerReadyToTrade: false, resendThrottleElapsed: true));
    }
}
