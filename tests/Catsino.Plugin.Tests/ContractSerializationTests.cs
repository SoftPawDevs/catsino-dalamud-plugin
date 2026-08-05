using System.Text.Json;
using Catsino.Plugin.Contracts;

namespace Catsino.Plugin.Tests;

public sealed class ContractSerializationTests
{
    [Fact]
    public void VersionIsStable()
    {
        Assert.Equal("1.0.0", ContractVersion.Current);
    }

    [Fact]
    public void SessionUsesCamelCaseStringsDecimalAndLong()
    {
        var id = Guid.NewGuid();
        var timestamp = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
        var session = new GameSessionDto(
            id,
            "plinko",
            12.50m,
            GameSessionState.Created,
            2,
            9_007_199_254_740_991L,
            "pending",
            "clear",
            timestamp,
            null,
            null);

        var json = JsonSerializer.Serialize(session, ContractJson.Options);

        Assert.Contains("\"sessionId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"feePercent\":12.50", json, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"created\"", json, StringComparison.Ordinal);
        Assert.Contains("9007199254740991", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionId", json, StringComparison.Ordinal);
        Assert.Equal(session, JsonSerializer.Deserialize<GameSessionDto>(json, ContractJson.Options));
    }

    [Fact]
    public void PayoutTimestampRemainsUtc()
    {
        var payoutEvent = TestData.PayoutEvent();
        var json = JsonSerializer.Serialize(payoutEvent, ContractJson.Options);
        var roundTrip = JsonSerializer.Deserialize<PayoutEventDto>(json, ContractJson.Options)!;

        Assert.Equal(TimeSpan.Zero, roundTrip.OccurredAt.Offset);
        Assert.Equal(payoutEvent.OperationId, roundTrip.OperationId);
        Assert.Equal(payoutEvent.SequenceNumber, roundTrip.SequenceNumber);
    }

    [Fact]
    public void NonUtcInputIsNormalizedByTheContract()
    {
        var payoutEvent = TestData.PayoutEvent() with
        {
            OccurredAt = new DateTimeOffset(2026, 8, 5, 14, 0, 0, TimeSpan.FromHours(2)),
        };

        var json = JsonSerializer.Serialize(payoutEvent, ContractJson.Options);
        var roundTrip = JsonSerializer.Deserialize<PayoutEventDto>(json, ContractJson.Options)!;

        Assert.Contains("12:00:00", json, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.Zero, roundTrip.OccurredAt.Offset);
        Assert.Equal(12, roundTrip.OccurredAt.Hour);
    }
}
