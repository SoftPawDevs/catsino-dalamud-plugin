namespace Catsino.Plugin.Payout;

public sealed record PayoutExecutorReadiness(
    bool IsReady,
    Guid ExecutorInstanceId,
    PayoutTradeOperation? ActiveOperation,
    string Status);

public enum PayoutTradeState
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

public enum PayoutTradeEventType
{
    PlayerDetected,
    TradeOpened,
    TradeLocked,
    TradeCompleted,
    TradeCancelled,
    TradeFailed,
    TradeTimedOut,
}

public sealed record PayoutTradeOperation(
    Guid OperationId,
    Guid SessionId,
    string CharacterName,
    string HomeWorld,
    long AmountGil,
    PayoutTradeState State,
    Guid ExecutorInstanceId,
    long LastSequenceNumber,
    DateTimeOffset UpdatedAt,
    string? ErrorCode,
    string? ErrorMessage,
    bool IsAmbiguous);

public sealed record PayoutTradeEvent(
    Guid OperationId,
    Guid SessionId,
    string CharacterName,
    string HomeWorld,
    long AmountGil,
    PayoutTradeEventType EventType,
    Guid ExecutorInstanceId,
    long SequenceNumber,
    DateTimeOffset OccurredAt,
    string? ErrorCode,
    string? ErrorMessage,
    bool IsAmbiguous);

public enum TradeCloseDecision
{
    Cancelled,
    Completed,
    ReconciliationRequired,
}

// Decides the terminal outcome of a payout trade from the observed gil movement. It is deliberately
// conservative: only a confirmed, exact debit is Completed; only an unconfirmed, unchanged balance is
// a clean Cancelled; anything else is ambiguous and must be reconciled. This is the single source of
// financial truth — a button press is never treated as proof of payment.
public static class TradeCloseEvaluator
{
    public static TradeCloseDecision Evaluate(bool gilRead, long gilBefore, long gilCurrent, long expectedAmount, bool confirmationAccepted)
    {
        if (gilRead && confirmationAccepted && gilBefore >= expectedAmount && gilCurrent == gilBefore - expectedAmount)
        {
            return TradeCloseDecision.Completed;
        }

        if (gilRead && !confirmationAccepted && gilCurrent == gilBefore)
        {
            return TradeCloseDecision.Cancelled;
        }

        return TradeCloseDecision.ReconciliationRequired;
    }
}
