using System.Text.Json;
using Catsino.Plugin.Backend;
using Catsino.Plugin.Contracts;

namespace Catsino.Plugin.Tests;

public sealed class ContractSerializationTests
{
    [Fact]
    public void VersionIsStable()
    {
        Assert.Equal("1.7.0", ContractVersion.Current);
        Assert.Equal("1.7.0", PluginVersion.Current);
    }

    // The dealer's Hold'em view is the newest wire contact point between the two repositories, so its exact
    // shape is pinned here: camelCase names, string statuses (no enums), UTC timestamps — and, critically,
    // no hole cards. The backend deliberately withholds them from the dealer audience.
    [Fact]
    public void HoldemTableUsesCamelCaseAndNeverCarriesHoleCards()
    {
        var sessionId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var membershipId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var timestamp = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var table = new HoldemTableDto(
            sessionId,
            Guid.Parse("30000000-0000-0000-0000-000000000003"),
            "flop",
            [new HoldemSeatDto(membershipId, "Exact Player", "Ragnarok", 0, 750_000, 50_000, 150_000, [], true, "playing", true, true, false, false, false, null, null, null)],
            [new BlackjackCardDto(1, 3), new BlackjackCardDto(13, 2), new BlackjackCardDto(7, 0)],
            [new HoldemPotDto(300_000, [membershipId])],
            300_000, 50_000, 50_000, 25_000, 50_000,
            membershipId, null, 10, timestamp.AddSeconds(45),
            ["fold", "call", "raise"], 50_000, 100_000, 750_000,
            timestamp);

        var json = JsonSerializer.Serialize(table, ContractJson.Options);
        Assert.Contains("\"sessionId\":\"10000000-0000-0000-0000-000000000001\"", json, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"flop\"", json, StringComparison.Ordinal);
        Assert.Contains("\"seatCapacity\":10", json, StringComparison.Ordinal);
        Assert.Contains("\"totalPot\":300000", json, StringComparison.Ordinal);
        Assert.Contains("\"observedAt\":\"2026-08-15T12:00:00+00:00\"", json, StringComparison.Ordinal);
        // The seat carries no cards even though it is in the hand; only the face-down marker.
        Assert.Contains("\"cards\":[]", json, StringComparison.Ordinal);
        Assert.Contains("\"hasHiddenCards\":true", json, StringComparison.Ordinal);

        var restored = JsonSerializer.Deserialize<HoldemTableDto>(json, ContractJson.Options)!;
        Assert.Equal(table.Status, restored.Status);
        Assert.Equal(table.SeatCapacity, restored.SeatCapacity);
        Assert.Equal(3, restored.Board.Count);
        Assert.Empty(Assert.Single(restored.Seats).Cards);
        Assert.Equal(TimeSpan.Zero, restored.ObservedAt.Offset);
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
            Guid.NewGuid(), sessionId, "Exact Player", "Ragnarok", 750,
            1000, true, "queued", "clear", now);
        var invite = new PendingInviteDto(
            Guid.NewGuid(), sessionId, "Other Player", "Phoenix", 500, now, now.AddMinutes(2));
        var roster = new SessionRosterDto(sessionId, [player], [invite], now);

        Assert.Equal(
            ["membershipId", "sessionId", "characterName", "homeWorld", "tokens", "deposit", "bettingLocked", "payoutState", "reconciliationState", "joinedAt"],
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
