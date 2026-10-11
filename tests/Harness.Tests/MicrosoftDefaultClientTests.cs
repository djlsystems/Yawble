using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// THE INSTANCE'S MICROSOFT CLIENT, on the real Host with a FAKED Microsoft - no test calls a real one.
/// Started with <c>HARNESS_MICROSOFT_CLIENT_ID</c> and nothing saved, Microsoft is that public client:
/// audience <c>common</c>, a sign-in with a code, no secret. A saved client wins; with no variable,
/// Microsoft is not set up, as before; <c>clientSource</c> says which is in use.
/// </summary>
public sealed class MicrosoftDefaultClientTests
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string Account = "someone@example.test";
    private const string GraphMail = "https://graph.microsoft.com/Mail.Read";

    /// <summary>What the operator CLI would pass: any public client's ID, none of them real.</summary>
    private const string InstanceClient = "5d1f7e20-3c4b-4a8e-9f06-1b2c3d4e5f60";

    private const string SavedClient = "8f3c2a71-0b6e-4d2a-9c11-5e7f4a2b9d03";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_the_variable_set_and_nothing_saved_a_device_sign_in_starts_with_that_client_and_no_secret()
    {
        await using var host = await Instance.StartAsync(InstanceClient);

        var flowId = (await host.StartDeviceAsync()).GetProperty("flowId").GetString()!;
        var start = Assert.Single(host.Microsoft.Starts);
        Assert.Equal(InstanceClient, start.ClientId);
        Assert.False(start.SecretSet);
        Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/devicecode", start.Url);

        host.Microsoft.Answers.Writer.TryWrite("approve");
        var id = (await host.FlowAsync(flowId, until: "done")).GetProperty("connection").GetProperty("id").GetString()!;
        var poll = Assert.Single(host.Microsoft.Polls);
        Assert.Equal(InstanceClient, poll.ClientId);
        Assert.Null(poll.Secret);

        host.Clock.Advance(TimeSpan.FromHours(2));
        var (grant, refusal) = await host.Services.GetRequiredService<Connections>().GrantAsync(id, "mail", Ct);
        Assert.Null(refusal);
        Assert.NotNull(grant);
        var refresh = Assert.Single(host.Microsoft.Refreshes);
        Assert.Equal(InstanceClient, refresh.ClientId);
        Assert.Null(refresh.Secret);

        // The default client registers no redirect URI: the browser sign-in is refused with a sentence.
        var browser = await host.SendAsync(HttpMethod.Post, "/api/connections/start", new { provider = "microsoft", scopes = new[] { GraphMail } });
        Assert.Equal(HttpStatusCode.BadRequest, browser.Status);
        Assert.Contains("code", JsonDocument.Parse(browser.Body).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_saved_client_wins_over_the_instances()
    {
        await using var host = await Instance.StartAsync(InstanceClient);

        // Advanced's redirect client with a secret, for one tenant.
        var advanced = await host.SendAsync(HttpMethod.Put, "/api/connections/providers/microsoft",
            new { clientId = "advanced-client", clientSecret = "msecret-advanced-9Kz", tenant = "contoso.onmicrosoft.com" });
        Assert.True(advanced.Status == HttpStatusCode.OK, advanced.Body);

        var flowId = (await host.StartDeviceAsync()).GetProperty("flowId").GetString()!;
        var start = Assert.Single(host.Microsoft.Starts);
        Assert.Equal("advanced-client", start.ClientId);
        Assert.Equal("https://login.microsoftonline.com/contoso.onmicrosoft.com/oauth2/v2.0/devicecode", start.Url);
        host.Microsoft.Answers.Writer.TryWrite("approve");
        await host.FlowAsync(flowId, until: "done");
        Assert.Equal(("advanced-client", "msecret-advanced-9Kz"), (host.Microsoft.Polls[0].ClientId, host.Microsoft.Polls[0].Secret));

        // The guided public client a person saves wins alike, with its own audience.
        var guided = await host.SendAsync(HttpMethod.Put, "/api/connections/providers/microsoft",
            new { clientId = SavedClient, audience = "organizations" });
        Assert.True(guided.Status == HttpStatusCode.OK, guided.Body);
        await host.StartDeviceAsync();
        Assert.Equal(SavedClient, host.Microsoft.Starts[^1].ClientId);
        Assert.Equal("https://login.microsoftonline.com/organizations/oauth2/v2.0/devicecode", host.Microsoft.Starts[^1].Url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-client-id")]
    public async Task With_the_variable_empty_microsoft_reads_not_set_up_as_today(string? variable)
    {
        await using var host = await Instance.StartAsync(variable);

        var microsoft = await host.MicrosoftViewAsync();
        Assert.False(microsoft.GetProperty("configured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, microsoft.GetProperty("clientId").ValueKind);
        Assert.Equal(JsonValueKind.Null, microsoft.GetProperty("clientSource").ValueKind);

        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/connections/start",
            new { provider = "microsoft", scopes = new[] { GraphMail }, flow = "device" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("has no OAuth client yet", JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
        Assert.Empty(host.Microsoft.Starts);
    }

    [Fact]
    public async Task The_providers_route_answers_clientSource_default_or_saved()
    {
        // Through the Host's own wiring: the variable as the operator CLI passes it.
        await using var host = await Instance.StartAsync(InstanceClient, ownConnections: false);

        var microsoft = await host.MicrosoftViewAsync();
        Assert.Equal("default", microsoft.GetProperty("clientSource").GetString());
        Assert.True(microsoft.GetProperty("configured").GetBoolean());
        Assert.Equal("common", microsoft.GetProperty("audience").GetString());
        Assert.False(microsoft.GetProperty("clientSecretSet").GetBoolean());
        // The instance's ID is not a person's client: the forms that edit one never see it.
        Assert.Equal(JsonValueKind.Null, microsoft.GetProperty("clientId").ValueKind);
        Assert.DoesNotContain(InstanceClient, host.Responses);

        var google = (await host.GetAsync("/api/connections/providers")).EnumerateArray().Single(p => p.GetProperty("id").GetString() == "google");
        Assert.Equal(JsonValueKind.Null, google.GetProperty("clientSource").ValueKind);

        var saved = await host.SendAsync(HttpMethod.Put, "/api/connections/providers/microsoft", new { clientId = SavedClient, audience = "common" });
        Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);
        Assert.Equal("saved", JsonDocument.Parse(saved.Body).RootElement.GetProperty("clientSource").GetString());
        microsoft = await host.MicrosoftViewAsync();
        Assert.Equal("saved", microsoft.GetProperty("clientSource").GetString());
        Assert.Equal(SavedClient, microsoft.GetProperty("clientId").GetString());
    }

    [Fact]
    public async Task A_refusal_needing_an_organisations_admin_ends_with_one_sentence_and_no_code()
    {
        await using var host = await Instance.StartAsync(InstanceClient);

        var flowId = (await host.StartDeviceAsync()).GetProperty("flowId").GetString()!;
        host.Microsoft.Answers.Writer.TryWrite("admin");

        var refused = await host.FlowAsync(flowId, until: "refused");
        var sentence = refused.GetProperty("sentence").GetString()!;
        Assert.Equal(ConnectionProviders.MicrosoftAdminApproval, sentence);
        Assert.Contains("admin of that organisation approves the app", sentence);
        Assert.DoesNotContain("AADSTS", sentence);
        Assert.Single(sentence.Split(". ", StringSplitOptions.RemoveEmptyEntries));
        Assert.Empty((await host.GetAsync("/api/connections")).EnumerateArray());
    }

    /// <summary>One Host on its own data root, signed in as a person, with Microsoft faked.</summary>
    private sealed class Instance : IAsyncDisposable
    {
        private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-msdefault-{Guid.NewGuid():N}");
        private WebApplicationFactory<Program> _factory = null!;
        private HttpClient _person = null!;

        public FakeMicrosoft Microsoft { get; } = new();

        public Clock Clock { get; } = new();

        public List<string> Responses { get; } = [];

        public IServiceProvider Services => _factory.Services;

        /// <param name="ownConnections">False keeps the Host's own <see cref="Connections"/>, read from
        /// configuration as in production; true replaces it with one whose waits pass at once.</param>
        public static async Task<Instance> StartAsync(string? microsoftClient, bool ownConnections = true)
        {
            var instance = new Instance();
            Directory.CreateDirectory(instance._dataRoot);

            instance._factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            {
                host.UseSetting("DataRoot", instance._dataRoot).UseSetting("Logging:LogLevel:Default", "Warning");
                if (microsoftClient is not null) host.UseSetting(Connections.MicrosoftClientVariable, microsoftClient);
                host.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IAgentRunner>(new FakeAgent());
                    services.AddSingleton<IOAuthEndpoints>(instance.Microsoft);
                    if (!ownConnections) return;
                    services.AddSingleton(sp => new Connections(
                        sp.GetRequiredService<ConnectionStore>(), instance.Microsoft, instance.Clock, sp.GetRequiredService<IUserStore>(),
                        delay: (wait, _) =>
                        {
                            instance.Clock.Advance(wait);
                            return Task.CompletedTask;
                        },
                        microsoftClientId: sp.GetRequiredService<IConfiguration>()[Connections.MicrosoftClientVariable]));
                });
            });

            await instance.Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
            instance._person = instance._factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            (await instance._person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
            return instance;
        }

        public async Task<JsonElement> MicrosoftViewAsync() =>
            (await GetAsync("/api/connections/providers")).EnumerateArray().Single(p => p.GetProperty("id").GetString() == "microsoft");

        public async Task<JsonElement> StartDeviceAsync()
        {
            var (status, body) = await SendAsync(HttpMethod.Post, "/api/connections/start",
                new { provider = "microsoft", scopes = new[] { GraphMail }, flow = "device" });
            Assert.True(status == HttpStatusCode.OK, body);
            return JsonDocument.Parse(body).RootElement.Clone();
        }

        public async Task<JsonElement> FlowAsync(string flowId, string until)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);

            while (true)
            {
                var flow = await GetAsync($"/api/connections/flows/{flowId}");
                if (flow.GetProperty("state").GetString() == until) return flow;
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"The flow stayed {flow.GetProperty("state").GetString()}, not {until}.");
                await Task.Delay(20, Ct);
            }
        }

        public async Task<JsonElement> GetAsync(string path)
        {
            var (status, body) = await SendAsync(HttpMethod.Get, path, null);
            Assert.True(status == HttpStatusCode.OK, $"{path}: {status} {body}");
            return JsonDocument.Parse(body).RootElement.Clone();
        }

        public async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, object? body)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await _person.SendAsync(request, Ct);
            var text = await response.Content.ReadAsStringAsync(Ct);
            Responses.Add(text);
            return (response.StatusCode, text);
        }

        public async ValueTask DisposeAsync()
        {
            _person?.Dispose();
            await _factory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dataRoot, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// MICROSOFT, FAKED: it notes the client each request names, and whether a secret came with it.
    /// Each poll is answered in turn from <see cref="Answers"/>: <c>approve</c>, or <c>admin</c> - the
    /// refusal of an organisation that lets only an admin approve the app.
    /// </summary>
    private sealed class FakeMicrosoft : IOAuthEndpoints
    {
        public readonly List<(string? ClientId, bool SecretSet, string? Url)> Starts = [];
        public readonly List<(string? ClientId, string? Secret)> Polls = [];
        public readonly List<(string? ClientId, string? Secret)> Refreshes = [];
        public readonly Channel<string> Answers = Channel.CreateUnbounded<string>();
        private readonly Lock _lock = new();

        public Task<DeviceAuthorizationAnswer> DeviceAuthorizationAsync(OAuthProvider provider, IReadOnlyList<string> scopes, CancellationToken ct)
        {
            lock (_lock) Starts.Add((provider.ClientId, provider.ClientSecretSet, provider.DeviceAuthorizationUrl));
            return Task.FromResult(new DeviceAuthorizationAnswer(
                $"devicecode-{Guid.NewGuid():N}", "ABCD-EFGH", "https://microsoft.com/devicelogin", 900, 5, null, null));
        }

        public async Task<OAuthTokenAnswer> DeviceTokenAsync(OAuthProvider provider, string? clientSecret, string deviceCode, CancellationToken ct)
        {
            var answer = await Answers.Reader.ReadAsync(ct);
            lock (_lock) Polls.Add((provider.ClientId, clientSecret));

            return answer == "approve"
                ? new OAuthTokenAnswer($"access-{Guid.NewGuid():N}", $"refresh-{Guid.NewGuid():N}", 3600, null, IdToken(), null, null, false)
                : new OAuthTokenAnswer(null, null, null, null, null, "invalid_grant",
                    "AADSTS90094: The grant requires admin permission. Trace ID: 0000 Correlation ID: 0000", true);
        }

        public Task<OAuthTokenAnswer> RefreshAsync(OAuthProvider provider, string? clientSecret, string refreshToken, CancellationToken ct, IReadOnlyList<string>? scopes = null)
        {
            lock (_lock) Refreshes.Add((provider.ClientId, clientSecret));
            return Task.FromResult(new OAuthTokenAnswer($"access-{Guid.NewGuid():N}", null, 3600, null, null, null, null, false));
        }

        public Task<OAuthTokenAnswer> ExchangeCodeAsync(
            OAuthProvider provider, string? clientSecret, string code, string redirectUri, string codeVerifier, CancellationToken ct) =>
            Task.FromResult(new OAuthTokenAnswer(null, null, null, null, null, "invalid_grant", "No browser sign-in here.", true));

        public Task<JsonElement?> UserInfoAsync(OAuthProvider provider, string accessToken, CancellationToken ct) =>
            Task.FromResult<JsonElement?>(null);

        public Task<string?> RevokeAsync(OAuthProvider provider, string token, CancellationToken ct) => Task.FromResult<string?>(null);

        private static string IdToken()
        {
            static string Part(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

            return $"{Part(new { alg = "none" })}.{Part(new { email = Account, sub = "1234" })}.";
        }
    }

    /// <summary>A clock the test moves; the rest of the Host keeps the system's.</summary>
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
