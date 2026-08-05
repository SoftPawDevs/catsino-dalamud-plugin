namespace Catsino.Plugin.Backend;

public static class PluginHubProtocol
{
    public const string Path = "/hubs/plugin";
    public const string RefreshDealerSessions = "RefreshDealerSessions";
    public const string QueuePayoutLeg = "QueuePayoutLeg";
    public const string CancelPayoutOperation = "CancelPayoutOperation";
    public const string RequestPayoutReconciliation = "RequestPayoutReconciliation";
    public const string SessionClosed = "SessionClosed";
    public const string DealerAuthorizationRevoked = "DealerAuthorizationRevoked";
    public const string ReconnectRequired = "ReconnectRequired";
    public const string ReportDepositStatus = "ReportDepositStatus";
    public const string ReportDropboxStatus = "ReportDropboxStatus";
    public const string ReportOutgoingTradeStatus = "ReportOutgoingTradeStatus";
    public const string ReportOutboxStatus = "ReportOutboxStatus";

    public static IReadOnlyList<string> ServerToPluginEvents { get; } =
    [
        RefreshDealerSessions,
        QueuePayoutLeg,
        CancelPayoutOperation,
        RequestPayoutReconciliation,
        SessionClosed,
        DealerAuthorizationRevoked,
        ReconnectRequired,
    ];

    public static IReadOnlyList<string> PluginToServerReports { get; } =
    [
        ReportDepositStatus,
        ReportDropboxStatus,
        ReportOutgoingTradeStatus,
        ReportOutboxStatus,
    ];
}
