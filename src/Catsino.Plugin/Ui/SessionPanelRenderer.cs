using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using Catsino.Plugin.Contracts;
using Catsino.Plugin.Runtime;
using Catsino.Plugin.Security;
using Catsino.Plugin.Workflow;
using Dalamud.Bindings.ImGui;

namespace Catsino.Plugin.Ui;

public sealed class SessionPanelRenderer(CatsinoRuntime runtime, Action<Guid> openDetached, BlackjackPanelRenderer blackjackPanel, HoldemPanelRenderer holdemPanel)
{
    private readonly Dictionary<Guid, PanelState> states = [];
    private readonly ConcurrentDictionary<SessionPlayerKey, byte> busyPlayers = new();
    private readonly ConcurrentQueue<Action> pendingUiUpdates = new();

    // Formats a gil amount with '.' thousands separators (e.g. 5000000 -> "5.000.000").
    private static readonly NumberFormatInfo DottedGil = new() { NumberGroupSeparator = ".", NumberGroupSizes = [3], NumberDecimalDigits = 0 };
    private static string FormatGilDotted(long amount) => amount.ToString("N0", DottedGil);
    // Signed dotted gil for the Net column: "+1.000" / "0" / "-1.000" (N0 already prints the minus sign).
    private static string FormatGilSignedDotted(long amount) => (amount > 0 ? "+" : string.Empty) + amount.ToString("N0", DottedGil);
    // Net colour: green when the player is up on their deposit, red when down, white when even.
    private static Vector4 NetColor(long net) => net > 0
        ? new Vector4(0.48f, 1f, 0.69f, 1f)
        : net < 0 ? new Vector4(1f, 0.53f, 0.58f, 1f) : new Vector4(1f, 1f, 1f, 1f);

    public void Draw(Guid sessionId)
    {
        while (pendingUiUpdates.TryDequeue(out var update))
        {
            update();
        }

        var session = runtime.GetSession(sessionId);
        if (session is null)
        {
            ImGui.TextDisabled("This session is no longer available.");
            return;
        }

        runtime.TrackSession(sessionId);
        var state = GetState(session);
        var roster = runtime.GetRoster(sessionId);
        if (roster is null && !state.RosterRequested)
        {
            state.RosterRequested = true;
            RunSession(state, () => runtime.RefreshRosterAsync(sessionId));
        }

        ImGui.PushID(sessionId.ToString("D"));
        // A turn-based session keeps the normal management view, plus a "Table" sub-tab hosting the live
        // table for that game. Plinko has no table and shows the management view alone.
        Action<Guid>? tablePanel = session.GameType?.ToLowerInvariant() switch
        {
            "blackjack" => blackjackPanel.Draw,
            "holdem" => holdemPanel.Draw,
            _ => null
        };
        if (tablePanel is not null)
        {
            if (ImGui.BeginTabBar("TableGameSessionTabs"))
            {
                if (ImGui.BeginTabItem("Manage"))
                {
                    DrawManagement(sessionId, session, roster, state);
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Table"))
                {
                    tablePanel(sessionId);
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }
        }
        else
        {
            DrawManagement(sessionId, session, roster, state);
        }

        ImGui.PopID();
    }

    private void DrawManagement(Guid sessionId, GameSessionDto session, SessionRosterDto? roster, PanelState state)
    {
        if (ImGui.Button("Open in new window"))
        {
            openDetached(sessionId);
        }

        ImGui.SameLine();
        BeginDisabled(state.Busy);
        if (ImGui.Button("Refresh roster"))
        {
            RunSession(state, () => runtime.RefreshRosterAsync(sessionId));
        }

        EndDisabled(state.Busy);
        ImGui.SameLine();
        var canDelete = roster is not null && roster.Players.Count == 0;
        BeginDisabled(state.Busy || !canDelete);
        if (ImGui.Button("Delete session"))
        {
            RunSession(state, async () =>
            {
                await runtime.DeleteSessionAsync(sessionId).ConfigureAwait(false);
            });
        }

        EndDisabled(state.Busy || !canDelete);
        if (!canDelete)
        {
            ShowTooltip("A session can only be deleted when the backend roster has no active players.");
        }

        ImGui.TextUnformatted($"{GameTypeLabels.Summary(session)} | {roster?.Players.Count ?? session.PlayerCount} players");
        ImGui.TextUnformatted($"Deposited: {session.TotalDepositedGil:N0} gil");
        DrawSessionControls(session, state);

        if (!string.IsNullOrWhiteSpace(state.ValidationMessage))
        {
            ImGui.TextWrapped(state.ValidationMessage);
        }

        if (roster is null)
        {
            ImGui.TextDisabled("Loading authoritative roster...");
            return;
        }

        DrawRosterTable(session, roster, state);
        DrawConfirmations(roster, state);
    }

    private void DrawSessionControls(GameSessionDto session, PanelState state)
    {
        ImGui.TextDisabled($"Dealer fee fixed at creation: {session.FeePercent.ToString("0.00", CultureInfo.InvariantCulture)}%");
        ImGui.TextDisabled(session.MaxPlayers is { } cap
            ? $"Players: {session.PlayerCount} / {cap.ToString(CultureInfo.InvariantCulture)}"
            : $"Players: {session.PlayerCount} (no cap)");
    }

    private void DrawRosterTable(GameSessionDto session, SessionRosterDto roster, PanelState state)
    {
        ImGui.Spacing();
        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
                                      ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("DealerSessionRoster", 6, flags))
        {
            return;
        }

        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Home World", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Deposit", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Tokens", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Net", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableHeadersRow();

        foreach (var player in roster.Players)
        {
            DrawPlayerRow(player, state);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var invite in roster.PendingInvites.Where(invite => InviteCountdown.IsVisible(invite, now)))
        {
            DrawPendingInviteRow(invite, now, state);
        }

        DrawInvitationInputRow(session, roster, state);
        ImGui.EndTable();
    }

    private void DrawPlayerRow(SessionRosterPlayerDto player, PanelState state)
    {
        var key = new SessionPlayerKey(player.SessionId, player.MembershipId);
        var adjustment = runtime.GetBalanceAdjustment(key);
        var cashOut = runtime.GetCashOut(key);
        var rowBusy = busyPlayers.ContainsKey(key) ||
                      adjustment?.State == DealerActionState.Sending ||
                      cashOut?.State == DealerActionState.Sending;
        var payoutLocked = HasOpenPayout(player.PayoutState);
        var cashOutRequested = player.CashOutRequestedAt is not null && !payoutLocked;

        ImGui.PushID(player.MembershipId.ToString("D"));
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(player.CharacterName);
        ShowTooltip($"Betting locked: {player.BettingLocked}\nJoined: {player.JoinedAt:u}");
        if (cashOutRequested)
        {
            ImGui.TextColored(new Vector4(0.36f, 0.90f, 0.83f, 1f), "Cash-out requested");
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(player.HomeWorld);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(FormatGilDotted(player.Deposit));
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(FormatGilDotted(player.Tokens));
        ImGui.TableNextColumn();
        // Net = current tokens minus what the player deposited: green if up, red if down, white if even.
        var net = player.Tokens - player.Deposit;
        ImGui.TextColored(NetColor(net), FormatGilSignedDotted(net));

        ImGui.TableNextColumn();
        var draft = runtime.ActionDrafts.GetBalanceAdjustment(key);
        ImGui.SetNextItemWidth(100);
        // No CharsDecimal filter so k/m/b shorthand (e.g. "5m", "250k") can be typed; the parser
        // interprets it and the preview shows the resolved whole-gil amount.
        if (ImGui.InputText("##signedAdjustment", ref draft, 20))
        {
            runtime.ActionDrafts.SetBalanceAdjustment(key, draft);
        }

        if (DealerInputValidator.TryParseBalanceAdjustment(draft, out var adjustmentPreview))
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"({FormatGilDotted(adjustmentPreview)})");
        }

        ImGui.SameLine();
        BeginDisabled(rowBusy || payoutLocked || player.BettingLocked || adjustment is not null);
        if (ImGui.Button("+##apply"))
        {
            if (DealerInputValidator.TryParseBalanceAdjustment(draft, out var amount))
            {
                TryUiAction(state, () => runtime.PrepareBalanceAdjustment(key, amount));
            }
            else
            {
                state.ValidationMessage = "Enter a signed, non-zero whole gil adjustment. Positive deposits; negative debits available Tokens.";
            }
        }

        EndDisabled(rowBusy || payoutLocked || player.BettingLocked || adjustment is not null);
        ImGui.SameLine();
        BeginDisabled(rowBusy || payoutLocked || cashOut is not null);
        if (ImGui.Button(cashOutRequested ? "Cash out (requested)" : "Cash out"))
        {
            RunPlayer(state, key, () => runtime.RequestCashOutPreviewAsync(key));
        }

        EndDisabled(rowBusy || payoutLocked || cashOut is not null);
        if (cashOutRequested)
        {
            ImGui.SameLine();
            BeginDisabled(rowBusy || cashOut is not null);
            if (ImGui.Button("Dismiss request"))
            {
                RunPlayer(state, key, () => runtime.DismissCashOutRequestAsync(key));
            }

            EndDisabled(rowBusy || cashOut is not null);
        }

        ImGui.SameLine();
        BeginDisabled(rowBusy);
        if (ImGui.Button("Reinvite"))
        {
            RunPlayer(state, key, () => runtime.ReinviteAndTellAsync(player.SessionId, player.MembershipId, player.CharacterName, player.HomeWorld));
        }

        EndDisabled(rowBusy);
        ShowTooltip("Send this player a fresh invite link via /tell. Redeeming it resumes their session; balance is kept.");

        if (payoutLocked)
        {
            ImGui.TextDisabled("Payout open");
        }

        ImGui.PopID();
    }

    private void DrawPendingInviteRow(PendingInviteDto invite, DateTimeOffset now, PanelState state)
    {
        ImGui.PushID(invite.InviteId.ToString("D"));
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(invite.CharacterName);
        ImGui.TextDisabled("Pending invite");
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(invite.HomeWorld);
        ImGui.TableNextColumn(); // Deposit column: the invite's starting balance
        ImGui.TextUnformatted($"{FormatGilDotted(invite.InitialBalanceGil)} gil");
        ImGui.TableNextColumn(); // Tokens: not applicable until the invite is redeemed
        ImGui.TableNextColumn(); // Net: not applicable yet
        ImGui.TableNextColumn();
        ImGui.TextDisabled("Pending");
        ImGui.SameLine();
        ImGui.TextUnformatted($"Expires in {InviteCountdown.Format(invite.ExpiresAt, now)}");
        ImGui.SameLine();
        BeginDisabled(state.Busy);
        if (ImGui.Button("Cancel invite"))
        {
            RunSession(state, () => runtime.CancelInviteAsync(invite.SessionId, invite.InviteId));
        }

        EndDisabled(state.Busy);
        ImGui.PopID();
    }

    private void DrawInvitationInputRow(GameSessionDto session, SessionRosterDto roster, PanelState state)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled("Character Name");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##inviteCharacterName", ref state.InviteName, 32);
        ImGui.TableNextColumn();
        ImGui.TextDisabled("Home World");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##inviteHomeWorld", ref state.InviteWorld, 32);
        ImGui.TableNextColumn();
        ImGui.TextDisabled("Balance");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##inviteBalance", ref state.InviteBalance, 20, ImGuiInputTextFlags.CharsDecimal);
        ImGui.TableNextColumn(); // Tokens column (empty for the input row)
        ImGui.TableNextColumn(); // Net column (empty for the input row)
        ImGui.TableNextColumn();
        ImGui.TextDisabled("New player");
        ImGui.SameLine();
        BeginDisabled(state.Busy || session.State == GameSessionState.Closed);
        if (ImGui.Button("+##invite"))
        {
            var name = state.InviteName.Trim();
            var world = state.InviteWorld.Trim();
            var error = DealerInputValidator.ValidateCharacter(name, world);
            if (error is not null)
            {
                state.ValidationMessage = error;
            }
            else if (!DealerInputValidator.TryParseGil(state.InviteBalance.Trim(), out var balance))
            {
                state.ValidationMessage = "Invite balance must be zero or a positive whole gil amount.";
            }
            else if ((error = DealerInputValidator.ValidateInviteBalance(balance)) is not null)
            {
                state.ValidationMessage = error;
            }
            else if ((error = SessionRosterStore.FindInviteConflict(roster, name, world)) is not null)
            {
                state.ValidationMessage = error;
            }
            else
            {
                RunSession(
                    state,
                    () => runtime.CreateInviteAndTellAsync(session.SessionId, name, world, balance),
                    () =>
                    {
                        state.InviteName = string.Empty;
                        state.InviteWorld = string.Empty;
                        state.InviteBalance = "0";
                    });
            }
        }

        EndDisabled(state.Busy || session.State == GameSessionState.Closed);
        ShowTooltip("Create the exact invite and send it through the native FFXIV /tell path.");
    }

    private void DrawConfirmations(SessionRosterDto roster, PanelState state)
    {
        foreach (var player in roster.Players)
        {
            var key = new SessionPlayerKey(player.SessionId, player.MembershipId);
            if (runtime.GetBalanceAdjustment(key) is { } adjustment)
            {
                ImGui.PushID($"adjust-{player.MembershipId:D}");
                ImGui.Separator();
                ImGui.TextWrapped(
                    $"Confirm {FormatSigned(adjustment.AmountGil)} gil for {player.CharacterName}@{player.HomeWorld}. " +
                    $"Idempotency key: {adjustment.IdempotencyKey:D}");
                if (!string.IsNullOrWhiteSpace(adjustment.ErrorMessage))
                {
                    ImGui.TextWrapped(adjustment.ErrorMessage);
                }

                if (adjustment.State == DealerActionState.Failed && !adjustment.CanDiscardFailure)
                {
                    ImGui.TextDisabled("The outcome is ambiguous. Retry with the retained idempotency key before dismissing.");
                }

                var busy = adjustment.State == DealerActionState.Sending || busyPlayers.ContainsKey(key);
                BeginDisabled(busy);
                if (ImGui.Button(adjustment.State == DealerActionState.Failed ? "Retry adjustment" : "Confirm adjustment"))
                {
                    RunPlayer(state, key, () => runtime.SubmitBalanceAdjustmentAsync(key));
                }

                EndDisabled(busy);

                ImGui.SameLine();
                BeginDisabled(busy || adjustment.State == DealerActionState.Failed && !adjustment.CanDiscardFailure);
                if (ImGui.Button("Cancel"))
                {
                    TryUiAction(state, () => runtime.CancelBalanceAdjustment(key));
                }

                EndDisabled(busy || adjustment.State == DealerActionState.Failed && !adjustment.CanDiscardFailure);
                ImGui.PopID();
            }

            if (runtime.GetCashOut(key) is { } cashOut)
            {
                DrawCashOutConfirmation(player, key, cashOut, state);
            }
        }
    }

    private void DrawCashOutConfirmation(
        SessionRosterPlayerDto player,
        SessionPlayerKey key,
        CashOutSubmission cashOut,
        PanelState state)
    {
        ImGui.PushID($"cashout-{player.MembershipId:D}");
        ImGui.Separator();
        ImGui.TextUnformatted($"Cash out all available Tokens for {player.CharacterName}@{player.HomeWorld}");
        ImGui.TextUnformatted($"Gross: {cashOut.Preview.Gross:N0} gil");
        ImGui.TextUnformatted($"Fee percent: {cashOut.Preview.FeePercent.ToString(CultureInfo.InvariantCulture)}%");
        ImGui.TextUnformatted($"Fee: {cashOut.Preview.Fee:N0} gil");
        ImGui.TextUnformatted($"Net: {cashOut.Preview.Net:N0} gil");

        if (!string.IsNullOrWhiteSpace(cashOut.ErrorMessage))
        {
            ImGui.TextWrapped(cashOut.ErrorMessage);
        }

        if (cashOut.State == DealerActionState.Failed && !cashOut.CanDiscardFailure)
        {
            ImGui.TextDisabled("The outcome is ambiguous. Retry with the retained idempotency key before dismissing.");
        }

        var confirmNetZero = runtime.ActionDrafts.GetNetZeroConfirmation(key);
        if (cashOut.Preview.NetIsZero)
        {
            if (ImGui.Checkbox("I explicitly confirm the zero net payout", ref confirmNetZero))
            {
                runtime.ActionDrafts.SetNetZeroConfirmation(key, confirmNetZero);
            }
        }

        var busy = cashOut.State == DealerActionState.Sending || busyPlayers.ContainsKey(key);
        BeginDisabled(busy || cashOut.Preview.NetIsZero && !confirmNetZero);
        if (ImGui.Button(cashOut.State == DealerActionState.Failed ? "Retry cash out" : "Confirm cash out"))
        {
            RunPlayer(state, key, () => runtime.SubmitCashOutAsync(key, confirmNetZero));
        }

        EndDisabled(busy || cashOut.Preview.NetIsZero && !confirmNetZero);

        ImGui.SameLine();
        BeginDisabled(busy || cashOut.State == DealerActionState.Failed && !cashOut.CanDiscardFailure);
        if (ImGui.Button("Cancel"))
        {
            TryUiAction(state, () => runtime.CancelCashOut(key));
        }

        EndDisabled(busy || cashOut.State == DealerActionState.Failed && !cashOut.CanDiscardFailure);
        ImGui.PopID();
    }

    private PanelState GetState(GameSessionDto session)
    {
        if (!states.TryGetValue(session.SessionId, out var state))
        {
            state = new PanelState();
            states.Add(session.SessionId, state);
        }

        return state;
    }

    private void RunSession(PanelState state, Func<Task> action, Action? onSuccess = null) =>
        _ = RunSessionCoreAsync(state, action, onSuccess);

    private async Task RunSessionCoreAsync(PanelState state, Func<Task> action, Action? onSuccess)
    {
        if (state.Busy)
        {
            return;
        }

        state.Busy = true;
        state.ValidationMessage = string.Empty;
        try
        {
            await action().ConfigureAwait(false);
            pendingUiUpdates.Enqueue(() =>
            {
                onSuccess?.Invoke();
                state.Busy = false;
            });
        }
        catch (Exception exception)
        {
            var message = SecretRedactor.Redact(exception.Message);
            pendingUiUpdates.Enqueue(() =>
            {
                state.ValidationMessage = message;
                state.Busy = false;
            });
        }
    }

    private void RunPlayer(PanelState state, SessionPlayerKey player, Func<Task> action) =>
        _ = RunPlayerCoreAsync(state, player, action);

    private async Task RunPlayerCoreAsync(PanelState state, SessionPlayerKey player, Func<Task> action)
    {
        if (!busyPlayers.TryAdd(player, 0))
        {
            return;
        }

        state.ValidationMessage = string.Empty;
        try
        {
            await action().ConfigureAwait(false);
            pendingUiUpdates.Enqueue(() => busyPlayers.TryRemove(player, out _));
        }
        catch (Exception exception)
        {
            var message = SecretRedactor.Redact(exception.Message);
            pendingUiUpdates.Enqueue(() =>
            {
                state.ValidationMessage = message;
                busyPlayers.TryRemove(player, out _);
            });
        }
    }

    private static void TryUiAction(PanelState state, Action action)
    {
        state.ValidationMessage = string.Empty;
        try
        {
            action();
        }
        catch (Exception exception)
        {
            state.ValidationMessage = SecretRedactor.Redact(exception.Message);
        }
    }

    private static bool HasOpenPayout(string state) => state.ToLowerInvariant() is
        "queued" or "waitingforplayer" or "tradeopened" or "tradelocked" or
        "reconciliationrequired" or "processing" or "inprogress" or "pending";

    private static string FormatSigned(long amount) => amount.ToString("+#,0;-#,0;0", CultureInfo.InvariantCulture);

    private static void ShowTooltip(string text)
    {
        if (!ImGui.IsItemHovered())
        {
            return;
        }

        ImGui.BeginTooltip();
        ImGui.TextUnformatted(text);
        ImGui.EndTooltip();
    }

    private static void BeginDisabled(bool disabled) => ImGui.BeginDisabled(disabled);

    private static void EndDisabled(bool _) => ImGui.EndDisabled();

    private sealed class PanelState
    {
        internal string InviteName = string.Empty;
        internal string InviteWorld = string.Empty;
        internal string InviteBalance = "0";
        internal string ValidationMessage = string.Empty;
        internal bool Busy;
        internal bool RosterRequested;
    }
}
