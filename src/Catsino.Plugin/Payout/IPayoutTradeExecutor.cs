using Catsino.Plugin.Contracts;

namespace Catsino.Plugin.Payout;

public interface IPayoutTradeExecutor : IDisposable
{
    event Action<PayoutTradeEvent>? TradeEventReceived;

    PayoutExecutorReadiness Probe();

    bool StartOperation(PayoutLegDto leg);

    bool CancelOperation(Guid operationId);

    PayoutTradeOperation? GetOperation(Guid operationId);
}
