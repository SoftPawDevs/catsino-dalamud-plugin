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
