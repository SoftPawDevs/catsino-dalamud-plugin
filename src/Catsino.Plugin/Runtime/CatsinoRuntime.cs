using System.Collections.Concurrent;
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
    private readonly IPluginLog pluginLog;
    private readonly PluginConfiguration configuration;
    private readonly IDropboxPayoutClient dropbox;
    private readonly CatsinoApiClient api;
    private readonly PluginHubClient hub;
    private readonly IPayoutOutbox outbox;
    private readonly PayoutCoordinator payoutCoordinator;
    private readonly LogicalIdempotencyKeys financialKeys = new();
    private readonly SessionRosterStore rosterStore = new();
    private readonly ConcurrentDictionary<SessionPlayerKey, BalanceAdjustmentSubmission> balanceAdjustments = new();
    private readonly ConcurrentDictionary<SessionPlayerKey, CashOutSubmission> cashOuts = new();
    private readonly object stateSync = new();
    private readonly object trackedSessionsSync = new();
    private readonly HashSet<Guid> trackedSessions = [];
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim sessionRefreshGate = new(1, 1);
    private readonly SemaphoreSlim hubRecoveryGate = new(1, 1);
    private DateTimeOffset nextIdentityCheck = DateTimeOffset.MinValue;
    private DateTimeOffset nextHeartbeat = DateTimeOffset.MinValue;
    private DateTimeOffset nextRosterPoll = DateTimeOffset.MinValue;
    private CharacterIdentityDto character = new(string.Empty, string.Empty, string.Empty, false);
    private long selectionRevision;
    private long authorizationEpoch;
    private bool disposed;

    public CatsinoRuntime(
        IDalamudPluginInterface pluginInterface,
        IPlayerState playerState,
        IFramework framework,
        IPluginLog pluginLog)
    {
        this.pluginInterface = pluginInterface;
        this.playerState = playerState;
        this.framework = framework;
        this.pluginLog = pluginLog;
        configuration = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        if (configuration.DeviceId == Guid.Empty)
        {
            configuration.DeviceId = Guid.NewGuid();
        }

        if (DealerInputValidator.ValidateFee(configuration.DefaultDealerFeePercent, GameSessionState.Created) is not null)
        {
            configuration.DefaultDealerFeePercent = 0m;
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
        hub.Reconnected += () => RunBackground(SynchronizeAfterHubConnectionAsync);
        hub.TerminallyDisconnected += () => RunBackground(RecoverHubConnectionAsync);

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

    public IReadOnlyList<PayoutOperationDto> OpenPayoutOperations { get; private set; } = [];

    public PayoutOperationDto? ActivePayout => payoutCoordinator.ActiveOperation;

    public ReconciliationRequestDto? LastReconciliationRequest { get; private set; }

    public SessionActionDraftStore ActionDrafts { get; } = new();

    public DropboxCapabilitiesDto DropboxCapabilities => GetDropboxCapabilities();

    public decimal DefaultDealerFeePercent => configuration.DefaultDealerFeePercent;

    public int PendingOutboxEvents { get; private set; }

    public event Action<Guid>? SessionRemoved;

    public SessionRosterDto? GetRoster(Guid sessionId) => rosterStore.Get(sessionId);

    public GameSessionDto? GetSession(Guid sessionId) =>
        SelectedSession?.SessionId == sessionId
            ? SelectedSession
            : Sessions.FirstOrDefault(item => item.SessionId == sessionId);

    public BalanceAdjustmentSubmission? GetBalanceAdjustment(SessionPlayerKey player) =>
        balanceAdjustments.GetValueOrDefault(player);

    public CashOutSubmission? GetCashOut(SessionPlayerKey player) => cashOuts.GetValueOrDefault(player);

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
        InvalidateAuthorizationEpoch();
        await hub.StopAsync(cancellationToken).ConfigureAwait(false);
        await api.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        ClearDealerState();
        SetStatus("Dealer authorization disconnected.");
    }

    public async Task RefreshSessionsAsync(CancellationToken cancellationToken = default)
    {
        await RefreshSessionsCoreAsync(true, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshSessionsCoreAsync(bool reportStatus, CancellationToken cancellationToken)
    {
        await sessionRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireAuthorization();
            var epoch = Volatile.Read(ref authorizationEpoch);
            var selection = Volatile.Read(ref selectionRevision);
            var selectedId = SelectedSession?.SessionId;
            var sessions = await api.GetSessionsAsync(cancellationToken).ConfigureAwait(false);
            var selected = selectedId is null
                ? await api.GetActiveSessionAsync(cancellationToken).ConfigureAwait(false)
                : sessions.FirstOrDefault(item => item.SessionId == selectedId);
            var operations = await api.GetOpenPayoutOperationsAsync(cancellationToken).ConfigureAwait(false);
            if (epoch != Volatile.Read(ref authorizationEpoch) || !api.IsAuthorized)
            {
                return;
            }

            lock (stateSync)
            {
                if (epoch != Volatile.Read(ref authorizationEpoch) || !api.IsAuthorized)
                {
                    return;
                }

                Sessions = sessions;
                OpenPayoutOperations = operations;
                if (selection == Volatile.Read(ref selectionRevision))
                {
                    SelectedSession = selected;
                    if (selected is not null)
                    {
                        TrackSession(selected.SessionId);
                    }
                }

                var available = sessions.Select(item => item.SessionId).ToHashSet();
                foreach (var removedSessionId in rosterStore.SessionIds.Where(id => !available.Contains(id)).ToArray())
                {
                    ClearSessionState(removedSessionId, notify: true);
                }
            }

            try
            {
                await RefreshTrackedRostersAsync(epoch, cancellationToken).ConfigureAwait(false);
            }
            catch when (epoch != Volatile.Read(ref authorizationEpoch))
            {
                return;
            }
            catch (Exception exception)
            {
                pluginLog.Warning("Session data refreshed, but a roster refresh failed: {Message}", SecretRedactor.Redact(exception.Message));
            }

            lock (stateSync)
            {
                if (epoch == Volatile.Read(ref authorizationEpoch) && reportStatus)
                {
                    SetStatus("Dealer sessions refreshed.");
                }
            }
        }
        finally
        {
            sessionRefreshGate.Release();
        }
    }

    public async Task SelectSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var revision = Interlocked.Increment(ref selectionRevision);
        var epoch = Volatile.Read(ref authorizationEpoch);
        var session = await api.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        lock (stateSync)
        {
            if (revision != Volatile.Read(ref selectionRevision) || epoch != Volatile.Read(ref authorizationEpoch) || !api.IsAuthorized)
            {
                return;
            }

            SelectedSession = session;
            ApplySession(session);
            TrackSession(sessionId);
            rosterStore.Invalidate(sessionId);
        }

        await rosterStore.RefreshAsync(sessionId, LoadRoster, cancellationToken).ConfigureAwait(false);

        async Task<SessionRosterDto> LoadRoster(Guid id, CancellationToken token)
        {
            var roster = await api.GetSessionRosterAsync(id, token).ConfigureAwait(false);
            if (epoch != Volatile.Read(ref authorizationEpoch))
            {
                throw new OperationCanceledException("Dealer authorization changed while selecting the session.");
            }

            return roster;
        }
    }

    public void TrackSession(Guid sessionId)
    {
        lock (trackedSessionsSync)
        {
            trackedSessions.Add(sessionId);
        }
    }

    public void SetDefaultDealerFeePercent(decimal feePercent)
    {
        var error = DealerInputValidator.ValidateFee(feePercent, GameSessionState.Created);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        if (configuration.DefaultDealerFeePercent == feePercent)
        {
            return;
        }

        configuration.DefaultDealerFeePercent = feePercent;
        pluginInterface.SavePluginConfig(configuration);
    }

    public async Task CreatePlinkoSessionAsync(decimal feePercent, CancellationToken cancellationToken = default)
    {
        var feeError = DealerInputValidator.ValidateFee(feePercent, GameSessionState.Created);
        if (feeError is not null)
        {
            throw new InvalidOperationException(feeError);
        }

        var epoch = Volatile.Read(ref authorizationEpoch);
        var logicalOperation = $"session:create:plinko:{feePercent.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var key = financialKeys.GetOrCreate(logicalOperation);
        var created = await api.CreateSessionAsync(new CreateGameSessionRequest("plinko", feePercent), key, cancellationToken).ConfigureAwait(false);
        financialKeys.Complete(logicalOperation, key);
        lock (stateSync)
        {
            if (epoch != Volatile.Read(ref authorizationEpoch) || !api.IsAuthorized)
            {
                return;
            }

            SelectedSession = created;
            ApplySession(created);
            TrackSession(created.SessionId);
            SetStatus("Created a Plinko session.");
        }

        await TryRefreshSessionsAfterMutationAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateFeeAsync(Guid sessionId, decimal feePercent, CancellationToken cancellationToken = default)
    {
        var session = GetSession(sessionId) ?? throw new InvalidOperationException("The session is not available.");
        var error = DealerInputValidator.ValidateFee(feePercent, session.State);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        var epoch = Volatile.Read(ref authorizationEpoch);
        var logicalOperation = $"session:{session.SessionId:D}:fee:{feePercent.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var key = financialKeys.GetOrCreate(logicalOperation);
        var updated = await api.UpdateSessionFeeAsync(session.SessionId, new UpdateSessionFeeRequest(feePercent), key, cancellationToken).ConfigureAwait(false);
        financialKeys.Complete(logicalOperation, key);
        lock (stateSync)
        {
            if (epoch != Volatile.Read(ref authorizationEpoch) || !api.IsAuthorized)
            {
                return;
            }

            ApplySession(updated);
            SetStatus("Session fee updated.");
        }
    }

    public async Task OpenSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = GetSession(sessionId) ?? throw new InvalidOperationException("The session is not available.");
        var epoch = Volatile.Read(ref authorizationEpoch);
        var logicalOperation = $"session:{session.SessionId:D}:open";
        var key = financialKeys.GetOrCreate(logicalOperation);
        var updated = await api.OpenSessionAsync(session.SessionId, key, cancellationToken).ConfigureAwait(false);
        financialKeys.Complete(logicalOperation, key);
        lock (stateSync)
        {
            if (epoch != Volatile.Read(ref authorizationEpoch) || !api.IsAuthorized)
            {
                return;
            }

            ApplySession(updated);
            SetStatus("Session opened.");
        }

        await TryRefreshSessionsAfterMutationAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = GetSession(sessionId) ?? throw new InvalidOperationException("The session is not available.");
        var epoch = Volatile.Read(ref authorizationEpoch);
        var logicalOperation = $"session:{session.SessionId:D}:close";
        var key = financialKeys.GetOrCreate(logicalOperation);
        var updated = await api.CloseSessionAsync(session.SessionId, key, cancellationToken).ConfigureAwait(false);
        financialKeys.Complete(logicalOperation, key);
        lock (stateSync)
        {
            if (epoch != Volatile.Read(ref authorizationEpoch) || !api.IsAuthorized)
            {
                return;
            }

            ApplySession(updated);
            SetStatus("Session closed.");
        }

        await TryRefreshSessionsAfterMutationAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateInviteAndTellAsync(
        Guid sessionId,
        string characterName,
        string homeWorld,
        long initialBalanceGil,
        CancellationToken cancellationToken = default)
    {
        _ = GetSession(sessionId) ?? throw new InvalidOperationException("The session is not available.");
        var error = DealerInputValidator.ValidateCharacter(characterName, homeWorld);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        error = DealerInputValidator.ValidateInviteBalance(initialBalanceGil);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        if (GetRoster(sessionId) is null)
            await RefreshRosterAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var roster = GetRoster(sessionId) ?? throw new InvalidOperationException("The session roster is not available.");
        error = SessionRosterStore.FindInviteConflict(roster, characterName, homeWorld);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        var invite = await api.CreateInviteAsync(
            sessionId,
            new CreateInviteRequest(characterName, homeWorld, initialBalanceGil),
            cancellationToken).ConfigureAwait(false);
        if (invite.SessionId != sessionId ||
            !string.Equals(invite.CharacterName, characterName, StringComparison.Ordinal) ||
            !string.Equals(invite.HomeWorld, homeWorld, StringComparison.Ordinal) ||
            invite.InitialBalanceGil != initialBalanceGil ||
            !string.Equals(invite.InviteUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            invite.InviteUrl.OriginalString.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException("The backend returned mismatched or unsafe invite data.");
        }

        rosterStore.UpsertPendingInvite(new PendingInviteDto(
            invite.InviteId,
            invite.SessionId,
            invite.CharacterName,
            invite.HomeWorld,
            invite.InitialBalanceGil,
            DateTimeOffset.UtcNow,
            invite.ExpiresAt));
        nextRosterPoll = DateTimeOffset.MinValue;
        var command = GameChat.BuildTellCommand(characterName, homeWorld, invite.InviteUrl);
        await framework.Run(() => GameChat.SendCommand(command), cancellationToken).ConfigureAwait(false);
        SetStatus("The invite tell command was submitted locally; delivery is not confirmed.");
    }

    public async Task CancelInviteAsync(Guid sessionId, Guid inviteId, CancellationToken cancellationToken = default)
    {
        await api.CancelInviteAsync(sessionId, inviteId, cancellationToken).ConfigureAwait(false);
        rosterStore.RemovePendingInvite(sessionId, inviteId);
        nextRosterPoll = DateTimeOffset.MinValue;
        SetStatus("Invite cancelled.");
    }

    public void PrepareBalanceAdjustment(SessionPlayerKey player, long amountGil)
    {
        var rosterPlayer = RequireRosterPlayer(player);
        var error = DealerInputValidator.ValidateBalanceAdjustment(amountGil);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        balanceAdjustments[player] = new BalanceAdjustmentSubmission(player, amountGil);
        SetStatus($"Confirm the signed balance adjustment for {rosterPlayer.CharacterName}@{rosterPlayer.HomeWorld}.");
    }

    public void CancelBalanceAdjustment(SessionPlayerKey player)
    {
        if (balanceAdjustments.TryGetValue(player, out var submission) &&
            submission.State == DealerActionState.Failed && !submission.CanDiscardFailure)
        {
            throw new InvalidOperationException("Retry this ambiguous adjustment with its existing idempotency key.");
        }

        balanceAdjustments.TryRemove(player, out _);
    }

    public async Task SubmitBalanceAdjustmentAsync(SessionPlayerKey player, CancellationToken cancellationToken = default)
    {
        var submission = balanceAdjustments.GetValueOrDefault(player)
            ?? throw new InvalidOperationException("Prepare and confirm a balance adjustment first.");
        submission.MarkSending();
        try
        {
            await api.AdjustPlayerBalanceAsync(
                player.SessionId,
                player.MembershipId,
                new AdjustPlayerBalanceRequest(submission.AmountGil),
                submission.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            submission.MarkFailed(exception.Message, IsDefinitiveRejection(exception));
            SetStatus($"Balance adjustment failed. Retry retains idempotency key {submission.IdempotencyKey:D}.");
            throw;
        }

        submission.MarkSucceeded();
        balanceAdjustments.TryRemove(player, out _);
        ActionDrafts.SetBalanceAdjustment(player, string.Empty);
        SetStatus($"Applied {submission.AmountGil:+#,0;-#,0} gil to the exact session member.");
        await TryRefreshRosterAfterMutationAsync(player.SessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task RequestCashOutPreviewAsync(SessionPlayerKey player, CancellationToken cancellationToken = default)
    {
        _ = RequireRosterPlayer(player);
        if (cashOuts.TryGetValue(player, out var existing) && existing.State == DealerActionState.Sending)
        {
            throw new InvalidOperationException("A cash out is already being submitted for this member.");
        }

        var preview = await api.GetPlayerCashOutPreviewAsync(player.SessionId, player.MembershipId, cancellationToken).ConfigureAwait(false);
        cashOuts[player] = new CashOutSubmission(player, preview);
        ActionDrafts.SetNetZeroConfirmation(player, false);
        SetStatus("Review the backend cash-out preview before confirming.");
    }

    public void CancelCashOut(SessionPlayerKey player)
    {
        if (cashOuts.TryGetValue(player, out var submission) &&
            submission.State == DealerActionState.Failed && !submission.CanDiscardFailure)
        {
            throw new InvalidOperationException("Retry this ambiguous cash out with its existing idempotency key.");
        }

        cashOuts.TryRemove(player, out _);
        ActionDrafts.SetNetZeroConfirmation(player, false);
    }

    public async Task SubmitCashOutAsync(
        SessionPlayerKey player,
        bool confirmNetZero,
        CancellationToken cancellationToken = default)
    {
        var submission = cashOuts.GetValueOrDefault(player)
            ?? throw new InvalidOperationException("Fetch and review a cash-out preview first.");
        if (submission.Preview.NetIsZero && !confirmNetZero)
        {
            throw new InvalidOperationException("Explicitly confirm the zero net payout before continuing.");
        }

        submission.MarkSending();
        try
        {
            await api.StartPlayerCashOutAsync(
                player.SessionId,
                player.MembershipId,
                new DealerCashOutRequest(
                    true,
                    confirmNetZero,
                    submission.Preview.Gross,
                    submission.Preview.Fee,
                    submission.Preview.Net),
                submission.IdempotencyKey,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            submission.MarkFailed(exception.Message, IsDefinitiveRejection(exception));
            SetStatus($"Cash out failed. Retry retains idempotency key {submission.IdempotencyKey:D}.");
            throw;
        }

        submission.MarkSucceeded();
        cashOuts.TryRemove(player, out _);
        ActionDrafts.SetNetZeroConfirmation(player, false);
        SetStatus("The backend accepted the full-token cash out.");
        await TryRefreshRosterAfterMutationAsync(player.SessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionRemovalDto> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var removal = await api.DeleteSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (removal.SessionId != sessionId)
        {
            throw new InvalidDataException("The backend returned a removal result for a different session.");
        }

        Sessions = Sessions.Where(item => item.SessionId != sessionId).ToArray();
        if (SelectedSession?.SessionId == sessionId)
        {
            SelectedSession = null;
            Interlocked.Increment(ref selectionRevision);
        }

        ClearSessionState(sessionId, notify: true);
        SetStatus($"Session {removal.Mode}.");
        return removal;
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
        SetStatus("Backend payout retry requested. The plugin did not retry the trade directly.");
        await TryRefreshSessionsAfterMutationAsync(cancellationToken).ConfigureAwait(false);
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
        SetStatus("Payout evidence submitted to backend reconciliation.");
        await TryRefreshSessionsAfterMutationAsync(cancellationToken).ConfigureAwait(false);
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

        await payoutCoordinator.DisposeAsync().ConfigureAwait(false);
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

        if (api.IsAuthorized && now >= nextRosterPoll)
        {
            nextRosterPoll = now.AddSeconds(rosterStore.HasUnexpiredPendingInvites(now) ? 5 : 30);
            RunBackground(PollBackendStateAsync);
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
                InvalidateAuthorizationEpoch();
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
        await SynchronizeAfterHubConnectionAsync(cancellationToken).ConfigureAwait(false);

        nextHeartbeat = DateTimeOffset.MinValue;
    }

    private async Task RevokeAuthorizationAsync(string reason, CancellationToken cancellationToken)
    {
        InvalidateAuthorizationEpoch();
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
        await SynchronizeAfterHubConnectionAsync(cancellationToken).ConfigureAwait(false);
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

    public Task RefreshRosterAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        RequireAuthorization();
        TrackSession(sessionId);
        rosterStore.Invalidate(sessionId);
        return rosterStore.RefreshAsync(sessionId, api.GetSessionRosterAsync, cancellationToken);
    }

    private async Task RefreshTrackedRostersAsync(long epoch, CancellationToken cancellationToken)
    {
        Guid[] sessionIds;
        var availableSessionIds = Sessions.Select(session => session.SessionId).ToHashSet();
        if (SelectedSession is { } selected)
        {
            availableSessionIds.Add(selected.SessionId);
        }

        lock (trackedSessionsSync)
        {
            sessionIds = trackedSessions.Where(availableSessionIds.Contains).ToArray();
        }

        foreach (var sessionId in sessionIds)
        {
            if (epoch != Volatile.Read(ref authorizationEpoch))
            {
                return;
            }

            rosterStore.Invalidate(sessionId);
        }

        await Task.WhenAll(sessionIds.Select(sessionId =>
            rosterStore.RefreshAsync(sessionId, LoadRoster, cancellationToken))).ConfigureAwait(false);

        async Task<SessionRosterDto> LoadRoster(Guid sessionId, CancellationToken token)
        {
            var roster = await api.GetSessionRosterAsync(sessionId, token).ConfigureAwait(false);
            if (epoch != Volatile.Read(ref authorizationEpoch))
            {
                throw new OperationCanceledException("Dealer authorization changed while refreshing rosters.");
            }

            return roster;
        }
    }

    private SessionRosterPlayerDto RequireRosterPlayer(SessionPlayerKey player)
    {
        var roster = rosterStore.Get(player.SessionId)
            ?? throw new InvalidOperationException("The session roster has not loaded.");
        return roster.Players.FirstOrDefault(item => item.MembershipId == player.MembershipId)
            ?? throw new InvalidOperationException("The exact session member is no longer active.");
    }

    private void ApplySession(GameSessionDto session)
    {
        Sessions = Sessions.Any(item => item.SessionId == session.SessionId)
            ? Sessions.Select(item => item.SessionId == session.SessionId ? session : item).ToArray()
            : Sessions.Append(session).ToArray();
        if (SelectedSession?.SessionId == session.SessionId)
        {
            SelectedSession = session;
        }
    }

    private void ClearDealerState()
    {
        lock (stateSync)
        {
            Interlocked.Increment(ref authorizationEpoch);
            Sessions = [];
            SelectedSession = null;
            OpenPayoutOperations = [];
            LastReconciliationRequest = null;
            rosterStore.Clear();
            RemoveResolvedSubmissions();
            ActionDrafts.Clear();
            lock (trackedSessionsSync)
            {
                trackedSessions.Clear();
            }

            Interlocked.Increment(ref selectionRevision);
            nextRosterPoll = DateTimeOffset.MinValue;
        }
    }

    private void ClearSessionState(Guid sessionId, bool notify)
    {
        lock (trackedSessionsSync)
        {
            trackedSessions.Remove(sessionId);
        }

        rosterStore.Remove(sessionId);
        ActionDrafts.RemoveSession(sessionId);
        foreach (var player in balanceAdjustments.Keys.Where(item => item.SessionId == sessionId).ToArray())
        {
            if (balanceAdjustments.TryGetValue(player, out var submission) && !MustPreserve(submission.State, submission.CanDiscardFailure))
            {
                balanceAdjustments.TryRemove(player, out _);
            }
        }

        foreach (var player in cashOuts.Keys.Where(item => item.SessionId == sessionId).ToArray())
        {
            if (cashOuts.TryGetValue(player, out var submission) && !MustPreserve(submission.State, submission.CanDiscardFailure))
            {
                cashOuts.TryRemove(player, out _);
            }
        }

        if (notify)
        {
            SessionRemoved?.Invoke(sessionId);
        }
    }

    private async Task TryRefreshRosterAfterMutationAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await RefreshRosterAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            pluginLog.Warning("The mutation succeeded, but the roster refresh failed: {Message}", SecretRedactor.Redact(exception.Message));
        }
    }

    private async Task TryRefreshSessionsAfterMutationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshSessionsCoreAsync(false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            pluginLog.Warning("The mutation succeeded, but the session refresh failed: {Message}", SecretRedactor.Redact(exception.Message));
        }
    }

    private async Task PollBackendStateAsync(CancellationToken cancellationToken)
    {
        await RefreshSessionsCoreAsync(false, cancellationToken).ConfigureAwait(false);
        await RecoverOpenPayoutAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SynchronizeAfterHubConnectionAsync(CancellationToken cancellationToken)
    {
        if (!api.IsAuthorized)
        {
            return;
        }

        await payoutCoordinator.ReplayOutboxAsync(cancellationToken).ConfigureAwait(false);
        await RefreshSessionsCoreAsync(false, cancellationToken).ConfigureAwait(false);
        await RecoverOpenPayoutAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverOpenPayoutAsync(CancellationToken cancellationToken)
    {
        foreach (var operation in OpenPayoutOperations)
        {
            if (await payoutCoordinator.RecoverBackendOperationAsync(operation, cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private async Task RecoverHubConnectionAsync(CancellationToken cancellationToken)
    {
        await hubRecoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var delay = TimeSpan.FromSeconds(2);
            while (!disposed && api.IsAuthorized)
            {
                try
                {
                    await api.EnsureFreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
                    if (!hub.IsConnected)
                    {
                        await hub.StartAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await SynchronizeAfterHubConnectionAsync(cancellationToken).ConfigureAwait(false);
                    SetStatus("Backend realtime connection restored.");
                    return;
                }
                catch (Exception exception) when (!disposed && api.IsAuthorized)
                {
                    pluginLog.Warning("Catsino realtime reconnect failed: {Message}", SecretRedactor.Redact(exception.Message));
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
                }
            }
        }
        finally
        {
            hubRecoveryGate.Release();
        }
    }

    private void InvalidateAuthorizationEpoch()
    {
        lock (stateSync)
        {
            Interlocked.Increment(ref authorizationEpoch);
        }
    }

    private void RemoveResolvedSubmissions()
    {
        foreach (var item in balanceAdjustments.ToArray())
        {
            if (!MustPreserve(item.Value.State, item.Value.CanDiscardFailure))
            {
                balanceAdjustments.TryRemove(item.Key, out _);
            }
        }

        foreach (var item in cashOuts.ToArray())
        {
            if (!MustPreserve(item.Value.State, item.Value.CanDiscardFailure))
            {
                cashOuts.TryRemove(item.Key, out _);
            }
        }
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

    private static bool IsDefinitiveRejection(Exception exception) =>
        exception is BackendApiException apiException && (int)apiException.StatusCode < 500;

    private static bool MustPreserve(DealerActionState state, bool canDiscardFailure) =>
        state == DealerActionState.Sending || state == DealerActionState.Failed && !canDiscardFailure;
}
