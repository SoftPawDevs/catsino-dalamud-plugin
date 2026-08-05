using System.Text.RegularExpressions;

namespace Catsino.Plugin.Security;

public static partial class SecretRedactor
{
    public const string Redacted = "[REDACTED]";

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var redacted = BearerRegex().Replace(value, $"Bearer {Redacted}");
        redacted = JwtRegex().Replace(redacted, Redacted);
        redacted = CredentialRegex().Replace(redacted, "$1=" + Redacted);
        return redacted;
    }

    [GeneratedRegex("Bearer\\s+[^\\s,;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex("eyJ[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();

    [GeneratedRegex("(activationJwt|accessToken|refreshCredential|token)\\s*[=:]\\s*[^\\s,;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialRegex();
}
