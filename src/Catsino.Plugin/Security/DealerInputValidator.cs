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

        if (feePercent is < 0m or > 100m)
        {
            return "Fee percentage must be between 0 and 100.";
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

    public static bool TryParseFee(string text, out decimal fee) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out fee);

    public static bool TryParseGil(string text, out long amount) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out amount);

    public static bool TryParseBalanceAdjustment(string text, out long amount) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount) && amount is not (0 or long.MinValue);

    [GeneratedRegex("^[\\p{L}][\\p{L}'-]{1,14} [\\p{L}][\\p{L}'-]{1,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex CharacterNameRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9-]{1,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex WorldNameRegex();
}
