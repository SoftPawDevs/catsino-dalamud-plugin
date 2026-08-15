using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using Catsino.Plugin.Contracts;
using Catsino.Plugin.Runtime;
using Catsino.Plugin.Security;
using Dalamud.Bindings.ImGui;

namespace Catsino.Plugin.Ui;

// The dealer's Texas Hold'em surface, shown as the "Table" sub-tab of a Hold'em session: the community
// cards and pots, a per-seat block (name with D/SB/BB markers, stack, current bet, status), the 45s action
// countdown, and the single Deal control.
//
// The dealer plays no hand here — the backend deals every card, enforces the betting rules and settles the
// pots. Hole cards are deliberately absent even at showdown: the dealer never needs them, and not sending
// them is the only way they cannot be leaked.
public sealed class HoldemPanelRenderer(CatsinoRuntime runtime, CardTextures cards)
{
    private readonly ConcurrentDictionary<Guid, byte> busySessions = new();
    private readonly ConcurrentQueue<Action> pendingUiUpdates = new();
    private readonly Dictionary<Guid, string> validationMessages = [];
    private readonly HashSet<Guid> requested = [];

    private static readonly Vector4 ActiveColor = new(0.95f, 0.79f, 0.41f, 1f);
    private static readonly Vector4 WinColor = new(0.48f, 1f, 0.69f, 1f);
    private static readonly Vector4 LossColor = new(1f, 0.53f, 0.58f, 1f);
    private static readonly Vector4 MutedColor = new(0.7f, 0.7f, 0.7f, 1f);
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
        var table = runtime.GetHoldemTable(sessionId);
        if (table is null)
        {
            ImGui.TextDisabled("Loading the Hold'em table...");
            if (requested.Add(sessionId))
            {
                Run(sessionId, () => runtime.RefreshHoldemTableAsync(sessionId));
            }

            return;
        }

        ImGui.PushID(sessionId.ToString("D"));
        DrawHeader(table, DateTimeOffset.UtcNow);
        ImGui.Separator();
        DrawBoard(table);
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

    private static void DrawHeader(HoldemTableDto table, DateTimeOffset now)
    {
        ImGui.TextUnformatted($"Hand: {StatusLabel(table.Status)}");
        ImGui.SameLine();
        ImGui.TextDisabled($"| Seats: {table.Seats.Count} / {table.SeatCapacity}");
        ImGui.SameLine();
        ImGui.TextDisabled($"| SB {Dots(table.SmallBlind)} · BB {Dots(table.BigBlind)}");
        if (table.DeadlineAt is { } deadline && table.ActiveMembershipId is { } active)
        {
            ImGui.SameLine();
            var whose = table.Seats.FirstOrDefault(seat => seat.MembershipId == active)?.Name ?? "Player";
            ImGui.TextColored(ActiveColor, $"| {whose}'s turn — {InviteCountdown.Format(deadline, now)}");
        }
    }

    private void DrawBoard(HoldemTableDto table)
    {
        ImGui.TextUnformatted("Board");
        ImGui.SameLine();
        ImGui.TextDisabled(table.TotalPot > 0 ? $"(pot {Dots(table.TotalPot)})" : "(no chips in the middle)");
        var drew = false;
        foreach (var card in table.Board)
        {
            DrawCard(cards.Handle(card), $"{CardTextures.RankCode(card.Rank)}{CardTextures.SuitCode(card.Suit)}", ref drew);
        }

        if (!drew)
        {
            ImGui.TextDisabled("No community cards yet.");
        }

        // Side pots only exist once someone is all in for less than the others; showing one line per pot
        // makes it obvious who is playing for what.
        if (table.Pots.Count > 1)
        {
            for (var i = 0; i < table.Pots.Count; i++)
            {
                var names = table.Pots[i].EligibleMembershipIds
                    .Select(id => table.Seats.FirstOrDefault(seat => seat.MembershipId == id)?.Name ?? "?")
                    .ToArray();
                ImGui.TextDisabled($"{(i == 0 ? "Main pot" : $"Side pot {i}")}: {Dots(table.Pots[i].Amount)} — {string.Join(", ", names)}");
            }
        }
    }

    private void DrawControls(Guid sessionId, HoldemTableDto table)
    {
        var busy = busySessions.ContainsKey(sessionId);
        ImGui.BeginDisabled(busy);
        // A hand can only be started when none is running and at least two seats have chips.
        var canDeal = table.Status is "idle" or "waitingForPlayers" or "settled" && table.Seats.Count(seat => !seat.SittingOut && seat.Tokens > 0) >= 2;
        ImGui.BeginDisabled(!canDeal);
        if (ImGui.Button("Deal"))
        {
            Run(sessionId, () => runtime.DealHoldemAsync(sessionId));
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(table.Status switch
        {
            "idle" or "waitingForPlayers" => canDeal ? "Ready — deal the next hand." : "Waiting for at least two players with chips.",
            "settled" => canDeal ? "Hand over — deal the next one." : "Hand over. Waiting for players.",
            "preflop" or "flop" or "turn" or "river" => "Players are betting — the server runs the streets.",
            "showdown" => "Showdown.",
            _ => string.Empty
        });

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Refresh"))
        {
            Run(sessionId, () => runtime.RefreshHoldemTableAsync(sessionId));
        }
    }

    private void DrawSeats(HoldemTableDto table)
    {
        if (table.Seats.Count == 0)
        {
            ImGui.TextDisabled("Nobody is seated yet. Players take a seat from the web app.");
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

            var markers = Markers(seat);
            if (markers.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(ActiveColor, markers);
            }

            ImGui.SameLine();
            var (statusText, color) = SeatStatus(seat);
            ImGui.TextColored(color ?? MutedColor, $"[{statusText}]");

            // Hole cards are never sent to the dealer; face-down backs keep the table readable without them.
            var drew = false;
            if (seat.HasHiddenCards)
            {
                DrawCard(cards.Back, "??", ref drew);
                DrawCard(cards.Back, "??", ref drew);
            }

            if (!drew)
            {
                ImGui.TextDisabled(seat.SittingOut ? "Leaving after this hand." : "Not in this hand.");
            }

            ImGui.TextUnformatted($"Stack: {Dots(seat.Tokens)}");
            if (seat.Committed > 0)
            {
                ImGui.SameLine(0f, 28f);
                ImGui.TextUnformatted($"Bet: {Dots(seat.Committed)}");
            }

            if (seat.TotalCommitted > 0)
            {
                ImGui.SameLine(0f, 28f);
                ImGui.TextUnformatted($"In pot: {Dots(seat.TotalCommitted)}");
            }

            if (seat.HandDescription is { Length: > 0 } description)
            {
                ImGui.SameLine(0f, 28f);
                ImGui.TextColored(ActiveColor, description);
            }

            if (seat.Net is { } net)
            {
                ImGui.SameLine(0f, 28f);
                ImGui.TextColored(net >= 0 ? WinColor : LossColor, $"Result: {(net >= 0 ? "+" : "-")}{Dots(Math.Abs(net))}");
            }

            ImGui.Separator();
        }
    }

    private static string Markers(HoldemSeatDto seat)
    {
        var markers = new List<string>(3);
        if (seat.IsButton)
        {
            markers.Add("D");
        }

        if (seat.IsSmallBlind)
        {
            markers.Add("SB");
        }

        if (seat.IsBigBlind)
        {
            markers.Add("BB");
        }

        return markers.Count == 0 ? string.Empty : $"({string.Join('/', markers)})";
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

    private static (string Text, Vector4? Color) SeatStatus(HoldemSeatDto seat) => seat.Status switch
    {
        "won" => (seat.Net is { } net ? $"Won +{net:N0}" : "Won", WinColor),
        "lost" => ("Lost", LossColor),
        "folded" => ("Folded", null),
        "allIn" => ("All in", ActiveColor),
        _ => (seat.SittingOut ? "Leaving" : "Playing", null)
    };

    private static string StatusLabel(string status) => status switch
    {
        "waitingForPlayers" => "Waiting for players",
        "preflop" => "Pre-flop",
        "flop" => "Flop",
        "turn" => "Turn",
        "river" => "River",
        "showdown" => "Showdown",
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
