using Catsino.Plugin.Payout;

namespace Catsino.Plugin.Tests;

public sealed class TradeCloseEvaluatorTests
{
    private const long GilBefore = 5000;
    private const long Amount = 1000;

    [Fact]
    public void CleanCancelWhenNoGilMovedAndNotConfirmed()
    {
        Assert.Equal(
            TradeCloseDecision.Cancelled,
            TradeCloseEvaluator.Evaluate(gilRead: true, GilBefore, GilBefore, Amount, confirmationAccepted: false));
    }

    [Fact]
    public void CompletedOnExactDebitWithConfirmation()
    {
        Assert.Equal(
            TradeCloseDecision.Completed,
            TradeCloseEvaluator.Evaluate(gilRead: true, GilBefore, GilBefore - Amount, Amount, confirmationAccepted: true));
    }

    [Fact]
    public void ReconciliationWhenConfirmedButNoDebit()
    {
        Assert.Equal(
            TradeCloseDecision.ReconciliationRequired,
            TradeCloseEvaluator.Evaluate(gilRead: true, GilBefore, GilBefore, Amount, confirmationAccepted: true));
    }

    [Fact]
    public void ReconciliationWhenExactDebitButNotConfirmed()
    {
        Assert.Equal(
            TradeCloseDecision.ReconciliationRequired,
            TradeCloseEvaluator.Evaluate(gilRead: true, GilBefore, GilBefore - Amount, Amount, confirmationAccepted: false));
    }

    [Fact]
    public void ReconciliationWhenPartialDebit()
    {
        Assert.Equal(
            TradeCloseDecision.ReconciliationRequired,
            TradeCloseEvaluator.Evaluate(gilRead: true, GilBefore, GilBefore - 1, Amount, confirmationAccepted: false));
    }

    [Fact]
    public void ReconciliationWhenGilCouldNotBeRead()
    {
        Assert.Equal(
            TradeCloseDecision.ReconciliationRequired,
            TradeCloseEvaluator.Evaluate(gilRead: false, GilBefore, GilBefore, Amount, confirmationAccepted: false));
    }
}
