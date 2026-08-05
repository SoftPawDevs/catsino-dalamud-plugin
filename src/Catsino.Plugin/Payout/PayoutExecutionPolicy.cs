using Catsino.Dropbox.Contracts;
using Catsino.Plugin.Contracts;
using Catsino.Plugin.Security;

namespace Catsino.Plugin.Payout;

public sealed record DropboxCompatibility(
    bool IsAvailable,
    DropboxVersionInfo? Version,
    IReadOnlyList<string> Capabilities,
    bool SupportsLanguageIndependentTradeState,
    DropboxTradeOperation? ActiveOperation);

public static class PayoutExecutionPolicy
{
    public static string? Validate(PayoutLegDto leg, bool backendConnected, DropboxCompatibility dropbox)
    {
        if (!backendConnected)
        {
            return "The backend must be connected before a payout can start.";
        }

        if (leg.OperationId == Guid.Empty || leg.LegId == Guid.Empty || leg.SessionId == Guid.Empty)
        {
            return "The backend payout operation has an invalid identity.";
        }

        if (leg.IssuedAt.Offset != TimeSpan.Zero)
        {
            return "The backend payout timestamp must be UTC.";
        }

        var identityError = DealerInputValidator.ValidateCharacter(leg.CharacterName, leg.HomeWorld);
        if (identityError is not null)
        {
            return identityError;
        }

        if (leg.AmountGil is < 1 or > DropboxPayoutContract.MaximumGil)
        {
            return $"Payout gil must be between 1 and {DropboxPayoutContract.MaximumGil}.";
        }

        if (!dropbox.IsAvailable || dropbox.Version is null)
        {
            return "Dropbox payout IPC is unavailable.";
        }

        if (!string.Equals(leg.RequiredDropboxIpcVersion, DropboxPayoutContract.IpcVersion, StringComparison.Ordinal) ||
            !string.Equals(dropbox.Version.IpcVersion, DropboxPayoutContract.IpcVersion, StringComparison.Ordinal))
        {
            return "The Dropbox IPC version is not exactly supported.";
        }

        if (!string.Equals(leg.RequiredDropboxBuildVersion, DropboxPayoutContract.SupportedBuildVersion, StringComparison.Ordinal) ||
            !string.Equals(dropbox.Version.BuildVersion, DropboxPayoutContract.SupportedBuildVersion, StringComparison.Ordinal))
        {
            return "The Dropbox build is not exactly supported.";
        }

        if (!dropbox.SupportsLanguageIndependentTradeState)
        {
            return "Dropbox does not provide language-independent trade state.";
        }

        if (DropboxPayoutContract.RequiredCapabilities.Any(required => !dropbox.Capabilities.Contains(required, StringComparer.Ordinal)))
        {
            return "Dropbox is missing a required payout capability.";
        }

        if (dropbox.ActiveOperation is not null && dropbox.ActiveOperation.State is not
            (DropboxTradeState.Completed or DropboxTradeState.Cancelled or DropboxTradeState.Failed or DropboxTradeState.ReconciliationRequired))
        {
            return "Dropbox already has an active payout operation.";
        }

        return null;
    }
}
