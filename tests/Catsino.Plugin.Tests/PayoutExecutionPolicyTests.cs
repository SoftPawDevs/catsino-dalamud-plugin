using Catsino.Dropbox.Contracts;
using Catsino.Plugin.Payout;

namespace Catsino.Plugin.Tests;

public sealed class PayoutExecutionPolicyTests
{
    [Fact]
    public void AcceptsOnlyExactSupportedVersionCapabilitiesIdentityAndAmount()
    {
        var leg = TestData.Leg();
        var compatible = TestData.CompatibleDropbox();

        Assert.Null(PayoutExecutionPolicy.Validate(leg, true, compatible));
        Assert.NotNull(PayoutExecutionPolicy.Validate(leg with { AmountGil = 0 }, true, compatible));
        Assert.NotNull(PayoutExecutionPolicy.Validate(leg with { AmountGil = 1_000_001 }, true, compatible));
        Assert.NotNull(PayoutExecutionPolicy.Validate(leg with { CharacterName = "Wrong" }, true, compatible));
        Assert.NotNull(PayoutExecutionPolicy.Validate(leg with { RequiredDropboxIpcVersion = "1.0.1" }, true, compatible));
        Assert.NotNull(PayoutExecutionPolicy.Validate(leg with { IssuedAt = leg.IssuedAt.ToOffset(TimeSpan.FromHours(1)) }, true, compatible));
        Assert.NotNull(PayoutExecutionPolicy.Validate(leg, false, compatible));
    }

    [Fact]
    public void RejectsMissingCapabilityAndOneActiveOperation()
    {
        var leg = TestData.Leg();
        var compatible = TestData.CompatibleDropbox();
        Assert.NotNull(PayoutExecutionPolicy.Validate(
            leg,
            true,
            compatible with { Capabilities = compatible.Capabilities.Skip(1).ToArray() }));

        var active = new DropboxTradeOperation(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Other Player",
            "Ragnarok",
            1,
            DropboxTradeState.TradeOpened,
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow,
            null,
            null,
            false);
        Assert.NotNull(PayoutExecutionPolicy.Validate(leg, true, compatible with { ActiveOperation = active }));
    }
}
