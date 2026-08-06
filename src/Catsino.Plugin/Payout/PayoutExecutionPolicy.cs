using Catsino.Plugin.Contracts;
using Catsino.Plugin.Security;

namespace Catsino.Plugin.Payout;

public static class PayoutExecutionPolicy
{
    public const long MaximumTradeGil = 1_000_000;

    public static string? Validate(PayoutLegDto leg, bool backendConnected, PayoutExecutorReadiness executor)
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

        if (leg.AmountGil is < 1 or > MaximumTradeGil)
        {
            return $"Payout gil must be between 1 and {MaximumTradeGil}.";
        }

        if (!executor.IsReady || executor.ExecutorInstanceId == Guid.Empty)
        {
            return "The built-in payout executor is unavailable.";
        }

        if (executor.ActiveOperation is not null && executor.ActiveOperation.State is not
            (PayoutTradeState.Completed or PayoutTradeState.Cancelled or PayoutTradeState.Failed or PayoutTradeState.ReconciliationRequired))
        {
            return "The payout executor already has an active payout operation.";
        }

        return null;
    }
}
