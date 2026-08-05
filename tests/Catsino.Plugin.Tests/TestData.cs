using Catsino.Dropbox.Contracts;
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
        DropboxPayoutContract.IpcVersion,
        DropboxPayoutContract.SupportedBuildVersion,
        DateTimeOffset.UtcNow);

    internal static DropboxCompatibility CompatibleDropbox() => new(
        true,
        new DropboxVersionInfo(DropboxPayoutContract.IpcVersion, DropboxPayoutContract.SupportedBuildVersion, CompatibleDropboxPluginInstance),
        DropboxPayoutContract.RequiredCapabilities,
        true,
        null);

    internal static DropboxTradeEvent DropboxEvent(PayoutLegDto leg, DropboxTradeEventType eventType, bool ambiguous) => new(
        leg.OperationId,
        leg.SessionId,
        leg.CharacterName,
        leg.HomeWorld,
        leg.AmountGil,
        eventType,
        CompatibleDropboxPluginInstance,
        1,
        DateTimeOffset.UtcNow,
        ambiguous ? "reconciliationRequired" : "definiteFailure",
        ambiguous ? "Outcome is ambiguous." : "Trade definitely failed.",
        ambiguous);

    internal static readonly Guid CompatibleDropboxPluginInstance = Guid.NewGuid();

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
