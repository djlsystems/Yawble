using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// The real Host over its own data root with two teams, each holding a real target for every verb
/// the cross-team tests try: a Manager to tell and to read the status of, and a kanban card to move.
/// Both teams are PAUSED before the card is made, so the tell that makes it queues and launches no
/// agent. Every credential is minted through <see cref="IPrincipalStore"/> in the shape the platform
/// mints it: a container as <c>AgentEnvironment</c> does, a person's key as <c>KeyEndpoints</c>
/// does, and the tenant Concierge as <c>ConciergeLaunchFactory</c> does. Containers hold every
/// permit, so only identity bounds them.
/// </summary>
public sealed class CrossTeamFixture : IAsyncLifetime
{
    public const string Password = "correct horse battery";
    public const string Email = "cross-team@example.test";

    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"harness-crossteam-test-{Guid.NewGuid():N}");

    private WebApplicationFactory<Program> _factory = null!;

    public string Alpha { get; private set; } = "";

    public string Beta { get; private set; } = "";

    public string ManagerName { get; } = TeamRegistry.DefaultManagerName;

    /// <summary>The `HARNESS_KEY` a Manager and a member on each team would run with.</summary>
    public IReadOnlyDictionary<string, string> ContainerKeys { get; private set; } =
        new Dictionary<string, string>();

    public string PersonApiKey { get; private set; } = "";

    public string ConciergeKey { get; private set; } = "";

    /// <summary>A card on each team's board, keyed by team.</summary>
    public IReadOnlyDictionary<string, string> Cards { get; private set; } =
        new Dictionary<string, string>();

    public IServiceProvider Services => _factory.Services;

    public static string Key(string team, string member) => $"{team}/{member}";

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host
                .UseSetting("DataRoot", _dataRoot)
                .UseSetting("Logging:LogLevel:Default", "Warning"));

        var registry = Services.GetRequiredService<TeamRegistry>();
        var principals = Services.GetRequiredService<IPrincipalStore>();
        var agent = Services.GetRequiredService<AgentCatalog>().Definitions
            .First(d => d.Mode == AgentMode.Headless).Name;

        Alpha = (await registry.CreateAsync("Alpha", agent, memberAgent: agent)).Id;
        Beta = (await registry.CreateAsync("Beta", agent, memberAgent: agent)).Id;

        var keys = new Dictionary<string, string>();

        foreach (var team in new[] { Alpha, Beta })
        {
            foreach (var member in new[] { ManagerName, "Worker" })
            {
                keys[Key(team, member)] = await principals.MintAsync(
                    new ContainerId(team, member).ToString(), PrincipalKind.Container, team, Permits.All);
            }
        }

        ContainerKeys = keys;

        var person = await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);

        PersonApiKey = await principals.MintAsync(
            "key-crossteam", PrincipalKind.ApiKey, team: null, Permits.All,
            ownerUserId: person.Id, label: "cross-team");

        ConciergeKey = await principals.MintAsync(
            ConciergeLaunchFactory.PrincipalId(person.Id), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: person.Id);

        using var client = await PersonAsync();
        var cards = new Dictionary<string, string>();

        foreach (var team in new[] { Alpha, Beta })
        {
            (await client.PostAsync($"/api/teams/{team}/pause", null)).EnsureSuccessStatusCode();

            (await client.PostAsJsonAsync(
                $"/api/teams/{team}/containers/{ManagerName}/tell",
                new { instruction = $"Seed a card on {team}." })).EnsureSuccessStatusCode();

            cards[team] = await FirstCardAsync(client, team);
        }

        Cards = cards;
    }

    private static async Task<string> FirstCardAsync(HttpClient client, string team)
    {
        var board = await client.GetStringAsync($"/api/teams/{team}/kanban/board");
        using var document = JsonDocument.Parse(board);

        return document.RootElement.GetProperty("cards").EnumerateArray()
            .Select(card => card.GetProperty("id").GetString())
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"No card on {team}'s board: {board}");
    }

    public HttpClient WithKey(string key)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
        return client;
    }

    public async Task<HttpClient> PersonAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password });

        login.EnsureSuccessStatusCode();
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
