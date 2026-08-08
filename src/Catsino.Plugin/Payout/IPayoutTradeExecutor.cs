using Catsino.Plugin.Contracts;

namespace Catsino.Plugin.Payout;

public interface IPayoutTradeExecutor : IDisposable
{
    event Action<PayoutTradeEvent>? TradeEventReceived;

    PayoutExecutorReadiness Probe();

    bool StartOperation(PayoutLegDto leg);

    bool CancelOperation(Guid operationId);

    PayoutTradeOperation? GetOperation(Guid operationId);

    // Signals that the operation's TradeOpened event has been durably persisted. The executor must not
    // move gil (confirm the trade) before receiving this, so a crash can never leave a physically
    // completed trade with no durable trace that recovery could re-run.
    void MarkOpenEventPersisted(Guid operationId);
}
