using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A person may let the Concierge archive and unarchive a team. The setting
/// <c>concierge.mayArchive</c> is off by default; with it on, the Concierge holding <c>Archive</c>
/// archives through the person's own routes, and the tenant row names the person with
/// <c>viaConcierge</c>. A Manager, a member, a plugin and any key made with the permit are refused
/// whatever the setting; a person always may. Deleting a team stays a person's.
/// </summary>
public sealed class ConciergeArchiveTests : IAsyncLifetime
{
    private const string Person = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-concierge-archive-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private TeamRegistry Teams => Services.GetRequiredService<TeamRegistry>();

    private TenantSettings Settings => Services.GetRequiredService<TenantSettings>();

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .UseSetting("ScheduleRunnerEnabled", "false")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        await Services.GetRequiredService<IUserStore>().CreateAsync(Person, Password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Person, password = Password }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task With_the_setting_off_by_default_the_Concierge_is_refused_naming_it_and_a_person_is_always_allowed()
    {
        Assert.False(Settings.ConciergeMayArchive);
        var team = await TeamAsync("Off Shelf");
        var concierge = await ConciergeAsync();

        var reply = await concierge.ArchiveTeam("archive", team, Ct);
        Assert.StartsWith("HTTP 403", reply, StringComparison.Ordinal);
        Assert.Contains(TenantSettings.ConciergeMayArchiveName, reply, StringComparison.Ordinal);
        Assert.Contains("only a person turns it on", reply, StringComparison.Ordinal);
        Assert.False(Teams.IsArchived(team));

        // Over HTTP too: the route's own sentence names the setting.
        using (var raw = KeyClient(concierge.Key))
        {
            var refused = await raw.PostAsync($"/api/teams/{team}/archive", null, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(ConciergeArchiveGate.SettingOff, await ErrorAsync(refused));
        }

        // A person always may.
        Assert.Equal(HttpStatusCode.OK, (await _person.PostAsync($"/api/teams/{team}/archive", null, Ct)).StatusCode);
        Assert.True(Teams.IsArchived(team));
        Assert.StartsWith("HTTP 403", await concierge.ArchiveTeam("unarchive", team, Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await _person.PostAsync($"/api/teams/{team}/unarchive", null, Ct)).StatusCode);
        Assert.False(Teams.IsArchived(team));
    }

    [Fact]
    public async Task With_the_setting_on_the_Concierge_archives_and_unarchives_and_the_rows_name_the_person()
    {
        await SetAsync("on");
        var team = await TeamAsync("On Shelf");
        var concierge = await ConciergeAsync();

        var check = await concierge.ArchiveTeam("check", team, Ct);
        Assert.StartsWith("HTTP 200", check, StringComparison.Ordinal);
        Assert.Contains("\"quiet\":true", check, StringComparison.Ordinal);

        var archived = await concierge.ArchiveTeam("archive", team, Ct);
        Assert.StartsWith("HTTP 200", archived, StringComparison.Ordinal);
        Assert.True(Teams.IsArchived(team));
        Assert.True(Teams.IsPaused(team));
        Assert.Equal($"the Concierge for {Person}", Teams.All().Single(t => t.Id == team).ArchivedBy);

        var row = (await Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.TeamArchived, team, Ct))!;
        Assert.Equal(Person, row.ActorEmail);
        Assert.True(ViaConcierge(row));

        Assert.StartsWith("HTTP 200", await concierge.ArchiveTeam("unarchive", team, Ct), StringComparison.Ordinal);
        Assert.False(Teams.IsArchived(team));
        Assert.True(Teams.IsPaused(team));
        Assert.True(ViaConcierge((await Services.GetRequiredService<ITenantLog>().FindLatestAsync(TenantActions.TeamUnarchived, team, Ct))!));

        Assert.StartsWith("Refused:", await concierge.ArchiveTeam("delete", team, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manager_a_member_a_plugin_and_any_key_made_with_archive_are_refused_with_the_setting_on()
    {
        await SetAsync("on");
        var team = await TeamAsync("Kept");
        var owner = (await Services.GetRequiredService<IUserStore>().FindAsync(Person, Ct))!;
        var every = new HashSet<string>(Permits.All, StringComparer.Ordinal) { Permits.Archive };
        var principals = Services.GetRequiredService<IPrincipalStore>();

        var keys = new List<(string Who, string Key)>
        {
            ("Manager", await principals.MintAsync(
                new ContainerId(team, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container, team, every, ct: Ct)),
            ("member", await principals.MintAsync(
                new ContainerId(team, "Worker").ToString(), PrincipalKind.Container, team, every, ct: Ct)),
            ("plugin", await principals.MintAsync(
                new ContainerId(team, "plugin-widget").ToString(), PrincipalKind.Container, team, every, ct: Ct)),
            ("api key", await principals.MintAsync(
                "key-with-archive", PrincipalKind.ApiKey, null, every, ownerUserId: owner.Id, ct: Ct)),
            ("tenant concierge kind, other id", await principals.MintAsync(
                "not-the-concierge", PrincipalKind.TenantConcierge, null, every, ownerUserId: owner.Id, ct: Ct)),
            ("team concierge", await principals.MintAsync(
                "concierge-session", PrincipalKind.Concierge, team, every, ownerUserId: owner.Id, ct: Ct)),
        };

        foreach (var (who, key) in keys)
        {
            using var client = KeyClient(key);
            foreach (var action in new[] { "archive", "unarchive" })
            {
                var refused = await client.PostAsync($"/api/teams/{team}/{action}", null, Ct);
                Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{who} {action}: {refused.StatusCode}");
                Assert.Equal(PermitGate.HumansOnlyMessage, await ErrorAsync(refused));
            }
        }

        Assert.False(Teams.IsArchived(team));
    }

    [Fact]
    public void Archive_is_the_Concierges_permit_alone()
    {
        Assert.Contains(Permits.Archive, ConciergeLaunchFactory.ConciergePermits);
        Assert.DoesNotContain(Permits.Archive, TeamRegistry.ManagerPermits);
        Assert.DoesNotContain(Permits.Archive, Permits.All);

        var owner = "user-1";
        var concierge = new Principal(
            ConciergeLaunchFactory.PrincipalId(owner), PrincipalKind.TenantConcierge, ConciergeLaunchFactory.ConciergePermits, owner);
        Assert.True(ConciergeLaunchFactory.Strip(concierge).May(Permits.Archive));

        var every = new HashSet<string>(Permits.All, StringComparer.Ordinal) { Permits.Archive };
        foreach (var other in new[]
                 {
                     new Principal("alpha/Manager", PrincipalKind.Container, every),
                     new Principal("key", PrincipalKind.ApiKey, every, owner),
                     new Principal("other", PrincipalKind.TenantConcierge, every, owner),
                     new Principal(ConciergeLaunchFactory.PrincipalId(owner), PrincipalKind.TenantConcierge, every, "user-2"),
                     new Principal(ConciergeLaunchFactory.PrincipalId(owner), PrincipalKind.Concierge, every, owner),
                 })
        {
            var stripped = ConciergeLaunchFactory.Strip(other);
            Assert.False(stripped.May(Permits.Archive), other.Id);
            Assert.True(stripped.May(Permits.Read), other.Id);
        }
    }

    [Fact]
    public async Task Turning_the_setting_off_mid_session_refuses_the_next_call_without_a_restart()
    {
        await SetAsync("on");
        var first = await TeamAsync("First Shelf");
        var second = await TeamAsync("Second Shelf");
        var concierge = await ConciergeAsync();

        Assert.StartsWith("HTTP 200", await concierge.ArchiveTeam("archive", first, Ct), StringComparison.Ordinal);

        await SetAsync("off");
        var refused = await concierge.ArchiveTeam("archive", second, Ct);
        Assert.StartsWith("HTTP 403", refused, StringComparison.Ordinal);
        Assert.Contains("is off", refused, StringComparison.Ordinal);
        Assert.False(Teams.IsArchived(second));
        Assert.StartsWith("HTTP 403", await concierge.ArchiveTeam("unarchive", first, Ct), StringComparison.Ordinal);
        Assert.True(Teams.IsArchived(first));
    }

    [Fact]
    public async Task Delete_stays_a_persons_only_with_the_setting_on()
    {
        await SetAsync("on");
        var team = await TeamAsync("Not Yours");
        var concierge = await ConciergeAsync();

        using var raw = KeyClient(concierge.Key);
        var refused = await raw.DeleteAsync($"/api/teams/{team}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(PermitGate.HumansOnlyMessage, await ErrorAsync(refused));
        Assert.Equal(team, Teams.ExistingName(team));
    }

    // ---- helpers ----

    private async Task SetAsync(string value)
    {
        var response = await _person.PutAsJsonAsync(
            "/api/tenant/settings", new Dictionary<string, object> { [TenantSettings.ConciergeMayArchiveName] = value }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(value == "on", Settings.ConciergeMayArchive);
    }

    private async Task<string> TeamAsync(string name) =>
        (await Teams.CreateAsync(name, "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

    private static bool ViaConcierge(TenantEvent row)
    {
        using var detail = JsonDocument.Parse(row.Detail!);
        return detail.RootElement.TryGetProperty("viaConcierge", out var via) && via.ValueKind == JsonValueKind.True;
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("error").GetString()!;
    }

    private HttpClient KeyClient(string key)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);
        return client;
    }

    private sealed record KeyedTools(PlatformMcpTools Tools, string Key)
    {
        public Task<string> ArchiveTeam(string action, string team, CancellationToken ct) =>
            Tools.ArchiveTeam(action, team, ct);
    }

    /// <summary>The person's tenant Concierge, minted as <see cref="ConciergeLaunchFactory"/> mints it.</summary>
    private async Task<KeyedTools> ConciergeAsync()
    {
        var user = (await Services.GetRequiredService<IUserStore>().FindAsync(Person, Ct))!;
        var key = await Services.GetRequiredService<IPrincipalStore>().MintAsync(
            ConciergeLaunchFactory.PrincipalId(user.Id), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: user.Id, ct: Ct);

        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeyAuthenticationHandler.Header] = key;
        context.User = PrincipalClaims.ToClaimsPrincipal(new Principal("tools", PrincipalKind.TenantConcierge, new HashSet<string>()), "test");

        var tools = new PlatformMcpTools(
            new FixedAccessor(context),
            Services.GetRequiredService<IPrincipalStore>(),
            Services.GetRequiredService<AgentCatalog>(),
            new ServerClients(_factory.Server.CreateHandler()));
        return new KeyedTools(tools, key);
    }

    private sealed class FixedAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => context; set { } }
    }

    private sealed class ServerClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
    }
}
