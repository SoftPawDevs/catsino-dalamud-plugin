using Catsino.Plugin.Security;

namespace Catsino.Plugin.Workflow;

public enum DepositSubmissionState
{
    PendingConfirmation,
    Sending,
    Succeeded,
    Failed,
}

public sealed class DepositSubmission
{
    public DepositSubmission(Guid sessionId, Guid playerId, long amountGil, Guid? idempotencyKey = null)
    {
        if (sessionId == Guid.Empty || playerId == Guid.Empty || amountGil <= 0)
        {
            throw new ArgumentException("A session, player, and positive amount are required.");
        }

        SessionId = sessionId;
        PlayerId = playerId;
        AmountGil = amountGil;
        IdempotencyKey = idempotencyKey ?? Guid.NewGuid();
    }

    public Guid SessionId { get; }

    public Guid PlayerId { get; }

    public long AmountGil { get; }

    public Guid IdempotencyKey { get; }

    public DepositSubmissionState State { get; private set; } = DepositSubmissionState.PendingConfirmation;

    public string? ResultMessage { get; private set; }

    public void MarkSending()
    {
        if (State is not (DepositSubmissionState.PendingConfirmation or DepositSubmissionState.Failed))
        {
            throw new InvalidOperationException("Only a pending or failed deposit can be sent.");
        }

        State = DepositSubmissionState.Sending;
        ResultMessage = null;
    }

    public void MarkSucceeded(string message)
    {
        State = DepositSubmissionState.Succeeded;
        ResultMessage = message;
    }

    public void MarkFailed(string message)
    {
        State = DepositSubmissionState.Failed;
        ResultMessage = SecretRedactor.Redact(message);
    }
}
