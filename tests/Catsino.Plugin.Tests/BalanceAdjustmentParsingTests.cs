using Catsino.Plugin.Security;

namespace Catsino.Plugin.Tests;

public sealed class BalanceAdjustmentParsingTests
{
    [Theory]
    [InlineData("250k", 250_000)]
    [InlineData("5m", 5_000_000)]
    [InlineData("5M", 5_000_000)]
    [InlineData("-1.5m", -1_500_000)]
    [InlineData("1.234m", 1_234_000)]
    [InlineData("2b", 2_000_000_000)]
    [InlineData("1000", 1_000)]
    [InlineData("-500", -500)]
    [InlineData("5.000.000", 5_000_000)]
    [InlineData("+250k", 250_000)]
    public void ParsesValidAdjustments(string text, long expected)
    {
        Assert.True(DealerInputValidator.TryParseBalanceAdjustment(text, out var amount));
        Assert.Equal(expected, amount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("m")]
    [InlineData("k")]
    [InlineData("0")]
    [InlineData("0m")]
    [InlineData("1.2345k")]   // 1234.5 gil is not whole
    [InlineData("abc")]
    [InlineData("5x")]
    public void RejectsInvalidAdjustments(string text)
    {
        Assert.False(DealerInputValidator.TryParseBalanceAdjustment(text, out _));
    }

    // The invite balance box reads the same shorthand as the adjustment box next to it — a dealer should not
    // have to remember which field takes "2.5m" and which one wants the zeros typed out.
    [Theory]
    [InlineData("2.5m", 2_500_000)]
    [InlineData("250k", 250_000)]
    [InlineData("1b", 1_000_000_000)]
    [InlineData("500", 500)]
    [InlineData("2.500.000", 2_500_000)]
    [InlineData("1 500 000", 1_500_000)]
    [InlineData("0", 0)]
    public void ParsesInviteBalances(string text, long expected)
    {
        Assert.True(DealerInputValidator.TryParseGilAmount(text, out var amount));
        Assert.Equal(expected, amount);
        Assert.Null(DealerInputValidator.ValidateInviteBalance(amount));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]        // an invite cannot start a player in debt
    [InlineData("-2.5m")]
    [InlineData("1.2345k")]   // 1234.5 gil is not whole
    [InlineData("abc")]
    [InlineData("5x")]
    public void RejectsInvalidInviteBalances(string text)
    {
        Assert.False(DealerInputValidator.TryParseGilAmount(text, out _));
    }
}
