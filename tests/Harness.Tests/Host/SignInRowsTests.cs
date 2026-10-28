using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// THE SIGN-IN PAGE'S ROWS ARE THE TABLE SETTING TYPE'S, on the real Host with a fixture plugin whose
/// <c>entries</c> table has a Site URL, extra hosts, Active, a <c>sign-in</c> row action on
/// <c>method = interactive</c> and a masked read-only <c>session</c> column. The Host's own
/// <see cref="ISignInRows"/> answers only an active interactive row, and keeps a session as that row's
/// masked secret cell in <see cref="PluginMemberSecretStore"/>: never in any settings answer, a tenant
/// row or a file in plain text, handed to the plugin's next run under <c>secrets</c>, and redacted from
/// what it writes.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SignInRowsTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private const string Entries = """
        {
          "type": "table",
          "key": "name",
          "columns": [
            { "name": "name", "type": "string", "required": true },
            { "name": "siteUrl", "type": "string" },
            { "name": "hosts", "type": "string" },
            { "name": "method", "type": "enum", "enum": ["password", "token", "interactive"], "required": true },
            { "name": "active", "type": "bool", "default": true },
            { "name": "password", "type": "secret" },
            { "name": "session", "type": "secret", "readOnly": true }
          ],
          "rowActions": [ { "action": "sign-in", "label": "Sign in", "when": { "column": "method", "equals": "interactive" } } ]
        }
        """;

    private const string Rows = """
        [
          {"name":"site-admin","siteUrl":"https://app.example.test/home","hosts":"www.example.test, https://API.example.test/x","method":"interactive"},
          {"name":"off","siteUrl":"https://app.example.test","method":"interactive","active":false},
          {"name":"pw","siteUrl":"https://app.example.test","method":"password"},
          {"name":"nowhere","siteUrl":"ftp://app.example.test","method":"interactive"}
        ]
        """;

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-sign-in-rows-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"harness-sign-in-rows-out-{Guid.NewGuid():N}");
    private readonly List<string> _responses = [];
    private readonly ToldWorker _workers = new();

    /// <summary>A worker that only listens: what control told it about sign-in profiles.</summary>
    private sealed class ToldWorker : IRunWorker
    {
        public System.Collections.Concurrent.ConcurrentQueue<ControlMessage> Sent { get; } = new();

        public WorkerId Id { get; } = new("w-told");

        public Task Closed { get; } = new TaskCompletionSource().Task;

        public Task SendAsync(ControlMessage message, CancellationToken ct = default)
        {
            Sent.Enqueue(message);
            return Task.CompletedTask;
        }
    }
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private string Requests => Path.Combine(_outside, "requests.jsonl");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_outside);

        // ECHOES its request, so a kept session's way into the run (and out of its output) is seen, and
        // prints each `piece-` value and each `value` field it finds on its own, as a plugin printing one cookie would,
        // then a fixed sentence of its own (`said:`) with short values as whole tokens and inside words.
        var script = $"req=$(cat); printf '%s\\n' \"$req\" >> '{Requests}'; printf '%s\\n' \"$req\"; printf '%s' \"$req\" | grep -o '[a-z]*piece-[0-9a-f]*' | sed 's/^/part: /'; printf '%s' \"$req\" | grep -o 'value[^,}}]*' | sed 's/^/value: /'; echo 'said: the check is true, trueness, True; mid k9x4z; x=k9x4z; abk9x4zcd; flag=code123;'; echo '{{\"t\":\"result\",\"ok\":true,\"output\":\"checked\"}}'";
        PluginInstall.Write(_dataRoot, "webcreds", script: script, manifest: PluginInstall.Manifest("webcreds", edit: m =>
            m["config"] = new JsonObject { ["entries"] = JsonNode.Parse(Entries) }));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());
                services.AddSingleton(new SignInProfileCleanup(() => [_workers]));
            }));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Signers", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Creds", agent = "plugin:webcreds" }, Ct);
        Assert.True(hired.StatusCode == HttpStatusCode.OK, await hired.Content.ReadAsStringAsync(Ct));

        var saved = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Creds/plugin-settings", new
        {
            config = new Dictionary<string, JsonElement> { ["entries"] = JsonDocument.Parse(Rows).RootElement.Clone() },
        });
        Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var directory in new[] { _dataRoot, _outside })
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private ContainerId Creds => new(_team, "Creds");

    [Fact]
    public async Task Only_an_active_interactive_row_with_a_site_url_is_an_entry_with_its_hosts_and_session_column()
    {
        var rows = Services.GetRequiredService<ISignInRows>();
        Assert.IsType<PluginSignInRows>(rows);

        var entry = await rows.EntryAsync(Creds, "entries", "SITE-ADMIN", Ct);
        Assert.NotNull(entry);
        Assert.Equal("https://app.example.test/home", entry.SiteUrl);
        Assert.Equal(["www.example.test", "api.example.test"], entry.Hosts);
        Assert.Equal("session", entry.SessionColumn);

        foreach (var row in new[] { "off", "pw", "nowhere", "ghost" })
        {
            Assert.Null(await rows.EntryAsync(Creds, "entries", row, Ct));
        }

        Assert.Null(await rows.EntryAsync(Creds, "password", "site-admin", Ct));
        Assert.Null(await rows.EntryAsync(new ContainerId(_team, "Nobody"), "entries", "site-admin", Ct));
    }

    [Theory]
    [InlineData("off", "switched off")]
    [InlineData("pw", "not interactive")]
    [InlineData("nowhere", "no http(s) Site URL")]
    public async Task Opening_a_row_that_cannot_be_signed_in_to_tells_the_person_why_and_starts_no_browser(string row, string why)
    {
        using var login = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        var answer = await login.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct);
        answer.EnsureSuccessStatusCode();
        var cookie = string.Join("; ", answer.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Cookie = cookie;
        using var socket = await client.ConnectAsync(new Uri(_factory.Server.BaseAddress, $"/api/teams/{_team}/members/Creds/sign-in/entries/{row}"), Ct);

        var buffer = new byte[64 * 1024];
        var first = await socket.ReceiveAsync(buffer, Ct);
        Assert.Equal(WebSocketMessageType.Text, first.MessageType);
        var ended = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, first.Count)).RootElement;
        Assert.Equal("ended", ended.GetProperty("kind").GetString());
        Assert.Equal("refused", ended.GetProperty("cause").GetString());
        var reason = ended.GetProperty("reason").GetString()!;
        Assert.Contains(row, reason, StringComparison.Ordinal);
        Assert.Contains(why, reason, StringComparison.Ordinal);
        Assert.Equal(WebSocketMessageType.Close, (await socket.ReceiveAsync(buffer, Ct)).MessageType);

        // A row that is not there at all is still not found.
        Assert.Equal(HttpStatusCode.NotFound, (await login.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/teams/{_team}/members/Creds/sign-in/entries/ghost")
        {
            Headers = { { "Cookie", cookie } },
        }, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_kept_session_is_the_rows_masked_secret_cell_never_answered_and_reaches_the_next_run_redacted()
    {
        var state = $$"""{"cookies":[{"name":"sid","value":"{{Guid.NewGuid():N}}","domain":"app.example.test"}],"origins":[]}""";
        var marker = JsonNode.Parse(state)!["cookies"]![0]!["value"]!.GetValue<string>();

        await Services.GetRequiredService<ISignInRows>().SaveSessionAsync(
            Creds, "entries", "site-admin", "session", new SignInStorageState(state), new CredentialActor("u1", Email), Ct);

        // Kept as the row's cell, through the member's own secret store.
        Assert.Equal(state, await Services.GetRequiredService<PluginMemberSecretStore>().CellValueAsync(Creds, "entries", "site-admin", "session", Ct));

        // Every settings answer says it is set, by whom - never its value.
        var settings = await GetAsync($"/api/teams/{_team}/members/Creds/plugin-settings");
        var cell = settings.GetProperty("tableSecretStates").GetProperty("entries").GetProperty("site-admin").GetProperty("session");
        Assert.True(cell.GetProperty("set").GetBoolean());
        Assert.Equal(Email, cell.GetProperty("setBy").GetString());
        Assert.False(cell.TryGetProperty("value", out _));
        await GetAsync($"/api/teams/{_team}/members/Creds/secrets");
        await GetAsync("/api/plugins");

        // The plugin gets it on its next run under secrets, and what it echoes back is redacted.
        var run = await TellAndAwaitAsync("Creds");
        var request = File.ReadAllLines(Requests).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!).Last();
        Assert.Equal(state, (string?)request["secrets"]!["entries"]!["site-admin"]!["session"]);
        Assert.DoesNotContain(marker, request["config"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(marker, run.Payload, StringComparison.Ordinal);

        Assert.All(_responses, body => Assert.DoesNotContain(marker, body, StringComparison.Ordinal));

        // Its tenant row names the row and column, never the value.
        var details = await ColumnAsync("SELECT COALESCE(detail, '') FROM tenant_events WHERE action LIKE 'member.secret-%'");
        Assert.Contains(details, d => d.Contains("\"row\":\"site-admin\"", StringComparison.Ordinal) && d.Contains("\"column\":\"session\"", StringComparison.Ordinal));
        Assert.All(details, d => Assert.DoesNotContain(marker, d, StringComparison.Ordinal));

        // Nothing under the data root holds it in plain text.
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(file, Ct); }
            catch (IOException) { continue; }

            Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)) < 0, $"A sign-in session is in {Path.GetRelativePath(_dataRoot, file)}.");
        }
    }

    [Fact]
    public async Task A_v2_session_with_its_browser_identity_is_kept_whole_never_answered_and_reaches_the_plugin()
    {
        var cookie = Guid.NewGuid().ToString("N");
        var identity = new SignInBrowserIdentity(
            "chrome", "156.0.8078.73", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/156.0.0.0 Safari/537.36",
            new SignInUserAgentMetadata(
                [new("Not:A-Brand", "8"), new("Chromium", "156"), new("Google Chrome", "156")],
                [new("Not:A-Brand", "8.0.0.0"), new("Chromium", "156.0.8078.73"), new("Google Chrome", "156.0.8078.73")],
                "Linux", "", "x86", "", false, "64"),
            "de-DE", ["de-DE", "de"], "Europe/Berlin", new SignInBrowserSize(1920, 1080), new SignInBrowserSize(1280, 800), 1);
        var envelope = SignInSessionValue.Write($$"""{"cookies":[{"name":"sid","value":"{{cookie}}","domain":"app.example.test"}],"origins":[]}""", identity);

        await Services.GetRequiredService<ISignInRows>().SaveSessionAsync(
            Creds, "entries", "site-admin", "session", new SignInStorageState(envelope), new CredentialActor("u1", Email), Ct);

        // Through the real store, whole, and it reads back as the envelope it was.
        var kept = await Services.GetRequiredService<PluginMemberSecretStore>().CellValueAsync(Creds, "entries", "site-admin", "session", Ct);
        Assert.Equal(envelope, kept);
        var (state, read) = SignInSessionValue.Read(kept!);
        Assert.Equal(identity with { Languages = read!.Languages, UserAgentMetadata = read.UserAgentMetadata }, read);
        Assert.Equal(identity.Languages, read.Languages);
        Assert.Equal(identity.UserAgentMetadata.FullVersionList, read.UserAgentMetadata.FullVersionList);
        Assert.Contains(cookie, state);
        using (var parsed = JsonDocument.Parse(kept!))
        {
            Assert.Equal(2, parsed.RootElement.GetProperty("v").GetInt32());
            Assert.Equal("Europe/Berlin", parsed.RootElement.GetProperty("identity").GetProperty("timezoneId").GetString());
            Assert.Equal("Google Chrome", parsed.RootElement.GetProperty("identity").GetProperty("userAgentMetadata").GetProperty("brands")[2].GetProperty("brand").GetString());
        }

        // No settings, secrets or plugins answer carries it.
        await GetAsync($"/api/teams/{_team}/members/Creds/plugin-settings");
        await GetAsync($"/api/teams/{_team}/members/Creds/secrets");
        await GetAsync("/api/plugins");
        Assert.All(_responses, body => Assert.DoesNotContain(cookie, body, StringComparison.Ordinal));

        // The plugin's next run has the whole envelope, identity and all.
        await TellAndAwaitAsync("Creds");
        var request = File.ReadAllLines(Requests).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!).Last();
        Assert.Equal(envelope, (string?)request["secrets"]!["entries"]!["site-admin"]!["session"]);
    }

    [Fact]
    public async Task A_plugin_printing_one_cookie_or_local_storage_value_of_a_session_has_it_redacted()
    {
        var hex = Guid.NewGuid().ToString("N");
        var (cookie, local, inner) = ("cpiece-" + hex, "lpiece-" + hex, "jpiece-" + hex);
        var json = JsonSerializer.Serialize(new { token = inner, theme = "dark" });
        var state = JsonSerializer.Serialize(new
        {
            cookies = new[] { new { name = "sid", value = cookie, domain = "app.example.test", path = "/" } },
            origins = new[] { new { origin = "https://app.example.test", localStorage = new[] { new { name = "t", value = local }, new { name = "j", value = json } } } },
        });
        var identity = new SignInBrowserIdentity(
            "chrome", "156.0.8078.73", "Mozilla/5.0", new SignInUserAgentMetadata([], [], "Linux", "", "x86", "", false, "64"),
            "en-US", ["en-US"], "UTC", new SignInBrowserSize(1920, 1080), new SignInBrowserSize(1280, 800), 1);
        await Services.GetRequiredService<ISignInRows>().SaveSessionAsync(
            Creds, "entries", "site-admin", "session", new SignInStorageState(SignInSessionValue.Write(state, identity)), new CredentialActor("u1", Email), Ct);

        var run = await TellAndAwaitAsync("Creds");

        // The plugin printed each value alone (its `part:` lines); every one is redacted.
        Assert.Contains("part: ", run.Payload, StringComparison.Ordinal);
        foreach (var part in new[] { cookie, local, inner }) Assert.DoesNotContain(part, run.Payload, StringComparison.Ordinal);
        Assert.Contains(DiagnosticRedaction.Placeholder, run.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plugin_printing_a_short_cookie_or_local_storage_value_has_it_redacted()
    {
        // A short code kept whole in a cookie (7 characters) or in local storage (5): each is redacted on its own.
        var (cookie, local) = ("Qz7k9wX", "Vb3Rn");
        var state = JsonSerializer.Serialize(new
        {
            cookies = new[] { new { name = "oauth_done", value = cookie, domain = "app.example.test", path = "/" } },
            origins = new[] { new { origin = "https://app.example.test", localStorage = new[] { new { name = "pin", value = local } } } },
        });
        var identity = new SignInBrowserIdentity(
            "chrome", "156.0.8078.73", "Mozilla/5.0", new SignInUserAgentMetadata([], [], "Linux", "", "x86", "", false, "64"),
            "en-US", ["en-US"], "UTC", new SignInBrowserSize(1920, 1080), new SignInBrowserSize(1280, 800), 1);
        await Services.GetRequiredService<ISignInRows>().SaveSessionAsync(
            Creds, "entries", "site-admin", "session", new SignInStorageState(SignInSessionValue.Write(state, identity)), new CredentialActor("u1", Email), Ct);

        var run = await TellAndAwaitAsync("Creds");

        // The plugin printed each value alone (its `value:` lines); neither is shown.
        Assert.Contains("value: ", run.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(cookie, run.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(local, run.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_short_session_value_is_redacted_only_as_a_whole_token_and_a_common_word_never()
    {
        // consent=true (a common word), a 5-character cookie and a 7-character code: D13.
        var state = JsonSerializer.Serialize(new
        {
            cookies = new[]
            {
                new { name = "mid", value = "k9x4z", domain = "app.example.test", path = "/" },
                new { name = "oauth_done", value = "code123", domain = "app.example.test", path = "/" },
            },
            origins = new[] { new { origin = "https://app.example.test", localStorage = new[] { new { name = "consent", value = "true" } } } },
        });
        var identity = new SignInBrowserIdentity(
            "chrome", "156.0.8078.73", "Mozilla/5.0", new SignInUserAgentMetadata([], [], "Linux", "", "x86", "", false, "64"),
            "en-US", ["en-US"], "UTC", new SignInBrowserSize(1920, 1080), new SignInBrowserSize(1280, 800), 1);
        await Services.GetRequiredService<ISignInRows>().SaveSessionAsync(
            Creds, "entries", "site-admin", "session", new SignInStorageState(SignInSessionValue.Write(state, identity)), new CredentialActor("u1", Email), Ct);

        var run = await TellAndAwaitAsync("Creds");

        var said = ((string)JsonNode.Parse(run.Payload)!["output"]!).Split('\n').Single(l => l.StartsWith("said: ", StringComparison.Ordinal));
        var placeholder = DiagnosticRedaction.Placeholder;
        // (a) `true` is a common word: never redacted, as a word, inside one, or in another case.
        Assert.Contains("the check is true, trueness, True;", said, StringComparison.Ordinal);
        // (b) a 5-character value as a whole token is redacted; (d) inside a longer word it is not.
        Assert.Contains($"mid {placeholder};", said, StringComparison.Ordinal);
        Assert.Contains($"x={placeholder};", said, StringComparison.Ordinal);
        Assert.Contains("abk9x4zcd;", said, StringComparison.Ordinal);
        // (c) a 7-character code is still redacted.
        Assert.Contains($"flag={placeholder};", said, StringComparison.Ordinal);
        Assert.DoesNotContain("code123", run.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_short_value_matches_only_as_a_whole_token_and_a_long_one_anywhere()
    {
        var redactor = ValueRedactor.For(["longsecret1"], ["k9x4z"]);
        var p = DiagnosticRedaction.Placeholder;

        Assert.Equal($"mid {p}; x={p}; abk9x4zcd; {p}_ {p}", redactor.Apply("mid k9x4z; x=k9x4z; abk9x4zcd; K9X4Z_ k9x4z"));
        Assert.Equal($"a{p}b", redactor.Apply("aLONGSECRET1b"));
        // An escape just before it is no word: JSON's quote, a newline, a percent-encoding.
        Assert.Equal($"\\u0022{p}\\u0022 \\n{p} %3D{p} ab\\{p} uk9x4z", redactor.Apply("\\u0022k9x4z\\u0022 \\nk9x4z %3Dk9x4z ab\\k9x4z uk9x4z"));
    }

    [Fact]
    public void A_whole_value_of_eight_or_more_is_redacted_anywhere_four_to_seven_as_a_whole_token_and_a_common_word_never()
    {
        var json = JsonSerializer.Serialize(new { code = "abc1234", token = "tok-12345678" });
        var state = JsonSerializer.Serialize(new
        {
            cookies = new[]
            {
                new { name = "a", value = "1" }, new { name = "b", value = "abc" }, new { name = "c", value = "abcd" }, new { name = "d", value = "a%20b" },
                new { name = "e", value = "code123" }, new { name = "f", value = "longcode1" }, new { name = "g", value = "TRUE" }, new { name = "h", value = "Dark" },
            },
            origins = new[] { new { origin = "https://a.test", localStorage = new[] { new { name = "x", value = "yes" }, new { name = "y", value = "Vb3Rn" }, new { name = "z", value = "undefined" }, new { name = "j", value = json } } } },
        });

        var parts = SignInSessionValue.SecretParts(state);

        // 8 or more, whole or inside JSON: anywhere. 4-7 whole (and a decoded form of 4-7): whole token only.
        foreach (var part in new[] { "longcode1", json, "tok-12345678" }) Assert.Contains(part, parts.Anywhere);
        foreach (var part in new[] { "abcd", "a%20b", "code123", "Vb3Rn" }) Assert.Contains(part, parts.WholeTokens);
        // 1-3 characters stay out, a decoded form under 4 stays out, a string inside JSON under 8 stays out,
        // and a common word never goes in, in any case and at any length.
        var all = parts.Anywhere.Concat(parts.WholeTokens).ToList();
        foreach (var part in new[] { "1", "abc", "yes", "a b", "abc1234", "TRUE", "Dark", "undefined" }) Assert.DoesNotContain(part, all);
    }

    [Fact]
    public void A_bare_storage_state_saved_before_the_envelope_still_reads_without_an_identity()
    {
        var (state, identity) = SignInSessionValue.Read("""{"cookies":[{"name":"sid","value":"x","domain":"a.test"}],"origins":[]}""");
        Assert.Null(identity);
        Assert.Contains("\"sid\"", state);
        Assert.Throws<JsonException>(() => SignInSessionValue.Read("""{"v":3,"storageState":{}}"""));
        Assert.Throws<JsonException>(() => SignInSessionValue.Read("""["not","a","session"]"""));
    }

    [Fact]
    public async Task A_row_removed_by_a_settings_save_takes_its_sign_in_profile_and_a_deleted_member_takes_all_of_its()
    {
        var kept = JsonNode.Parse(Rows)!.AsArray();
        kept.RemoveAt(0);
        var saved = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Creds/plugin-settings", new
        {
            config = new Dictionary<string, JsonElement> { ["entries"] = JsonDocument.Parse(kept.ToJsonString()).RootElement.Clone() },
        });
        Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);

        var row = SignInProfileKey.Row(Creds, "entries", "site-admin");
        Assert.Equal([row], _workers.Sent.OfType<DeleteSignInProfiles>().Select(d => d.Profile));

        var deleted = await _person.DeleteAsync($"/api/teams/{_team}/containers/Creds", Ct);
        Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync(Ct));
        Assert.Contains(_workers.Sent.OfType<DeleteSignInProfiles>(), d => d.Profile == SignInProfileKey.Member(Creds));
    }

    [Fact]
    public async Task A_session_is_refused_for_a_row_that_is_not_an_entry_and_a_person_may_not_set_it()
    {
        var rows = Services.GetRequiredService<ISignInRows>();
        var state = new SignInStorageState("""{"cookies":[],"origins":[]}""");
        var actor = new CredentialActor("u1", Email);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rows.SaveSessionAsync(Creds, "entries", "pw", "session", state, actor, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rows.SaveSessionAsync(Creds, "entries", "site-admin", "password", state, actor, Ct));
        Assert.Null(await Services.GetRequiredService<PluginMemberSecretStore>().CellValueAsync(Creds, "entries", "pw", "session", Ct));

        // The session column is the platform's: a person's settings save cannot write it.
        var forged = await SendAsync(HttpMethod.Put, $"/api/teams/{_team}/members/Creds/plugin-settings", new
        {
            config = new Dictionary<string, JsonElement> { ["entries"] = JsonDocument.Parse(Rows).RootElement.Clone() },
            tableSecretValues = new { entries = new Dictionary<string, object> { ["site-admin"] = new { session = "forged-session-value" } } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, forged.Status);
        Assert.Null(await Services.GetRequiredService<PluginMemberSecretStore>().CellValueAsync(Creds, "entries", "site-admin", "session", Ct));
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("a.test b.test,a.test", new[] { "a.test", "b.test" })]
    [InlineData("https://Login.Example.test/path; host! ~x", new[] { "login.example.test" })]
    public void A_hosts_cell_is_hosts_by_name_or_url(string? cell, string[] hosts) =>
        Assert.Equal(hosts, PluginSignInRows.Hosts(cell));

    private async Task<Message> TellAndAwaitAsync(string member)
    {
        var who = new ContainerId(_team, member);
        var log = Services.GetRequiredService<IMessageLog>();

        async Task<IReadOnlyList<Message>> Terminal() =>
            [.. (await log.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct)).Where(m => m.Source == who.ToString())];

        var told = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/{member}/tell", new { instruction = "check" }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if ((await Terminal()).FirstOrDefault() is { } row)
            {
                Assert.True(row.Type == MessageTypes.Completed, row.Payload);
                return row;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"{who} wrote no terminal row.");
    }

    private async Task<JsonElement> GetAsync(string path)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, path, null);
        Assert.True(status == HttpStatusCode.OK, $"{path}: {status} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _person.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        _responses.Add(text);
        return (response.StatusCode, text);
    }

    private async Task<IReadOnlyList<string>> ColumnAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) rows.Add(reader.GetString(0));
        return rows;
    }
}
