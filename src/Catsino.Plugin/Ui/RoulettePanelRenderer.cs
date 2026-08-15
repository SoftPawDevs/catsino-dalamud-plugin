using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using Catsino.Plugin.Contracts;
using Catsino.Plugin.Runtime;
using Catsino.Plugin.Security;
using Dalamud.Bindings.ImGui;

namespace Catsino.Plugin.Ui;

// The dealer's roulette surface, shown as the "Table" sub-tab of a roulette session: the wheel with the
// ball sitting in its pocket, every player's chips grouped by player, the last numbers, and the single
// Spin control.
//
// The dealer decides nothing about the outcome — the backend draws the number the moment the wheel is
// released, and the payouts only book when the ball lands. Nothing at this table is secret, so the dealer
// sees exactly the same table the players do.
public sealed class RoulettePanelRenderer(CatsinoRuntime runtime, RouletteTextures art)
{
    private readonly ConcurrentDictionary<Guid, byte> busySessions = new();
    private readonly ConcurrentQueue<Action> pendingUiUpdates = new();
    private readonly Dictionary<Guid, string> validationMessages = [];
    private readonly HashSet<Guid> requested = [];

    private static readonly Vector4 ActiveColor = new(0.95f, 0.79f, 0.41f, 1f);
    private static readonly Vector4 WinColor = new(0.48f, 1f, 0.69f, 1f);
    private static readonly Vector4 LossColor = new(1f, 0.53f, 0.58f, 1f);
    private static readonly Vector4 MutedColor = new(0.7f, 0.7f, 0.7f, 1f);
    private static readonly Vector4 RedPocket = new(0.83f, 0.28f, 0.22f, 1f);
    private static readonly Vector4 GreenPocket = new(0.35f, 0.80f, 0.52f, 1f);

    // Pocket order as painted on roulette_wheel.png: zero at 12 o'clock, running clockwise. This mirrors
    // the artwork, not the game — the backend owns the real wheel.
    private static readonly int[] Pockets =
    [
        0, 32, 15, 19, 4, 21, 2, 25, 17, 34, 6, 27, 13, 36, 11, 30, 8, 23, 10, 5,
        24, 16, 33, 1, 20, 14, 31, 9, 22, 18, 29, 7, 28, 12, 35, 3, 26
    ];

    private const float WheelSize = 260f;
    // The disc is 452px wide on a 600px board, and its numbered ring sits at ~86% of the disc's radius.
    private const float DiscRatio = 452f / 600f;
    private const float RingRatio = DiscRatio / 2f * 0.86f;

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
        var table = runtime.GetRouletteTable(sessionId);
        if (table is null)
        {
            ImGui.TextDisabled("Loading the roulette table...");
            if (requested.Add(sessionId))
            {
                Run(sessionId, () => runtime.RefreshRouletteTableAsync(sessionId));
            }

            return;
        }

        ImGui.PushID(sessionId.ToString("D"));
        DrawHeader(table, DateTimeOffset.UtcNow);
        ImGui.Separator();
        DrawWheel(table, DateTimeOffset.UtcNow);
        ImGui.Separator();
        DrawControls(sessionId, table);
        ImGui.Separator();
        DrawBets(table);
        if (validationMessages.TryGetValue(sessionId, out var message) && !string.IsNullOrWhiteSpace(message))
        {
            ImGui.Spacing();
            ImGui.TextColored(LossColor, message);
        }

        ImGui.PopID();
    }

    private static void DrawHeader(RouletteTableDto table, DateTimeOffset now)
    {
        ImGui.TextUnformatted($"Round: {StatusLabel(table.Status)}");
        ImGui.SameLine();
        ImGui.TextDisabled($"| On the table: {Dots(table.TotalStaked)}");
        ImGui.SameLine();
        ImGui.TextDisabled($"| Min {Dots(table.MinBet)} · Max {Dots(table.MaxBet)} per field");
        if (table.Status == "spinning" && table.DeadlineAt is { } deadline)
        {
            ImGui.SameLine();
            ImGui.TextColored(ActiveColor, $"| Ball lands {InviteCountdown.Format(deadline, now)}");
        }
    }

    // The board and the disc are drawn as they are — a spinning number ring would be unreadable at this
    // size — and the ball is placed in the pocket the round is heading for. While the wheel is spinning it
    // races around and eases into that pocket, so the dealer sees the same thing the players do.
    private void DrawWheel(RouletteTableDto table, DateTimeOffset now)
    {
        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var drew = false;
        if (art.Board is { } board)
        {
            drawList.AddImage(board, origin, origin + new Vector2(WheelSize, WheelSize));
            drew = true;
        }

        if (art.Wheel is { } wheel)
        {
            var inset = WheelSize * (1f - DiscRatio) / 2f;
            drawList.AddImage(wheel, origin + new Vector2(inset, inset), origin + new Vector2(WheelSize - inset, WheelSize - inset));
            drew = true;
        }

        if (drew)
        {
            if (BallAngle(table, now) is { } angle && art.Ball is { } ball)
            {
                var centre = origin + new Vector2(WheelSize / 2f, WheelSize / 2f);
                var radians = angle * MathF.PI / 180f;
                var position = centre + new Vector2(MathF.Sin(radians), -MathF.Cos(radians)) * (WheelSize * RingRatio);
                var half = WheelSize / 26f;
                drawList.AddImage(ball, position - new Vector2(half, half), position + new Vector2(half, half));
            }

            ImGui.Dummy(new Vector2(WheelSize, WheelSize));
        }
        else
        {
            ImGui.TextDisabled("(wheel art unavailable)");
        }

        if (table.WinningNumber is { } number)
        {
            ImGui.TextColored(PocketColor(table.WinningColor), $"{number} {table.WinningColor}");
            ImGui.SameLine();
            ImGui.TextDisabled(table.Status == "spinning" ? "— the ball is still running" : "— paid out");
        }

        if (table.RecentNumbers.Count > 0)
        {
            ImGui.TextDisabled($"Last numbers: {string.Join("  ", table.RecentNumbers)}");
        }
    }

    // Where the ball sits, in degrees clockwise from 12 o'clock, or null when no number is in play.
    private static float? BallAngle(RouletteTableDto table, DateTimeOffset now)
    {
        if (table.WinningNumber is not { } number)
        {
            return null;
        }

        var index = Array.IndexOf(Pockets, number);
        if (index < 0)
        {
            return null;
        }

        var target = index * (360f / Pockets.Length);
        if (table.Status != "spinning" || table.DeadlineAt is not { } deadline)
        {
            return target;
        }

        // Phase comes from the round's deadline, so a plugin that reconnects mid-spin picks the animation
        // up where it actually is rather than starting over.
        var remaining = (float)(deadline - now).TotalSeconds;
        var progress = Math.Clamp(1f - remaining / RouletteBetDefaults.SpinSeconds, 0f, 1f);
        var eased = 1f - MathF.Pow(1f - progress, 4f);
        return target - (1f - eased) * 6f * 360f;
    }

    private void DrawControls(Guid sessionId, RouletteTableDto table)
    {
        var busy = busySessions.ContainsKey(sessionId);
        ImGui.BeginDisabled(busy);
        var canSpin = table.Status is "betting" && table.Bets.Count > 0;
        ImGui.BeginDisabled(!canSpin);
        if (ImGui.Button("Spin"))
        {
            Run(sessionId, () => runtime.SpinRouletteAsync(sessionId));
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled(table.Status switch
        {
            "betting" => canSpin ? "Ready — release the ball." : "Waiting for the first chip.",
            "spinning" => "No more bets — the ball is running.",
            "settled" => "Paid out. Betting reopens in a moment.",
            _ => "Waiting for players to place chips."
        });

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Refresh"))
        {
            Run(sessionId, () => runtime.RefreshRouletteTableAsync(sessionId));
        }
    }

    private static void DrawBets(RouletteTableDto table)
    {
        if (table.Bets.Count == 0)
        {
            ImGui.TextDisabled("No chips on the table yet. Players bet from the web app.");
            return;
        }

        foreach (var group in table.Bets.GroupBy(bet => bet.MembershipId))
        {
            var bets = group.ToList();
            ImGui.TextUnformatted(bets[0].Name);
            ImGui.SameLine();
            ImGui.TextColored(ActiveColor, $"({Dots(bets.Sum(bet => bet.Amount))})");
            var net = bets.Sum(bet => bet.Net ?? 0);
            if (bets.Any(bet => bet.Net is not null))
            {
                ImGui.SameLine();
                ImGui.TextColored(net >= 0 ? WinColor : LossColor, $"Result: {(net >= 0 ? "+" : "-")}{Dots(Math.Abs(net))}");
            }

            foreach (var bet in bets)
            {
                ImGui.TextColored(MutedColor, $"    {FieldName(bet.Type, bet.Selection)} — {Dots(bet.Amount)}");
                if (bet.Payout is { } payout && payout > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(WinColor, $"pays {Dots(payout)}");
                }
            }

            ImGui.Separator();
        }
    }

    // The wire carries the field as a type plus the numbers it covers; this is the dealer-readable name.
    private static string FieldName(string type, IReadOnlyList<int> selection) => type switch
    {
        "straight" => $"Straight up {Join(selection)}",
        "split" => $"Split {Join(selection)}",
        "street" => selection.Contains(0) ? $"Trio {Join(selection)}" : $"Street {Join(selection)}",
        "corner" => selection.Contains(0) ? "First four 0/1/2/3" : $"Corner {Join(selection)}",
        "sixLine" => selection.Count > 0 ? $"Six line {selection[0]}-{selection[^1]}" : "Six line",
        "column" => selection.Count > 0 ? $"Column {selection[0]}" : "Column",
        "dozen" => selection.Count > 0 ? selection[0] switch { 1 => "1st 12", 2 => "2nd 12", _ => "3rd 12" } : "Dozen",
        "red" => "Red",
        "black" => "Black",
        "odd" => "Odd",
        "even" => "Even",
        "low" => "1 to 18",
        "high" => "19 to 36",
        _ => type
    };

    private static string Join(IReadOnlyList<int> selection) => string.Join('/', selection);

    private static Vector4 PocketColor(string? color) => color switch
    {
        "red" => RedPocket,
        "green" => GreenPocket,
        _ => MutedColor
    };

    private static string StatusLabel(string status) => status switch
    {
        "betting" => "Betting open",
        "spinning" => "No more bets",
        "settled" => "Paid out",
        _ => "Waiting"
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
