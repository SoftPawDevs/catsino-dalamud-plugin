using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using Catsino.Plugin.Contracts;
using Catsino.Plugin.Runtime;
using Catsino.Plugin.Security;
using Dalamud.Bindings.ImGui;

namespace Catsino.Plugin.Ui;

// The dealer's Blackjack surface (a per-session tab): the shared table with the dealer's hand, a per-player
// row (Player | Tokens | Bet | Hand | Status), an always-on 45s turn countdown, and the dealer's Deal / Hit /
// Stay controls. Layout follows the reference GUI, minus the payment/to-pay columns and with a Tokens column
// (live balance, bet already escrowed) placed before Bet.
public sealed class BlackjackPanelRenderer(CatsinoRuntime runtime)
{
    private readonly ConcurrentDictionary<Guid, byte> busySessions = new();
    private readonly ConcurrentQueue<Action> pendingUiUpdates = new();
    private readonly Dictionary<Guid, string> validationMessages = [];
    private readonly HashSet<Guid> requested = [];

    private static readonly Vector4 ActiveColor = new(0.95f, 0.79f, 0.41f, 1f);
    private static readonly Vector4 WinColor = new(0.48f, 1f, 0.69f, 1f);
    private static readonly Vector4 LossColor = new(1f, 0.53f, 0.58f, 1f);

    public void Draw(Guid sessionId)
    {
        while (pendingUiUpdates.TryDequeue(out var update))
        {
            update();
        }

        runtime.TrackSession(sessionId);
        var table = runtime.GetBlackjackTable(sessionId);
        if (table is null)
        {
            ImGui.TextDisabled("Loading the blackjack table...");
            if (requested.Add(sessionId))
            {
                Run(sessionId, () => runtime.RefreshBlackjackTableAsync(sessionId));
            }

            return;
        }

        ImGui.PushID(sessionId.ToString("D"));
        var now = DateTimeOffset.UtcNow;
        DrawHeader(table, now);
        ImGui.Separator();
        DrawDealer(table);
        ImGui.Separator();
        DrawControls(sessionId, table);
        ImGui.Separator();
        DrawSeats(table);
        if (validationMessages.TryGetValue(sessionId, out var message) && !string.IsNullOrWhiteSpace(message))
        {
            ImGui.Spacing();
            ImGui.TextColored(LossColor, message);
        }

        ImGui.PopID();
    }

    private static void DrawHeader(BlackjackTableDto table, DateTimeOffset now)
    {
        ImGui.TextUnformatted($"Round: {StatusLabel(table.Status)}");
        ImGui.SameLine();
        ImGui.TextDisabled($"| Seated: {table.Seats.Count}");
        if (table.DeadlineAt is { } deadline && table.Status is "playerTurns" or "dealerTurn")
        {
            ImGui.SameLine();
            var whose = table.DealerTurn
                ? "Dealer"
                : table.Seats.FirstOrDefault(seat => seat.MembershipId == table.ActiveMembershipId)?.Name ?? "Player";
            ImGui.TextColored(ActiveColor, $"| {whose}'s turn — {InviteCountdown.Format(deadline, now)}");
        }
    }

    private static void DrawDealer(BlackjackTableDto table)
    {
        var hand = string.Join(" ", table.DealerCards.Select(CardText));
        if (table.DealerHasHiddenCard)
        {
            hand = string.IsNullOrEmpty(hand) ? "[??]" : $"{hand} [??]";
        }

        ImGui.TextUnformatted($"Dealer: {(string.IsNullOrEmpty(hand) ? "no cards" : hand)}");
        ImGui.SameLine();
        ImGui.TextDisabled(table.DealerHasHiddenCard ? $"(showing {table.DealerValue})" : $"(value {table.DealerValue})");
    }

    private void DrawControls(Guid sessionId, BlackjackTableDto table)
    {
        var busy = busySessions.ContainsKey(sessionId);
        ImGui.BeginDisabled(busy);
        if (table.Status == "dealerTurn")
        {
            if (ImGui.Button("Hit"))
            {
                Run(sessionId, () => runtime.DealerBlackjackHitAsync(sessionId));
            }

            ImGui.SameLine();
            if (ImGui.Button("Stay"))
            {
                Run(sessionId, () => runtime.DealerBlackjackStayAsync(sessionId));
            }
        }
        else
        {
            var canDeal = table.Status == "betting" && table.Seats.Any(seat => seat.Bet > 0);
            ImGui.BeginDisabled(!canDeal);
            if (ImGui.Button("Deal"))
            {
                Run(sessionId, () => runtime.DealBlackjackAsync(sessionId));
            }

            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextDisabled(table.Status switch
            {
                "betting" => canDeal ? "Deal the players who have bet." : "Waiting for players to place bets.",
                "playerTurns" => "Players are acting — watch the timer.",
                "settled" => "Hand over. Players bet again to start the next one.",
                _ => "Waiting for players to place bets."
            });
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Refresh"))
        {
            Run(sessionId, () => runtime.RefreshBlackjackTableAsync(sessionId));
        }
    }

    private static void DrawSeats(BlackjackTableDto table)
    {
        if (table.Seats.Count == 0)
        {
            ImGui.TextDisabled("No players have placed a bet yet.");
            return;
        }

        if (!ImGui.BeginTable("BlackjackSeats", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Tokens", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Bet", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Hand", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableHeadersRow();

        foreach (var seat in table.Seats)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (seat.IsActive)
            {
                ImGui.TextColored(ActiveColor, $"> {seat.Name}");
            }
            else
            {
                ImGui.TextUnformatted(seat.Name);
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(seat.Tokens.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(seat.Bet.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            var hand = string.Join(" ", seat.Cards.Select(CardText));
            ImGui.TextUnformatted(string.IsNullOrEmpty(hand) ? "-" : $"{hand} ({seat.Value})");
            ImGui.TableNextColumn();
            var (statusText, color) = SeatStatus(seat);
            if (color is { } tint)
            {
                ImGui.TextColored(tint, statusText);
            }
            else
            {
                ImGui.TextUnformatted(statusText);
            }
        }

        ImGui.EndTable();
    }

    private static (string Text, Vector4? Color) SeatStatus(BlackjackSeatDto seat) => seat.Status switch
    {
        "won" => (seat.Net is { } net ? $"Won +{net:N0}" : "Won", WinColor),
        "lost" => ("Lost", LossColor),
        "push" => ("Push", null),
        "busted" => ("Bust", LossColor),
        "blackjack" => ("Blackjack", ActiveColor),
        "stood" => ("Stand", null),
        _ => (seat.IsBlackjack ? "Blackjack" : "Playing", null)
    };

    private static string StatusLabel(string status) => status switch
    {
        "betting" => "Betting open",
        "playerTurns" => "Players' turn",
        "dealerTurn" => "Dealer's turn",
        "settled" => "Hand over",
        _ => "Idle"
    };

    private static string CardText(BlackjackCardDto card) => $"{Rank(card.Rank)}{Suit(card.Suit)}";
    private static string Rank(int rank) => rank switch { 1 => "A", 11 => "J", 12 => "Q", 13 => "K", _ => rank.ToString(CultureInfo.InvariantCulture) };
    private static string Suit(int suit) => suit switch { 0 => "C", 1 => "D", 2 => "H", _ => "S" };

    private void Run(Guid sessionId, Func<Task> action) => _ = RunCoreAsync(sessionId, action);

    private async Task RunCoreAsync(Guid sessionId, Func<Task> action)
    {
        if (!busySessions.TryAdd(sessionId, 0))
        {
            return;
        }

        pendingUiUpdates.Enqueue(() => validationMessages[sessionId] = string.Empty);
        try
        {
            await action().ConfigureAwait(false);
            pendingUiUpdates.Enqueue(() => busySessions.TryRemove(sessionId, out _));
        }
        catch (Exception exception)
        {
            var message = SecretRedactor.Redact(exception.Message);
            pendingUiUpdates.Enqueue(() =>
            {
                validationMessages[sessionId] = message;
                busySessions.TryRemove(sessionId, out _);
            });
        }
    }
}
