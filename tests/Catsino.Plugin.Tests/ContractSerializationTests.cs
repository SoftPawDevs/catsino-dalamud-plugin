using System.Text.Json;
using Catsino.Plugin.Backend;
using Catsino.Plugin.Contracts;

namespace Catsino.Plugin.Tests;

public sealed class ContractSerializationTests
{
    [Fact]
    public void VersionIsStable()
    {
        Assert.Equal("1.1.0", ContractVersion.Current);
        Assert.Equal("1.1.2", PluginVersion.Current);
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

    [Fact]
    public void DealerRosterContractsUseTheExactWireShape()
    {
        var now = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
        var sessionId = Guid.NewGuid();
        var player = new SessionRosterPlayerDto(
            Guid.NewGuid(), sessionId, "Exact Player", "Ragnarok", 1_000, -250, 750, 50,
            true, "queued", "clear", now);
        var invite = new PendingInviteDto(
            Guid.NewGuid(), sessionId, "Other Player", "Phoenix", 500, now, now.AddMinutes(2));
        var roster = new SessionRosterDto(sessionId, [player], [invite], now);

        Assert.Equal(
            ["membershipId", "sessionId", "characterName", "homeWorld", "balanceGil", "netGil", "tokens", "reservedTokens", "bettingLocked", "payoutState", "reconciliationState", "joinedAt"],
            PropertyNames(player));
        Assert.Equal(
            ["inviteId", "sessionId", "characterName", "homeWorld", "initialBalanceGil", "createdAt", "expiresAt"],
            PropertyNames(invite));
        Assert.Equal(["sessionId", "players", "pendingInvites", "observedAt"], PropertyNames(roster));
        var roundTrip = JsonSerializer.Deserialize<SessionRosterDto>(
            JsonSerializer.Serialize(roster, ContractJson.Options), ContractJson.Options)!;
        Assert.Equal(roster.SessionId, roundTrip.SessionId);
        Assert.Equal(roster.Players, roundTrip.Players);
        Assert.Equal(roster.PendingInvites, roundTrip.PendingInvites);
        Assert.Equal(roster.ObservedAt, roundTrip.ObservedAt);
    }

    [Fact]
    public void DealerMutationContractsUseTheExactWireShape()
    {
        Assert.Equal("{\"amountGil\":-500}", JsonSerializer.Serialize(new AdjustPlayerBalanceRequest(-500), ContractJson.Options));
        Assert.Equal(
            "{\"confirmAllAvailable\":true,\"confirmNetZero\":false,\"expectedGross\":1000,\"expectedFee\":50,\"expectedNet\":950}",
            JsonSerializer.Serialize(new DealerCashOutRequest(true, false, 1000, 50, 950), ContractJson.Options));
        Assert.Equal(
            ["sessionId", "mode"],
            PropertyNames(new SessionRemovalDto(Guid.NewGuid(), "archived")));
        Assert.Equal(
            "{\"characterName\":\"Exact Player\",\"homeWorld\":\"Ragnarok\",\"initialBalanceGil\":500}",
            JsonSerializer.Serialize(new CreateInviteRequest("Exact Player", "Ragnarok", 500), ContractJson.Options));
    }

    private static string[] PropertyNames<T>(T value) =>
        JsonSerializer.SerializeToElement(value, ContractJson.Options)
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
}
