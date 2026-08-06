using Catsino.Plugin.Contracts;
using Catsino.Plugin.Payout;

namespace Catsino.Plugin.Tests;

internal static class TestData
{
    internal static PayoutLegDto Leg() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "Exact Player",
        "Ragnarok",
        1_000_000,
        DateTimeOffset.UtcNow);

    internal static PayoutExecutorReadiness ReadyExecutor() => new(
        true,
        ExecutorInstanceId,
        null,
        "ready");

    internal static PayoutTradeEvent TradeEvent(PayoutLegDto leg, PayoutTradeEventType eventType, bool ambiguous) => new(
        leg.OperationId,
        leg.SessionId,
        leg.CharacterName,
        leg.HomeWorld,
        leg.AmountGil,
        eventType,
        ExecutorInstanceId,
        1,
        DateTimeOffset.UtcNow,
        ambiguous ? "reconciliationRequired" : "definiteFailure",
        ambiguous ? "Outcome is ambiguous." : "Trade definitely failed.",
        ambiguous);

    internal static readonly Guid ExecutorInstanceId = Guid.NewGuid();

    internal static PayoutEventDto PayoutEvent() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        1,
        Guid.NewGuid(),
        PayoutEventType.TradeLocked,
        "Exact Player",
        "Ragnarok",
        500,
        DateTimeOffset.UtcNow,
        null,
        null,
        false);

    internal static SessionPlayerDto Player(SessionPlayerState state) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "Exact Player",
        "Ragnarok",
        state,
        0,
        null,
        "pending",
        "clear",
        DateTimeOffset.UtcNow);
}
