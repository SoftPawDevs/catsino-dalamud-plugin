using Catsino.Plugin.Contracts;
using Catsino.Plugin.Runtime;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.Automation.UIInput;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using static ECommons.GenericHelpers;

namespace Catsino.Plugin.Payout;

// Drives a single outgoing payout trade end-to-end using ECommons' NeoTaskManager and trade
// primitives: target the exact player → /trade →
// fill the exact gil → lock → confirm the SelectYesno → wait for the trade to close. Catsino's own
// guarantees are layered on top: sequence-numbered PayoutTradeEvents are raised for the backend, and
// the terminal outcome is decided strictly from the exact gil debit (TradeCloseEvaluator), never from
// the fact that a button was pressed.
public sealed unsafe class BuiltInPayoutTradeExecutor : IPayoutTradeExecutor
{
    // Generous per-task time limit: normal trades complete in seconds once both sides confirm, and a
    // never-appearing / never-confirming player eventually aborts to a terminal outcome.
    private const int TaskTimeLimitMs = 180_000;

    private readonly IObjectTable objects;
    private readonly ITargetManager targets;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly IDataManager data;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Guid executorInstanceId = Guid.NewGuid();
    private readonly HashSet<Guid> usedOperationIds = [];
    private readonly TaskManager taskManager;
    private readonly object openPersistLock = new();
    private Guid openPersistedOperationId;

    private PayoutTradeOperation? operation;
    private long sequenceNumber;
    private long gilBefore;
    private uint? expectedHomeWorldRowId;
    private bool playerDetected;
    private bool tradeOpened;
    private bool confirmationAccepted;
    private bool sequenceQueued;
    private bool cancelRequested;
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
        taskManager = new TaskManager(new TaskManagerConfiguration
        {
            TimeLimitMS = TaskTimeLimitMs,
            AbortOnTimeout = true,
            ShowError = false,
            ShowDebug = false,
        });
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
        // The task queue is enqueued on the framework thread (see OnFrameworkUpdate); StartOperation
        // runs on a background coordinator thread, so it must not touch the TaskManager directly.
        return true;
    }

    public bool CancelOperation(Guid operationId)
    {
        if (operation?.OperationId != operationId || !IsAutomating())
        {
            return false;
        }

        // Always honour an abort. The terminal outcome is then decided from the ACTUAL gil movement
        // (ResolveOutcome on the framework thread), never from a possibly-stale trade-open flag: if no gil
        // left the dealer's pouch the remainder is released cleanly to the player; if gil did move it
        // settles as completed/ambiguous so a real transfer is never lost. This lets the dealer abort even
        // when the game/plugin state wrongly reports the trade window as open.
        cancelRequested = true;
        return true;
    }

    public PayoutTradeOperation? GetOperation(Guid operationId) => operation?.OperationId == operationId ? operation : null;

    // Called from the coordinator thread once the TradeOpened event is durably persisted. ConfirmTrade
    // (which is where gil actually moves) refuses to proceed until this matches the current operation.
    public void MarkOpenEventPersisted(Guid operationId)
    {
        lock (openPersistLock)
        {
            openPersistedOperationId = operationId;
        }
    }

    private bool IsOpenEventPersisted(Guid operationId)
    {
        lock (openPersistLock)
        {
            return openPersistedOperationId == operationId;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        framework.Update -= OnFrameworkUpdate;
        taskManager.Abort();
        taskManager.Dispose();
        operation = null;
        TradeEventReceived = null;
    }

    // Framework-thread supervisor: enqueues the trade sequence, performs backend-requested aborts, and
    // resolves the terminal outcome if the sequence ends (timeout/abort) without the resolve step.
    private void OnFrameworkUpdate(IFramework _)
    {
        if (disposed || operation is null)
        {
            return;
        }

        try
        {
            if (cancelRequested)
            {
                cancelRequested = false;
                taskManager.Abort();
                // Resolve from the gil balance, not from the button: a never-opened trade releases cleanly,
                // an actually-open one is flagged for review, a moved-gil one settles as completed.
                ResolveOutcome(tradeCurrentlyOpen: condition[ConditionFlag.TradeOpen]);
                return;
            }

            if (!sequenceQueued)
            {
                EnqueueTradeSequence();
                sequenceQueued = true;
                return;
            }

            // The resolve step clears `operation` on success; if the operation is still present with no
            // tasks left, the sequence aborted or timed out — resolve it from the gil balance.
            if (!taskManager.IsBusy)
            {
                ResolveOutcome(tradeCurrentlyOpen: condition[ConditionFlag.TradeOpen]);
            }
        }
        catch (Exception exception)
        {
            log.Error($"Catsino payout supervisor failed: {exception}");
            if (operation is not null)
            {
                PublishTerminal(PayoutTradeEventType.TradeFailed, PayoutTradeState.Failed, "automationFailed", exception.Message, false);
                operation = null;
            }
        }
    }

    private void EnqueueTradeSequence()
    {
        taskManager.Abort();
        taskManager.Enqueue(TargetAndRequestTrade, "Catsino.TargetAndRequestTrade");
        taskManager.Enqueue(WaitTradeOpen, "Catsino.WaitTradeOpen");
        taskManager.Enqueue(OpenGilInput, "Catsino.OpenGilInput");
        taskManager.Enqueue(SetGilAmount, "Catsino.SetGilAmount");
        taskManager.Enqueue(ConfirmTrade, "Catsino.ConfirmTrade");
        taskManager.Enqueue(WaitTradeClosed, "Catsino.WaitTradeClosed");
        taskManager.Enqueue(ResolveOnClose, "Catsino.ResolveOnClose");
    }

    private bool TargetAndRequestTrade()
    {
        var current = operation;
        if (current is null)
        {
            return true;
        }

        expectedHomeWorldRowId ??= ResolveHomeWorldRowId(current.HomeWorld);
        if (expectedHomeWorldRowId is null)
        {
            return false;
        }

        var worldRowId = expectedHomeWorldRowId.Value;
        var target = objects
            .OfType<IPlayerCharacter>()
            .FirstOrDefault(player => player.IsTargetable &&
                player.HomeWorld.RowId == worldRowId &&
                string.Equals(player.Name.ToString(), current.CharacterName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        if (!playerDetected)
        {
            playerDetected = true;
            UpdateOperation(PayoutTradeState.PlayerDetected, null, null, false);
            Publish(PayoutTradeEventType.PlayerDetected, null, null, false);
        }

        if (targets.Target?.Address == target.Address)
        {
            if (FrameThrottler.Throttle("CatsinoPayoutTask", 8) && EzThrottler.Throttle("CatsinoPayoutTradeOpen", 2000))
            {
                GameChat.SendCommand("/trade");
                return true;
            }
        }
        else if (FrameThrottler.Throttle("CatsinoPayoutTask", 8))
        {
            targets.Target = target;
        }

        return false;
    }

    private bool WaitTradeOpen()
    {
        if (!condition[ConditionFlag.TradeOpen])
        {
            return false;
        }

        if (!tradeOpened)
        {
            tradeOpened = true;
            UpdateOperation(PayoutTradeState.TradeOpened, null, null, false);
            Publish(PayoutTradeEventType.TradeOpened, null, null, false);
        }

        return true;
    }

    private bool OpenGilInput()
    {
        if (TryGetAddonByName<AtkUnitBase>("Trade", out var addon) && IsAddonReady(addon))
        {
            if (FrameThrottler.Throttle("CatsinoPayoutTask", 8))
            {
                Callback.Fire(addon, true, 2, Callback.ZeroAtkValue);
                return true;
            }
        }
        else
        {
            FrameThrottler.Throttle("CatsinoPayoutTask", 8, true);
        }

        return false;
    }

    private bool SetGilAmount()
    {
        var current = operation;
        if (current is null)
        {
            return true;
        }

        if (TryGetAddonByName<AtkUnitBase>("InputNumeric", out var addon) && IsAddonReady(addon))
        {
            if (FrameThrottler.Throttle("CatsinoPayoutTask", 8))
            {
                Callback.Fire(addon, true, (int)current.AmountGil);
                return true;
            }
        }
        else
        {
            FrameThrottler.Throttle("CatsinoPayoutTask", 8, true);
        }

        return false;
    }

    // Locks the trade (node-3 button) and confirms the resulting SelectYesno, retrying each until the
    // Trade addon disappears (trade completed or cancelled).
    private bool ConfirmTrade()
    {
        var current = operation;
        if (current is null)
        {
            return true;
        }

        // Never move gil until the TradeOpened event is durably recorded. If a crash struck between the
        // confirm and any durable write, recovery could otherwise re-run a completed physical trade.
        if (!IsOpenEventPersisted(current.OperationId))
        {
            return false;
        }

        if (TryGetAddonByName<AtkUnitBase>("Trade", out var tradeAddon) && IsAddonReady(tradeAddon))
        {
            var tradeButton = (AtkComponentButton*)tradeAddon->UldManager.NodeList[3]->GetComponent();
            if (EzThrottler.Check("CatsinoPayoutLock")
                && FrameThrottler.Check("CatsinoPayoutLock")
                && tradeButton != null && tradeButton->IsEnabled
                && EzThrottler.Throttle("CatsinoPayoutConfirmDelay", 200)
                && EzThrottler.Throttle("CatsinoPayoutLock", 2000))
            {
                tradeButton->ClickAddonButton(tradeAddon);
            }
        }
        else
        {
            return true;
        }

        if (TryFindSelectYesNo(out var yesno)
            && EzThrottler.Throttle("CatsinoPayoutConfirmDelay", 200)
            && EzThrottler.Throttle("CatsinoPayoutSelectYes", 2000))
        {
            new AddonMaster.SelectYesno(yesno).Yes();
            confirmationAccepted = true;
        }

        return !TryGetAddonByName<AtkUnitBase>("Trade", out _);
    }

    private bool WaitTradeClosed() => !condition[ConditionFlag.TradeOpen];

    private bool ResolveOnClose()
    {
        ResolveOutcome(tradeCurrentlyOpen: false);
        return true;
    }

    // Single terminal-outcome decision, shared by the normal close path and the abort/timeout path.
    // The exact gil debit (TradeCloseEvaluator) is the authoritative proof; a button press is not.
    private void ResolveOutcome(bool tradeCurrentlyOpen)
    {
        if (operation is null)
        {
            return;
        }

        var amount = operation.AmountGil;
        var gilRead = TryReadCurrentGil(out var gil);
        switch (TradeCloseEvaluator.Evaluate(gilRead, gilBefore, gil, amount, confirmationAccepted))
        {
            case TradeCloseDecision.Completed:
                PublishTerminal(PayoutTradeEventType.TradeCompleted, PayoutTradeState.Completed, null, null, false);
                break;
            case TradeCloseDecision.Cancelled when tradeCurrentlyOpen:
                // The sequence gave up while a trade window is still open (gil could yet move): the
                // outcome is genuinely unknown, so flag it for reconciliation rather than releasing blind.
                PublishTerminal(
                    PayoutTradeEventType.TradeFailed,
                    PayoutTradeState.ReconciliationRequired,
                    "reconciliationRequired",
                    "The payout was abandoned while the trade window was still open; the outcome is unverified.",
                    true);
                break;
            case TradeCloseDecision.Cancelled when !tradeOpened:
                PublishTerminal(
                    PayoutTradeEventType.TradeTimedOut,
                    PayoutTradeState.Failed,
                    "playerWaitTimedOut",
                    "The paying-out player did not become tradeable in time; no gil was transferred.",
                    false);
                break;
            case TradeCloseDecision.Cancelled:
                PublishTerminal(
                    PayoutTradeEventType.TradeCancelled,
                    PayoutTradeState.Cancelled,
                    "tradeWindowClosed",
                    "The trade window closed before the payout was confirmed; no gil was transferred.",
                    false);
                break;
            default:
                PublishTerminal(
                    PayoutTradeEventType.TradeFailed,
                    PayoutTradeState.ReconciliationRequired,
                    "reconciliationRequired",
                    "The trade closed without complete proof of the exact gil transfer.",
                    true);
                break;
        }

        operation = null;
    }

    // The trade-confirmation SelectYesno. During a payout trade the only dialog raised is the trade
    // confirm, so the first ready SelectYesno is accepted.
    private bool TryFindSelectYesNo(out AtkUnitBase* prompt)
    {
        prompt = null;
        for (var index = 1; index < 20; index++)
        {
            var pointer = gameGui.GetAddonByName("SelectYesno", index);
            if (pointer.Address == IntPtr.Zero)
            {
                break;
            }

            var addon = (AtkUnitBase*)pointer.Address;
            if (IsAddonReady(addon))
            {
                prompt = addon;
                return true;
            }
        }

        return false;
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
        expectedHomeWorldRowId = null;
        playerDetected = false;
        tradeOpened = false;
        confirmationAccepted = false;
        sequenceQueued = false;
        cancelRequested = false;
        lock (openPersistLock)
        {
            openPersistedOperationId = Guid.Empty;
        }
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
