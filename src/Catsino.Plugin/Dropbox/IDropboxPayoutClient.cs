using Catsino.Dropbox.Contracts;
using Catsino.Plugin.Payout;

namespace Catsino.Plugin.Dropbox;

public interface IDropboxPayoutClient : IDisposable
{
    event Action<DropboxTradeEvent>? TradeEventReceived;

    DropboxCompatibility Probe();

    bool EnablePayoutMode(Guid sessionId);

    bool DisablePayoutMode(Guid sessionId);

    bool QueueOutgoingGilTrade(Guid operationId, string characterName, string homeWorld, long amountGil);

    bool CancelOutgoingTrade(Guid operationId);

    DropboxTradeOperation? GetTradeOperation(Guid operationId);
}
