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
}
