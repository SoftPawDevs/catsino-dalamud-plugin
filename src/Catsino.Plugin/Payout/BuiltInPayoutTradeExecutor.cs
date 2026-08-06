using Catsino.Plugin.Contracts;
using Catsino.Plugin.Runtime;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace Catsino.Plugin.Payout;

public sealed unsafe class BuiltInPayoutTradeExecutor : IPayoutTradeExecutor
{
    private static readonly TimeSpan AmountSubmissionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TradeReadyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ActionThrottle = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan TradeRequestThrottle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(2);

    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly ITargetManager targets;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Guid executorInstanceId = Guid.NewGuid();
    private readonly HashSet<Guid> usedOperationIds = [];

    private PayoutTradeOperation? operation;
    private TradeCompletionDetector? detector;
    private long sequenceNumber;
    private long gilBefore;
    private bool playerDetected;
    private uint expectedPlayerEntityId;
    private uint? expectedHomeWorldRowId;
    private DateTimeOffset? lastWaitDiagnosticAt;
    private bool exactPartnerVerified;
    private bool tradeOpened;
    private bool tradeConditionSeenOpen;
    private DateTimeOffset? lastLockDiagnosticAt;
    private bool tradeRequested;
    private bool gilInputOpened;
    private bool amountSubmitted;
    private bool tradeLocked;
    private bool confirmationAccepted;
    private bool structuredDumpLogged;
    private DateTimeOffset? tradeConditionOpenSince;
    private DateTimeOffset? tradeClosedAt;
    private DateTimeOffset nextActionAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextTradeRequestAt = DateTimeOffset.MinValue;
    private bool disposed;

    public BuiltInPayoutTradeExecutor(
        IFramework framework,
        IObjectTable objects,
        ITargetManager targets,
        ICondition condition,
        IGameGui gameGui,
        IDataManager data,
        IPluginLog log)
    {
        this.framework = framework;
        this.objects = objects;
        this.targets = targets;
        this.condition = condition;
        this.gameGui = gameGui;
        this.data = data;
        this.log = log;
        framework.Update += OnFrameworkUpdate;
    }

    public event Action<PayoutTradeEvent>? TradeEventReceived;

    public PayoutExecutorReadiness Probe() => new(
        !disposed,
        executorInstanceId,
        operation,
        disposed ? "disposed" : operation is null ? "ready" : operation.State.ToString());

    public bool StartOperation(PayoutLegDto leg)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (operation is not null || leg.OperationId == Guid.Empty || leg.SessionId == Guid.Empty || leg.LegId == Guid.Empty ||
            usedOperationIds.Contains(leg.OperationId) || leg.AmountGil is < 1 or > PayoutExecutionPolicy.MaximumTradeGil ||
            !IsExactCharacterName(leg.CharacterName) || !IsExactWorldName(leg.HomeWorld))
        {
            return false;
        }

        usedOperationIds.Add(leg.OperationId);
        ResetTransientState();
        gilBefore = ReadCurrentGil();
        detector = new TradeCompletionDetector(leg.AmountGil);
        operation = new PayoutTradeOperation(
            leg.OperationId,
            leg.SessionId,
            leg.CharacterName,
            leg.HomeWorld,
            leg.AmountGil,
            PayoutTradeState.WaitingForPlayer,
            executorInstanceId,
            sequenceNumber,
            DateTimeOffset.UtcNow,
            null,
            null,
            false);
        return true;
    }

    public bool CancelOperation(Guid operationId)
    {
        if (operation?.OperationId != operationId || !IsAutomating())
        {
            return false;
        }

        if (tradeOpened || condition[ConditionFlag.TradeOpen])
        {
            return false;
        }

        PublishTerminal(PayoutTradeEventType.TradeCancelled, PayoutTradeState.Cancelled, "backendCancelled", null, false);
        operation = null;
        detector = null;
        return true;
    }

    public PayoutTradeOperation? GetOperation(Guid operationId) => operation?.OperationId == operationId ? operation : null;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        framework.Update -= OnFrameworkUpdate;
        operation = null;
        detector = null;
        TradeEventReceived = null;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!IsAutomating() || operation is null || detector is null)
        {
            return;
        }

        var currentOperation = operation!;

        try
        {
            if (!condition[ConditionFlag.TradeOpen])
            {
                if (!tradeOpened)
                {
                    if (!tradeConditionSeenOpen)
                    {
                        // The player has not accepted a trade yet: keep waiting for them.
                        tradeConditionOpenSince = null;
                        WaitForExactPlayer(currentOperation);
                        return;
                    }

                    // The trade window was accepted and then closed before the executor could
                    // drive it (e.g. the Trade addon never became ready). Never loop back to
                    // re-requesting the trade; settle briefly to read the final gil, then emit a
                    // terminal event so the backend can release the tokens.
                    tradeClosedAt ??= DateTimeOffset.UtcNow;
                    if (DateTimeOffset.UtcNow - tradeClosedAt.Value < SettleWindow &&
                        (!TryReadCurrentGil(out var undrivenGil) || undrivenGil != gilBefore - currentOperation.AmountGil))
                    {
                        return;
                    }

                    ResolveClosedTrade(currentOperation);
                    operation = null;
                    detector = null;
                    return;
                }

                tradeClosedAt ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - tradeClosedAt.Value < SettleWindow &&
                    (!TryReadCurrentGil(out var closedGil) || closedGil != gilBefore - currentOperation.AmountGil))
                {
                    return;
                }

                ObserveAndFinish(currentOperation, false, false, false);
                operation = null;
                detector = null;
                return;
            }

            tradeConditionOpenSince ??= DateTimeOffset.UtcNow;
            tradeConditionSeenOpen = true;
            if (!TryGetAddonByName("Trade", out var tradeAddon) || !PayoutTradeUiAccessor.IsAddonReady(tradeAddon))
            {
                if (tradeConditionOpenSince is not null && DateTimeOffset.UtcNow - tradeConditionOpenSince.Value > TradeReadyTimeout)
                {
                    PublishTerminal(
                        PayoutTradeEventType.TradeFailed,
                        PayoutTradeState.Failed,
                        "tradeAddonUnavailable",
                        "The trade opened but its addon never became ready in time.",
                        false);
                    operation = null;
                    detector = null;
                }

                return;
            }

            if (!tradeOpened)
            {
                tradeOpened = true;
                UpdateOperation(PayoutTradeState.TradeOpened, null, null, false);
                Publish(PayoutTradeEventType.TradeOpened, null, null, false);
            }

            if (!gilInputOpened && DateTimeOffset.UtcNow >= nextActionAt)
            {
                PayoutTradeUiAccessor.FireTradeGilInputCallback(tradeAddon);
                gilInputOpened = true;
                nextActionAt = DateTimeOffset.UtcNow.Add(ActionThrottle);
            }
            else if (gilInputOpened && !amountSubmitted && TrySubmitTradeAmount((int)operation.AmountGil))
            {
                amountSubmitted = true;
                nextActionAt = DateTimeOffset.UtcNow.Add(ActionThrottle);
            }

            if (!TryReadStructuredTradeState(operation, out var state))
            {
                return;
            }

            if (!structuredDumpLogged)
            {
                structuredDumpLogged = true;
                LogStructuredDump(operation);
            }

            if (!amountSubmitted)
            {
                if (tradeConditionOpenSince is not null &&
                    DateTimeOffset.UtcNow - tradeConditionOpenSince.Value > AmountSubmissionTimeout)
                {
                    PublishTerminal(
                        PayoutTradeEventType.TradeFailed,
                        PayoutTradeState.Failed,
                        "amountSubmissionFailed",
                        "The trade opened, but the expected gil amount could not be entered.",
                        false);
                    operation = null;
                    detector = null;
                    return;
                }

                return;
            }

            if (!tradeLocked && state.LocalTradeLocked && state.PartnerTradeLocked)
            {
                tradeLocked = true;
                UpdateOperation(PayoutTradeState.TradeLocked, null, null, false);
                Publish(PayoutTradeEventType.TradeLocked, null, null, false);
            }

            TryConfirmTrade(tradeAddon, state);
            detector.Observe(state);
            LogLockDiagnostics(currentOperation, state, tradeAddon);
        }
        catch (Exception exception)
        {
            if (tradeOpened)
            {
                LogPayoutFailure("update", currentOperation, exception);
                PublishTerminal(
                    PayoutTradeEventType.TradeFailed,
                    PayoutTradeState.Failed,
                    "structuredObservationFailed",
                    $"Structured payout observation failed after the trade opened: {exception.Message}",
                    false);
            }
            else
            {
                log.Error($"Catsino payout failed before trade opened for {currentOperation.CharacterName}@{currentOperation.HomeWorld}: {exception}");
                PublishTerminal(PayoutTradeEventType.TradeFailed, PayoutTradeState.Failed, "automationFailed", exception.Message, false);
            }

            operation = null;
            detector = null;
        }
    }

    private void WaitForExactPlayer(PayoutTradeOperation current)
    {
        if (expectedHomeWorldRowId is null)
        {
            expectedHomeWorldRowId = ResolveHomeWorldRowId(current.HomeWorld);
            if (expectedHomeWorldRowId is null)
            {
                LogWaitDiagnostics(current, $"the Home World '{current.HomeWorld}' did not resolve to any game world");
                return;
            }
        }

        var worldRowId = expectedHomeWorldRowId.Value;
        var targetPlayer = objects
            .OfType<IPlayerCharacter>()
            .FirstOrDefault(player => player.IsTargetable &&
                player.HomeWorld.RowId == worldRowId &&
                string.Equals(player.Name.ToString(), current.CharacterName, StringComparison.OrdinalIgnoreCase));
        if (targetPlayer is null)
        {
            LogWaitDiagnostics(current, "no targetable nearby player matched the exact name and Home World");
            return;
        }

        if (!playerDetected)
        {
            playerDetected = true;
            UpdateOperation(PayoutTradeState.PlayerDetected, null, null, false);
            Publish(PayoutTradeEventType.PlayerDetected, null, null, false);
        }

        expectedPlayerEntityId = targetPlayer.EntityId;
        if (targets.Target?.Address != targetPlayer.Address)
        {
            targets.Target = targetPlayer;
            nextTradeRequestAt = DateTimeOffset.UtcNow.Add(ActionThrottle);
            return;
        }

        if (tradeRequested || DateTimeOffset.UtcNow < nextTradeRequestAt)
        {
            return;
        }

        GameChat.SendCommand("/trade");
        tradeRequested = true;
        nextTradeRequestAt = DateTimeOffset.UtcNow.Add(TradeRequestThrottle);
    }

    private bool TryReadStructuredTradeState(PayoutTradeOperation current, out TradeStateSnapshot state)
    {
        state = null!;
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null || inventoryManager->TradeItemsLocal.Length < 6)
        {
            return false;
        }

        var localState = inventoryManager->TradeLocalState;
        var remoteState = inventoryManager->TradeRemoteState;
        if (localState == TradeState.NotTrading)
        {
            return false;
        }

        if (!TryReadCurrentGil(out var currentGil))
        {
            return false;
        }

        exactPartnerVerified = expectedPlayerEntityId != 0 &&
                              inventoryManager->TradePartnerEntityId == expectedPlayerEntityId &&
                              string.Equals(inventoryManager->TradePartnerNameString, current.CharacterName, StringComparison.OrdinalIgnoreCase);

        var anyNonGilItem = false;
        var slots = inventoryManager->TradeItemsLocal;
        for (var index = 0; index < slots.Length; index++)
        {
            var itemId = slots[index].ItemId;
            if (itemId != 0 && itemId != 1)
            {
                anyNonGilItem = true;
            }
        }

        var localLocked = localState >= TradeState.LockedIn;
        var partnerLocked = remoteState >= TradeState.LockedIn;
        state = new TradeStateSnapshot(
            condition[ConditionFlag.TradeOpen],
            true,
            exactPartnerVerified,
            amountSubmitted && !anyNonGilItem,
            localLocked,
            partnerLocked,
            confirmationAccepted || localState == TradeState.Confirmed,
            gilBefore,
            currentGil,
            false,
            false);
        return true;
    }

    private void ObserveAndFinish(PayoutTradeOperation current, bool tradeAddonReady, bool localLocked, bool partnerLocked)
    {
        var currentGil = TryReadCurrentGil(out var observedGil) ? observedGil : gilBefore;
        var snapshot = new TradeStateSnapshot(
            condition[ConditionFlag.TradeOpen],
            tradeAddonReady,
            false,
            false,
            localLocked,
            partnerLocked,
            false,
            gilBefore,
            currentGil,
            false,
            false);
        var result = detector!.Observe(snapshot);
        if (result == TradeObservationResult.Completed)
        {
            PublishTerminal(PayoutTradeEventType.TradeCompleted, PayoutTradeState.Completed, null, null, false);
        }
        else if (result == TradeObservationResult.Cancelled)
        {
            // The trade window was closed (by the dealer or the paying-out player) before the
            // payout was confirmed and no gil moved. Cancel cleanly: the backend releases the
            // unpaid remainder to the player so the dealer can start a fresh cash-out.
            PublishTerminal(
                PayoutTradeEventType.TradeCancelled,
                PayoutTradeState.Cancelled,
                "tradeWindowClosed",
                "The trade window closed before the payout was confirmed; no gil was transferred.",
                false);
        }
        else if (result == TradeObservationResult.ReconciliationRequired)
        {
            PublishTerminal(
                PayoutTradeEventType.TradeFailed,
                PayoutTradeState.ReconciliationRequired,
                "reconciliationRequired",
                "The trade closed without complete structured proof of the exact gil transfer.",
                true);
        }
    }

    private void ResolveClosedTrade(PayoutTradeOperation current)
    {
        var gilRead = TryReadCurrentGil(out var gil);
        switch (TradeCloseEvaluator.Evaluate(gilRead, gilBefore, gil, current.AmountGil, confirmationAccepted))
        {
            case TradeCloseDecision.Completed:
                PublishTerminal(PayoutTradeEventType.TradeCompleted, PayoutTradeState.Completed, null, null, false);
                break;
            case TradeCloseDecision.Cancelled:
                PublishTerminal(
                    PayoutTradeEventType.TradeCancelled,
                    PayoutTradeState.Cancelled,
                    "tradeWindowClosed",
                    "The trade window closed before the payout could be processed; no gil was transferred.",
                    false);
                break;
            default:
                PublishTerminal(
                    PayoutTradeEventType.TradeFailed,
                    PayoutTradeState.ReconciliationRequired,
                    "reconciliationRequired",
                    "The trade closed without complete structured proof of the exact gil transfer.",
                    true);
                break;
        }
    }

    private bool TrySubmitTradeAmount(int amountGil)
    {
        if (DateTimeOffset.UtcNow < nextActionAt || !TryGetAddonByName("InputNumeric", out var inputNumericAddon) || !PayoutTradeUiAccessor.IsAddonReady(inputNumericAddon))
        {
            return false;
        }

        inputNumericAddon->FireCallbackInt(amountGil);
        return true;
    }

    private void TryConfirmTrade(AtkUnitBase* tradeAddon, TradeStateSnapshot state)
    {
        if (!PayoutTradeUiAccessor.TryGetLockState(tradeAddon, out var tradeButton, out _))
        {
            return;
        }

        // Keep retrying the lock-in click while the trade is open and the exact payout amount is
        // already present. Final confirmation is a strictly later phase and must not start until
        // both sides are observed as locked.
        if (!tradeLocked)
        {
            if (tradeButton->IsEnabled && state.ExactPartnerVerified && state.ExactAmountSubmitted && DateTimeOffset.UtcNow >= nextActionAt)
            {
                PayoutTradeUiAccessor.ClickButton(tradeButton, tradeAddon);
                nextActionAt = DateTimeOffset.UtcNow.Add(ActionThrottle);
            }

            return;
        }

        if (!condition[ConditionFlag.TradeOpen] || DateTimeOffset.UtcNow < nextActionAt)
        {
            return;
        }

        // Once both sides are locked, keep retrying the final confirm while the window remains
        // open. ConfirmationAccepted is only set after the yes button is actually pressed.
        if (!TryFindSelectYesNo(out var prompt))
        {
            return;
        }

        if (prompt->YesButton != null && prompt->YesButton->IsEnabled)
        {
            PayoutTradeUiAccessor.ClickButton(prompt->YesButton, (AtkUnitBase*)prompt);
            confirmationAccepted = true;
            nextActionAt = DateTimeOffset.UtcNow.Add(ActionThrottle);
        }
    }

    private bool TryFindSelectYesNo(out AddonSelectYesno* prompt)
    {
        prompt = null;
        for (var index = 1; index < 20; index++)
        {
            if (!TryGetAddonByName("SelectYesno", out var addon, index) || !PayoutTradeUiAccessor.IsAddonReady(addon))
            {
                continue;
            }

            prompt = (AddonSelectYesno*)addon;
            return true;
        }

        return false;
    }

    private bool TryGetAddonByName(string name, out AtkUnitBase* addon, int index = 1)
    {
        addon = null;
        var pointer = gameGui.GetAddonByName(name, index);
        if (pointer.Address == IntPtr.Zero)
        {
            return false;
        }

        addon = (AtkUnitBase*)pointer.Address;
        return addon != null;
    }

    private uint? ResolveHomeWorldRowId(string homeWorld)
    {
        var sheet = data.GetExcelSheet<World>();
        if (sheet is null || string.IsNullOrWhiteSpace(homeWorld))
        {
            return null;
        }

        foreach (var world in sheet)
        {
            var name = world.Name.ToString();
            if (!string.IsNullOrEmpty(name) && string.Equals(name, homeWorld, StringComparison.OrdinalIgnoreCase))
            {
                return world.RowId;
            }
        }

        return null;
    }

    private void LogWaitDiagnostics(PayoutTradeOperation current, string reason)
    {
        if (lastWaitDiagnosticAt is not null && DateTimeOffset.UtcNow - lastWaitDiagnosticAt.Value < TimeSpan.FromSeconds(5))
        {
            return;
        }

        lastWaitDiagnosticAt = DateTimeOffset.UtcNow;
        var builder = new System.Text.StringBuilder();
        builder.Append($"Catsino payout: still WaitingForPlayer - {reason}. Expected name='{current.CharacterName}', world='{current.HomeWorld}' (resolved row {(expectedHomeWorldRowId?.ToString() ?? "none")}). Nearby targetable players: ");
        var any = false;
        foreach (var player in objects.OfType<IPlayerCharacter>().Where(player => player.IsTargetable))
        {
            any = true;
            builder.Append($"{player.Name}@{player.HomeWorld.Value.Name}(row {player.HomeWorld.RowId}); ");
        }

        if (!any)
        {
            builder.Append("(none)");
        }

        log.Debug(builder.ToString());
    }

    private void LogLockDiagnostics(PayoutTradeOperation current, TradeStateSnapshot state, AtkUnitBase* tradeAddon)
    {
        var stalledAtLock = amountSubmitted && !tradeLocked;
        var stalledAtConfirm = tradeLocked && !confirmationAccepted;
        if (!stalledAtLock && !stalledAtConfirm)
        {
            lastLockDiagnosticAt = null;
            return;
        }

        if (lastLockDiagnosticAt is not null && DateTimeOffset.UtcNow - lastLockDiagnosticAt.Value < TimeSpan.FromSeconds(5))
        {
            return;
        }

        lastLockDiagnosticAt = DateTimeOffset.UtcNow;
        var lockStateRead = PayoutTradeUiAccessor.TryGetLockState(tradeAddon, out var tradeButton, out _);
        var lockButtonEnabled = lockStateRead && tradeButton != null && tradeButton->IsEnabled;
        log.Debug(
            $"Catsino payout lock diagnostics for {current.CharacterName}@{current.HomeWorld}: " +
            $"stage={(stalledAtLock ? "awaitingLock" : "awaitingConfirm")}, exactPartnerVerified={state.ExactPartnerVerified}, " +
            $"exactAmountSubmitted={state.ExactAmountSubmitted}, localLocked={state.LocalTradeLocked}, partnerLocked={state.PartnerTradeLocked}, " +
            $"confirmationAccepted={confirmationAccepted}, lockStateRead={lockStateRead}, lockButtonEnabled={lockButtonEnabled}.");
    }

    private void LogStructuredDump(PayoutTradeOperation current)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            log.Debug("Catsino payout dump: InventoryManager unavailable.");
            return;
        }

        var builder = new System.Text.StringBuilder();
        builder.Append($"Catsino payout structured dump for {current.CharacterName}@{current.HomeWorld} (want {current.AmountGil:N0} gil): ");
        builder.Append($"localState={inventoryManager->TradeLocalState}, remoteState={inventoryManager->TradeRemoteState}, ");
        builder.Append($"partnerId={inventoryManager->TradePartnerEntityId} (expect {expectedPlayerEntityId}), partner='{inventoryManager->TradePartnerNameString}'. Local slots: ");
        var slots = inventoryManager->TradeItemsLocal;
        for (var index = 0; index < slots.Length; index++)
        {
            builder.Append($"[{index}] item={slots[index].ItemId} qty={slots[index].Quantity}; ");
        }

        log.Debug(builder.ToString());
    }

    private void LogPayoutFailure(string stage, PayoutTradeOperation current, Exception exception)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append($"Catsino payout failure during {stage} for {current.CharacterName}@{current.HomeWorld} / {current.AmountGil:N0} gil. ");
        builder.Append($"operationId={current.OperationId}, state={current.State}, ");
        builder.Append($"flags: playerDetected={playerDetected}, tradeRequested={tradeRequested}, tradeOpened={tradeOpened}, gilInputOpened={gilInputOpened}, amountSubmitted={amountSubmitted}, tradeLocked={tradeLocked}, confirmationAccepted={confirmationAccepted}. ");
        builder.Append($"exception={exception}");
        log.Error(builder.ToString());
        LogStructuredDump(current);
    }

    private bool TryReadCurrentGil(out long gil)
    {
        gil = 0;
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            return false;
        }

        var container = inventoryManager->GetInventoryContainer(InventoryType.Currency);
        if (container == null || !container->IsLoaded)
        {
            return false;
        }

        gil = container->GetInventorySlot(0)->Quantity;
        return true;
    }

    private long ReadCurrentGil() =>
        TryReadCurrentGil(out var gil)
            ? gil
            : throw new InvalidOperationException("The currency inventory is unavailable.");

    private void PublishTerminal(PayoutTradeEventType eventType, PayoutTradeState state, string? errorCode, string? errorMessage, bool ambiguous)
    {
        UpdateOperation(state, errorCode, errorMessage, ambiguous);
        Publish(eventType, errorCode, errorMessage, ambiguous);
    }

    private void Publish(PayoutTradeEventType eventType, string? errorCode, string? errorMessage, bool ambiguous)
    {
        if (operation is null)
        {
            return;
        }

        sequenceNumber++;
        operation = operation with { LastSequenceNumber = sequenceNumber, UpdatedAt = DateTimeOffset.UtcNow };
        TradeEventReceived?.Invoke(new PayoutTradeEvent(
            operation.OperationId,
            operation.SessionId,
            operation.CharacterName,
            operation.HomeWorld,
            operation.AmountGil,
            eventType,
            executorInstanceId,
            sequenceNumber,
            DateTimeOffset.UtcNow,
            errorCode,
            errorMessage,
            ambiguous));
    }

    private void UpdateOperation(PayoutTradeState state, string? errorCode, string? errorMessage, bool ambiguous)
    {
        operation = operation! with
        {
            State = state,
            UpdatedAt = DateTimeOffset.UtcNow,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            IsAmbiguous = ambiguous,
        };
    }

    private void ResetTransientState()
    {
        playerDetected = false;
        expectedPlayerEntityId = 0;
        expectedHomeWorldRowId = null;
        lastWaitDiagnosticAt = null;
        exactPartnerVerified = false;
        tradeOpened = false;
        tradeConditionSeenOpen = false;
        lastLockDiagnosticAt = null;
        tradeRequested = false;
        gilInputOpened = false;
        amountSubmitted = false;
        tradeLocked = false;
        confirmationAccepted = false;
        structuredDumpLogged = false;
        tradeConditionOpenSince = null;
        tradeClosedAt = null;
        nextActionAt = DateTimeOffset.MinValue;
        nextTradeRequestAt = DateTimeOffset.MinValue;
    }

    private static bool IsExactCharacterName(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts.All(part => part.Length is >= 2 and <= 15 &&
            part.All(character => char.IsLetter(character) || character is '\'' or '-'));
    }

    private static bool IsExactWorldName(string value) =>
        value.Length is >= 2 and <= 32 && char.IsLetter(value[0]) &&
        value.All(character => char.IsLetterOrDigit(character) || character == '-');

    private bool IsAutomating() => operation is { State: not (PayoutTradeState.Completed or PayoutTradeState.Cancelled or PayoutTradeState.Failed) };
}
