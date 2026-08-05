using System.Net;
using System.Net.Http.Json;
using Catsino.Plugin.Backend;
using Catsino.Plugin.Contracts;
using Catsino.Plugin.Security;

namespace Catsino.Plugin.Tests;

public sealed class ApiProtocolTests
{
    [Fact]
    public async Task EveryFinancialMutationUsesTheDocumentedPathAndIdempotencyHeader()
    {
        var handler = new ProtocolHandler();
        var store = new MemoryCredentialStore();
        using var api = CreateClient(handler, store);
        await api.AuthorizeAsync("eyJactivation.payload.signature");
        handler.Requests.Clear();

        var sessionId = ProtocolHandler.SessionId;
        var playerId = ProtocolHandler.PlayerId;
        var operationId = ProtocolHandler.OperationId;
        var keys = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();
        await api.CreateSessionAsync(new CreateGameSessionRequest("plinko", 0m), keys[0]);
        await api.UpdateSessionFeeAsync(sessionId, new UpdateSessionFeeRequest(5m), keys[1]);
        await api.OpenSessionAsync(sessionId, keys[2]);
        await api.CloseSessionAsync(sessionId, keys[3]);
        await api.CreateInviteAsync(sessionId, new CreateInviteRequest("Exact Player", "Ragnarok"));
        await api.CreateDepositAsync(sessionId, new CreateManualDepositRequest(playerId, 100), keys[4]);
        var payoutEvent = TestData.PayoutEvent() with { OperationId = operationId, SequenceNumber = 7 };
        await api.ReportPayoutEventAsync(payoutEvent);
        await api.AcknowledgePayoutEventAsync(new PayoutEventAckDto(operationId, 7, DateTimeOffset.UtcNow));
        await api.RetryCashoutAsync(new RetryCashoutRequest(operationId, "dealerTriggered"), keys[5]);
        await api.ReconcileCashoutAsync(new ReconcileCashoutRequest(operationId, "evidence"), keys[6]);

        AssertMutation(handler, HttpMethod.Post, "/api/v1/game-sessions", keys[0]);
        AssertMutation(handler, HttpMethod.Patch, $"/api/v1/game-sessions/{sessionId:D}/fee", keys[1]);
        AssertMutation(handler, HttpMethod.Post, $"/api/v1/game-sessions/{sessionId:D}/open", keys[2]);
        AssertMutation(handler, HttpMethod.Post, $"/api/v1/game-sessions/{sessionId:D}/close", keys[3]);
        AssertNoIdempotency(handler, HttpMethod.Post, $"/api/v1/game-sessions/{sessionId:D}/invites");
        AssertMutation(handler, HttpMethod.Post, $"/api/v1/game-sessions/{sessionId:D}/deposits", keys[4]);
        AssertMutation(handler, HttpMethod.Post, "/api/v1/payout-events", FinancialIdempotency.ForPayoutEvent(operationId, 7));
        AssertMutation(
            handler,
            HttpMethod.Post,
            $"/api/v1/payout-events/{operationId:D}/7/ack",
            FinancialIdempotency.ForPayoutAcknowledgment(operationId, 7));
        AssertMutation(handler, HttpMethod.Post, $"/api/v1/cashouts/{operationId:D}/retry", keys[5]);
        AssertMutation(handler, HttpMethod.Post, $"/api/v1/cashouts/{operationId:D}/reconciliation", keys[6]);
    }

    [Fact]
    public async Task UnauthorizedHttpRetryReusesTheSameLogicalKey()
    {
        var handler = new ProtocolHandler { RejectFirstSessionCreate = true };
        using var api = CreateClient(handler, new MemoryCredentialStore());
        await api.AuthorizeAsync("eyJactivation.payload.signature");
        handler.Requests.Clear();
        var key = Guid.NewGuid();

        await api.CreateSessionAsync(new CreateGameSessionRequest("plinko", 0m), key);

        var attempts = handler.Requests
            .Where(item => item.Method == HttpMethod.Post && item.Path == "/api/v1/game-sessions")
            .ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.All(attempts, attempt => Assert.Equal(key, attempt.IdempotencyKey));
        Assert.Contains(handler.Requests, item => item.Path == "/api/v1/dealers/refresh");
    }

    [Fact]
    public async Task EveryNonFinancialClientMethodUsesTheDocumentedPath()
    {
        var handler = new ProtocolHandler();
        using var api = CreateClient(handler, new MemoryCredentialStore());
        await api.AuthorizeAsync("eyJactivation.payload.signature");
        var pairingId = ProtocolHandler.PairingId;
        var character = new CharacterIdentityDto("Exact Dealer", "Ragnarok", "Ragnarok", true);
        var dropbox = new DropboxCapabilitiesDto(false, null, null, [], false, null);
        await api.CreatePairingAsync(new PluginPairingRequest(Guid.NewGuid(), character, "1.0.0", "1.0.0", dropbox));
        await api.SendHeartbeatAsync(new PluginHeartbeatRequest(
            pairingId, Guid.NewGuid(), character, "1.0.0", "1.0.0", dropbox, 0, DateTimeOffset.UtcNow));
        await api.ReportDropboxStatusAsync(new DropboxStatusDto(false, false, false, null, "unavailable", DateTimeOffset.UtcNow));
        await api.GetSessionsAsync();
        await api.GetActiveSessionAsync();
        await api.GetSessionAsync(ProtocolHandler.SessionId);
        await api.GetPlayersAsync(ProtocolHandler.SessionId);
        await api.GetOpenPayoutOperationsAsync();
        await api.DisconnectAsync();

        AssertRoute(handler, HttpMethod.Post, "/api/v1/dealers/authorize");
        AssertRoute(handler, HttpMethod.Post, "/api/v1/plugin/pairings");
        AssertRoute(handler, HttpMethod.Post, $"/api/v1/plugin/pairings/{pairingId:D}/heartbeat");
        AssertRoute(handler, HttpMethod.Post, "/api/v1/dropbox/status");
        AssertRoute(handler, HttpMethod.Get, "/api/v1/game-sessions");
        AssertRoute(handler, HttpMethod.Get, "/api/v1/game-sessions/active");
        AssertRoute(handler, HttpMethod.Get, $"/api/v1/game-sessions/{ProtocolHandler.SessionId:D}");
        AssertRoute(handler, HttpMethod.Get, $"/api/v1/game-sessions/{ProtocolHandler.SessionId:D}/players");
        AssertRoute(handler, HttpMethod.Get, "/api/v1/payout-operations/open");
        AssertRoute(handler, HttpMethod.Post, "/api/v1/dealers/disconnect");
    }

    [Fact]
    public async Task JsonNullActiveSessionReturnsNull()
    {
        var handler = new ProtocolHandler { ReturnJsonNullActiveSession = true };
        using var api = CreateClient(handler, new MemoryCredentialStore());
        await api.AuthorizeAsync("eyJactivation.payload.signature");

        var activeSession = await api.GetActiveSessionAsync();

        Assert.Null(activeSession);
        AssertRoute(handler, HttpMethod.Get, "/api/v1/game-sessions/active");
    }

    [Fact]
    public void LogicalKeySurvivesFailureAndClearsOnlyAfterSuccess()
    {
        var keys = new LogicalIdempotencyKeys();
        var first = keys.GetOrCreate("session:create:plinko:0");

        Assert.Equal(first, keys.GetOrCreate("session:create:plinko:0"));
        keys.Complete("session:create:plinko:0", Guid.NewGuid());
        Assert.Equal(first, keys.GetOrCreate("session:create:plinko:0"));
        keys.Complete("session:create:plinko:0", first);
        Assert.NotEqual(first, keys.GetOrCreate("session:create:plinko:0"));
    }

    [Fact]
    public void PayoutEventKeyIsDeterministicAndIdentityBound()
    {
        var operationId = Guid.NewGuid();
        var first = FinancialIdempotency.ForPayoutEvent(operationId, 1);

        Assert.Equal(first, FinancialIdempotency.ForPayoutEvent(operationId, 1));
        Assert.NotEqual(first, FinancialIdempotency.ForPayoutEvent(operationId, 2));
        Assert.NotEqual(first, FinancialIdempotency.ForPayoutAcknowledgment(operationId, 1));
    }

    private static CatsinoApiClient CreateClient(HttpMessageHandler handler, IProtectedCredentialStore store) => new(
        new Uri("https://localhost/"),
        store,
        () => new CharacterIdentityDto("Exact Dealer", "Ragnarok", "Ragnarok", true),
        () => new DropboxCapabilitiesDto(false, null, null, [], false, null),
        Guid.NewGuid(),
        handler);

    private static void AssertMutation(ProtocolHandler handler, HttpMethod method, string path, Guid key)
    {
        var request = Assert.Single(handler.Requests, item => item.Method == method && item.Path == path);
        Assert.Equal(key, request.IdempotencyKey);
    }

    private static void AssertRoute(ProtocolHandler handler, HttpMethod method, string path) =>
        Assert.Contains(handler.Requests, item => item.Method == method && item.Path == path);

    private static void AssertNoIdempotency(ProtocolHandler handler, HttpMethod method, string path)
    {
        var request = Assert.Single(handler.Requests, item => item.Method == method && item.Path == path);
        Assert.Null(request.IdempotencyKey);
    }

    private sealed class MemoryCredentialStore : IProtectedCredentialStore
    {
        private string? value;

        public Task StoreAsync(string credential, CancellationToken cancellationToken = default)
        {
            value = credential;
            return Task.CompletedTask;
        }

        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(value);

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            value = null;
            return Task.CompletedTask;
        }
    }

    private sealed class ProtocolHandler : HttpMessageHandler
    {
        internal static readonly Guid SessionId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        internal static readonly Guid PlayerId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        internal static readonly Guid OperationId = Guid.Parse("30000000-0000-0000-0000-000000000003");
        internal static readonly Guid PairingId = Guid.Parse("40000000-0000-0000-0000-000000000004");
        private bool rejectedSessionCreate;

        internal List<CapturedRequest> Requests { get; } = [];

        internal bool RejectFirstSessionCreate { get; init; }

        internal bool ReturnJsonNullActiveSession { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var idempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var values)
                ? Guid.Parse(Assert.Single(values))
                : (Guid?)null;
            Requests.Add(new CapturedRequest(request.Method, path, idempotencyKey));

            if (RejectFirstSessionCreate && !rejectedSessionCreate && request.Method == HttpMethod.Post && path == "/api/v1/game-sessions")
            {
                rejectedSessionCreate = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            if (ReturnJsonNullActiveSession && request.Method == HttpMethod.Get && path == "/api/v1/game-sessions/active")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json"),
                });
            }

            object? body = (request.Method.Method, path) switch
            {
                ("POST", "/api/v1/dealers/authorize") or ("POST", "/api/v1/dealers/refresh") => Authorization(),
                ("POST", "/api/v1/plugin/pairings") => new PluginPairingDto(PairingId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                ("GET", "/api/v1/game-sessions") => new[] { Session(GameSessionState.Open) },
                ("GET", "/api/v1/game-sessions/active") => Session(GameSessionState.Open),
                ("GET", var value) when value == $"/api/v1/game-sessions/{SessionId:D}" => Session(GameSessionState.Open),
                ("GET", var value) when value.EndsWith("/players", StringComparison.Ordinal) => new[] { TestData.Player(SessionPlayerState.Open) },
                ("GET", "/api/v1/payout-operations/open") => new[] { PayoutOperation() },
                ("POST", "/api/v1/game-sessions") => Session(GameSessionState.Created),
                (_, var value) when value.EndsWith("/fee", StringComparison.Ordinal) => Session(GameSessionState.Created),
                (_, var value) when value.EndsWith("/open", StringComparison.Ordinal) => Session(GameSessionState.Open),
                (_, var value) when value.EndsWith("/close", StringComparison.Ordinal) => Session(GameSessionState.Closing),
                (_, var value) when value.EndsWith("/invites", StringComparison.Ordinal) => new InviteDto(
                    Guid.NewGuid(), SessionId, "Exact Player", "Ragnarok", new Uri("https://localhost/invite"), DateTimeOffset.UtcNow.AddMinutes(5)),
                (_, var value) when value.EndsWith("/deposits", StringComparison.Ordinal) => new DepositDto(
                    Guid.NewGuid(), SessionId, PlayerId, 100, idempotencyKey!.Value, DateTimeOffset.UtcNow),
                ("POST", "/api/v1/payout-events") => new PayoutEventAckDto(OperationId, 7, DateTimeOffset.UtcNow),
                (_, var value) when value.Contains("/cashouts/", StringComparison.Ordinal) => PayoutOperation(),
                _ => null,
            };

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(body, body.GetType(), options: ContractJson.Options),
                });
        }

        private static DealerAuthorizationDto Authorization() => new(
            PairingId,
            Guid.Parse("50000000-0000-0000-0000-000000000005"),
            "access-token",
            "refresh-credential",
            DateTimeOffset.UtcNow.AddMinutes(10),
            DateTimeOffset.UtcNow);

        private static GameSessionDto Session(GameSessionState state) => new(
            SessionId, "plinko", 0m, state, 0, 0, "pending", "clear", DateTimeOffset.UtcNow, null, null);

        private static PayoutOperationDto PayoutOperation() => new(
            OperationId,
            Guid.NewGuid(),
            SessionId,
            "Exact Player",
            "Ragnarok",
            100,
            PayoutOperationState.Failed,
            "failed",
            "Failed.",
            DateTimeOffset.UtcNow);
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path, Guid? IdempotencyKey);
}
