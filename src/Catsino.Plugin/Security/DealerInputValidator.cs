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

    public static string? ValidateMaxPlayers(int? maxPlayers) =>
        maxPlayers is < 1 ? "Maximum players must be at least 1 when set." : null;

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

    public static bool TryParseGil(string text, out long amount) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out amount);

    // Parses a signed, non-zero whole-gil balance adjustment. Accepts a case-insensitive k/m/b
    // shorthand suffix (250k = 250,000; 5m = 5,000,000; -1.5m = -1,500,000) and tolerates grouping
    // separators (dots/commas/spaces) the dealer may have typed. Rejects non-whole-gil results.
    public static bool TryParseBalanceAdjustment(string text, out long amount)
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

        return amount is not (0 or long.MinValue);
    }

    [GeneratedRegex("^[\\p{L}][\\p{L}'-]{1,14} [\\p{L}][\\p{L}'-]{1,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex CharacterNameRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9-]{1,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex WorldNameRegex();
}
