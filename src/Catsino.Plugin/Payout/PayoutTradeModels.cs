using FFXIVClientStructs.FFXIV.Component.GUI;

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
        if (exactPartnerVerified && exactAmountSubmitted && localTradeLocked && partnerTradeLocked &&
            confirmationAccepted && exactGilDebit)
        {
            return TradeObservationResult.Completed;
        }

        // The trade opened but closed without a completed transfer. Either party (the dealer or
        // the paying-out player) can close the window. If no confirmation was accepted and no
        // gil left the dealer's wallet, we are certain no payout happened: this is a clean
        // cancellation, not an ambiguous outcome that would require reconciliation.
        if (!confirmationAccepted && snapshot.GilCurrent == snapshot.GilBefore)
        {
            return TradeObservationResult.Cancelled;
        }

        return TradeObservationResult.ReconciliationRequired;
    }
}

public enum PlayerWaitAction
{
    KeepWaiting,
    ResendTradeRequest,
    TimedOut,
}

// Pure decision helper for the phase before the trade window opens, while the executor is
// waiting for the paying-out player. Timeout takes precedence so a player who never appears (or
// never accepts the request) frees the payout slot instead of blocking it forever; otherwise the
// trade request is re-sent on the throttle. Purely time-based — the FFXIV client language must
// never influence this (spec 6.10).
public static class PlayerWaitPlanner
{
    public static PlayerWaitAction Plan(
        bool tradeWindowOpen,
        bool waitTimedOut,
        bool playerReadyToTrade,
        bool resendThrottleElapsed)
    {
        if (tradeWindowOpen)
        {
            return PlayerWaitAction.KeepWaiting;
        }

        if (waitTimedOut)
        {
            return PlayerWaitAction.TimedOut;
        }

        return playerReadyToTrade && resendThrottleElapsed
            ? PlayerWaitAction.ResendTradeRequest
            : PlayerWaitAction.KeepWaiting;
    }
}

public enum TradeConfirmationAction
{
    None,
    Lock,
    SummonConfirm,
    ConfirmYes,
}

// Pure decision helper for the two-phase trade lock / confirm handshake. It keeps all game
// pointer work in the unsafe executor while the ordering rules stay unit-testable:
//   Phase A (lock):    keep pressing the trade button until BOTH sides are structurally locked.
//   Phase B (confirm): only after lock, raise the SelectYesno dialog and accept it, retrying
//                      every throttle while the window stays open.
// The final confirmation is never treated as accepted here; the caller only records that after
// it actually presses the Yes button (ConfirmYes).
public static class TradeConfirmationPlanner
{
    public static TradeConfirmationAction Plan(
        bool tradeOpen,
        bool throttleElapsed,
        bool amountSubmitted,
        bool exactPartnerVerified,
        bool exactAmountSubmitted,
        bool bothSidesLocked,
        bool lockButtonEnabled,
        bool selectYesNoReady,
        bool yesButtonEnabled)
    {
        if (!tradeOpen || !throttleElapsed || !amountSubmitted)
        {
            return TradeConfirmationAction.None;
        }

        if (!bothSidesLocked)
        {
            // Phase A: retry the lock-in press until both sides are observed locked.
            return lockButtonEnabled && exactPartnerVerified && exactAmountSubmitted
                ? TradeConfirmationAction.Lock
                : TradeConfirmationAction.None;
        }

        // Phase B: both sides are locked. Accept the confirmation dialog if it is up, otherwise
        // re-press the trade button to raise it. Neither branch implies confirmation happened.
        if (selectYesNoReady && yesButtonEnabled)
        {
            return TradeConfirmationAction.ConfirmYes;
        }

        return lockButtonEnabled ? TradeConfirmationAction.SummonConfirm : TradeConfirmationAction.None;
    }
}

public enum TradeCloseDecision
{
    Cancelled,
    Completed,
    ReconciliationRequired,
}

// Decides the terminal outcome when the trade window closed but the executor never drove the
// trade to a structured, detector-observed state (e.g. the window was accepted and closed
// before the Trade addon became ready). It is deliberately conservative: it only reports a
// clean Cancelled when no gil moved and nothing was confirmed, and only Completed on an exact
// confirmed debit — anything else is ambiguous and must be reconciled.
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

internal static unsafe class PayoutTradeUiAccessor
{
    internal static bool IsAddonReady(AtkUnitBase* addon) =>
        addon != null && addon->IsVisible && addon->UldManager.LoadedState == AtkLoadState.Loaded;

    internal static bool TryGetTradeLockButton(AtkUnitBase* addon, out AtkComponentButton* tradeButton)
    {
        tradeButton = null;
        if (!IsAddonReady(addon) || addon->UldManager.NodeList == null || addon->UldManager.NodeListCount <= 3)
        {
            return false;
        }

        var tradeButtonNode = addon->UldManager.NodeList[3];
        if (tradeButtonNode == null || tradeButtonNode->Type != NodeType.Component)
        {
            return false;
        }

        var tradeComponent = tradeButtonNode->GetComponent();
        if (tradeComponent == null || tradeComponent->GetComponentType() != ComponentType.Button)
        {
            return false;
        }

        tradeButton = (AtkComponentButton*)tradeComponent;
        return true;
    }

    internal static bool TryGetLockState(AtkUnitBase* addon, out AtkComponentButton* tradeButton, out bool partnerLocked)
    {
        tradeButton = null;
        partnerLocked = false;
        if (!IsAddonReady(addon) || addon->UldManager.NodeList == null || addon->UldManager.NodeListCount <= 31)
        {
            return false;
        }

        var tradeButtonNode = addon->UldManager.NodeList[3];
        var partnerReadyContainer = addon->UldManager.NodeList[31];
        if (tradeButtonNode == null || partnerReadyContainer == null ||
            tradeButtonNode->Type != NodeType.Component || partnerReadyContainer->Type != NodeType.Component)
        {
            return false;
        }

        var tradeComponent = tradeButtonNode->GetComponent();
        var partnerComponentNode = partnerReadyContainer->GetAsAtkComponentNode();
        if (tradeComponent == null || tradeComponent->GetComponentType() != ComponentType.Button ||
            partnerComponentNode == null || partnerComponentNode->Component == null ||
            partnerComponentNode->Component->UldManager.NodeList == null ||
            partnerComponentNode->Component->UldManager.NodeListCount == 0)
        {
            return false;
        }

        var partnerReadyNode = partnerComponentNode->Component->UldManager.NodeList[0];
        var partnerReadyImage = partnerReadyNode == null ? null : partnerReadyNode->GetAsAtkImageNode();
        if (partnerReadyImage == null || partnerReadyNode->Type != NodeType.Image)
        {
            return false;
        }

        tradeButton = (AtkComponentButton*)tradeComponent;
        partnerLocked = partnerReadyImage->AtkResNode.Color.A == 0xFF;
        return true;
    }

    internal static void FireTradeGilInputCallback(AtkUnitBase* addon)
    {
        var values = stackalloc AtkValue[2];
        values[0] = new AtkValue { Type = AtkValueType.Int, Int = 2 };
        values[1] = new AtkValue { Type = 0, Int = 0 };
        addon->FireCallback(2, values, true);
    }

    internal static void ClickButton(AtkComponentButton* button, AtkUnitBase* addon)
    {
        var buttonNode = button->AtkComponentBase.OwnerNode;
        var buttonResource = buttonNode->AtkResNode;
        var eventData = (AtkEvent*)buttonResource.AtkEventManager.Event;
        addon->ReceiveEvent(eventData->State.EventType, (int)eventData->Param, buttonResource.AtkEventManager.Event);
    }
}
