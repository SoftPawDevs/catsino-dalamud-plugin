using System.Text.Json;
using Catsino.Plugin.Backend;
using Catsino.Plugin.Contracts;

namespace Catsino.Plugin.Tests;

public sealed class ProtocolFixtureTests
{
    [Fact]
    public void FixtureEnumeratesEveryRouteAndExactHubName()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "backend-v1.fixture.json")));
        var root = document.RootElement;
        Assert.Equal("1.6.0", root.GetProperty("contractVersion").GetString());
        var routes = root.GetProperty("routes").EnumerateArray().ToArray();
        Assert.Equal(30, routes.Length);

        var financialRoutes = routes.Where(route => route.GetProperty("financialMutation").GetBoolean()).ToArray();
        Assert.Equal(12, financialRoutes.Length);
        Assert.All(financialRoutes, route => Assert.True(route.GetProperty("idempotencyKeyRequired").GetBoolean()));
        var inviteRoute = Assert.Single(routes, route => route.GetProperty("operation").GetString() == "createInvite");
        Assert.False(inviteRoute.GetProperty("financialMutation").GetBoolean());
        Assert.False(inviteRoute.GetProperty("idempotencyKeyRequired").GetBoolean());

        var serverEvents = root.GetProperty("hub").GetProperty("serverToPlugin").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(PluginHubProtocol.ServerToPluginEvents, serverEvents);

        var reports = root.GetProperty("hub").GetProperty("pluginToServer").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(PluginHubProtocol.PluginToServerReports, reports);
        Assert.Equal(PluginHubProtocol.Path, root.GetProperty("hub").GetProperty("path").GetString());
    }

    [Fact]
    public void FixtureHasAnExampleForEveryNamedDto()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "backend-v1.fixture.json")));
        var examples = document.RootElement.GetProperty("dtoExamples");
        var names = document.RootElement.GetProperty("routes").EnumerateArray()
            .SelectMany(route => new[] { route.GetProperty("requestDto"), route.GetProperty("responseDto") })
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()!)
            .Select(value => value.Replace("[]", string.Empty, StringComparison.Ordinal).Replace("|null", string.Empty, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);

        Assert.All(names, name => Assert.True(examples.TryGetProperty(name, out _), $"Missing fixture example for {name}."));
    }

    [Fact]
    public void HighlightedDefinitionsMatchTheAuthoritativePublicTypes()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "backend-v1.fixture.json")));
        var root = document.RootElement;

        var now = DateTimeOffset.UtcNow;
        AssertDefinitionMatches(root, "PayoutExecutorStatusDto", new PayoutExecutorStatusDto(
            Guid.NewGuid(), true, false, Guid.NewGuid(), "ready", now));
        AssertDefinitionMatches(root, "GameSessionDto", new GameSessionDto(
            Guid.NewGuid(), "plinko", 0m, GameSessionState.Closing, 1, 100, "pending", "clear", now, now, now, 4));
        AssertDefinitionMatches(root, "SessionPlayerDto", new SessionPlayerDto(
            Guid.NewGuid(), Guid.NewGuid(), "Exact Player", "Ragnarok", SessionPlayerState.Open, 100, 50, "pending", "clear", now));
        AssertDefinitionMatches(root, "SessionRosterPlayerDto", new SessionRosterPlayerDto(
            Guid.NewGuid(), Guid.NewGuid(), "Exact Player", "Ragnarok", 125, 300, false, "none", "clear", now));
        AssertDefinitionMatches(root, "PendingInviteDto", new PendingInviteDto(
            Guid.NewGuid(), Guid.NewGuid(), "Exact Player", "Ragnarok", 100, now, now.AddMinutes(2)));
        AssertDefinitionMatches(root, "SessionRosterDto", new SessionRosterDto(Guid.NewGuid(), [], [], now));
        AssertDefinitionMatches(root, "AdjustPlayerBalanceRequest", new AdjustPlayerBalanceRequest(-100));
        AssertDefinitionMatches(root, "DealerCashOutRequest", new DealerCashOutRequest(true, false, 100, 5, 95));
        AssertDefinitionMatches(root, "SessionRemovalDto", new SessionRemovalDto(Guid.NewGuid(), "archived"));
        AssertDefinitionMatches(root, "CashOutPreviewResponse", new CashOutPreviewResponse(100, 5m, 5, 95, false, []));
        AssertDefinitionMatches(root, "CashOutResponse", new CashOutResponse(
            Guid.NewGuid(), Guid.NewGuid(), 100, 5m, 5, 95, "queued", 0, 0, [], now));
        AssertDefinitionMatches(root, "DepositDto", new DepositDto(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100, Guid.NewGuid(), now));
        AssertDefinitionMatches(root, "PayoutOperationDto", new PayoutOperationDto(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Exact Player", "Ragnarok", 100,
            PayoutOperationState.Failed, "error", "message", now));
        AssertDefinitionMatches(root, "PayoutEventDto", new PayoutEventDto(
            Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), PayoutEventType.TradeFailed,
            "Exact Player", "Ragnarok", 100, now, "error", "message", true));

        var definitions = root.GetProperty("dtoDefinitions");
        Assert.Equal("guid", definitions.GetProperty("PayoutExecutorStatusDto").GetProperty("executorInstanceId").GetString());
        Assert.Equal("string", definitions.GetProperty("GameSessionDto").GetProperty("gameType").GetString());
        Assert.Equal("int64|null", definitions.GetProperty("SessionPlayerDto").GetProperty("payoutGil").GetString());
        Assert.Equal("string", definitions.GetProperty("SessionPlayerDto").GetProperty("payoutState").GetString());
        Assert.Equal("string", definitions.GetProperty("SessionPlayerDto").GetProperty("reconciliationState").GetString());
        Assert.Equal("guid", definitions.GetProperty("DepositDto").GetProperty("idempotencyKey").GetString());
        Assert.Equal("utcDateTimeOffset", definitions.GetProperty("DepositDto").GetProperty("recordedAt").GetString());
        Assert.Equal("int64", definitions.GetProperty("SessionRosterPlayerDto").GetProperty("tokens").GetString());
        Assert.Equal("bool", definitions.GetProperty("SessionRosterPlayerDto").GetProperty("bettingLocked").GetString());
        Assert.Equal("int64", definitions.GetProperty("PendingInviteDto").GetProperty("initialBalanceGil").GetString());
        Assert.Equal("int64", definitions.GetProperty("AdjustPlayerBalanceRequest").GetProperty("amountGil").GetString());

        Assert.Equal(
            SerializeEnumValues<GameSessionState>(),
            root.GetProperty("enumDefinitions").GetProperty("GameSessionState").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Equal(
            SerializeEnumValues<SessionPlayerState>(),
            root.GetProperty("enumDefinitions").GetProperty("SessionPlayerState").EnumerateArray().Select(value => value.GetString()!).ToArray());
    }

    private static void AssertDefinitionMatches<T>(JsonElement root, string name, T value)
    {
        var example = root.GetProperty("dtoExamples").GetProperty(name);
        Assert.NotNull(JsonSerializer.Deserialize<T>(example.GetRawText(), ContractJson.Options));
        var serializedProperties = JsonSerializer.SerializeToElement(value, ContractJson.Options)
            .EnumerateObject().Select(property => property.Name).ToArray();
        var definedProperties = root.GetProperty("dtoDefinitions").GetProperty(name)
            .EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(serializedProperties, definedProperties);
    }

    private static string[] SerializeEnumValues<T>() where T : struct, Enum =>
        Enum.GetValues<T>()
            .Select(value => JsonSerializer.Serialize(value, ContractJson.Options).Trim('"'))
            .ToArray();
}
