using System.Globalization;
using System.Collections.Concurrent;
using System.Numerics;
using Catsino.Plugin.Backend;
using Catsino.Plugin.Contracts;
using Catsino.Plugin.Runtime;
using Catsino.Plugin.Security;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Catsino.Plugin.Ui;

public sealed class CatsinoWindow : Window
{
    private string activationJwt = string.Empty;
    private string createFee;
    private string createMinBet;
    private string createMaxBet;
    private string createMaxPlayers;
    private int createGameTypeIndex;
    private string validationMessage = string.Empty;
    private bool busy;
    private readonly ConcurrentQueue<Action> pendingUiUpdates = new();
    private readonly CatsinoRuntime runtime;
    private readonly SessionPanelRenderer sessionPanel;
    // Display labels and the wire values they map to — the two differ for Hold'em, whose game type is the
    // single word "holdem".
    private static readonly string[] GameTypes = ["Plinko", "Blackjack", "Texas Hold'em"];
    private static readonly string[] GameTypeValues = ["plinko", "blackjack", "holdem"];

    public CatsinoWindow(CatsinoRuntime runtime, SessionPanelRenderer sessionPanel)
        : base("Catsino###CatsinoMainWindow")
    {
        this.runtime = runtime;
        this.sessionPanel = sessionPanel;
        createFee = runtime.DefaultDealerFeePercent.ToString(CultureInfo.InvariantCulture);
        createMinBet = runtime.DefaultMinBet.ToString(CultureInfo.InvariantCulture);
        createMaxBet = runtime.DefaultMaxBet.ToString(CultureInfo.InvariantCulture);
        createMaxPlayers = runtime.DefaultMaxPlayers?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public override void PreDraw()
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(900, 480),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        while (pendingUiUpdates.TryDequeue(out var update))
        {
            update();
        }

        DrawHeader();
        if (!ImGui.BeginTabBar("CatsinoTabs"))
        {
            return;
        }

        if (ImGui.BeginTabItem("Authorization"))
        {
            DrawAuthorization();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Sessions"))
        {
            DrawSessions();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Payouts"))
        {
            DrawPayouts();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Status"))
        {
            DrawStatus();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawHeader()
    {
        var character = runtime.Character;
        ImGui.TextUnformatted(character.IsLoggedIn
            ? $"{character.CharacterName} | Home: {character.HomeWorld} | Current: {character.CurrentWorld}"
            : "No character logged in");
        ImGui.SameLine();
        ImGui.TextDisabled(runtime.IsBackendConnected ? "Connected" : "Disconnected");
        ImGui.Separator();
        ImGui.TextWrapped(runtime.StatusMessage);
        if (!string.IsNullOrWhiteSpace(validationMessage))
        {
            ImGui.TextWrapped(validationMessage);
        }
        ImGui.Separator();
    }

    private void DrawAuthorization()
    {
        var character = runtime.Character;
        ImGui.TextUnformatted("Authorization is bound to the displayed character and Home World.");
        ImGui.Spacing();
        if (!runtime.IsAuthorized)
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##activationJwt", ref activationJwt, 4096, ImGuiInputTextFlags.Password);
            ImGui.TextDisabled("Dealer activation JWT");
            BeginDisabled(busy || !character.IsLoggedIn || string.IsNullOrWhiteSpace(activationJwt));
            if (ImGui.Button("Authorize dealer"))
            {
                var token = activationJwt;
                activationJwt = string.Empty;
                Run(() => runtime.AuthorizeAsync(token));
            }

            EndDisabled(busy || !character.IsLoggedIn || string.IsNullOrWhiteSpace(activationJwt));
        }
        else
        {
            ImGui.TextUnformatted($"Authorized: {character.CharacterName}@{character.HomeWorld}");
            BeginDisabled(busy);
            if (ImGui.Button("Disconnect authorization"))
            {
                Run(() => runtime.DisconnectAsync());
            }

            EndDisabled(busy);
        }
    }

    private void DrawSessions()
    {
        BeginDisabled(!runtime.IsAuthorized || busy);
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputText("Default Dealer Fee %", ref createFee, 16, ImGuiInputTextFlags.CharsDecimal) &&
            DealerInputValidator.TryParseFee(createFee, out var configuredFee) &&
            DealerInputValidator.ValidateFee(configuredFee, GameSessionState.Created) is null)
        {
            runtime.SetDefaultDealerFeePercent(configuredFee);
            validationMessage = string.Empty;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.InputText("Min bet", ref createMinBet, 20, ImGuiInputTextFlags.CharsDecimal);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.InputText("Max bet", ref createMaxBet, 20, ImGuiInputTextFlags.CharsDecimal);
        var selectedGameType = GameTypeValues[createGameTypeIndex];
        var isHoldem = selectedGameType == "holdem";

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.InputText("Max players", ref createMaxPlayers, 12, ImGuiInputTextFlags.CharsDecimal);
        ImGui.SameLine();
        // A Hold'em table has a fixed number of seats, so "unlimited" does not exist for it.
        ImGui.TextDisabled(isHoldem ? $"(Texas Hold'em: max {HoldemBetDefaults.MaxSeats} players)" : "(empty = unlimited)");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        ImGui.Combo("Game", ref createGameTypeIndex, GameTypes, GameTypes.Length);

        ImGui.SameLine();
        if (ImGui.Button($"Create {GameTypes[createGameTypeIndex]}"))
        {
            var gameType = selectedGameType;
            if (!DealerInputValidator.TryParseFee(createFee, out var fee) ||
                DealerInputValidator.ValidateFee(fee, GameSessionState.Created) is not null)
            {
                validationMessage = "Default dealer fee must be between 0 and 100 with at most two decimal places.";
            }
            else if (!DealerInputValidator.TryParseGil(createMinBet.Trim(), out var minBet) ||
                     !DealerInputValidator.TryParseGil(createMaxBet.Trim(), out var maxBet))
            {
                validationMessage = "Min and max bet must be whole, non-negative gil amounts.";
            }
            else if (DealerInputValidator.ValidateBetLimits(minBet, maxBet) is { } betError)
            {
                validationMessage = betError;
            }
            else if (!DealerInputValidator.TryParseMaxPlayers(createMaxPlayers, out var maxPlayers))
            {
                validationMessage = isHoldem
                    ? $"Max players must be empty (a full table) or a whole number between 1 and {HoldemBetDefaults.MaxSeats}."
                    : "Max players must be empty (unlimited) or a whole number of at least 1.";
            }
            else if (DealerInputValidator.ValidateMaxPlayers(maxPlayers, gameType) is { } maxPlayersError)
            {
                validationMessage = maxPlayersError;
            }
            else
            {
                Run(() => runtime.CreateSessionAsync(gameType, fee, minBet, maxBet, maxPlayers));
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh"))
        {
            Run(() => runtime.RefreshSessionsAsync());
        }

        if (isHoldem)
        {
            ImGui.TextDisabled($"Texas Hold'em tables seat up to {HoldemBetDefaults.MaxSeats} players (the dealer does not take a seat). Blinds come from the min bet: big blind = min bet, small blind = half of it.");
        }

        EndDisabled(!runtime.IsAuthorized || busy);
        ImGui.Separator();

        ImGui.BeginChild("SessionList", new Vector2(210, 0), true, ImGuiWindowFlags.HorizontalScrollbar);
        foreach (var session in runtime.Sessions)
        {
            var selected = runtime.SelectedSession?.SessionId == session.SessionId;
            if (ImGui.Selectable($"{GameTypeLabels.Summary(session)}##{session.SessionId:D}", selected))
            {
                Run(() => runtime.SelectSessionAsync(session.SessionId));
            }
        }

        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginGroup();
        var selectedSessionId = runtime.SelectedSession?.SessionId;
        if (selectedSessionId is null)
        {
            ImGui.TextDisabled("Select a session.");
        }
        else
        {
            sessionPanel.Draw(selectedSessionId.Value);
        }
        ImGui.EndGroup();
    }

    private void DrawPayouts()
    {
        ImGui.TextWrapped("Only backend-issued exact payout legs can be queued. Failed payouts release their unpaid remainder back to the player so the dealer can start a new cash out.");
        if (runtime.ActivePayout is { } active)
        {
            Section("Active operation");
            ImGui.PushID($"active-{active.OperationId:D}");
            DrawPayoutSummary(active);
            DrawPayoutActions(active);
            ImGui.PopID();
        }

        Section("Backend payout operations");
        foreach (var operation in runtime.OpenPayoutOperations.Where(operation => operation.OperationId != runtime.ActivePayout?.OperationId))
        {
            ImGui.PushID(operation.OperationId.ToString("D"));
            DrawPayoutSummary(operation);
            DrawPayoutActions(operation);

            ImGui.Separator();
            ImGui.PopID();
        }
    }

    private void DrawPayoutActions(PayoutOperationDto operation)
    {
        if (operation.State == PayoutOperationState.Failed)
        {
            ImGui.TextDisabled("The unpaid remainder is back in the player's available balance. Start a new cash out to retry the remaining gil.");
            return;
        }

        // A payout that never reaches the trade (e.g. the target never became targetable) keeps
        // the player's tokens inside the active cash-out. Only the active, not-yet-open
        // operation can be aborted here; aborting returns the unpaid gross tokens to the player.
        if (operation.OperationId == runtime.ActivePayout?.OperationId &&
            operation.State is PayoutOperationState.Queued or PayoutOperationState.WaitingForPlayer)
        {
            var ctrlHeld = ImGui.GetIO().KeyCtrl;
            if (!ctrlHeld)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Button("Abort payout"))
            {
                runtime.RequestAbortActivePayout();
            }

            if (!ctrlHeld)
            {
                ImGui.EndDisabled();
            }

            ImGui.SameLine();
            ImGui.TextDisabled("Hold Ctrl to enable. Returns the reserved gil to the player and re-enables new cash outs.");
        }
    }

    private static void DrawPayoutSummary(PayoutOperationDto operation)
    {
        ImGui.TextUnformatted($"{operation.CharacterName}@{operation.HomeWorld} | {operation.AmountGil:N0} gil | {operation.State}");
        ImGui.TextDisabled($"Operation {operation.OperationId:D} | Leg {operation.LegId:D}");
        if (!string.IsNullOrWhiteSpace(operation.LastErrorMessage))
        {
            ImGui.TextWrapped($"{operation.LastErrorCode}: {SecretRedactor.Redact(operation.LastErrorMessage)}");
        }
    }

    private void DrawStatus()
    {
        var executor = runtime.PayoutExecutorStatus;
        ImGui.TextUnformatted($"Plugin version: {PluginVersion.Current}");
        ImGui.TextUnformatted($"Contract version: {ContractVersion.Current}");
        ImGui.TextUnformatted($"Backend: {(runtime.IsBackendConnected ? "connected" : "disconnected")}");
        ImGui.TextUnformatted($"Heartbeat: {(runtime.LastHeartbeatAt?.ToString("u") ?? "not sent")}");
        ImGui.TextUnformatted($"Pending financial outbox events: {runtime.PendingOutboxEvents}");
        Section("Payout Executor");
        ImGui.TextUnformatted($"Ready: {executor.IsReady}");
        ImGui.TextUnformatted($"Instance: {executor.ExecutorInstanceId:D}");
        ImGui.TextUnformatted($"Status: {executor.Status}");
        ImGui.TextUnformatted($"Active operation: {(executor.ActiveOperation?.OperationId.ToString("D") ?? "none")}");
    }

    private void Run(Func<Task> action) => _ = RunCoreAsync(action);

    private async Task RunCoreAsync(Func<Task> action)
    {
        if (busy)
        {
            return;
        }

        busy = true;
        validationMessage = string.Empty;
        try
        {
            await action().ConfigureAwait(false);
            pendingUiUpdates.Enqueue(() => busy = false);
        }
        catch (Exception exception)
        {
            var message = SecretRedactor.Redact(exception.Message);
            pendingUiUpdates.Enqueue(() =>
            {
                validationMessage = message;
                busy = false;
            });
        }
    }

    private static void BeginDisabled(bool disabled)
    {
        ImGui.BeginDisabled(disabled);
    }

    private static void EndDisabled(bool _)
    {
        ImGui.EndDisabled();
    }

    private static void Section(string title)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled(title);
    }
}
