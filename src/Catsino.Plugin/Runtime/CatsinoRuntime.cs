using Catsino.Dropbox.Contracts;
using Catsino.Plugin.Backend;
using Catsino.Plugin.Configuration;
using Catsino.Plugin.Contracts;
using Catsino.Plugin.Dropbox;
using Catsino.Plugin.Payout;
using Catsino.Plugin.Security;
using Catsino.Plugin.Workflow;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Catsino.Plugin.Runtime;

public sealed class CatsinoRuntime : IAsyncDisposable
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPlayerState playerState;
    private readonly IFramework framework;
    private readonly ICommandManager commandManager;
    private readonly IPluginLog pluginLog;
    private readonly PluginConfiguration configuration;
    private readonly IDropboxPayoutClient dropbox;
    private readonly CatsinoApiClient api;
    private readonly PluginHubClient hub;
    private readonly IPayoutOutbox outbox;
    private readonly PayoutCoordinator payoutCoordinator;
    private readonly LogicalIdempotencyKeys financialKeys = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private DateTimeOffset nextIdentityCheck = DateTimeOffset.MinValue;
    private DateTimeOffset nextHeartbeat = DateTimeOffset.MinValue;
    private CharacterIdentityDto character = new(string.Empty, string.Empty, string.Empty, false);
    private bool disposed;

    public CatsinoRuntime(
        IDalamudPluginInterface pluginInterface,
        IPlayerState playerState,
        IFramework framework,
        ICommandManager commandManager,
        IPluginLog pluginLog)
    {
        this.pluginInterface = pluginInterface;
        this.playerState = playerState;
        this.framework = framework;
        this.commandManager = commandManager;
        this.pluginLog = pluginLog;
        configuration = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        if (configuration.DeviceId == Guid.Empty)
        {
            configuration.DeviceId = Guid.NewGuid();
        }

        pluginInterface.SavePluginConfig(configuration);
        character = ReadCharacter();
        dropbox = new DalamudDropboxPayoutClient(pluginInterface);

        var configDirectory = pluginInterface.GetPluginConfigDirectory();
        var credentialStore = new DpapiCredentialStore(Path.Combine(configDirectory, "credentials.dat"));
        outbox = new PersistentPayoutOutbox(Path.Combine(configDirectory, "outbox"));
        var apiBaseUri = new Uri(configuration.ApiBaseUrl, UriKind.Absolute);
        api = new CatsinoApiClient(apiBaseUri, credentialStore, () => Character, GetDropboxCapabilities, configuration.DeviceId);
        hub = new PluginHubClient(apiBaseUri, api);
        payoutCoordinator = new PayoutCoordinator(
            dropbox,
            outbox,
            new BackendPayoutEventTransport(api),
            () => hub.IsConnected,
            SetStatus);

        hub.RefreshDealerSessions += () => RunBackground(RefreshSessionsAsync);
        hub.QueuePayoutLeg += leg => RunBackground(token => payoutCoordinator.StartBackendLegAsync(leg, token));
        hub.CancelPayoutOperation += request => RunBackground(token => payoutCoordinator.CancelFromBackendAsync(request, token));
        hub.RequestPayoutReconciliation += request =>
        {
            LastReconciliationRequest = request;
            SetStatus("The backend requested dealer payout reconciliation.");
        };
        hub.SessionClosed += _ => RunBackground(RefreshSessionsAsync);
        hub.DealerAuthorizationRevoked += reason => RunBackground(token => RevokeAuthorizationAsync(reason, token));
        hub.ReconnectRequired += reason => RunBackground(token => ReconnectHubAsync(reason, token));
        hub.ConnectionChanged += connected =>
        {
            if (connected)
            {
                RunBackground(payoutCoordinator.ReplayOutboxAsync);
            }
        };

        framework.Update += OnFrameworkUpdate;
        if (character.IsLoggedIn)
        {
            RunBackground(RestoreAuthorizationAsync);
        }
    }

    public CharacterIdentityDto Character => character;

    public bool IsAuthorized => api.IsAuthorized;

    public bool IsBackendConnected => hub.IsConnected;

    public string StatusMessage { get; private set; } = "Not authorized.";

    public DateTimeOffset? LastHeartbeatAt { get; private set; }

    public IReadOnlyList<GameSessionDto> Sessions { get; private set; } = [];

    public GameSessionDto? SelectedSession { get; private set; }

    public IReadOnlyList<SessionPlayerDto> Players { get; private set; } = [];

    public SessionPlayerDto? SelectedPlayer { get; private set; }

    public IReadOnlyList<PayoutOperationDto> OpenPayoutOperations { get; private set; } = [];

    public PayoutOperationDto? ActivePayout => payoutCoordinator.ActiveOperation;

    public ReconciliationRequestDto? LastReconciliationRequest { get; private set; }

    public DepositSubmission? PendingDeposit { get; private set; }

    public DepositSubmission? RecentDeposit { get; private set; }

    public DropboxCapabilitiesDto DropboxCapabilities => GetDropboxCapabilities();

    public int PendingOutboxEvents { get; private set; }

    public async Task AuthorizeAsync(string activationJwt, CancellationToken cancellationToken = default)
    {
        if (!Character.IsLoggedIn)
        {
            throw new InvalidOperationException("Log in before authorizing this dealer client.");
        }

        await api.AuthorizeAsync(activationJwt, cancellationToken).ConfigureAwait(false);
        await ConnectAuthorizedClientAsync(cancellationToken).ConfigureAwait(false);
        SetStatus($"Authorized for {Character.CharacterName}@{Character.HomeWorld}.");
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await hub.StopAsync(cancellationToken).ConfigureAwait(false);
        await api.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        ClearDealerState();
        SetStatus("Dealer authorization disconnected.");
    }

    public async Task RefreshSessionsAsync(CancellationToken cancellationToken = default)
    {
        RequireAuthorization();
        Sessions = await api.GetSessionsAsync(cancellationToken).ConfigureAwait(false);
        var selectedId = SelectedSession?.SessionId;
        SelectedSession = selectedId is null
            ? await api.GetActiveSessionAsync(cancellationToken).ConfigureAwait(false)
            : Sessions.FirstOrDefault(item => item.SessionId == selectedId);
        if (SelectedSession is not null)
        {
            await LoadPlayersAsync(SelectedSession.SessionId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Players = [];
            SelectedPlayer = null;
        }

        OpenPayoutOperations = await api.GetOpenPayoutOperationsAsync(cancellationToken).ConfigureAwait(false);
        SetStatus("Dealer sessions refreshed.");
    }

    public async Task SelectSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        SelectedSession = await api.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await LoadPlayersAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    public void SelectPlayer(Guid playerId)
    {
        SelectedPlayer = Players.FirstOrDefault(item => item.PlayerId == playerId);
        PendingDeposit = null;
    }

    public async Task CreatePlinkoSessionAsync(decimal feePercent, CancellationToken cancellationToken = default)
    {
        var feeError = DealerInputValidator.ValidateFee(feePercent, GameSessionState.Created);
        if (feeError is not null)
        {
            throw new InvalidOperationException(feeError);
        }

        var logicalOperation = $"session:create:plinko:{feePercent.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var key = financialKeys.GetOrCreate(logicalOperation);
        SelectedSession = await api.CreateSessionAsync(new CreateGameSessionRequest("plinko", feePercent), key, cancellationToken).ConfigureAwait(false);
        financialKeys.Complete(logicalOperation, key);
        await RefreshSessionsAsync(cancellationToken).ConfigureAwait(false);
        SetStatus("Created a Plinko session.");
    }

    public async Task UpdateFeeAsync(decimal feePercent, CancellationToken cancellationToken = default)
    {
        var session = SelectedSession ?? throw new InvalidOperationException("Select a session.");
        var error = DealerInputValidator.ValidateFee(feePercent, session.State);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        var logicalOperation = $"session:{session.SessionId:D}:fee:{feePercent.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var key = financialKeys.GetOrCreate(logicalOperation);
        SelectedSession = await api.UpdateSessionFeeAsync(session.SessionId, new UpdateSessionFeeRequest(feePercent), key, cancellationToken).ConfigureAwait(false);
        financialKeys.Complete(logicalOperation, key);
        SetStatus("Session fee updated.");
    }

    public async Task OpenSessionAsync(CancellationToken cancellationToken = default)
    {
        var session = SelectedSession ?? throw new InvalidOperationException("Select a session.");
        var logicalOperation = $"session:{session.SessionId:D}:open";
        var key = financialKeys.GetOrCreate(logicalOperation);
        SelectedSession = await api.OpenSessionAsync(session.SessionId, key, cancellationToken).ConfigureAwait(false);
        financialKeys.Complete(logicalOperation, key);
        await RefreshSessionsAsync(cancellationToken).ConfigureAwait(false);
        SetStatus("Session opened.");
    }

    public async Task CloseSessionAsync(CancellationToken cancellationToken = default)
    {
        var session = SelectedSession ?? throw new InvalidOperationException("Select a session.");
        var logicalOperation = $"session:{session.SessionId:D}:close";
        var key = financialKeys.GetOrCreate(logicalOperation);
        SelectedSession = await api.CloseSessionAsync(session.SessionId, key, cancellationToken).ConfigureAwait(false);
        financialKeys.Complete(logicalOperation, key);
        await RefreshSessionsAsync(cancellationToken).ConfigureAwait(false);
        SetStatus("Session closed.");
    }

    public async Task CreateInviteAndTellAsync(string characterName, string homeWorld, CancellationToken cancellationToken = default)
    {
        var session = SelectedSession ?? throw new InvalidOperationException("Select a session.");
        var error = DealerInputValidator.ValidateCharacter(characterName, homeWorld);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        var invite = await api.CreateInviteAsync(
            session.SessionId,
            new CreateInviteRequest(characterName, homeWorld),
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(invite.InviteUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            invite.InviteUrl.OriginalString.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException("The backend returned an unsafe invite URL.");
        }

        var processed = commandManager.ProcessCommand($"/tell {characterName}@{homeWorld} {invite.InviteUrl.AbsoluteUri}");
        SetStatus(processed
            ? "The invite tell command was processed locally; delivery is not confirmed."
            : "The invite was created, but the tell command was not processed locally.");
    }

    public void PrepareDeposit(long amountGil)
    {
        var session = SelectedSession ?? throw new InvalidOperationException("Select a session.");
        var player = SelectedPlayer;
        var error = DealerInputValidator.ValidateDeposit(player, amountGil);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        PendingDeposit = new DepositSubmission(session.SessionId, player!.PlayerId, amountGil);
        SetStatus("Confirm the exact manual deposit before submitting it.");
    }

    public void CancelPendingDeposit() => PendingDeposit = null;

    public async Task SubmitDepositAsync(CancellationToken cancellationToken = default)
    {
        var submission = PendingDeposit ?? throw new InvalidOperationException("Prepare and confirm a deposit first.");
        submission.MarkSending();
        try
        {
            var result = await api.CreateDepositAsync(
                submission.SessionId,
                new CreateManualDepositRequest(submission.PlayerId, submission.AmountGil),
                submission.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
            submission.MarkSucceeded($"Recorded {result.AmountGil:N0} gil at {result.RecordedAt:u}.");
            await hub.ReportDepositStatusAsync(submission.SessionId, submission.PlayerId, submission.IdempotencyKey, "succeeded").ConfigureAwait(false);
            RecentDeposit = submission;
            PendingDeposit = null;
            await LoadPlayersAsync(submission.SessionId, cancellationToken).ConfigureAwait(false);
            SetStatus(submission.ResultMessage!);
        }
        catch (Exception exception)
        {
            submission.MarkFailed(exception.Message);
            RecentDeposit = submission;
            await hub.ReportDepositStatusAsync(submission.SessionId, submission.PlayerId, submission.IdempotencyKey, "failed", GetErrorCode(exception)).ConfigureAwait(false);
            SetStatus($"Deposit failed. Retry uses the same idempotency key: {submission.ResultMessage}");
            throw;
        }
    }

    public async Task RetryRecentDepositAsync(CancellationToken cancellationToken = default)
    {
        if (RecentDeposit?.State != DepositSubmissionState.Failed)
        {
            throw new InvalidOperationException("Only a failed deposit can be retried.");
        }

        PendingDeposit = RecentDeposit;
        await SubmitDepositAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RequestCashoutRetryAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        var operation = ActivePayout is { } active && active.OperationId == operationId
            ? active
            : OpenPayoutOperations.FirstOrDefault(item => item.OperationId == operationId)
            ?? throw new InvalidOperationException("The payout operation is not open.");
        if (operation.State != PayoutOperationState.Failed)
        {
            throw new InvalidOperationException("Only a definite failed operation can be sent for dealer-triggered backend retry.");
        }

        var logicalOperation = $"cashout:{operationId:D}:retry";
        var key = financialKeys.GetOrCreate(logicalOperation);
        await api.RetryCashoutAsync(new RetryCashoutRequest(operationId, "dealerTriggered"), key, cancellationToken).ConfigureAwait(false);
        financialKeys.Complete(logicalOperation, key);
        await RefreshSessionsAsync(cancellationToken).ConfigureAwait(false);
        SetStatus("Backend payout retry requested. The plugin did not retry the trade directly.");
    }

    public async Task SubmitReconciliationAsync(Guid operationId, string evidence, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(evidence))
        {
            throw new InvalidOperationException("Enter dealer evidence for reconciliation.");
        }

        var normalizedEvidence = evidence.Trim();
        var logicalOperation = $"cashout:{operationId:D}:reconciliation:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedEvidence)))}";
        var key = financialKeys.GetOrCreate(logicalOperation);
        await api.ReconcileCashoutAsync(new ReconcileCashoutRequest(operationId, normalizedEvidence), key, cancellationToken).ConfigureAwait(false);
        financialKeys.Complete(logicalOperation, key);
        await RefreshSessionsAsync(cancellationToken).ConfigureAwait(false);
        SetStatus("Payout evidence submitted to backend reconciliation.");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        framework.Update -= OnFrameworkUpdate;
        try
        {
            await hub.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Plugin disposal must continue even if the backend is unavailable.
        }

        payoutCoordinator.Dispose();
        await hub.DisposeAsync().ConfigureAwait(false);
        api.Dispose();
        dropbox.Dispose();
        lifecycleGate.Dispose();
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = DateTimeOffset.UtcNow;
        if (now >= nextIdentityCheck)
        {
            nextIdentityCheck = now.AddSeconds(1);
            var current = ReadCharacter();
            if (current != character)
            {
                var previous = character;
                character = current;
                RunBackground(token => HandleCharacterChangedAsync(previous, current, token));
            }
        }

        if (api.IsAuthorized && now >= nextHeartbeat)
        {
            nextHeartbeat = now.AddSeconds(30);
            RunBackground(SendHeartbeatAsync);
        }
    }

    private CharacterIdentityDto ReadCharacter()
    {
        if (!playerState.IsLoaded)
        {
            return new CharacterIdentityDto(string.Empty, string.Empty, string.Empty, false);
        }

        return new CharacterIdentityDto(
            playerState.CharacterName.ToString(),
            playerState.HomeWorld.Value.Name.ToString(),
            playerState.CurrentWorld.Value.Name.ToString(),
            true);
    }

    private async Task HandleCharacterChangedAsync(
        CharacterIdentityDto previous,
        CharacterIdentityDto current,
        CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (api.IsAuthorized && (!current.IsLoggedIn ||
                !string.Equals(previous.CharacterName, current.CharacterName, StringComparison.Ordinal) ||
                !string.Equals(previous.HomeWorld, current.HomeWorld, StringComparison.Ordinal)))
            {
                if (payoutCoordinator.ActiveOperation is { } activeOperation)
                {
                    try
                    {
                        await payoutCoordinator.CancelFromBackendAsync(
                            new CancelPayoutOperationDto(activeOperation.OperationId, "dealerCharacterChanged"),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        SetStatus($"The open payout could not be definitively cancelled and requires safe observation: {exception.Message}");
                    }
                }

                await hub.StopAsync(cancellationToken).ConfigureAwait(false);
                await api.InvalidateLocalAuthorizationAsync(cancellationToken).ConfigureAwait(false);
                ClearDealerState();
                SetStatus(current.IsLoggedIn
                    ? "Authorization was cleared because the character or Home World changed."
                    : "Authorization was cleared on logout.");
            }

            if (!previous.IsLoggedIn && current.IsLoggedIn && !api.IsAuthorized)
            {
                await RestoreAuthorizationAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task RestoreAuthorizationAsync(CancellationToken cancellationToken)
    {
        if (await api.TryRestoreAsync(cancellationToken).ConfigureAwait(false))
        {
            await ConnectAuthorizedClientAsync(cancellationToken).ConfigureAwait(false);
            SetStatus($"Authorization restored for {Character.CharacterName}@{Character.HomeWorld}.");
        }
    }

    private async Task ConnectAuthorizedClientAsync(CancellationToken cancellationToken)
    {
        var pairing = await api.CreatePairingAsync(new PluginPairingRequest(
            configuration.DeviceId,
            Character,
            PluginVersion.Current,
            ContractVersion.Current,
            GetDropboxCapabilities()), cancellationToken).ConfigureAwait(false);
        if (api.PairingId != pairing.PairingId)
        {
            throw new InvalidDataException("The dealer authorization and plugin pairing identities do not match.");
        }

        await hub.StartAsync(cancellationToken).ConfigureAwait(false);
        await payoutCoordinator.ReplayOutboxAsync(cancellationToken).ConfigureAwait(false);
        await RefreshSessionsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var operation in OpenPayoutOperations)
        {
            if (await payoutCoordinator.RecoverBackendOperationAsync(operation, cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }

        nextHeartbeat = DateTimeOffset.MinValue;
    }

    private async Task RevokeAuthorizationAsync(string reason, CancellationToken cancellationToken)
    {
        if (payoutCoordinator.ActiveOperation is { } activeOperation)
        {
            try
            {
                await payoutCoordinator.CancelFromBackendAsync(
                    new CancelPayoutOperationDto(activeOperation.OperationId, "authorizationRevoked"),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                SetStatus($"Authorization was revoked while payout cancellation remained unproven: {exception.Message}");
            }
        }

        await hub.StopAsync(cancellationToken).ConfigureAwait(false);
        await api.InvalidateLocalAuthorizationAsync(cancellationToken).ConfigureAwait(false);
        ClearDealerState();
        SetStatus($"Dealer authorization was revoked: {reason}");
    }

    private async Task ReconnectHubAsync(string reason, CancellationToken cancellationToken)
    {
        SetStatus($"Backend requested reconnection: {reason}");
        await hub.StopAsync(cancellationToken).ConfigureAwait(false);
        await api.EnsureFreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        await hub.StartAsync(cancellationToken).ConfigureAwait(false);
        await payoutCoordinator.ReplayOutboxAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        if (api.PairingId is not Guid pairingId)
        {
            return;
        }

        PendingOutboxEvents = await outbox.CountAsync(cancellationToken).ConfigureAwait(false);
        var heartbeat = new PluginHeartbeatRequest(
            pairingId,
            configuration.DeviceId,
            Character,
            PluginVersion.Current,
            ContractVersion.Current,
            GetDropboxCapabilities(),
            PendingOutboxEvents,
            DateTimeOffset.UtcNow);
        await api.SendHeartbeatAsync(heartbeat, cancellationToken).ConfigureAwait(false);
        await hub.ReportOutboxStatusAsync(PendingOutboxEvents).ConfigureAwait(false);

        var compatibility = dropbox.Probe();
        var status = new DropboxStatusDto(
            compatibility.IsAvailable,
            PayoutExecutionPolicy.Validate(CreateCompatibilityProbeLeg(), true, compatibility) is null,
            payoutCoordinator.HasActiveOperation,
            payoutCoordinator.ActiveOperation?.OperationId,
            compatibility.IsAvailable ? "available" : "unavailable",
            DateTimeOffset.UtcNow);
        await api.ReportDropboxStatusAsync(status, cancellationToken).ConfigureAwait(false);
        await hub.ReportDropboxStatusAsync(status).ConfigureAwait(false);
        if (payoutCoordinator.ActiveOperation is { } operation)
        {
            await hub.ReportOutgoingTradeStatusAsync(operation).ConfigureAwait(false);
        }

        LastHeartbeatAt = DateTimeOffset.UtcNow;
    }

    private PayoutLegDto CreateCompatibilityProbeLeg() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "Probe Character",
        "ProbeWorld",
        1,
        DropboxPayoutContract.IpcVersion,
        DropboxPayoutContract.SupportedBuildVersion,
        DateTimeOffset.UtcNow);

    private DropboxCapabilitiesDto GetDropboxCapabilities()
    {
        var compatibility = dropbox.Probe();
        return new DropboxCapabilitiesDto(
            compatibility.IsAvailable,
            compatibility.Version?.IpcVersion,
            compatibility.Version?.BuildVersion,
            compatibility.Capabilities,
            compatibility.SupportsLanguageIndependentTradeState,
            compatibility.Version?.PluginInstanceId);
    }

    private async Task LoadPlayersAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        Players = await api.GetPlayersAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var selectedId = SelectedPlayer?.PlayerId;
        SelectedPlayer = selectedId is null ? null : Players.FirstOrDefault(item => item.PlayerId == selectedId);
    }

    private void ClearDealerState()
    {
        Sessions = [];
        SelectedSession = null;
        Players = [];
        SelectedPlayer = null;
        OpenPayoutOperations = [];
        PendingDeposit = null;
    }

    private void RequireAuthorization()
    {
        if (!api.IsAuthorized)
        {
            throw new InvalidOperationException("Dealer authorization is required.");
        }
    }

    private void RunBackground(Func<CancellationToken, Task> action) => _ = RunBackgroundCoreAsync(action);

    private async Task RunBackgroundCoreAsync(Func<CancellationToken, Task> action)
    {
        try
        {
            await action(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var safeMessage = SecretRedactor.Redact(exception.Message);
            SetStatus(safeMessage);
            pluginLog.Error("Catsino operation failed: {Message}", safeMessage);
        }
    }

    private void SetStatus(string message) => StatusMessage = SecretRedactor.Redact(message);

    private static string GetErrorCode(Exception exception) =>
        exception is BackendApiException apiException ? apiException.ErrorCode : "clientError";
}
