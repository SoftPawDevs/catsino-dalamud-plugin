using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Catsino.Plugin.Contracts;
using Catsino.Plugin.Security;

namespace Catsino.Plugin.Backend;

public sealed class CatsinoApiClient : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly IProtectedCredentialStore credentialStore;
    private readonly Func<CharacterIdentityDto> characterProvider;
    private readonly Guid deviceId;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private DealerAuthorizationDto? authorization;

    public CatsinoApiClient(
        Uri baseUri,
        IProtectedCredentialStore credentialStore,
        Func<CharacterIdentityDto> characterProvider,
        Guid deviceId,
        HttpMessageHandler? handler = null)
    {
        if (!baseUri.IsAbsoluteUri ||
            (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !(string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && baseUri.IsLoopback)))
        {
            throw new ArgumentException("The Catsino API URL must use HTTPS, except for loopback development.", nameof(baseUri));
        }

        this.credentialStore = credentialStore;
        this.characterProvider = characterProvider;
        this.deviceId = deviceId;
        httpClient = handler is null ? new HttpClient() : new HttpClient(handler, true);
        httpClient.BaseAddress = baseUri;
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"Catsino.Plugin/{PluginVersion.Current}");
    }

    public bool IsAuthorized => authorization is not null;

    public Guid? PairingId => authorization?.PairingId;

    public DateTimeOffset? AccessTokenExpiresAt => authorization?.AccessTokenExpiresAt;

    public string? GetAccessToken() => authorization?.AccessToken;

    public async Task<DealerAuthorizationDto> AuthorizeAsync(string activationJwt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activationJwt);
        var character = RequireLoggedInCharacter();
        var request = new AuthorizeDealerRequest(
            activationJwt,
            character,
            deviceId,
            PluginVersion.Current,
            ContractVersion.Current);

        var result = await SendAsync<DealerAuthorizationDto>(
            () => CreateJsonRequest(HttpMethod.Post, "api/v1/dealers/authorize", request),
            authorized: false,
            allowRefresh: false,
            cancellationToken).ConfigureAwait(false);

        await AcceptAuthorizationAsync(result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<bool> TryRestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!RequireLoggedInCharacter(throwIfMissing: false).IsLoggedIn)
        {
            return false;
        }

        var refreshCredential = await credentialStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(refreshCredential))
        {
            return false;
        }

        try
        {
            var result = await RefreshWithCredentialAsync(refreshCredential, cancellationToken).ConfigureAwait(false);
            await AcceptAuthorizationAsync(result, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (BackendApiException exception) when (exception.StatusCode is
            HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            authorization = null;
            await credentialStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (authorization is not null)
            {
                await SendNoContentAsync(
                    () => CreateJsonRequest(HttpMethod.Post, "api/v1/dealers/disconnect", new { authorization.PairingId }),
                    authorized: true,
                    allowRefresh: false,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            authorization = null;
            await credentialStore.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task InvalidateLocalAuthorizationAsync(CancellationToken cancellationToken = default)
    {
        authorization = null;
        await credentialStore.ClearAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<PluginPairingDto> CreatePairingAsync(PluginPairingRequest request, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<PluginPairingDto>(HttpMethod.Post, "api/v1/plugin/pairings", request, cancellationToken: cancellationToken);

    public Task SendHeartbeatAsync(PluginHeartbeatRequest request, CancellationToken cancellationToken = default) =>
        SendAuthorizedNoContentAsync(HttpMethod.Post, $"api/v1/plugin/pairings/{request.PairingId:D}/heartbeat", request, cancellationToken: cancellationToken);

    public Task ReportPayoutExecutorStatusAsync(PayoutExecutorStatusDto request, CancellationToken cancellationToken = default) =>
        SendAuthorizedNoContentAsync(HttpMethod.Post, "api/v1/payout-executor/status", request, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<GameSessionDto>> GetSessionsAsync(CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<IReadOnlyList<GameSessionDto>>(HttpMethod.Get, "api/v1/game-sessions", cancellationToken: cancellationToken);

    public Task<GameSessionDto?> GetActiveSessionAsync(CancellationToken cancellationToken = default) =>
        SendAuthorizedNullableAsync<GameSessionDto>(HttpMethod.Get, "api/v1/game-sessions/active", cancellationToken: cancellationToken);

    public Task<GameSessionDto> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<GameSessionDto>(HttpMethod.Get, $"api/v1/game-sessions/{sessionId:D}", cancellationToken: cancellationToken);

    public Task<GameSessionDto> CreateSessionAsync(CreateGameSessionRequest request, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<GameSessionDto>(HttpMethod.Post, "api/v1/game-sessions", request, idempotencyKey, cancellationToken);

    public Task<GameSessionDto> UpdateSessionFeeAsync(Guid sessionId, UpdateSessionFeeRequest request, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<GameSessionDto>(HttpMethod.Patch, $"api/v1/game-sessions/{sessionId:D}/fee", request, idempotencyKey, cancellationToken);

    public Task<GameSessionDto> OpenSessionAsync(Guid sessionId, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<GameSessionDto>(HttpMethod.Post, $"api/v1/game-sessions/{sessionId:D}/open", idempotencyKey: idempotencyKey, cancellationToken: cancellationToken);

    public Task<GameSessionDto> CloseSessionAsync(Guid sessionId, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<GameSessionDto>(HttpMethod.Post, $"api/v1/game-sessions/{sessionId:D}/close", idempotencyKey: idempotencyKey, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<SessionPlayerDto>> GetPlayersAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<IReadOnlyList<SessionPlayerDto>>(HttpMethod.Get, $"api/v1/game-sessions/{sessionId:D}/players", cancellationToken: cancellationToken);

    public Task<InviteDto> CreateInviteAsync(Guid sessionId, CreateInviteRequest request, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<InviteDto>(HttpMethod.Post, $"api/v1/game-sessions/{sessionId:D}/invites", request, cancellationToken: cancellationToken);

    public Task<InviteDto> ReinviteAsync(Guid sessionId, Guid membershipId, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<InviteDto>(HttpMethod.Post, $"api/v1/game-sessions/{sessionId:D}/players/{membershipId:D}/reinvite", cancellationToken: cancellationToken);

    public Task<SessionRosterDto> GetSessionRosterAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<SessionRosterDto>(HttpMethod.Get, $"api/v1/game-sessions/{sessionId:D}/roster", cancellationToken: cancellationToken);

    // === Blackjack (dealer surface) ===
    public Task<BlackjackTableDto> GetBlackjackTableAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<BlackjackTableDto>(HttpMethod.Get, $"api/v1/game-sessions/{sessionId:D}/blackjack", cancellationToken: cancellationToken);

    public Task<BlackjackTableDto> DealBlackjackAsync(Guid sessionId, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<BlackjackTableDto>(HttpMethod.Post, $"api/v1/game-sessions/{sessionId:D}/blackjack/deal", new BlackjackDealRequest(sessionId), idempotencyKey, cancellationToken);

    public Task<BlackjackTableDto> DealerBlackjackHitAsync(Guid sessionId, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<BlackjackTableDto>(HttpMethod.Post, $"api/v1/game-sessions/{sessionId:D}/blackjack/hit", new BlackjackDealerActionRequest(sessionId), idempotencyKey, cancellationToken);

    public Task<BlackjackTableDto> DealerBlackjackStayAsync(Guid sessionId, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<BlackjackTableDto>(HttpMethod.Post, $"api/v1/game-sessions/{sessionId:D}/blackjack/stay", new BlackjackDealerActionRequest(sessionId), idempotencyKey, cancellationToken);

    // === Texas Hold'em (dealer surface) ===
    public Task<HoldemTableDto> GetHoldemTableAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<HoldemTableDto>(HttpMethod.Get, $"api/v1/game-sessions/{sessionId:D}/holdem", cancellationToken: cancellationToken);

    public Task<HoldemTableDto> DealHoldemAsync(Guid sessionId, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<HoldemTableDto>(HttpMethod.Post, $"api/v1/game-sessions/{sessionId:D}/holdem/deal", new HoldemDealRequest(sessionId), idempotencyKey, cancellationToken);

    public Task<IReadOnlyList<PendingInviteDto>> GetPendingInvitesAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<IReadOnlyList<PendingInviteDto>>(HttpMethod.Get, $"api/v1/game-sessions/{sessionId:D}/invites", cancellationToken: cancellationToken);

    public Task CancelInviteAsync(Guid sessionId, Guid inviteId, CancellationToken cancellationToken = default) =>
        SendAuthorizedNoContentAsync(HttpMethod.Delete, $"api/v1/game-sessions/{sessionId:D}/invites/{inviteId:D}", cancellationToken: cancellationToken);

    public Task<SessionRosterPlayerDto> AdjustPlayerBalanceAsync(
        Guid sessionId,
        Guid membershipId,
        AdjustPlayerBalanceRequest request,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<SessionRosterPlayerDto>(
            HttpMethod.Post,
            $"api/v1/game-sessions/{sessionId:D}/players/{membershipId:D}/balance-adjustments",
            request,
            idempotencyKey,
            cancellationToken);

    public Task<CashOutPreviewResponse> GetPlayerCashOutPreviewAsync(
        Guid sessionId,
        Guid membershipId,
        CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<CashOutPreviewResponse>(
            HttpMethod.Get,
            $"api/v1/game-sessions/{sessionId:D}/players/{membershipId:D}/cashout-preview",
            cancellationToken: cancellationToken);

    public Task<CashOutResponse?> StartPlayerCashOutAsync(
        Guid sessionId,
        Guid membershipId,
        DealerCashOutRequest request,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default) =>
        SendAuthorizedNullableAsync<CashOutResponse>(
            HttpMethod.Post,
            $"api/v1/game-sessions/{sessionId:D}/players/{membershipId:D}/cashouts",
            request,
            idempotencyKey,
            cancellationToken);

    // Books a payout the dealer already made outside the game and clears the player from the table. The
    // expected gross/fee/net are echoed back so the backend refuses the call if the balance moved between
    // the quote the dealer read and the settlement they confirmed.
    public Task<CashOutResponse?> SettleManuallyAsync(
        Guid sessionId,
        Guid membershipId,
        ManualSettlementRequest request,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default) =>
        SendAuthorizedNullableAsync<CashOutResponse>(
            HttpMethod.Post,
            $"api/v1/game-sessions/{sessionId:D}/players/{membershipId:D}/manual-settlement",
            request,
            idempotencyKey,
            cancellationToken);

    // === Roulette (dealer surface) ===
    public Task<RouletteTableDto> GetRouletteTableAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<RouletteTableDto>(HttpMethod.Get, $"api/v1/game-sessions/{sessionId:D}/roulette", cancellationToken: cancellationToken);

    public Task<RouletteTableDto> SpinRouletteAsync(Guid sessionId, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<RouletteTableDto>(HttpMethod.Post, $"api/v1/game-sessions/{sessionId:D}/roulette/spin", new RouletteSpinRequest(sessionId), idempotencyKey, cancellationToken);

    public Task DismissCashOutRequestAsync(Guid sessionId, Guid membershipId, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAuthorizedNoContentAsync(
            HttpMethod.Delete,
            $"api/v1/game-sessions/{sessionId:D}/players/{membershipId:D}/cashout-request",
            idempotencyKey: idempotencyKey,
            cancellationToken: cancellationToken);

    public Task<SessionRemovalDto> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<SessionRemovalDto>(HttpMethod.Delete, $"api/v1/game-sessions/{sessionId:D}", cancellationToken: cancellationToken);

    public Task<DepositDto> CreateDepositAsync(
        Guid sessionId,
        CreateManualDepositRequest request,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<DepositDto>(
            HttpMethod.Post,
            $"api/v1/game-sessions/{sessionId:D}/deposits",
            request,
            idempotencyKey,
            cancellationToken);

    public Task<PayoutEventAckDto> ReportPayoutEventAsync(PayoutEventDto request, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<PayoutEventAckDto>(
            HttpMethod.Post,
            "api/v1/payout-events",
            request,
            FinancialIdempotency.ForPayoutEvent(request.OperationId, request.SequenceNumber),
            cancellationToken);

    public Task AcknowledgePayoutEventAsync(PayoutEventAckDto request, CancellationToken cancellationToken = default) =>
        SendAuthorizedNoContentAsync(
            HttpMethod.Post,
            $"api/v1/payout-events/{request.OperationId:D}/{request.SequenceNumber}/ack",
            request,
            FinancialIdempotency.ForPayoutAcknowledgment(request.OperationId, request.SequenceNumber),
            cancellationToken: cancellationToken);

    public Task<IReadOnlyList<PayoutOperationDto>> GetOpenPayoutOperationsAsync(CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<IReadOnlyList<PayoutOperationDto>>(HttpMethod.Get, "api/v1/payout-operations/open", cancellationToken: cancellationToken);

    public Task<PayoutOperationDto> RetryCashoutAsync(RetryCashoutRequest request, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<PayoutOperationDto>(HttpMethod.Post, $"api/v1/cashouts/{request.OperationId:D}/retry", request, idempotencyKey, cancellationToken);

    public Task<CashOutResponse> ReconcileOperationAsync(Guid operationId, string reason, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<CashOutResponse>(
            HttpMethod.Post,
            $"api/v1/payout-operations/{operationId:D}/reconcile",
            new ReconcileOperationRequest(operationId, reason),
            idempotencyKey,
            cancellationToken);

    public Task<CashOutResponse> SettleCashOutAsync(Guid cashOutId, CashOutSettlementRequest request, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<CashOutResponse>(
            HttpMethod.Post,
            $"api/v1/cashouts/{cashOutId:D}/settle",
            request,
            idempotencyKey,
            cancellationToken);

    public Task<IReadOnlyList<OpenCashOutDto>> GetOpenCashOutsAsync(CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<IReadOnlyList<OpenCashOutDto>>(HttpMethod.Get, "api/v1/cashouts/open", cancellationToken: cancellationToken);

    public async Task EnsureFreshAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (authorization is not null && authorization.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        httpClient.Dispose();
        refreshGate.Dispose();
    }

    private async Task<T> SendAuthorizedAsync<T>(
        HttpMethod method,
        string path,
        object? body = null,
        Guid? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        await SendAsync<T>(
            () => CreateJsonRequest(method, path, body, idempotencyKey),
            authorized: true,
            allowRefresh: true,
            cancellationToken).ConfigureAwait(false);

    private async Task<T?> SendAuthorizedNullableAsync<T>(
        HttpMethod method,
        string path,
        object? body = null,
        Guid? idempotencyKey = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        using var response = await SendResponseAsync(
            () => CreateJsonRequest(method, path, body, idempotencyKey),
            authorized: true,
            allowRefresh: true,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, cancellationToken).ConfigureAwait(false);
    }

    private Task SendAuthorizedNoContentAsync(
        HttpMethod method,
        string path,
        object? body = null,
        Guid? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        SendNoContentAsync(
            () => CreateJsonRequest(method, path, body, idempotencyKey),
            authorized: true,
            allowRefresh: true,
            cancellationToken);

    private async Task<T> SendAsync<T>(
        Func<HttpRequestMessage> requestFactory,
        bool authorized,
        bool allowRefresh,
        CancellationToken cancellationToken)
    {
        using var response = await SendResponseAsync(requestFactory, authorized, allowRefresh, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            throw new InvalidDataException("The Catsino API returned no content for a required response.");
        }

        return await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The Catsino API returned an empty response.");
    }

    private async Task SendNoContentAsync(
        Func<HttpRequestMessage> requestFactory,
        bool authorized,
        bool allowRefresh,
        CancellationToken cancellationToken)
    {
        using var response = await SendResponseAsync(requestFactory, authorized, allowRefresh, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendResponseAsync(
        Func<HttpRequestMessage> requestFactory,
        bool authorized,
        bool allowRefresh,
        CancellationToken cancellationToken)
    {
        if (authorized)
        {
            await EnsureFreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }

        var response = await SendOnceAsync(requestFactory, authorized, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized && authorized && allowRefresh)
        {
            response.Dispose();
            await RefreshAsync(cancellationToken, force: true).ConfigureAwait(false);
            response = await SendOnceAsync(requestFactory, authorized: true, cancellationToken).ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiErrorAsync(response, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        Func<HttpRequestMessage> requestFactory,
        bool authorized,
        CancellationToken cancellationToken)
    {
        using var request = requestFactory();
        if (authorized)
        {
            var token = authorization?.AccessToken ?? throw new InvalidOperationException("Dealer authorization is required.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken, bool force = false)
    {
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && authorization is not null && authorization.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return;
            }

            var refreshCredential = await credentialStore.ReadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No protected refresh credential is available.");
            var result = await RefreshWithCredentialAsync(refreshCredential, cancellationToken).ConfigureAwait(false);
            await AcceptAuthorizationAsync(result, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            authorization = null;
            throw;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private Task<DealerAuthorizationDto> RefreshWithCredentialAsync(string refreshCredential, CancellationToken cancellationToken)
    {
        var request = new RefreshDealerRequest(
            refreshCredential,
            RequireLoggedInCharacter(),
            deviceId,
            PluginVersion.Current,
            ContractVersion.Current);
        return SendAsync<DealerAuthorizationDto>(
            () => CreateJsonRequest(HttpMethod.Post, "api/v1/dealers/refresh", request),
            authorized: false,
            allowRefresh: false,
            cancellationToken);
    }

    private async Task AcceptAuthorizationAsync(DealerAuthorizationDto result, CancellationToken cancellationToken)
    {
        if (result.PairingId == Guid.Empty || result.DealerId == Guid.Empty ||
            string.IsNullOrWhiteSpace(result.AccessToken) || string.IsNullOrWhiteSpace(result.RefreshCredential) ||
            result.AccessTokenExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException("The Catsino API returned an invalid dealer authorization.");
        }

        await credentialStore.StoreAsync(result.RefreshCredential, cancellationToken).ConfigureAwait(false);
        authorization = result;
    }

    private CharacterIdentityDto RequireLoggedInCharacter(bool throwIfMissing = true)
    {
        var character = characterProvider();
        if (throwIfMissing && (!character.IsLoggedIn || string.IsNullOrWhiteSpace(character.CharacterName) || string.IsNullOrWhiteSpace(character.HomeWorld)))
        {
            throw new InvalidOperationException("Log in to the character whose Home World is authorized.");
        }

        return character;
    }

    private static HttpRequestMessage CreateJsonRequest(HttpMethod method, string path, object? body, Guid? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: ContractJson.Options);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.Value.ToString("D"));
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task ThrowApiErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ApiErrorDto error;
        try
        {
            error = await response.Content.ReadFromJsonAsync<ApiErrorDto>(ContractJson.Options, cancellationToken).ConfigureAwait(false)
                ?? new ApiErrorDto("httpError", $"Catsino API returned HTTP {(int)response.StatusCode}.");
        }
        catch (JsonException)
        {
            error = new ApiErrorDto("httpError", $"Catsino API returned HTTP {(int)response.StatusCode}.");
        }

        var statusCode = response.StatusCode;
        response.Dispose();
        throw new BackendApiException(statusCode, error);
    }
}

public static class PluginVersion
{
    public const string Current = "1.10.0";
}
