using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using Catsino.Plugin.Contracts;
using Catsino.Plugin.Runtime;
using Catsino.Plugin.Security;
using Dalamud.Bindings.ImGui;

namespace Catsino.Plugin.Ui;

// The dealer's Blackjack surface, shown as the "Table" sub-tab of a blackjack session: the dealer's hand
// (card images), a per-player block (name/status, card images, then Value / Tokens / Bet spaced apart), an
// always-on 45s turn countdown, and the dealer's Deal / Hit / Stay controls.
public sealed class BlackjackPanelRenderer(CatsinoRuntime runtime, CardTextures cards)
{
    private readonly ConcurrentDictionary<Guid, byte> busySessions = new();
    private readonly ConcurrentQueue<Action> pendingUiUpdates = new();
    private readonly Dictionary<Guid, string> validationMessages = [];
    private readonly HashSet<Guid> requested = [];

    private static readonly Vector4 ActiveColor = new(0.95f, 0.79f, 0.41f, 1f);
    private static readonly Vector4 WinColor = new(0.48f, 1f, 0.69f, 1f);
    private static readonly Vector4 LossColor = new(1f, 0.53f, 0.58f, 1f);
    private static readonly Vector2 CardSize = new(CardTextures.Width, CardTextures.Height);

    // "2.500.000" — dot thousands separators, matching the rest of the plugin and the web.
    private static readonly NumberFormatInfo DottedGil = new() { NumberGroupSeparator = ".", NumberGroupSizes = [3], NumberDecimalDigits = 0 };
    private static string Dots(long amount) => amount.ToString("N0", DottedGil);

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
        DrawHeader(table, DateTimeOffset.UtcNow);
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

    private void DrawDealer(BlackjackTableDto table)
    {
        ImGui.TextUnformatted("Dealer");
        ImGui.SameLine();
        ImGui.TextDisabled(table.DealerHasHiddenCard ? $"(showing {table.DealerValue})" : $"(value {table.DealerValue})");
        var drew = false;
        foreach (var card in table.DealerCards)
        {
            DrawCard(cards.Handle(card), $"{CardTextures.RankCode(card.Rank)}{CardTextures.SuitCode(card.Suit)}", ref drew);
        }

        if (table.DealerHasHiddenCard)
        {
            DrawCard(cards.Back, "??", ref drew);
        }

        if (!drew)
        {
            ImGui.TextDisabled("No cards dealt yet.");
        }
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
                "settled" => "Hand over. Betting reopens automatically.",
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

    private void DrawSeats(BlackjackTableDto table)
    {
        if (table.Seats.Count == 0)
        {
            ImGui.TextDisabled("No players have placed a bet yet.");
            return;
        }

        foreach (var seat in table.Seats)
        {
            if (seat.IsActive)
            {
                ImGui.TextColored(ActiveColor, $"> {seat.Name}");
            }
            else
            {
                ImGui.TextUnformatted(seat.Name);
            }

            ImGui.SameLine();
            var (statusText, color) = SeatStatus(seat);
            ImGui.TextColored(color ?? new Vector4(0.7f, 0.7f, 0.7f, 1f), $"[{statusText}]");

            var drew = false;
            foreach (var card in seat.Cards)
            {
                DrawCard(cards.Handle(card), $"{CardTextures.RankCode(card.Rank)}{CardTextures.SuitCode(card.Suit)}", ref drew);
            }

            if (!drew)
            {
                ImGui.TextDisabled("Waiting for the deal.");
            }

            // Value / Tokens / Bet spaced apart for readability, "Label: value" with dot separators.
            ImGui.TextUnformatted($"Value: {seat.Value.ToString(CultureInfo.InvariantCulture)}");
            ImGui.SameLine(0f, 28f);
            ImGui.TextUnformatted($"Tokens: {Dots(seat.Tokens)}");
            ImGui.SameLine(0f, 28f);
            ImGui.TextUnformatted($"Bet: {Dots(seat.Bet)}");
            if (seat.Net is { } net)
            {
                ImGui.SameLine(0f, 28f);
                ImGui.TextColored(net >= 0 ? WinColor : LossColor, $"Result: {(net >= 0 ? "+" : "-")}{Dots(Math.Abs(net))}");
            }

            ImGui.Separator();
        }
    }

    private static void DrawCard(ImTextureID? handle, string fallback, ref bool drew)
    {
        if (drew)
        {
            ImGui.SameLine();
        }

        if (handle is { } id)
        {
            ImGui.Image(id, CardSize);
        }
        else
        {
            ImGui.TextUnformatted(fallback);
        }

        drew = true;
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
