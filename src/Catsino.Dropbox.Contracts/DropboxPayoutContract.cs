using System.Text.Json;
using System.Text.Json.Serialization;

namespace Catsino.Dropbox.Contracts;

public static class DropboxPayoutContract
{
    public const string Prefix = "Catsino.Dropbox.Payout.v1";
    public const string IpcVersion = "1.0.0";
    public const string SupportedBuildVersion = "1.0.0.7-catsino.1";
    public const long MaximumGil = 1_000_000;

    public const string GetVersion = Prefix + ".GetVersion";
    public const string GetCapabilities = Prefix + ".GetCapabilities";
    public const string SupportsLanguageIndependentTradeState = Prefix + ".SupportsLanguageIndependentTradeState";
    public const string EnablePayoutMode = Prefix + ".EnablePayoutMode";
    public const string DisablePayoutMode = Prefix + ".DisablePayoutMode";
    public const string QueueOutgoingGilTrade = Prefix + ".QueueOutgoingGilTrade";
    public const string CancelOutgoingTrade = Prefix + ".CancelOutgoingTrade";
    public const string GetTradeOperation = Prefix + ".GetTradeOperation";

    public const string PlayerDetected = Prefix + ".PlayerDetected";
    public const string TradeOpened = Prefix + ".TradeOpened";
    public const string TradeLocked = Prefix + ".TradeLocked";
    public const string TradeCompleted = Prefix + ".TradeCompleted";
    public const string TradeCancelled = Prefix + ".TradeCancelled";
    public const string TradeFailed = Prefix + ".TradeFailed";
    public const string TradeTimedOut = Prefix + ".TradeTimedOut";

    public static IReadOnlyList<string> RequiredCapabilities { get; } =
    [
        "outgoingGilOnly",
        "exactCharacterIdentity",
        "oneActiveOperation",
        "maximumGil:1000000",
        "languageIndependentTradeState",
        "noAutomaticRetry",
    ];
}

public enum DropboxTradeState
{
    WaitingForPlayer,
    PlayerDetected,
    TradeOpened,
    TradeLocked,
    Completed,
    Cancelled,
    Failed,
    ReconciliationRequired,
}

public enum DropboxTradeEventType
{
    PlayerDetected,
    TradeOpened,
    TradeLocked,
    TradeCompleted,
    TradeCancelled,
    TradeFailed,
    TradeTimedOut,
}

public sealed record DropboxVersionInfo(
    string IpcVersion,
    string BuildVersion,
    Guid PluginInstanceId);

public sealed record DropboxTradeOperation(
    Guid OperationId,
    Guid SessionId,
    string CharacterName,
    string HomeWorld,
    long AmountGil,
    DropboxTradeState State,
    Guid PluginInstanceId,
    long LastSequenceNumber,
    DateTimeOffset UpdatedAt,
    string? ErrorCode,
    string? ErrorMessage,
    bool IsAmbiguous);

public sealed record DropboxTradeEvent(
    Guid OperationId,
    Guid SessionId,
    string CharacterName,
    string HomeWorld,
    long AmountGil,
    DropboxTradeEventType EventType,
    Guid PluginInstanceId,
    long SequenceNumber,
    DateTimeOffset OccurredAt,
    string? ErrorCode,
    string? ErrorMessage,
    bool IsAmbiguous);

public static class DropboxContractJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

internal sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset().ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime());
}

public sealed record TradeStateSnapshot(
    bool TradeConditionOpen,
    bool TradeAddonReady,
    bool ExactPartnerVerified,
    bool ExactAmountSubmitted,
    bool LocalTradeLocked,
    bool PartnerTradeLocked,
    bool ConfirmationAccepted,
    long GilBefore,
    long GilCurrent,
    bool DefiniteCancellation,
    bool DefiniteFailure);

public enum TradeObservationResult
{
    Waiting,
    InProgress,
    Completed,
    Cancelled,
    Failed,
    ReconciliationRequired,
}

public sealed class TradeCompletionDetector(long expectedAmount)
{
    private bool sawTradeOpen;
    private bool exactPartnerVerified;
    private bool exactAmountSubmitted;
    private bool localTradeLocked;
    private bool partnerTradeLocked;
    private bool confirmationAccepted;

    public TradeObservationResult Observe(TradeStateSnapshot snapshot)
    {
        if (snapshot.DefiniteCancellation)
        {
            return TradeObservationResult.Cancelled;
        }

        if (snapshot.DefiniteFailure)
        {
            return TradeObservationResult.Failed;
        }

        if (snapshot.TradeConditionOpen && snapshot.TradeAddonReady)
        {
            sawTradeOpen = true;
            exactPartnerVerified |= snapshot.ExactPartnerVerified;
            exactAmountSubmitted |= snapshot.ExactAmountSubmitted;
            localTradeLocked |= snapshot.LocalTradeLocked;
            partnerTradeLocked |= snapshot.PartnerTradeLocked;
            confirmationAccepted |= snapshot.ConfirmationAccepted;
            return TradeObservationResult.InProgress;
        }

        if (!sawTradeOpen)
        {
            return TradeObservationResult.Waiting;
        }

        var exactGilDebit = snapshot.GilBefore >= expectedAmount &&
                            snapshot.GilCurrent == snapshot.GilBefore - expectedAmount;
        return exactPartnerVerified && exactAmountSubmitted && localTradeLocked && partnerTradeLocked &&
               confirmationAccepted && exactGilDebit
            ? TradeObservationResult.Completed
            : TradeObservationResult.ReconciliationRequired;
    }
}
