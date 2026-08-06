using Catsino.Plugin.Contracts;
using Catsino.Plugin.Security;

namespace Catsino.Plugin.Workflow;

public readonly record struct SessionPlayerKey(Guid SessionId, Guid MembershipId);

public enum DealerActionState
{
    PendingConfirmation,
    Sending,
    Failed,
    Succeeded,
}

public sealed class BalanceAdjustmentSubmission
{
    public BalanceAdjustmentSubmission(SessionPlayerKey player, long amountGil, Guid? idempotencyKey = null)
    {
        if (player.SessionId == Guid.Empty || player.MembershipId == Guid.Empty || amountGil is 0 or long.MinValue)
        {
            throw new ArgumentException("A session, membership, and non-zero signed amount are required.");
        }

        Player = player;
        AmountGil = amountGil;
        IdempotencyKey = idempotencyKey ?? Guid.NewGuid();
    }

    public SessionPlayerKey Player { get; }

    public long AmountGil { get; }

    public Guid IdempotencyKey { get; }

    public DealerActionState State { get; private set; } = DealerActionState.PendingConfirmation;

    public string? ErrorMessage { get; private set; }

    public bool CanDiscardFailure { get; private set; }

    public void MarkSending()
    {
        if (State is not (DealerActionState.PendingConfirmation or DealerActionState.Failed))
        {
            throw new InvalidOperationException("Only a pending or failed adjustment can be sent.");
        }

        State = DealerActionState.Sending;
        ErrorMessage = null;
        CanDiscardFailure = false;
    }

    public void MarkFailed(string message, bool canDiscard = false)
    {
        State = DealerActionState.Failed;
        ErrorMessage = SecretRedactor.Redact(message);
        CanDiscardFailure = canDiscard;
    }

    public void MarkSucceeded() => State = DealerActionState.Succeeded;
}

public sealed class CashOutSubmission
{
    public CashOutSubmission(SessionPlayerKey player, CashOutPreviewResponse preview, Guid? idempotencyKey = null)
    {
        Player = player;
        Preview = preview;
        IdempotencyKey = idempotencyKey ?? Guid.NewGuid();
    }

    public SessionPlayerKey Player { get; }

    public CashOutPreviewResponse Preview { get; }

    public Guid IdempotencyKey { get; }

    public DealerActionState State { get; private set; } = DealerActionState.PendingConfirmation;

    public string? ErrorMessage { get; private set; }

    public bool CanDiscardFailure { get; private set; }

    public void MarkSending()
    {
        if (State is not (DealerActionState.PendingConfirmation or DealerActionState.Failed))
        {
            throw new InvalidOperationException("Only a pending or failed cash out can be sent.");
        }

        State = DealerActionState.Sending;
        ErrorMessage = null;
        CanDiscardFailure = false;
    }

    public void MarkFailed(string message, bool canDiscard = false)
    {
        State = DealerActionState.Failed;
        ErrorMessage = SecretRedactor.Redact(message);
        CanDiscardFailure = canDiscard;
    }

    public void MarkSucceeded() => State = DealerActionState.Succeeded;
}

public sealed class SessionActionDraftStore
{
    private readonly object sync = new();
    private readonly Dictionary<SessionPlayerKey, string> balanceAdjustments = [];
    private readonly Dictionary<SessionPlayerKey, bool> netZeroConfirmations = [];

    public string GetBalanceAdjustment(SessionPlayerKey player)
    {
        lock (sync)
        {
            return balanceAdjustments.GetValueOrDefault(player, string.Empty);
        }
    }

    public void SetBalanceAdjustment(SessionPlayerKey player, string value)
    {
        lock (sync)
        {
            balanceAdjustments[player] = value;
        }
    }

    public bool GetNetZeroConfirmation(SessionPlayerKey player)
    {
        lock (sync)
        {
            return netZeroConfirmations.GetValueOrDefault(player);
        }
    }

    public void SetNetZeroConfirmation(SessionPlayerKey player, bool value)
    {
        lock (sync)
        {
            netZeroConfirmations[player] = value;
        }
    }

    public void Remove(SessionPlayerKey player)
    {
        lock (sync)
        {
            balanceAdjustments.Remove(player);
            netZeroConfirmations.Remove(player);
        }
    }

    public void RemoveSession(Guid sessionId)
    {
        lock (sync)
        {
            foreach (var player in balanceAdjustments.Keys.Where(item => item.SessionId == sessionId).ToArray())
            {
                balanceAdjustments.Remove(player);
            }

            foreach (var player in netZeroConfirmations.Keys.Where(item => item.SessionId == sessionId).ToArray())
            {
                netZeroConfirmations.Remove(player);
            }
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            balanceAdjustments.Clear();
            netZeroConfirmations.Clear();
        }
    }
}
