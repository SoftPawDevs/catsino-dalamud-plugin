using System.Text.Json;
using Catsino.Dropbox.Contracts;
using Catsino.Plugin.Payout;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Catsino.Plugin.Dropbox;

public sealed class DalamudDropboxPayoutClient : IDropboxPayoutClient
{
    private Guid? lastOperationId;
    private bool disposed;
    private readonly ICallGateSubscriber<string> getVersion;
    private readonly ICallGateSubscriber<string> getCapabilities;
    private readonly ICallGateSubscriber<bool> supportsLanguageIndependentTradeState;
    private readonly ICallGateSubscriber<Guid, bool> enablePayoutMode;
    private readonly ICallGateSubscriber<Guid, bool> disablePayoutMode;
    private readonly ICallGateSubscriber<Guid, string, string, long, bool> queueOutgoingGilTrade;
    private readonly ICallGateSubscriber<Guid, bool> cancelOutgoingTrade;
    private readonly ICallGateSubscriber<Guid, string> getTradeOperation;
    private readonly IReadOnlyList<ICallGateSubscriber<string, object?>> eventSubscribers;

    public DalamudDropboxPayoutClient(IDalamudPluginInterface pluginInterface)
    {
        getVersion = pluginInterface.GetIpcSubscriber<string>(DropboxPayoutContract.GetVersion);
        getCapabilities = pluginInterface.GetIpcSubscriber<string>(DropboxPayoutContract.GetCapabilities);
        supportsLanguageIndependentTradeState = pluginInterface.GetIpcSubscriber<bool>(DropboxPayoutContract.SupportsLanguageIndependentTradeState);
        enablePayoutMode = pluginInterface.GetIpcSubscriber<Guid, bool>(DropboxPayoutContract.EnablePayoutMode);
        disablePayoutMode = pluginInterface.GetIpcSubscriber<Guid, bool>(DropboxPayoutContract.DisablePayoutMode);
        queueOutgoingGilTrade = pluginInterface.GetIpcSubscriber<Guid, string, string, long, bool>(DropboxPayoutContract.QueueOutgoingGilTrade);
        cancelOutgoingTrade = pluginInterface.GetIpcSubscriber<Guid, bool>(DropboxPayoutContract.CancelOutgoingTrade);
        getTradeOperation = pluginInterface.GetIpcSubscriber<Guid, string>(DropboxPayoutContract.GetTradeOperation);
        eventSubscribers =
        [
            pluginInterface.GetIpcSubscriber<string, object?>(DropboxPayoutContract.PlayerDetected),
            pluginInterface.GetIpcSubscriber<string, object?>(DropboxPayoutContract.TradeOpened),
            pluginInterface.GetIpcSubscriber<string, object?>(DropboxPayoutContract.TradeLocked),
            pluginInterface.GetIpcSubscriber<string, object?>(DropboxPayoutContract.TradeCompleted),
            pluginInterface.GetIpcSubscriber<string, object?>(DropboxPayoutContract.TradeCancelled),
            pluginInterface.GetIpcSubscriber<string, object?>(DropboxPayoutContract.TradeFailed),
            pluginInterface.GetIpcSubscriber<string, object?>(DropboxPayoutContract.TradeTimedOut),
        ];

        foreach (var subscriber in eventSubscribers)
        {
            subscriber.Subscribe(HandleEvent);
        }
    }

    public event Action<DropboxTradeEvent>? TradeEventReceived;

    public DropboxCompatibility Probe()
    {
        try
        {
            var version = JsonSerializer.Deserialize<DropboxVersionInfo>(getVersion.InvokeFunc(), DropboxContractJson.Options)
                ?? throw new InvalidDataException("Dropbox returned an empty version.");
            var capabilities = JsonSerializer.Deserialize<string[]>(getCapabilities.InvokeFunc(), DropboxContractJson.Options) ?? [];
            DropboxTradeOperation? activeOperation = lastOperationId is Guid operationId
                ? GetTradeOperation(operationId)
                : null;
            return new DropboxCompatibility(
                true,
                version,
                capabilities,
                supportsLanguageIndependentTradeState.InvokeFunc(),
                activeOperation);
        }
        catch
        {
            return new DropboxCompatibility(false, null, [], false, null);
        }
    }

    public bool EnablePayoutMode(Guid sessionId) => enablePayoutMode.InvokeFunc(sessionId);

    public bool DisablePayoutMode(Guid sessionId) => disablePayoutMode.InvokeFunc(sessionId);

    public bool QueueOutgoingGilTrade(Guid operationId, string characterName, string homeWorld, long amountGil)
    {
        var accepted = queueOutgoingGilTrade.InvokeFunc(operationId, characterName, homeWorld, amountGil);
        if (accepted)
        {
            lastOperationId = operationId;
        }

        return accepted;
    }

    public bool CancelOutgoingTrade(Guid operationId) => cancelOutgoingTrade.InvokeFunc(operationId);

    public DropboxTradeOperation? GetTradeOperation(Guid operationId)
    {
        var json = getTradeOperation.InvokeFunc(operationId);
        var operation = string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<DropboxTradeOperation>(json, DropboxContractJson.Options);
        if (operation is not null)
        {
            lastOperationId = operationId;
        }

        return operation;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var subscriber in eventSubscribers)
        {
            subscriber.Unsubscribe(HandleEvent);
        }

        TradeEventReceived = null;
    }

    private void HandleEvent(string json)
    {
        try
        {
            var tradeEvent = JsonSerializer.Deserialize<DropboxTradeEvent>(json, DropboxContractJson.Options);
            if (tradeEvent is not null)
            {
                lastOperationId = tradeEvent.OperationId;
                TradeEventReceived?.Invoke(tradeEvent);
            }
        }
        catch (JsonException)
        {
            // Invalid IPC payloads are ignored and never reach the financial outbox.
        }
    }
}
