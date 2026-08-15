using System.Globalization;
using System.Text.RegularExpressions;
using Catsino.Plugin.Contracts;

namespace Catsino.Plugin.Security;

public static partial class DealerInputValidator
{
    public static string? ValidateCharacter(string characterName, string homeWorld)
    {
        if (!CharacterNameRegex().IsMatch(characterName))
        {
            return "Enter the exact character name as First Last.";
        }

        if (!WorldNameRegex().IsMatch(homeWorld))
        {
            return "Enter the exact Home World name.";
        }

        return null;
    }

    public static string? ValidateFee(decimal feePercent, GameSessionState state)
    {
        if (state != GameSessionState.Created)
        {
            return "The fee can only be changed while the session is Created.";
        }

        if (decimal.Round(feePercent, 2) != feePercent || feePercent is < 0m or > 100m)
        {
            return "Fee percentage must be between 0 and 100 with at most two decimal places.";
        }

        return null;
    }

    public static string? ValidateDeposit(SessionPlayerDto? player, long amountGil)
    {
        if (player is null || player.State != SessionPlayerState.Open)
        {
            return "Select an Open session member.";
        }

        return amountGil <= 0
            ? "Deposit must be a positive whole gil amount."
            : null;
    }

    public static string? ValidateBalanceAdjustment(long amountGil) =>
        amountGil is 0 or long.MinValue ? "Balance adjustment must be non-zero and representable." : null;

    public static string? ValidateInviteBalance(long amountGil) =>
        amountGil < 0 ? "Invite balance must be zero or a positive whole gil amount." : null;

    public static string? ValidateBetLimits(long minBet, long maxBet)
    {
        if (minBet < 0)
        {
            return "Minimum bet cannot be negative.";
        }

        if (maxBet < 1 || maxBet < minBet)
        {
            return "Maximum bet must be positive and at least the minimum bet.";
        }

        return null;
    }

    // Texas Hold'em tables have a fixed number of seats, so the cap is checked against it here to give the
    // dealer an immediate, readable message. The backend clamps anything larger anyway — this is only the
    // early feedback, never the enforcement.
    public static string? ValidateMaxPlayers(int? maxPlayers, string? gameType = null)
    {
        if (maxPlayers is < 1)
        {
            return "Maximum players must be at least 1 when set.";
        }

        if (string.Equals(gameType, "holdem", StringComparison.OrdinalIgnoreCase) && maxPlayers > HoldemBetDefaults.MaxSeats)
        {
            return $"Texas Hold'em supports at most {HoldemBetDefaults.MaxSeats} players.";
        }

        return null;
    }

    // The player cap a create-session request should carry for this game. Hold'em has no "unlimited": an
    // empty field means a full table, so the value the dealer sees matches what the backend stores.
    public static int? ResolveMaxPlayers(int? maxPlayers, string gameType) =>
        string.Equals(gameType, "holdem", StringComparison.OrdinalIgnoreCase)
            ? Math.Min(maxPlayers ?? HoldemBetDefaults.MaxSeats, HoldemBetDefaults.MaxSeats)
            : maxPlayers;

    // Parses the optional player cap from the create-session field. Empty/whitespace = unlimited (null).
    // A set value must be a whole number >= 1.
    public static bool TryParseMaxPlayers(string text, out int? maxPlayers)
    {
        maxPlayers = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            return false;
        }

        maxPlayers = value;
        return true;
    }

    public static bool TryParseFee(string text, out decimal fee) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out fee);

    // A zero-or-positive gil amount typed the way a dealer thinks: the same k/m/b shorthand and grouping
    // separators the balance adjustment box accepts ("2.5m" = 2,500,000), so the invite balance does not
    // need a different habit from the box right next to it.
    public static bool TryParseGilAmount(string text, out long amount) =>
        TryParseShorthandGil(text, out amount) && amount >= 0;

    // Parses a signed, non-zero whole-gil balance adjustment. Accepts a case-insensitive k/m/b
    // shorthand suffix (250k = 250,000; 5m = 5,000,000; -1.5m = -1,500,000) and tolerates grouping
    // separators (dots/commas/spaces) the dealer may have typed. Rejects non-whole-gil results.
    public static bool TryParseBalanceAdjustment(string text, out long amount) =>
        TryParseShorthandGil(text, out amount) && amount is not (0 or long.MinValue);

    // The shared shorthand reader. Signed and allows zero; callers narrow it (an adjustment must be
    // non-zero, an invite balance must not be negative).
    private static bool TryParseShorthandGil(string text, out long amount)
    {
        amount = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        long multiplier = 1;
        var last = char.ToLowerInvariant(trimmed[^1]);
        if (last is 'k' or 'm' or 'b')
        {
            multiplier = last switch { 'k' => 1_000L, 'm' => 1_000_000L, _ => 1_000_000_000L };
            trimmed = trimmed[..^1].Trim();
        }

        var normalized = trimmed.Replace(" ", string.Empty);
        if (multiplier == 1)
        {
            // No suffix: treat dots/commas as thousands grouping and require a whole integer.
            normalized = normalized.Replace(".", string.Empty).Replace(",", string.Empty);
            if (!long.TryParse(normalized, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount))
            {
                return false;
            }
        }
        else
        {
            // With a suffix, the value may be fractional (1.5m); the product must be whole gil.
            if (!decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            var product = value * multiplier;
            if (product != decimal.Truncate(product) || product < long.MinValue || product > long.MaxValue)
            {
                return false;
            }

            amount = (long)product;
        }

        return true;
    }

    [GeneratedRegex("^[\\p{L}][\\p{L}'-]{1,14} [\\p{L}][\\p{L}'-]{1,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex CharacterNameRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9-]{1,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex WorldNameRegex();
}
