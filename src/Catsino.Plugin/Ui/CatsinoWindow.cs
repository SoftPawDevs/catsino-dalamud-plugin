using System.Globalization;
using System.Numerics;
using Catsino.Plugin.Backend;
using Catsino.Plugin.Contracts;
using Catsino.Plugin.Runtime;
using Catsino.Plugin.Security;
using Catsino.Plugin.Workflow;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Catsino.Plugin.Ui;

public sealed class CatsinoWindow(CatsinoRuntime runtime) : Window("Catsino###CatsinoMainWindow")
{
    private string activationJwt = string.Empty;
    private string createFee = "0";
    private string editFee = "0";
    private string inviteName = string.Empty;
    private string inviteWorld = string.Empty;
    private string depositGil = string.Empty;
    private string reconciliationEvidence = string.Empty;
    private string validationMessage = string.Empty;
    private bool busy;

    public override void PreDraw()
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 480),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
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
        ImGui.InputText("Create fee %", ref createFee, 16, ImGuiInputTextFlags.CharsDecimal);
        ImGui.SameLine();
        if (ImGui.Button("Create Plinko"))
        {
            if (DealerInputValidator.TryParseFee(createFee, out var fee))
            {
                Run(() => runtime.CreatePlinkoSessionAsync(fee));
            }
            else
            {
                validationMessage = "Create fee must be a decimal from 0 to 100.";
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
                editFee = session.FeePercent.ToString(CultureInfo.InvariantCulture);
            }
        }

        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginGroup();
        DrawSelectedSession();
        ImGui.EndGroup();
    }

    private void DrawSelectedSession()
    {
        var session = runtime.SelectedSession;
        if (session is null)
        {
            ImGui.TextDisabled("Select a session.");
            return;
        }

        ImGui.TextUnformatted($"{session.GameType} | {session.State} | {session.PlayerCount} players");
        ImGui.TextUnformatted($"Deposited: {session.TotalDepositedGil:N0} gil");
        ImGui.TextUnformatted($"Payout: {session.PayoutState} | Reconciliation: {session.ReconciliationState}");

        var feeLocked = session.State != GameSessionState.Created;
        BeginDisabled(feeLocked || busy);
        ImGui.SetNextItemWidth(100);
        ImGui.InputText("Fee %", ref editFee, 16, ImGuiInputTextFlags.CharsDecimal);
        ImGui.SameLine();
        if (ImGui.Button("Update fee") && DealerInputValidator.TryParseFee(editFee, out var fee))
        {
            Run(() => runtime.UpdateFeeAsync(fee));
        }
        else if (ImGui.IsItemClicked())
        {
            validationMessage = "Fee must be a decimal from 0 to 100.";
        }

        EndDisabled(feeLocked || busy);
        if (feeLocked)
        {
            ImGui.TextDisabled("Fee is locked after Created.");
        }

        BeginDisabled(busy || session.State != GameSessionState.Created);
        if (ImGui.Button("Open session"))
        {
            Run(() => runtime.OpenSessionAsync());
        }

        EndDisabled(busy || session.State != GameSessionState.Created);
        ImGui.SameLine();
        BeginDisabled(busy || session.State != GameSessionState.Open);
        if (ImGui.Button("Close session"))
        {
            Run(() => runtime.CloseSessionAsync());
        }

        EndDisabled(busy || session.State != GameSessionState.Open);
        Section("Invite exact player");
        ImGui.SetNextItemWidth(180);
        ImGui.InputText("Character name", ref inviteName, 32);
        ImGui.SetNextItemWidth(180);
        ImGui.InputText("Home World", ref inviteWorld, 32);
        BeginDisabled(busy || session.State == GameSessionState.Closed);
        if (ImGui.Button("Create invite and send /tell"))
        {
            Run(() => runtime.CreateInviteAndTellAsync(inviteName.Trim(), inviteWorld.Trim()));
        }

        EndDisabled(busy || session.State == GameSessionState.Closed);
        Section("Exact session players");
        foreach (var player in runtime.Players)
        {
            var selected = runtime.SelectedPlayer?.PlayerId == player.PlayerId;
            if (ImGui.Selectable($"{player.CharacterName}@{player.HomeWorld} | {player.State} | {player.DepositedGil:N0} gil##{player.PlayerId:D}", selected))
            {
                runtime.SelectPlayer(player.PlayerId);
            }

            if (selected)
            {
                ImGui.Indent();
                ImGui.TextUnformatted($"Payout: {player.PayoutState} | Reconciliation: {player.ReconciliationState}");
                ImGui.Unindent();
            }
        }

        DrawDeposit();
    }

    private void DrawDeposit()
    {
        Section("Manual deposit");
        ImGui.TextDisabled("Dropbox never handles inbound trades or deposits.");
        ImGui.SetNextItemWidth(180);
        ImGui.InputText("Whole gil", ref depositGil, 20, ImGuiInputTextFlags.CharsDecimal);
        var player = runtime.SelectedPlayer;
        BeginDisabled(busy || player?.State != SessionPlayerState.Open || runtime.PendingDeposit is not null);
        if (ImGui.Button("Review deposit") && DealerInputValidator.TryParseGil(depositGil, out var amount))
        {
            TryUiAction(() => runtime.PrepareDeposit(amount));
        }
        else if (ImGui.IsItemClicked())
        {
            validationMessage = "Deposit must be a positive whole gil amount.";
        }

        EndDisabled(busy || player?.State != SessionPlayerState.Open || runtime.PendingDeposit is not null);

        if (runtime.PendingDeposit is { } pending)
        {
            ImGui.TextWrapped($"Confirm {pending.AmountGil:N0} gil for the selected exact player. Key: {pending.IdempotencyKey:D}");
            BeginDisabled(busy);
            if (ImGui.Button("Confirm deposit"))
            {
                Run(() => runtime.SubmitDepositAsync());
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                runtime.CancelPendingDeposit();
            }

            EndDisabled(busy);
        }

        if (runtime.RecentDeposit is { } recent)
        {
            ImGui.TextWrapped($"Recent {recent.State}: {recent.ResultMessage} Key: {recent.IdempotencyKey:D}");
            if (recent.State == DepositSubmissionState.Failed)
            {
                BeginDisabled(busy);
                if (ImGui.Button("Retry with same key"))
                {
                    Run(() => runtime.RetryRecentDepositAsync());
                }

                EndDisabled(busy);
            }
        }
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
        }
        catch (Exception exception)
        {
            validationMessage = SecretRedactor.Redact(exception.Message);
        }
        finally
        {
            busy = false;
        }
    }

    private void TryUiAction(Action action)
    {
        validationMessage = string.Empty;
        try
        {
            action();
        }
        catch (Exception exception)
        {
            validationMessage = SecretRedactor.Redact(exception.Message);
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
