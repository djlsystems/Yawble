using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// WHAT A CONNECTION SHOULD ASK FOR, AND HOW TO SET ITS PROVIDER UP, on the real Host with a faked
/// provider - no test calls a real one. The scopes come from the installed plugins' connection slots,
/// never typed; each built-in provider serves its own setup guide, and no step carries a secret.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ConnectionNeedsTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string ClientSecret = "gsecret-never-in-a-guide-4Rk";
    private const string GmailModify = "https://www.googleapis.com/auth/gmail.modify";
    private const string DriveReadonly = "https://www.googleapis.com/auth/drive.readonly";
    private const string CalendarEvents = "https://www.googleapis.com/auth/calendar.events";
    private const string Unknown = "https://www.googleapis.com/auth/not-a-real-scope";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-needs-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);

        // Scopes as one list for every provider the slot admits.
        Install("mailer", """{"mail":{"description":"The mailbox.","providers":["google","microsoft"],"scopes":["GMAIL"],"required":true}}""");

        // Scopes keyed by provider, two slots, one of them Microsoft only.
        Install("filer", """
            {"files":{"description":"The drive.","providers":["google","microsoft"],"scopes":{"google":["DRIVE","GMAIL"],"microsoft":["Files.Read"]}},
             "calendar":{"providers":["microsoft"],"scopes":{"microsoft":["Calendars.Read"]}}}
            """);

        // A slot whose scope the Host has no words for.
        Install("odd", """{"thing":{"description":"Something odd.","providers":["google"],"scopes":{"google":["UNKNOWN","CALENDAR"]}}}""");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.AddSingleton<IOAuthEndpoints>(new RefusingEndpoints());
            }));

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    // ---- the needs read -------------------------------------------------------------------------

    [Fact]
    public async Task One_slot_answers_its_own_scopes_for_the_provider()
    {
        var needs = await GetAsync("/api/connections/needs?provider=google&plugin=filer&slot=files");

        Assert.Equal("google", needs.GetProperty("provider").GetString());
        var only = Assert.Single(needs.GetProperty("needs").EnumerateArray());
        Assert.Equal("filer", only.GetProperty("plugin").GetString());
        Assert.Equal("files", only.GetProperty("slot").GetString());
        Assert.Equal("The drive.", only.GetProperty("description").GetString());
        Assert.Equal([DriveReadonly, GmailModify], Strings(only.GetProperty("scopes")));
        Assert.Equal([DriveReadonly, GmailModify], needs.GetProperty("scopes").EnumerateArray().Select(s => s.GetProperty("scope").GetString()!));
    }

    [Fact]
    public async Task A_provider_answers_every_installed_slot_that_admits_it_each_with_its_plugin_merged()
    {
        var needs = await GetAsync("/api/connections/needs?provider=google");

        Assert.Equal(
            ["filer/files", "mailer/mail", "odd/thing"],
            needs.GetProperty("needs").EnumerateArray()
                .Select(n => n.GetProperty("plugin").GetString() + "/" + n.GetProperty("slot").GetString()).Order(StringComparer.Ordinal));

        var scopes = needs.GetProperty("scopes").EnumerateArray().ToDictionary(s => s.GetProperty("scope").GetString()!);

        // Merged and de-duplicated: gmail.modify once, naming both plugins that want it.
        Assert.Equal([CalendarEvents, DriveReadonly, GmailModify, Unknown], scopes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["filer", "mailer"], Strings(scopes[GmailModify].GetProperty("plugins")).Order(StringComparer.Ordinal));
        Assert.Equal(["odd"], Strings(scopes[Unknown].GetProperty("plugins")));

        // In words where the Host knows them; nothing guessed.
        Assert.Equal("Read, change and send your Gmail", scopes[GmailModify].GetProperty("words").GetString());
        Assert.Equal(JsonValueKind.Null, scopes[Unknown].GetProperty("words").ValueKind);

        // The APIs those scopes need, each with an enable link.
        Assert.Equal(
            ["calendar-json.googleapis.com", "drive.googleapis.com", "gmail.googleapis.com"],
            needs.GetProperty("apis").EnumerateArray().Select(a => a.GetProperty("api").GetString()!).Order(StringComparer.Ordinal));
        Assert.All(needs.GetProperty("apis").EnumerateArray(), a => Assert.Contains(
            a.GetProperty("api").GetString()!, a.GetProperty("link").GetString()!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_slot_keyed_by_provider_answers_that_providers_entry()
    {
        var microsoft = await GetAsync("/api/connections/needs?provider=microsoft");

        var byslot = microsoft.GetProperty("needs").EnumerateArray()
            .ToDictionary(n => n.GetProperty("plugin").GetString() + "/" + n.GetProperty("slot").GetString(), n => Strings(n.GetProperty("scopes")));

        Assert.Equal(["Files.Read"], byslot["filer/files"]);
        Assert.Equal(["Calendars.Read"], byslot["filer/calendar"]);
        // A list of scopes is every provider's: the Google scope is what mailer asks of Microsoft too.
        Assert.Equal([GmailModify], byslot["mailer/mail"]);
        Assert.False(byslot.ContainsKey("odd/thing"));
        Assert.Empty(microsoft.GetProperty("apis").EnumerateArray());

        var words = microsoft.GetProperty("scopes").EnumerateArray()
            .ToDictionary(s => s.GetProperty("scope").GetString()!, s => s.GetProperty("words").GetString());
        Assert.Equal("Read your files", words["Files.Read"]);
    }

    [Fact]
    public async Task Nothing_installed_gives_nothing()
    {
        Directory.Delete(PluginInstall.PluginsRoot(_dataRoot), recursive: true);
        await Services.GetRequiredService<PluginCatalog>().RescanAsync(Ct);

        foreach (var path in new[] { "/api/connections/needs?provider=google", "/api/connections/needs?provider=google&plugin=mailer&slot=mail" })
        {
            var needs = await GetAsync(path);
            Assert.Empty(needs.GetProperty("needs").EnumerateArray());
            Assert.Empty(needs.GetProperty("scopes").EnumerateArray());
            Assert.Empty(needs.GetProperty("apis").EnumerateArray());
        }
    }

    [Fact]
    public async Task The_needs_read_wants_a_provider()
    {
        using var response = await _person.GetAsync("/api/connections/needs", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_needs_read_is_a_persons_and_a_machine_principal_is_refused()
    {
        var team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Needs", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        var key = await Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(team, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container,
            team, TeamRegistry.ManagerPermits, ct: Ct);

        using var manager = _factory.CreateClient();
        manager.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);

        using var response = await manager.GetAsync("/api/connections/needs?provider=google", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- the setup guide ------------------------------------------------------------------------

    [Fact]
    public async Task Each_built_in_providers_guide_is_served_and_carries_no_secret()
    {
        var saved = await _person.PutAsJsonAsync("/api/connections/providers/google",
            new { clientId = "1234-abc.apps.googleusercontent.com", clientSecret = ClientSecret }, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain(ClientSecret, await saved.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var response = await _person.GetAsync("/api/connections/providers", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(ClientSecret, body, StringComparison.Ordinal);

        var providers = JsonDocument.Parse(body).RootElement.EnumerateArray().ToDictionary(p => p.GetProperty("id").GetString()!);

        foreach (var id in ConnectionProviders.BuiltIns)
        {
            var steps = providers[id].GetProperty("guide").GetProperty("steps").EnumerateArray().ToList();
            Assert.NotEmpty(steps);
            Assert.All(steps, s =>
            {
                Assert.False(string.IsNullOrWhiteSpace(s.GetProperty("id").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(s.GetProperty("title").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(s.GetProperty("text").GetString()));
                Assert.Equal(JsonValueKind.Array, s.GetProperty("copy").ValueKind);
            });

        }

        // Google's redirect URI to register is a value to copy, in the client step.
        var client = providers["google"].GetProperty("guide").GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("id").GetString() == "client");
        Assert.Contains(client.GetProperty("copy").EnumerateArray(), c => c.GetProperty("value").GetString()!.EndsWith(Connections.CallbackPath, StringComparison.Ordinal));

        Assert.Equal(
            ["project", "apis", "branding", "data-access", "client", "credentials"],
            providers["google"].GetProperty("guide").GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("id").GetString()!));

        var branding = providers["google"].GetProperty("guide").GetProperty("steps")[2].GetProperty("text").GetString()!;
        Assert.Contains("Publish app", branding, StringComparison.Ordinal);
        Assert.Contains("7 days", branding, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_microsoft_guide_sets_up_a_public_client_signed_in_with_a_code()
    {
        const string GraphMail = "https://graph.microsoft.com/Mail.Read";
        var providers = await GetAsync($"/api/connections/providers?scopes={Uri.EscapeDataString(GraphMail)}");
        var microsoft = providers.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "microsoft");
        var steps = microsoft.GetProperty("guide").GetProperty("steps").EnumerateArray().ToList();

        Assert.Equal(["register", "public-client", "data-access", "credentials"], steps.Select(s => s.GetProperty("id").GetString()!));

        // 1: Entra's new registration, and who can sign in.
        Assert.Equal("https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/CreateApplicationBlade", steps[0].GetProperty("link").GetString());
        Assert.Contains("who can sign in", steps[0].GetProperty("text").GetString()!, StringComparison.OrdinalIgnoreCase);

        // 2: public client flows on.
        Assert.Contains("Allow public client flows", steps[1].GetProperty("text").GetString()!, StringComparison.Ordinal);

        // 3: the delegated Graph permissions the scopes need, plus offline_access, each copyable,
        // and that a work tenant may need an admin's consent.
        Assert.Equal(["Mail.Read", "offline_access"], Strings(steps[2].GetProperty("copy"), "value").Where(v => v is "Mail.Read" or "offline_access"));
        Assert.Contains("admin", steps[2].GetProperty("text").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Strings(steps[2].GetProperty("copy"), "value"), v => v.StartsWith("https://", StringComparison.Ordinal));

        // 4: only the client ID, no secret and no redirect URI anywhere.
        Assert.Contains("Application (client) ID", steps[3].GetProperty("text").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain(steps, s => s.GetProperty("copy").EnumerateArray().Any(c => c.GetProperty("value").GetString()!.Contains(Connections.CallbackPath, StringComparison.Ordinal)));
        Assert.DoesNotContain(steps, s => s.GetProperty("title").GetString()!.Contains("secret", StringComparison.OrdinalIgnoreCase));

        Assert.True(microsoft.GetProperty("deviceFlow").GetBoolean());
        Assert.False(providers.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "google").GetProperty("deviceFlow").GetBoolean());
    }

    [Fact]
    public async Task A_google_guide_lists_exactly_the_apis_its_scopes_need()
    {
        var mailOnly = await GuideStepAsync("google", "apis", $"?scopes={Uri.EscapeDataString(GmailModify)}&scopes=openid");
        Assert.Equal(["gmail.googleapis.com"], Strings(mailOnly.GetProperty("copy"), "value"));
        Assert.Contains("gmail.googleapis.com", mailOnly.GetProperty("link").GetString()!, StringComparison.Ordinal);

        var none = await GuideStepAsync("google", "apis", "?scopes=openid&scopes=email");
        Assert.Empty(none.GetProperty("copy").EnumerateArray());

        // With no scopes named, the installed plugins' needs.
        var installed = await GuideStepAsync("google", "apis", "");
        Assert.Equal(
            ["calendar-json.googleapis.com", "drive.googleapis.com", "gmail.googleapis.com"],
            Strings(installed.GetProperty("copy"), "value").Order(StringComparer.Ordinal));

        var access = await GuideStepAsync("google", "data-access", $"?scopes={Uri.EscapeDataString(DriveReadonly)}");
        Assert.Contains(DriveReadonly, Strings(access.GetProperty("copy"), "value"));
    }

    [Fact]
    public async Task The_client_step_says_first_when_the_address_in_use_will_be_refused()
    {
        using var lan = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://192.168.1.20:8080"),
            AllowAutoRedirect = false,
        });
        (await lan.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        using var response = await lan.GetAsync("/api/connections/providers", Ct);
        var providers = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        var client = Step(providers, "google", "client");
        Assert.StartsWith("Google and Microsoft refuse a redirect URI on an IP address", client.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains("http://localhost:8080", client.GetProperty("text").GetString(), StringComparison.Ordinal);

        var local = Step(await GetAsync("/api/connections/providers"), "google", "client");
        Assert.DoesNotContain("refuse", local.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_google_client_id_not_shaped_like_one_is_refused_before_saving()
    {
        var refused = await _person.PutAsJsonAsync("/api/connections/providers/google", new { clientId = "1234-abc", clientSecret = ClientSecret }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains(".apps.googleusercontent.com", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var google = (await GetAsync("/api/connections/providers")).EnumerateArray().Single(p => p.GetProperty("id").GetString() == "google");
        Assert.Equal(JsonValueKind.Null, google.GetProperty("clientId").ValueKind);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private void Install(string id, string connections) =>
        PluginInstall.Write(_dataRoot, id, manifest: PluginInstall.Manifest(id, edit: m => m["connections"] = JsonNode.Parse(connections
            .Replace("GMAIL", GmailModify).Replace("DRIVE", DriveReadonly).Replace("CALENDAR", CalendarEvents).Replace("UNKNOWN", Unknown))));

    private async Task<JsonElement> GuideStepAsync(string provider, string step, string query) =>
        Step(await GetAsync("/api/connections/providers" + query), provider, step);

    private static JsonElement Step(JsonElement providers, string provider, string step) =>
        providers.EnumerateArray().Single(p => p.GetProperty("id").GetString() == provider)
            .GetProperty("guide").GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("id").GetString() == step);

    private static List<string> Strings(JsonElement array, string? property = null) =>
        [.. array.EnumerateArray().Select(e => (property is null ? e : e.GetProperty(property)).GetString()!)];

    private async Task<JsonElement> GetAsync(string path)
    {
        using var response = await _person.GetAsync(path, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>No provider is ever called here.</summary>
    private sealed class RefusingEndpoints : IOAuthEndpoints
    {
        public Task<OAuthTokenAnswer> ExchangeCodeAsync(OAuthProvider provider, string? clientSecret, string code, string redirectUri, string codeVerifier, CancellationToken ct) =>
            throw new InvalidOperationException("No provider is called in these tests.");

        public Task<OAuthTokenAnswer> RefreshAsync(OAuthProvider provider, string? clientSecret, string refreshToken, CancellationToken ct) =>
            throw new InvalidOperationException("No provider is called in these tests.");

        public Task<JsonElement?> UserInfoAsync(OAuthProvider provider, string accessToken, CancellationToken ct) =>
            throw new InvalidOperationException("No provider is called in these tests.");

        public Task<string?> RevokeAsync(OAuthProvider provider, string token, CancellationToken ct) =>
            throw new InvalidOperationException("No provider is called in these tests.");

        public Task<DeviceAuthorizationAnswer> DeviceAuthorizationAsync(OAuthProvider provider, IReadOnlyList<string> scopes, CancellationToken ct) =>
            throw new InvalidOperationException("No provider is called in these tests.");

        public Task<OAuthTokenAnswer> DeviceTokenAsync(OAuthProvider provider, string? clientSecret, string deviceCode, CancellationToken ct) =>
            throw new InvalidOperationException("No provider is called in these tests.");
    }
}
