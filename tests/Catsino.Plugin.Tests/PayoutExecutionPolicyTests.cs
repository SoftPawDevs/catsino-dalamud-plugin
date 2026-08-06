using Catsino.Plugin.Payout;

namespace Catsino.Plugin.Tests;

public sealed class PayoutExecutionPolicyTests
{
    [Fact]
    public void AcceptsOnlyExactSupportedVersionCapabilitiesIdentityAndAmount()
    {
        var leg = TestData.Leg();
        var executor = TestData.ReadyExecutor();

        Assert.Null(PayoutExecutionPolicy.Validate(leg, true, executor));
        Assert.NotNull(PayoutExecutionPolicy.Validate(leg with { AmountGil = 0 }, true, executor));
        Assert.NotNull(PayoutExecutionPolicy.Validate(leg with { AmountGil = 1_000_001 }, true, executor));
        Assert.NotNull(PayoutExecutionPolicy.Validate(leg with { CharacterName = "Wrong" }, true, executor));
        Assert.NotNull(PayoutExecutionPolicy.Validate(leg with { IssuedAt = leg.IssuedAt.ToOffset(TimeSpan.FromHours(1)) }, true, executor));
        Assert.NotNull(PayoutExecutionPolicy.Validate(leg, false, executor));
        Assert.NotNull(PayoutExecutionPolicy.Validate(leg, true, executor with { IsReady = false }));
    }

    [Fact]
    public void RejectsOneActiveOperation()
    {
        var leg = TestData.Leg();
        var active = new PayoutTradeOperation(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Other Player",
            "Ragnarok",
            1,
            PayoutTradeState.TradeOpened,
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow,
            null,
            null,
            false);
        Assert.NotNull(PayoutExecutionPolicy.Validate(leg, true, TestData.ReadyExecutor() with { ActiveOperation = active }));
    }
}
