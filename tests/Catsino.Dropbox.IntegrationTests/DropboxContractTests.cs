using System.Text.Json;
using Catsino.Dropbox.Contracts;

namespace Catsino.Dropbox.IntegrationTests;

public sealed class DropboxContractTests
{
    [Fact]
    public void VersionCapabilitiesAndMaximumAreStable()
    {
        Assert.Equal("Catsino.Dropbox.Payout.v1", DropboxPayoutContract.Prefix);
        Assert.Equal("1.0.0", DropboxPayoutContract.IpcVersion);
        Assert.Equal("1.0.0.7-catsino.1", DropboxPayoutContract.SupportedBuildVersion);
        Assert.Equal(1_000_000, DropboxPayoutContract.MaximumGil);
        Assert.Contains("languageIndependentTradeState", DropboxPayoutContract.RequiredCapabilities);
        Assert.Contains("noAutomaticRetry", DropboxPayoutContract.RequiredCapabilities);
    }

    [Fact]
    public void EventSerializationIsCamelCaseAndUtc()
    {
        var tradeEvent = new DropboxTradeEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Exact Player",
            "Ragnarok",
            1_000_000,
            DropboxTradeEventType.TradeCompleted,
            Guid.NewGuid(),
            7,
            new DateTimeOffset(2026, 8, 5, 14, 0, 0, TimeSpan.FromHours(2)),
            null,
            null,
            false);

        var json = JsonSerializer.Serialize(tradeEvent, DropboxContractJson.Options);
        var roundTrip = JsonSerializer.Deserialize<DropboxTradeEvent>(json, DropboxContractJson.Options)!;

        Assert.Contains("\"operationId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"eventType\":\"tradeCompleted\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("OperationId", json, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.Zero, roundTrip.OccurredAt.Offset);
    }
}
