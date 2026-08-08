namespace Catsino.Plugin.Contracts;

public enum PayoutOperationState
{
    Queued,
    WaitingForPlayer,
    TradeOpened,
    TradeLocked,
    Completed,
    Cancelled,
    Failed,
}

public enum PayoutEventType
{
    PlayerDetected,
    TradeOpened,
    TradeLocked,
    TradeCompleted,
    TradeCancelled,
    TradeFailed,
    TradeTimedOut,
}

public sealed record PayoutLegDto(
    Guid OperationId,
    Guid LegId,
    Guid SessionId,
    string CharacterName,
    string HomeWorld,
    long AmountGil,
    DateTimeOffset IssuedAt);

public sealed record PayoutOperationDto(
    Guid OperationId,
    Guid LegId,
    Guid SessionId,
    string CharacterName,
    string HomeWorld,
    long AmountGil,
    PayoutOperationState State,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTimeOffset UpdatedAt);

public sealed record PayoutEventDto(
    Guid OperationId,
    Guid LegId,
    long SequenceNumber,
    Guid PluginInstanceId,
    PayoutEventType EventType,
    string CharacterName,
    string HomeWorld,
    long AmountGil,
    DateTimeOffset OccurredAt,
    string? ErrorCode,
    string? ErrorMessage,
    bool IsAmbiguous);

public sealed record PayoutEventAckDto(Guid OperationId, long SequenceNumber, DateTimeOffset AcknowledgedAt);
public sealed record RetryCashoutRequest(Guid OperationId, string Reason);
public sealed record ReconcileOperationRequest(Guid OperationId, string Reason);
// Client-driven cash-out: the plugin runs the whole batch locally and reports every leg's outcome once.
// Outcome is "completed" | "failed" | "ambiguous". The backend books its own stored amounts.
public sealed record CashOutLegOutcome(int Number, string Outcome, string? ErrorCode);
public sealed record CashOutSettlementRequest(Guid CashOutId, IReadOnlyList<CashOutLegOutcome> Legs);
public sealed record OpenCashOutLegDto(int Number, long Gross, long Fee, long Net, string Status);
public sealed record OpenCashOutDto(Guid CashOutId, Guid SessionId, string CharacterName, string HomeWorld, IReadOnlyList<OpenCashOutLegDto> Legs);

public sealed record CancelPayoutOperationDto(Guid OperationId, string Reason);

public sealed record CashOutLegPreview(int Number, long Gross, long Fee, long Net);

public sealed record CashOutPreviewResponse(
    long Gross,
    decimal FeePercent,
    long Fee,
    long Net,
    bool NetIsZero,
    IReadOnlyList<CashOutLegPreview> Legs);

public sealed record PayoutLegResponse(
    Guid Id,
    int Number,
    long Gross,
    long Fee,
    long Net,
    string Status,
    int Attempt,
    Guid? OperationId);

public sealed record CashOutResponse(
    Guid Id,
    Guid SessionId,
    long Gross,
    decimal FeePercent,
    long Fee,
    long Net,
    string Status,
    long PaidGross,
    long PaidNet,
    IReadOnlyList<PayoutLegResponse> Legs,
    DateTimeOffset CreatedAt);
