using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harness.Tests;

/// <summary>
/// TEST CONNECTION (<c>POST /api/connections/{id}/test</c>) on the real Host, its test run by the
/// Host's own worker against FAKE servers only: a fake IMAP and SMTP server over real sockets, and a
/// fake Stripe, Namecheap, Twilio, sign-in page and OAuth provider answering through the worker's
/// HTTP handler - nothing reaches the network. A right value answers ok and a wrong one a refusal
/// sentence, for each kind that has a test, and the connection's health follows; a website and a
/// preset with nothing to try show no test; no secret is in an answer, a row or a log.
/// </summary>
public sealed class ConnectionTestTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string PersonPassword = "correct horse battery";
    private const string Account = "mailbox@example.com";
    private const string AppPassword = "abcd efgh ijkl mnop";

    private const string StripeKey = "sk_test_right_51Habc";
    private const string NamecheapKey = "namecheap-right-key-0123";
    private const string TwilioSecret = "twilio-right-secret-4567";
    private const string OtherToken = "other-right-token-89ab";
    private const string LoginPassword = "login-right-password";
    private const string RefreshToken = "refresh-right-token-cdef";
    private const string RotatedToken = "refresh-rotated-token-0011";
    private const string AccessToken = "access-token-issued-2233";
    private const string ClientSecret = "acme-client-secret-4455";
    private const string Wrong = "wrong-value-on-purpose";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-test-conn-{Guid.NewGuid():N}");
    private readonly FakeMailServer _mail = new(imapSecurity: "TLS", smtpSecurity: "STARTTLS") { Password = AppPassword };
    private readonly FakeServices _services = new();
    private readonly ConcurrentQueue<string> _logs = new();
    private readonly List<string> _responses = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private static readonly string[] Secrets =
        [AppPassword, StripeKey, NamecheapKey, TwilioSecret, OtherToken, LoginPassword, RefreshToken, RotatedToken, AccessToken, ClientSecret, Wrong];

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Trace")
            .ConfigureLogging(logging => logging.AddProvider(new ListLogger(_logs)))
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());

                // The Host saves a mailbox with a login, and its worker tests one with the same login:
                // both to the fake servers, trusting only their certificate.
                var login = new SocketMailLogin(_mail.Trust);
                services.AddSingleton<IMailLogin>(login);
                services.AddSingleton(new ConnectionTester(login, _services));
            }));

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, PersonPassword);
        _person = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = PersonPassword }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        await _mail.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    // ---- which connections have a test ---------------------------------------------------------------

    [Fact]
    public async Task Each_preset_says_whether_it_has_a_test_and_a_website_has_none()
    {
        var presets = (await GetAsync("/api/connections/credentials/presets")).EnumerateArray()
            .ToDictionary(p => $"{p.GetProperty("kind").GetString()}/{p.GetProperty("id").GetString()}", p => p.GetProperty("test").GetBoolean());

        Assert.Equal(
            new Dictionary<string, bool>
            {
                ["apikey/stripe"] = true, ["apikey/namecheap"] = true, ["apikey/twilio"] = true, ["apikey/other"] = true,
                ["login/"] = true, ["website/"] = false,
            },
            presets);
    }

    [Fact]
    public async Task A_website_an_other_key_with_no_url_and_a_login_with_no_url_have_no_test_and_record_nothing()
    {
        var website = await CredentialAsync("website", null, "Shop", []);
        var other = await CredentialAsync("apikey", "other", "Bare key", [new { name = "token", value = OtherToken, masked = true }]);
        var login = await CredentialAsync("login", null, "No address", [new { name = "username", value = "ada" }, new { name = "password", value = LoginPassword }]);

        foreach (var (id, words) in new[] { (website, "is a website"), (other, "has no test"), (login, "names no URL") })
        {
            Assert.False(Listed(await GetAsync("/api/connections"), id).GetProperty("testable").GetBoolean());

            var (status, body) = await TestAsync(id);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains(words, Error(body), StringComparison.Ordinal);
        }

        Assert.Empty(_services.Requests);
        Assert.DoesNotContain(await TenantRowsAsync(), r => r.Action == TenantActions.ConnectionTested);
    }

    // ---- API keys -------------------------------------------------------------------------------------

    [Fact]
    public async Task Stripe_reads_the_balance_with_the_key_ok_for_the_right_one_and_a_refusal_for_a_wrong_one()
    {
        _services.On("api.stripe.com", request =>
            request.RequestUri!.AbsolutePath == "/v1/balance" && request.Headers.Authorization?.ToString() == $"Bearer {StripeKey}"
                ? Json(HttpStatusCode.OK, """{"object":"balance"}""")
                : Json(HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid API Key provided: wrong***"}}"""));

        var id = await CredentialAsync("apikey", "stripe", "Stripe", [new { name = "key", value = StripeKey }]);
        Assert.True(Listed(await GetAsync("/api/connections"), id).GetProperty("testable").GetBoolean());

        await PassesAsync(id, "Connected: Stripe accepted the secret key.");

        await EditAsync(id, [new { name = "key", value = Wrong }]);
        await RefusedAsync(id, "Stripe refused the secret key (HTTP 401).");

        // RIGHT AGAIN: the next test clears what the last one recorded.
        await EditAsync(id, [new { name = "key", value = StripeKey }]);
        await PassesAsync(id, "Connected: Stripe accepted the secret key.");

        Assert.Equal(Enumerable.Repeat("GET api.stripe.com/v1/balance", 3), _services.Requests);
        await NothingLeakedAsync();
    }

    [Fact]
    public async Task Namecheap_reads_the_balances_and_a_status_error_is_its_refusal_with_namecheaps_reason()
    {
        _services.On("api.namecheap.com", request =>
        {
            var query = request.RequestUri!.Query;
            return query.Contains($"ApiKey={NamecheapKey}", StringComparison.Ordinal) && query.Contains("Command=namecheap.users.getBalances", StringComparison.Ordinal)
                ? Xml("""<ApiResponse Status="OK"><CommandResponse Type="namecheap.users.getBalances"/></ApiResponse>""")
                : Xml("""<ApiResponse Status="ERROR"><Errors><Error Number="1011102">API Key is invalid or API access has not been enabled</Error></Errors></ApiResponse>""");
        });

        var id = await CredentialAsync("apikey", "namecheap", "Namecheap", [new { name = "apiUser", value = "ada" }, new { name = "apiKey", value = NamecheapKey }]);
        await PassesAsync(id, "Connected: Namecheap accepted the API key.");

        await EditAsync(id, [new { name = "apiKey", value = Wrong }]);
        await RefusedAsync(id, "Namecheap refused the API key: API Key is invalid or API access has not been enabled.");

        await NothingLeakedAsync();
    }

    [Fact]
    public async Task Twilio_fetches_the_account_with_the_api_key_sid_and_secret()
    {
        var right = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"SK123:{TwilioSecret}"));
        _services.On("api.twilio.com", request =>
            request.RequestUri!.AbsolutePath == "/2010-04-01/Accounts/AC123.json" && request.Headers.Authorization?.ToString() == right
                ? Json(HttpStatusCode.OK, """{"sid":"AC123"}""")
                : Json(HttpStatusCode.Unauthorized, """{"code":20003,"message":"Authenticate"}"""));

        var id = await CredentialAsync("apikey", "twilio", "Twilio",
            [new { name = "accountSid", value = "AC123" }, new { name = "apiKeySid", value = "SK123" }, new { name = "apiKeySecret", value = TwilioSecret }]);
        await PassesAsync(id, "Connected: Twilio accepted the API key.");

        await EditAsync(id, [new { name = "apiKeySecret", value = Wrong }]);
        await RefusedAsync(id, "Twilio refused the API key (HTTP 401).");

        await NothingLeakedAsync();
    }

    [Fact]
    public async Task Other_with_a_url_and_one_masked_field_sends_it_as_a_bearer_token()
    {
        _services.On("api.service.test", request => request.Headers.Authorization?.ToString() == $"Bearer {OtherToken}"
            ? Json(HttpStatusCode.OK, "{}")
            : Json(HttpStatusCode.Forbidden, "{}"));

        var id = await CredentialAsync("apikey", "other", "Service",
            [new { name = "url", value = "https://api.service.test/v1/me", masked = false }, new { name = "token", value = OtherToken, masked = true }]);
        Assert.True(Listed(await GetAsync("/api/connections"), id).GetProperty("testable").GetBoolean());
        await PassesAsync(id, "Connected: api.service.test accepted the API key.");

        await EditAsync(id, [new { name = "url", value = "https://api.service.test/v1/me", masked = false }, new { name = "token", value = Wrong, masked = true }]);
        await RefusedAsync(id, "api.service.test refused the API key (HTTP 403).");

        await NothingLeakedAsync();
    }

    // ---- username and password ------------------------------------------------------------------------

    [Fact]
    public async Task A_username_and_password_with_a_url_signs_in_to_it_with_http_basic()
    {
        var right = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"ada:{LoginPassword}"));
        _services.On("portal.example.test", request => request.Headers.Authorization?.ToString() == right
            ? Json(HttpStatusCode.OK, "{}")
            : Json(HttpStatusCode.Unauthorized, "{}"));

        var id = await CredentialAsync("login", null, "Portal",
            [new { name = "username", value = "ada" }, new { name = "password", value = LoginPassword }, new { name = "url", value = "https://portal.example.test/account" }]);
        Assert.True(Listed(await GetAsync("/api/connections"), id).GetProperty("testable").GetBoolean());
        await PassesAsync(id, "Connected: portal.example.test accepted the username and password.");

        await EditAsync(id, [new { name = "password", value = Wrong }]);
        var refused = await RefusedAsync(id, "portal.example.test refused the username and password (HTTP 401).");

        // A REFUSED CREDENTIAL NOW BLOCKS A RUN with the test's sentence, as a refused refresh does.
        var (grant, refusal) = await Services.GetRequiredService<Connections>().GrantAsync(id, "portal", Ct);
        Assert.Null(grant);
        Assert.Contains(refused, refusal, StringComparison.Ordinal);

        await NothingLeakedAsync();
    }

    [Fact]
    public async Task A_sign_in_page_that_redirects_is_not_taken_as_a_yes()
    {
        _services.On("portal.example.test", _ =>
        {
            var answer = new HttpResponseMessage(HttpStatusCode.Found);
            answer.Headers.Location = new Uri("https://portal.example.test/login");
            return answer;
        });

        var id = await CredentialAsync("login", null, "Portal",
            [new { name = "username", value = "ada" }, new { name = "password", value = LoginPassword }, new { name = "url", value = "https://portal.example.test/" }]);

        await RefusedAsync(id, "portal.example.test answered with a redirect (HTTP 302) rather than accepting the username and password. Name the address that signs in.");
        Assert.Single(_services.Requests);
    }

    // ---- a mailbox ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_mailbox_logs_in_to_imap_and_smtp_ok_and_a_changed_password_is_refused()
    {
        var added = await SendAsync(HttpMethod.Post, "/api/connections/imap", new
        {
            preset = "other",
            name = "Work mail",
            account = Account,
            username = Account,
            password = AppPassword,
            imap = new { host = "127.0.0.1", port = _mail.ImapPort, security = _mail.ImapSecurity },
            smtp = new { host = "127.0.0.1", port = _mail.SmtpPort, security = _mail.SmtpSecurity },
        });
        Assert.True(added.Status == HttpStatusCode.OK, added.Body);
        var id = JsonDocument.Parse(added.Body).RootElement.GetProperty("connection").GetProperty("id").GetString()!;
        Assert.True(Listed(await GetAsync("/api/connections"), id).GetProperty("testable").GetBoolean());
        while (_mail.Logins.TryDequeue(out _)) { }

        await PassesAsync(id, "Connected: 1,240 messages in Inbox");
        Assert.Equal([$"imap ok {Account}", $"smtp ok {Account}"], _mail.Logins);

        // The password changed at the provider: the stored one is now wrong.
        _mail.Password = "a new password at the provider";
        await RefusedAsync(id, "The incoming (IMAP) server refused the password. Check the username, and use an app password where the provider issues one.");

        _mail.Password = AppPassword;
        await PassesAsync(id, "Connected: 1,240 messages in Inbox");

        await NothingLeakedAsync();
    }

    // ---- a sign-in ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_sign_in_is_refreshed_and_its_profile_read_ok_storing_a_rotated_refresh_token()
    {
        _services.On("auth.acme.test", request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Form(request).GetValueOrDefault("refresh_token") is RefreshToken
                && Form(request).GetValueOrDefault("client_secret") is ClientSecret
                && Form(request).GetValueOrDefault("grant_type") is "refresh_token"
                ? Json(HttpStatusCode.OK, $$"""{"access_token":"{{AccessToken}}","refresh_token":"{{RotatedToken}}","expires_in":3600}""")
                : Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}"""),
            "/userinfo" => request.Headers.Authorization?.ToString() == $"Bearer {AccessToken}"
                ? Json(HttpStatusCode.OK, """{"email":"ada@acme.test"}""")
                : Json(HttpStatusCode.Unauthorized, "{}"),
            _ => Json(HttpStatusCode.NotFound, "{}"),
        });

        var id = await SignInAsync(RefreshToken);
        Assert.True(Listed(await GetAsync("/api/connections"), id).GetProperty("testable").GetBoolean());

        await PassesAsync(id, "Connected: Acme renewed the sign-in and read the account's profile.");
        Assert.Equal(["POST auth.acme.test/token", "GET auth.acme.test/userinfo"], _services.Requests);

        // THE ROTATED TOKEN IS STORED: the next refresh sends it, and the old one is gone.
        Assert.Equal(RotatedToken, (await Services.GetRequiredService<IConnectionStore>().TokensAsync(id, Ct))!.RefreshToken);

        // The provider now refuses what is stored (it was rotated again elsewhere, or revoked).
        await RefusedAsync(id, "Acme refused to renew the sign-in (invalid_grant: Token has been expired or revoked.). Reconnect it.");

        await NothingLeakedAsync();
    }

    [Fact]
    public async Task A_renewed_sign_in_whose_profile_is_refused_needs_a_person()
    {
        _services.On("auth.acme.test", request => request.RequestUri!.AbsolutePath == "/token"
            ? Json(HttpStatusCode.OK, $$"""{"access_token":"{{AccessToken}}","expires_in":3600}""")
            : Json(HttpStatusCode.Forbidden, "{}"));

        var id = await SignInAsync(RefreshToken);
        await RefusedAsync(id, "Acme renewed the sign-in but refused to read the account's profile (HTTP 403). Reconnect it.");

        // No refresh token was issued again: the stored one stays.
        Assert.Equal(RefreshToken, (await Services.GetRequiredService<IConnectionStore>().TokensAsync(id, Ct))!.RefreshToken);
        await NothingLeakedAsync();
    }

    [Fact]
    public async Task A_sign_in_the_provider_cannot_be_reached_for_says_so_and_needs_a_person()
    {
        var id = await SignInAsync(RefreshToken);

        await RefusedAsync(id, "Could not reach auth.acme.test. The sign-in was not renewed.");
    }

    // ---- the protocol -------------------------------------------------------------------------------

    [Fact]
    public void The_probe_and_a_rotated_token_cross_a_worker_connection_only_sealed()
    {
        var key = WorkerFrameCodec.Key("worker-key", "control", "worker");
        var codec = new WorkerFrameCodec(key);
        var probe = new HttpProbe("Stripe", "secret key", "https://api.stripe.com/v1/balance",
            new Dictionary<string, string> { ["Authorization"] = $"Bearer {StripeKey}" }, [StripeKey]);

        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize<ControlMessage>(new TestConnection("r1", probe.Seal())));

        var command = codec.Write(new CommandFrame(1, new TestConnection("r1", probe.Seal())));
        Assert.DoesNotContain(StripeKey, command, StringComparison.Ordinal);
        var opened = Assert.IsType<TestConnection>(Assert.IsType<CommandFrame>(codec.Read(command)).Message);
        Assert.Equal(probe.Url, Assert.IsType<HttpProbe>(ConnectionProbe.Open(opened.Probe!)).Url);

        var answer = codec.Write(new EventFrame(new WorkerEnvelope(WorkerId.Local, 1,
            new ConnectionTested("r1", ConnectionTested.Ok, "Connected.", new ConnectionTestSecret(RotatedToken)))));
        Assert.DoesNotContain(RotatedToken, answer, StringComparison.Ordinal);
        var tested = Assert.IsType<ConnectionTested>(Assert.IsType<EventFrame>(codec.Read(answer)).Envelope.Event);
        Assert.Equal(RotatedToken, tested.RefreshToken!.Value);

        Assert.DoesNotContain(StripeKey, probe.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(RotatedToken, tested.ToString(), StringComparison.Ordinal);
    }

    // ---- helpers --------------------------------------------------------------------------------------

    private async Task<string> PassesAsync(string id, string sentence)
    {
        var (status, body) = await TestAsync(id);
        Assert.True(status == HttpStatusCode.OK, body);

        var answer = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ok", answer.GetProperty("health").GetString());
        Assert.Equal(sentence, answer.GetProperty("sentence").GetString());

        var listed = Listed(await GetAsync("/api/connections"), id);
        Assert.Equal("ok", listed.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("statusReason").ValueKind);

        var row = (await TenantRowsAsync()).Last(r => r.Action == TenantActions.ConnectionTested);
        Assert.Equal((Email, id), (row.ActorEmail, row.Subject));
        Assert.Contains("\"outcome\":\"ok\"", row.Detail);
        return sentence;
    }

    private async Task<string> RefusedAsync(string id, string sentence)
    {
        var (status, body) = await TestAsync(id);
        Assert.True(status == HttpStatusCode.OK, body);

        var answer = JsonDocument.Parse(body).RootElement;
        Assert.Equal("needs-reconnect", answer.GetProperty("health").GetString());
        Assert.Equal(sentence, answer.GetProperty("sentence").GetString());

        // THE LIST SHOWS IT where it shows every connection's health: needs reconnect, with the sentence.
        var listed = Listed(await GetAsync("/api/connections"), id);
        Assert.Equal("needs-reconnect", listed.GetProperty("status").GetString());
        Assert.Equal(sentence, listed.GetProperty("statusReason").GetString());

        var row = (await TenantRowsAsync()).Last(r => r.Action == TenantActions.ConnectionTested);
        Assert.DoesNotContain("\"outcome\":\"ok\"", row.Detail);
        return sentence;
    }

    /// <summary>No value a connection holds - right or wrong - is in an answer, a tenant row, a log line or a file the Host wrote.</summary>
    private async Task NothingLeakedAsync()
    {
        var rows = await TenantRowsAsync();

        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(_responses, r => r.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(_logs, l => l.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(rows, r => (r.Detail ?? "").Contains(secret, StringComparison.Ordinal));
        }

        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, Ct); }
            catch (IOException) { continue; }

            var text = Encoding.Latin1.GetString(bytes) + Encoding.Unicode.GetString(bytes);
            foreach (var secret in Secrets)
            {
                Assert.False(text.Contains(secret, StringComparison.Ordinal), $"{Path.GetRelativePath(_dataRoot, file)} holds a secret in plain text.");
            }
        }
    }

    private async Task<string> CredentialAsync(string kind, string? preset, string name, object[] fields)
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "/api/connections/credentials", new { kind, preset, name, fields });
        Assert.True(status == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("connection").GetProperty("id").GetString()!;
    }

    private async Task EditAsync(string id, object[] fields)
    {
        var (status, body) = await SendAsync(HttpMethod.Put, $"/api/connections/{id}/credentials", new { fields });
        Assert.True(status == HttpStatusCode.OK, body);
    }

    /// <summary>A custom provider at the fake host, and a connection signed in to it holding <paramref name="refresh"/>.</summary>
    private async Task<string> SignInAsync(string refresh)
    {
        var saved = await SendAsync(HttpMethod.Put, "/api/connections/providers/custom-acme", new
        {
            name = "Acme", clientId = "acme-client", clientSecret = ClientSecret, authorizeUrl = "https://auth.acme.test/authorize",
            tokenUrl = "https://auth.acme.test/token", userinfoUrl = "https://auth.acme.test/userinfo", defaultScopes = new[] { "read" },
        });
        Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);

        var record = new ConnectionRecord("conn-acme", "Acme account", "custom-acme", "ada@acme.test", ["read"], DateTimeOffset.UtcNow, null, ConnectionRecord.Ok, null);
        await Services.GetRequiredService<IConnectionStore>().InsertAsync(
            record, new ConnectionTokens(refresh, "access-old", DateTimeOffset.UtcNow.AddHours(1)), "u1",
            new TriggerAudit(null, null, TenantActions.ConnectionConnected, record.Id, record.Name, "{}"), Ct);
        return record.Id;
    }

    private Task<(HttpStatusCode Status, string Body)> TestAsync(string id) => SendAsync(HttpMethod.Post, $"/api/connections/{id}/test", null);

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _person.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        _responses.Add(text);
        return (response.StatusCode, text);
    }

    private async Task<JsonElement> GetAsync(string path)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, path, null);
        Assert.True(status == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static JsonElement Listed(JsonElement list, string id) => list.EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);

    private static string Error(string body) => JsonDocument.Parse(body).RootElement.GetProperty("error").GetString()!;

    private static Dictionary<string, string> Form(HttpRequestMessage request) =>
        request.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p.ElementAtOrDefault(1) ?? ""), StringComparer.Ordinal);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Xml(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };

    private async Task<IReadOnlyList<(string Action, string? ActorEmail, string? Subject, string? Detail)>> TenantRowsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT action, actor_email, subject, detail FROM tenant_events ORDER BY seq";
        var rows = new List<(string, string?, string?, string?)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>
    /// THE FAKE SERVICES: the worker's HTTP handler. A host with an answer set answers it; any other
    /// host is not reached, as an address that does not resolve is not. Each request is kept as
    /// "METHOD host/path" - never its query or headers.
    /// </summary>
    private sealed class FakeServices : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _hosts = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Requests { get; } = [];

        public void On(string host, Func<HttpRequestMessage, HttpResponseMessage> answer) => _hosts[host] = answer;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add($"{request.Method} {request.RequestUri!.Host}{request.RequestUri.AbsolutePath}");

            return _hosts.TryGetValue(request.RequestUri.Host, out var answer)
                ? Task.FromResult(answer(request))
                : Task.FromException<HttpResponseMessage>(new HttpRequestException($"No such host is known. ({request.RequestUri.Host})"));
        }
    }

    private sealed class ListLogger(ConcurrentQueue<string> lines) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue(formatter(state, exception) + (exception is null ? "" : "\n" + exception));

        public void Dispose() { }
    }
}
