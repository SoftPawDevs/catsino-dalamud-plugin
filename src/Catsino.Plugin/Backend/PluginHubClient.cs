using Catsino.Plugin.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace Catsino.Plugin.Backend;

public sealed class PluginHubClient : IAsyncDisposable
{
    private readonly HubConnection connection;

    public PluginHubClient(Uri apiBaseUri, CatsinoApiClient api)
    {
        connection = new HubConnectionBuilder()
            .WithUrl(new Uri(apiBaseUri, PluginHubProtocol.Path), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(api.GetAccessToken());
            })
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
            .Build();

        connection.Reconnecting += _ =>
        {
            ConnectionChanged?.Invoke(false);
            return Task.CompletedTask;
        };
        connection.Reconnected += _ =>
        {
            ConnectionChanged?.Invoke(true);
            return Task.CompletedTask;
        };
        connection.Closed += _ =>
        {
            ConnectionChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        connection.On(PluginHubProtocol.RefreshDealerSessions, () => RefreshDealerSessions?.Invoke());
        connection.On<PayoutLegDto>(PluginHubProtocol.QueuePayoutLeg, leg => QueuePayoutLeg?.Invoke(leg));
        connection.On<CancelPayoutOperationDto>(PluginHubProtocol.CancelPayoutOperation, request => CancelPayoutOperation?.Invoke(request));
        connection.On<ReconciliationRequestDto>(PluginHubProtocol.RequestPayoutReconciliation, request => RequestPayoutReconciliation?.Invoke(request));
        connection.On<Guid>(PluginHubProtocol.SessionClosed, sessionId => SessionClosed?.Invoke(sessionId));
        connection.On<string>(PluginHubProtocol.DealerAuthorizationRevoked, reason => DealerAuthorizationRevoked?.Invoke(reason));
        connection.On<string>(PluginHubProtocol.ReconnectRequired, reason => ReconnectRequired?.Invoke(reason));
    }

    public event Action? RefreshDealerSessions;
    public event Action<PayoutLegDto>? QueuePayoutLeg;
    public event Action<CancelPayoutOperationDto>? CancelPayoutOperation;
    public event Action<ReconciliationRequestDto>? RequestPayoutReconciliation;
    public event Action<Guid>? SessionClosed;
    public event Action<string>? DealerAuthorizationRevoked;
    public event Action<string>? ReconnectRequired;
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => connection.State == HubConnectionState.Connected;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (connection.State == HubConnectionState.Disconnected)
        {
            await connection.StartAsync(cancellationToken).ConfigureAwait(false);
            ConnectionChanged?.Invoke(true);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (connection.State != HubConnectionState.Disconnected)
        {
            await connection.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        ConnectionChanged?.Invoke(false);
    }

    public Task ReportDepositStatusAsync(Guid sessionId, Guid playerId, Guid idempotencyKey, string status, string? errorCode = null) =>
        SendIfConnectedAsync(PluginHubProtocol.ReportDepositStatus, sessionId, playerId, idempotencyKey, status, errorCode);

    public Task ReportDropboxStatusAsync(DropboxStatusDto status) => SendIfConnectedAsync(PluginHubProtocol.ReportDropboxStatus, status);

    public Task ReportOutgoingTradeStatusAsync(PayoutOperationDto status) => SendIfConnectedAsync(PluginHubProtocol.ReportOutgoingTradeStatus, status);

    public Task ReportOutboxStatusAsync(int pendingEvents) =>
        SendIfConnectedAsync(PluginHubProtocol.ReportOutboxStatus, pendingEvents, DateTimeOffset.UtcNow);

    public async ValueTask DisposeAsync()
    {
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private Task SendIfConnectedAsync(string methodName, params object?[] arguments) =>
        IsConnected ? connection.SendCoreAsync(methodName, arguments) : Task.CompletedTask;
}
