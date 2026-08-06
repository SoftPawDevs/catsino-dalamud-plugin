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
    private string reconciliationEvidence = string.Empty;
    private string validationMessage = string.Empty;
    private bool busy;
    private readonly ConcurrentQueue<Action> pendingUiUpdates = new();
    private readonly CatsinoRuntime runtime;
    private readonly SessionPanelRenderer sessionPanel;

    public CatsinoWindow(CatsinoRuntime runtime, SessionPanelRenderer sessionPanel)
        : base("Catsino###CatsinoMainWindow")
    {
        this.runtime = runtime;
        this.sessionPanel = sessionPanel;
        createFee = runtime.DefaultDealerFeePercent.ToString(CultureInfo.InvariantCulture);
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
        if (ImGui.Button("Create Plinko"))
        {
            if (DealerInputValidator.TryParseFee(createFee, out var fee) &&
                DealerInputValidator.ValidateFee(fee, GameSessionState.Created) is null)
            {
                Run(() => runtime.CreatePlinkoSessionAsync(fee));
            }
            else
            {
                validationMessage = "Default dealer fee must be between 0 and 100 with at most two decimal places.";
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh"))
        {
            Run(() => runtime.RefreshSessionsAsync());
        }

        EndDisabled(!runtime.IsAuthorized || busy);
        ImGui.Separator();

        ImGui.BeginChild("SessionList", new Vector2(210, 0), true, ImGuiWindowFlags.HorizontalScrollbar);
        foreach (var session in runtime.Sessions)
        {
            var selected = runtime.SelectedSession?.SessionId == session.SessionId;
            if (ImGui.Selectable($"{session.GameType} | {session.State}##{session.SessionId:D}", selected))
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
        ImGui.TextWrapped("Only backend-issued exact payout legs can be queued. Ambiguous outcomes require reconciliation and are never retried.");
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
            BeginDisabled(busy);
            if (ImGui.Button("Request backend retry"))
            {
                Run(() => runtime.RequestCashoutRetryAsync(operation.OperationId));
            }

            EndDisabled(busy);
        }

        if (operation.State == PayoutOperationState.ReconciliationRequired)
        {
            ImGui.InputTextMultiline("Dealer evidence", ref reconciliationEvidence, 1024, new Vector2(-1, 70));
            BeginDisabled(busy || string.IsNullOrWhiteSpace(reconciliationEvidence));
            if (ImGui.Button("Submit reconciliation evidence"))
            {
                Run(() => runtime.SubmitReconciliationAsync(operation.OperationId, reconciliationEvidence));
            }

            EndDisabled(busy || string.IsNullOrWhiteSpace(reconciliationEvidence));
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
        var dropbox = runtime.DropboxCapabilities;
        ImGui.TextUnformatted($"Plugin version: {PluginVersion.Current}");
        ImGui.TextUnformatted($"Contract version: {ContractVersion.Current}");
        ImGui.TextUnformatted($"Backend: {(runtime.IsBackendConnected ? "connected" : "disconnected")}");
        ImGui.TextUnformatted($"Heartbeat: {(runtime.LastHeartbeatAt?.ToString("u") ?? "not sent")}");
        ImGui.TextUnformatted($"Pending financial outbox events: {runtime.PendingOutboxEvents}");
        Section("Dropbox");
        ImGui.TextUnformatted($"Available: {dropbox.IsAvailable}");
        ImGui.TextUnformatted($"IPC version: {dropbox.IpcVersion ?? "unavailable"}");
        ImGui.TextUnformatted($"Build version: {dropbox.BuildVersion ?? "unavailable"}");
        ImGui.TextUnformatted($"Language-independent state: {dropbox.SupportsLanguageIndependentTradeState}");
        foreach (var capability in dropbox.Capabilities)
        {
            ImGui.BulletText(capability);
        }
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
