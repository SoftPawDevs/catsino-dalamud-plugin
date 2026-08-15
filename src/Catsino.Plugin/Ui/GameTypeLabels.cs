using Catsino.Plugin.Contracts;

namespace Catsino.Plugin.Ui;

// The wire carries game types as bare lower-case words ("holdem"); those are identifiers, not labels, so
// every dealer-facing surface runs them through here instead of printing them raw.
public static class GameTypeLabels
{
    public static string Label(string? gameType) => gameType?.Trim().ToLowerInvariant() switch
    {
        "plinko" => "Plinko",
        "blackjack" => "Blackjack",
        "holdem" => "Hold'em",
        "roulette" => "Roulette",
        // An unknown type is still worth showing rather than hiding — a newer backend may know a game this
        // plugin does not.
        _ => string.IsNullOrWhiteSpace(gameType) ? "Unknown" : gameType
    };

    // One line identifying a session in a list: "#1 Hold'em | Open". The number is the dealer's own
    // short label for the session, assigned by the backend and reused once a session is deleted.
    public static string Summary(GameSessionDto session) =>
        session.DealerSessionNumber > 0
            ? $"#{session.DealerSessionNumber} {Label(session.GameType)} | {session.State}"
            : $"{Label(session.GameType)} | {session.State}";
}
