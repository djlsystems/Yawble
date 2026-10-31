using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harness.Tests.Host;

/// <summary>
/// WEBSITE CONNECTIONS (CON 6) ON THE REAL HOST: the platform's sign-in page signs in to a local fake
/// site in Google Chrome and keeps the session on the connection; Test connection is the signed-in
/// check; a member asking Team Connections - the real plugin, the real worker runner, the platform's
/// Chrome - opens the check page and is answered with the page and a screenshot and no value; act is
/// refused while May act on pages is off and acts, held to the site's hosts, when it is on; a refused
/// sign-in is Needs you on the connection and a site that does not answer fails the run alone. No real
/// site and no real account.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class WebsiteConnectionsTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string SiteUser = "pat@example.test";
    private const string SitePassword = "site-password-7f3a91";
    private const string Session = "site-session-c0ffee9d";

    private static readonly HashSet<string> MemberPermits =
        new(StringComparer.Ordinal) { Permits.Progress, Permits.Skills, Permits.Read, Permits.Sites };

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-website-{Guid.NewGuid():N}");
    private readonly FakeAgent _agents = new();
    private readonly HashSet<string> _live = new(StringComparer.Ordinal);
    private int _loginPosts;
    /// <summary>Sessions have expired: arriving signs nobody in, and the dashboard answers 401 to a stale one.</summary>
    private volatile bool _expired;
    private WebApplicationFactory<Program> _factory = null!;
    private WebApplication _site = null!;
    private HttpClient _person = null!;
    private string _origin = "";
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private static readonly string Tc = TeamConnectionsMember.Name;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        _agents.Behaviour = invocation => Task.FromResult(new AgentResult(0, $"noted by {invocation.Container.Name}"));

        // THE FAKE SITE: arriving at / signs the visitor in (what a person does in the sign-in page), a
        // password form, a dashboard only a signed-in browser sees, a page that prints its own session, a
        // form to act on and a link off the site.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _site = builder.Build();
        string Page(string body) => $"<!doctype html><html><head><title>Fake portal</title></head><body>{body}</body></html>";
        bool SignedIn(HttpContext c) => c.Request.Cookies.TryGetValue("sess", out var s) && _live.Contains(s!);
        _site.MapGet("/", (HttpContext c) =>
        {
            if (_expired) return Results.Content(Page("<h1>Welcome</h1>You are not signed in."), "text/html");
            _live.Add(Session);
            c.Response.Cookies.Append("sess", Session, new CookieOptions { Expires = DateTimeOffset.UtcNow.AddHours(1) });
            return Results.Content(Page("<h1>Welcome</h1>You are signed in."), "text/html");
        });
        _site.MapGet("/dashboard", (HttpContext c) => SignedIn(c)
            ? Results.Content(Page("<h1>Dashboard</h1><p id=\"who\">Signed in as Pat</p>"), "text/html")
            : _expired ? Results.Content(Page("<h1>Session expired</h1>"), "text/html", statusCode: 401) : Results.Redirect("/login"));
        _site.MapGet("/login", () => Results.Content(Page(
            "<h1>Sign in</h1><form method=\"post\" action=\"/login\"><input type=\"email\" name=\"email\"/><input type=\"password\" name=\"password\"/><button type=\"submit\">Log in</button></form>"), "text/html"));
        _site.MapPost("/login", async (HttpContext c) =>
        {
            Interlocked.Increment(ref _loginPosts);
            var form = await c.Request.ReadFormAsync();
            if (form["email"] != SiteUser || form["password"] != SitePassword) return Results.Content(Page("<h1>Sign in</h1><p>Wrong username or password</p>"), "text/html");
            var token = "sess-" + Guid.NewGuid().ToString("N")[..12];
            _live.Add(token);
            c.Response.Cookies.Append("sess", token);
            return Results.Redirect("/dashboard");
        });
        _site.MapGet("/leaky", (HttpContext c) => SignedIn(c)
            ? Results.Content(Page($"<h1>Account</h1><p>Your session is {c.Request.Cookies["sess"]}.</p>"), "text/html")
            : Results.Redirect("/login"));
        _site.MapGet("/notes", (HttpContext c) => SignedIn(c)
            ? Results.Content(Page("<h1>Notes</h1><form method=\"post\" action=\"/notes\"><input id=\"note\" name=\"note\"/><button id=\"save\" type=\"submit\">Save</button></form><a id=\"off\" href=\"http://127.0.0.2:9/elsewhere\">Leave</a>"), "text/html")
            : Results.Redirect("/login"));
        _site.MapPost("/notes", async (HttpContext c) =>
        {
            var form = await c.Request.ReadFormAsync();
            return SignedIn(c) ? Results.Content(Page($"<h1>Saved</h1><p>Saved note: {form["note"]}</p>"), "text/html") : Results.Redirect("/login");
        });
        await _site.StartAsync(Ct);
        _origin = _site.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        _factory = Factory();

        _team = (await Services.GetRequiredService<TeamRegistry>().CreateAsync("Alpha", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Dev", agent = "claude-headless" }, Ct)).StatusCode);
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(_agents);
                // The worker's website runner, typing faster than a person does - still one key at a time.
                services.AddSingleton(new ConnectionTester(website: new WebsiteRunner(environment: new Dictionary<string, string> { ["WEB_CREDENTIALS_PACE"] = "0.2" })));
            }));

    /// <summary>The Host stopped and started again on the same data root, signed in as the person.</summary>
    private async Task RestartAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        _factory = Factory();
        _person = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        await _site.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    // ---- Done when 1 -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_website_signed_in_with_Sign_in_is_used_by_a_member_through_Team_Connections_and_the_answer_carries_the_page_and_a_screenshot_and_no_secret()
    {
        Assert.SkipWhen(SignInBrowserChromiumTests.Chromium() is null, SignInBrowserChromiumTests.NoChrome);

        var portal = await WebsiteAsync("Fake portal", ("siteUrl", _origin), ("method", "interactive"), ("checkUrl", "/dashboard"), ("checkText", "Signed in as"));
        await AssignAsync(portal);

        // Before Sign in, its test says a person signs in once - and marks it Needs you.
        var before = await TestAsync(portal);
        Assert.Equal(ConnectionRecord.NeedsReconnect, before.GetProperty("health").GetString());
        Assert.Contains("Sign in", before.GetProperty("sentence").GetString(), StringComparison.Ordinal);

        // SIGN IN, on the platform's sign-in page, in the connection's own profile.
        var saved = await SignInAsync(portal);
        Assert.Contains("127.0.0.1", saved.GetProperty("hosts").EnumerateArray().Select(h => h.GetString()));
        var connection = await ConnectionAsync(portal);
        Assert.True(connection["fields"]!.AsArray().Single(f => (string?)f!["name"] == "session")!["set"]!.GetValue<bool>());
        Assert.Equal(ConnectionRecord.Ok, (string?)connection["status"]);
        Assert.Contains(TenantActions.ConnectionSignedIn, await TenantActionsAsync());

        // TEST CONNECTION is the signed-in check, in the same browser.
        var tested = await TestAsync(portal);
        Assert.Equal(ConnectionRecord.Ok, tested.GetProperty("health").GetString());

        // A MEMBER ASKS TEAM CONNECTIONS to open its check page: the page, its text, a screenshot.
        using var dev = await KeyClientAsync(new ContainerId(_team, "Dev").ToString());
        var listed = Output(await TellAndAwaitAsync(dev, "list"));
        Assert.Contains("Fake portal (website): ok - check, session", listed, StringComparison.Ordinal);

        var check = await TellAndAwaitAsync(dev, "check Fake portal");
        Assert.Equal(MessageTypes.Completed, check.Type);
        var answer = Output(check);
        Assert.Contains("HTTP 200", answer, StringComparison.Ordinal);
        Assert.Contains("heading: Dashboard", answer, StringComparison.Ordinal);
        Assert.Contains("Signed in as Pat", answer, StringComparison.Ordinal);
        var shot = System.Text.RegularExpressions.Regex.Match(answer, @"screenshot: (Team Connections/fake-portal-[0-9-]+\.png)").Groups[1].Value;
        Assert.NotEqual("", shot);
        var png = await File.ReadAllBytesAsync(Path.Combine(_dataRoot, "documents", _team, shot), Ct);
        Assert.Equal("PNG", Encoding.ASCII.GetString(png, 1, 3));
        // Dev is answered with the same page, in its own workflow.
        var told = await AnswersToAsync("Dev", check.CausationSeq!.Value);
        Assert.Contains("Signed in as Pat", told, StringComparison.Ordinal);
        Assert.Contains(shot, told, StringComparison.Ordinal);

        // A page that prints the session: it is taken out of the answer.
        var leaky = Output(await TellAndAwaitAsync(dev, "check \"Fake portal\" /leaky"));
        Assert.Contains("Your session is [redacted]", leaky, StringComparison.Ordinal);

        // A path off the site's hosts is refused before the browser goes anywhere.
        var off = Output(await TellAndAwaitAsync(dev, "check Fake portal http://127.0.0.2:9/elsewhere"));
        Assert.Contains("not one of the connection's hosts", off, StringComparison.Ordinal);

        // SESSION hands a short-lived session to the member that asked, in its own workspace, as Web Credentials does.
        var session = Output(await TellAndAwaitAsync(dev, "session Fake portal for 5"));
        var handed = System.Text.RegularExpressions.Regex.Match(session, @"Dev:(\.team-connections-sessions/fake-portal-[0-9a-f]+\.json)").Groups[1].Value;
        Assert.NotEqual("", handed);
        var file = Path.Combine(_dataRoot, "teams", _team, "workspaces", "Dev", handed);
        Assert.Contains(Session, await File.ReadAllTextAsync(file, Ct), StringComparison.Ordinal);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(file));
        Assert.DoesNotContain(Session, session, StringComparison.Ordinal);

        // NO SECRET in any answer, row, log or file under the data root but the handoff itself.
        File.Delete(file);
        await AssertNowhereAsync(Session);
    }

    // ---- Done when 2 -----------------------------------------------------------------------------------

    [Fact]
    public async Task Act_is_refused_with_a_sentence_naming_May_act_on_pages_while_it_is_off_and_acts_held_to_the_sites_hosts_when_it_is_on()
    {
        Assert.SkipWhen(SignInBrowserChromiumTests.Chromium() is null, SignInBrowserChromiumTests.NoChrome);

        var portal = await WebsiteAsync("Portal", ("siteUrl", _origin), ("method", "password"), ("signInUrl", "/login"), ("checkUrl", "/dashboard"),
            ("checkText", "Signed in as"), ("username", SiteUser), ("password", SitePassword));
        await AssignAsync(portal);
        using var dev = await KeyClientAsync(new ContainerId(_team, "Dev").ToString());

        const string Act = "act Portal /notes fill \"#note\" \"hello from Dev\" then click \"#save\"";
        var refused = Output(await TellAndAwaitAsync(dev, Act));
        Assert.Contains("May act on pages", refused, StringComparison.Ordinal);
        Assert.Contains(Connections.MayActOffText("Portal"), refused, StringComparison.Ordinal);

        // A person turns the switch on - only a person can: every connection route is a person's.
        Assert.NotEqual(HttpStatusCode.OK, (await dev.PutAsJsonAsync($"/api/connections/{portal}/credentials", new { fields = new[] { new { name = "mayAct", value = "true" } } }, Ct)).StatusCode);
        var on = await _person.PutAsJsonAsync($"/api/connections/{portal}/credentials", new { fields = new[] { new { name = "mayAct", value = "true" } } }, Ct);
        Assert.True(on.IsSuccessStatusCode, await on.Content.ReadAsStringAsync(Ct));

        var acted = Output(await TellAndAwaitAsync(dev, Act));
        Assert.Contains("Saved note: hello from Dev", acted, StringComparison.Ordinal);
        Assert.Contains("done: fill #note; click #save", acted, StringComparison.Ordinal);
        Assert.Contains("screenshot: Team Connections/portal-", acted, StringComparison.Ordinal);

        // HELD TO THE SITE'S HOSTS: a click that leads off them is stopped in the browser and refused,
        // and the connection is still ok.
        var left = Output(await TellAndAwaitAsync(dev, "act Portal /notes click \"#off\""));
        Assert.Contains("127.0.0.2", left, StringComparison.Ordinal);
        Assert.Contains("off the connection's hosts", left, StringComparison.Ordinal);
        Assert.Equal(ConnectionRecord.Ok, (string?)(await ConnectionAsync(portal))["status"]);

        await AssertNowhereAsync(SitePassword);
    }

    // ---- Failures follow CON 4 ---------------------------------------------------------------------------

    [Fact]
    public async Task A_refused_sign_in_through_Team_Connections_is_a_connection_hold_a_passing_test_clears_it_and_tells_the_Manager_once_and_a_site_that_does_not_answer_fails_the_run_alone()
    {
        Assert.SkipWhen(SignInBrowserChromiumTests.Chromium() is null, SignInBrowserChromiumTests.NoChrome);

        var wrong = await WebsiteAsync("Wrong", ("siteUrl", _origin), ("method", "password"), ("signInUrl", "/login"), ("checkUrl", "/dashboard"),
            ("username", SiteUser), ("password", "not-the-password-1"));
        var down = await WebsiteAsync("Down", ("siteUrl", "http://127.0.0.1:9"), ("method", "password"), ("username", SiteUser), ("password", SitePassword));
        await AssignAsync(wrong);
        await AssignAsync(down);
        using var dev = await KeyClientAsync(new ContainerId(_team, "Dev").ToString());

        var refused = await TellAndAwaitAsync(dev, "check Wrong");
        Assert.Equal(MessageTypes.Failed, refused.Type);
        Assert.Contains("the sign-in was rejected", Output(refused), StringComparison.Ordinal);
        Assert.Equal(ConnectionRecord.NeedsReconnect, (string?)(await ConnectionAsync(wrong))["status"]);
        Assert.Contains(TenantActions.ConnectionNeedsReconnect, await TenantActionsAsync());

        // A CON 4 CONNECTION HOLD: the run waits for a person, held by the connection; Needs you lists the
        // connection once, naming the team and Team Connections, and no Failed item beside it; the member
        // reads held and the team's workflow does not read Failed.
        Assert.Equal(FailureClasses.NeedsPerson, Field(refused, PayloadFields.FailureClass));
        Assert.Equal(wrong, Field(refused, PayloadFields.HeldByConnection));
        await AssertHeldAsync(wrong, "the sign-in was rejected", refused);

        // While it needs a person, a member's command does not run the browser: the same hold.
        var posts = _loginPosts;
        var again = await TellAndAwaitAsync(dev, "check Wrong");
        Assert.Contains("Wrong needs a person, so it was not used", Output(again), StringComparison.Ordinal);
        Assert.Equal(posts, _loginPosts);
        Assert.Equal(FailureClasses.NeedsPerson, Field(again, PayloadFields.FailureClass));
        Assert.Equal(wrong, Field(again, PayloadFields.HeldByConnection));
        await AssertHeldAsync(wrong, "the sign-in was rejected", again);
        await ManagerRanAsync(refused);
        await ManagerRanAsync(again);

        // A person fixes it and tests it: the hold, the member's marks and Failed are cleared, as CON 4
        // clears them, and the Manager is told once in each workflow the connection held up.
        await _person.PutAsJsonAsync($"/api/connections/{wrong}/credentials", new { fields = new[] { new { name = "password", value = SitePassword } } }, Ct);
        Assert.Equal(ConnectionRecord.Ok, (await TestAsync(wrong)).GetProperty("health").GetString());
        await AssertClearedAsync(refused, again);
        var notices = await NoticesAsync();
        Assert.Equal([refused.CorrelationId, again.CorrelationId], notices.Select(n => n.CorrelationId).Distinct().Order());
        Assert.Equal(notices.Count, notices.Select(n => n.CorrelationId).Distinct().Count());
        Assert.All(notices, n => Assert.Contains("is ready now: a person put right the connection", n.Payload, StringComparison.Ordinal));

        // A second passing test sends no second notice.
        Assert.Equal(ConnectionRecord.Ok, (await TestAsync(wrong)).GetProperty("health").GetString());
        Assert.Equal(notices.Count, (await NoticesAsync()).Count);

        // A SITE THAT DOES NOT ANSWER fails that run only: the connection is left ok and nothing holds.
        var unreachable = await TellAndAwaitAsync(dev, "check Down");
        Assert.Equal(MessageTypes.Failed, unreachable.Type);
        Assert.Equal(ConnectionRecord.Ok, (string?)(await ConnectionAsync(down))["status"]);
        Assert.NotEqual(FailureClasses.NeedsPerson, Field(unreachable, PayloadFields.FailureClass));
        Assert.Null(Field(unreachable, PayloadFields.HeldByConnection));
        Assert.DoesNotContain(await NeedsYouAsync(), i => (string?)i["kind"] == NeedsYouKinds.Connection);

        await AssertNowhereAsync(SitePassword, "not-the-password-1");
    }

    [Fact]
    public async Task An_expired_session_through_Team_Connections_is_a_connection_hold_and_Sign_in_then_Done_clears_it()
    {
        Assert.SkipWhen(SignInBrowserChromiumTests.Chromium() is null, SignInBrowserChromiumTests.NoChrome);

        var portal = await WebsiteAsync("Fake portal", ("siteUrl", _origin), ("method", "interactive"), ("checkUrl", "/dashboard"), ("checkText", "Signed in as"));
        await AssignAsync(portal);
        await SignInAsync(portal);
        using var dev = await KeyClientAsync(new ContainerId(_team, "Dev").ToString());

        // The site forgets every session: the kept one has expired.
        _expired = true;
        _live.Clear();
        var expired = await TellAndAwaitAsync(dev, "check Fake portal");
        Assert.Equal(MessageTypes.Failed, expired.Type);
        Assert.Contains("is expired", Output(expired), StringComparison.Ordinal);
        Assert.Equal(ConnectionRecord.NeedsReconnect, (string?)(await ConnectionAsync(portal))["status"]);
        Assert.Equal(FailureClasses.NeedsPerson, Field(expired, PayloadFields.FailureClass));
        Assert.Equal(portal, Field(expired, PayloadFields.HeldByConnection));
        await AssertHeldAsync(portal, "Sign in", expired);
        await ManagerRanAsync(expired);

        // SIGN IN THEN DONE restores the connection and clears the same hold, telling the Manager once.
        _expired = false;
        await SignInAsync(portal);
        Assert.Equal(ConnectionRecord.Ok, (string?)(await ConnectionAsync(portal))["status"]);
        await AssertClearedAsync(expired);
        Assert.Equal(expired.CorrelationId, Assert.Single(await NoticesAsync()).CorrelationId);

        Assert.Equal(MessageTypes.Completed, (await TellAndAwaitAsync(dev, "check Fake portal")).Type);
        await AssertNowhereAsync(Session);
    }

    /// <summary>
    /// One Needs you item for <paramref name="connection"/>, a <c>connection</c> item naming the team and
    /// Team Connections, no Failed item for Team Connections; the member held by it, reading needs-person;
    /// each held run's workflow not Failed.
    /// </summary>
    private async Task AssertHeldAsync(string connection, string words, params Message[] runs)
    {
        var items = await NeedsYouAsync();
        var item = Assert.Single(items, i => (string?)i["kind"] == NeedsYouKinds.Connection);
        Assert.Equal(connection, (string?)item["connection"]!["id"]);
        Assert.Contains(words, (string?)item["words"], StringComparison.Ordinal);
        var held = Assert.Single(item["connection"]!["holds"]!.AsArray());
        Assert.Equal(_team, (string?)held!["team"]!["id"]);
        Assert.Equal([Tc], held["members"]!.AsArray().Select(m => (string?)m));
        Assert.DoesNotContain(items, i => (string?)i["kind"] == NeedsYouKinds.Failed && JsonSerializer.Serialize(i).Contains(Tc, StringComparison.Ordinal));

        var member = TeamConnections().Snapshot();
        Assert.Equal(FailureClasses.NeedsPerson, member.FailureClass);
        Assert.Contains(words, member.NeedsPerson, StringComparison.Ordinal);
        Assert.Equal(connection, TeamConnections().HeldByConnection);

        foreach (var run in runs)
        {
            var workflow = await WorkflowAsync(run.CorrelationId);
            Assert.NotEqual("Failed", (string?)workflow["state"]);
            Assert.Empty(workflow["failedMembers"]!.AsArray());
        }
    }

    /// <summary>No connection item, Team Connections' failed and needs-person marks clear, no held workflow Failed.</summary>
    private async Task AssertClearedAsync(params Message[] runs)
    {
        Assert.DoesNotContain(await NeedsYouAsync(), i => (string?)i["kind"] == NeedsYouKinds.Connection
            || JsonSerializer.Serialize(i).Contains(Tc, StringComparison.Ordinal));
        var member = TeamConnections().Snapshot();
        Assert.Null(member.Failed);
        Assert.Null(member.NeedsPerson);
        Assert.Null(TeamConnections().HeldByConnection);
        foreach (var run in runs)
        {
            Assert.NotEqual("Failed", (string?)(await WorkflowAsync(run.CorrelationId))["state"]);
        }
    }

    private MemberRuntime TeamConnections() => Services.GetRequiredService<ContainerHost>().Find(new ContainerId(_team, Tc))!;

    private async Task<IReadOnlyList<JsonNode>> NeedsYouAsync() =>
        [.. (await _person.GetFromJsonAsync<JsonObject>("/api/needs-you?part=live", Ct))!["items"]!.AsArray().Select(i => i!)];

    private async Task<JsonNode> WorkflowAsync(long correlation) =>
        (await _person.GetFromJsonAsync<JsonObject>($"/api/teams/{_team}/workflows", Ct))!["workflows"]!.AsArray()
            .Single(w => (long)w!["correlation"]! == correlation)!;

    private static string? Field(Message row, string field) =>
        JsonNode.Parse(row.Payload)![field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private async Task<IReadOnlyList<Message>> NoticesAsync()
    {
        var manager = MessageTypes.InstructionFor(new ContainerId(_team, TeamRegistry.DefaultManagerName));
        var rows = await Services.GetRequiredService<IMessageLog>().ReadAfterAsync(0, [manager], int.MaxValue, Ct);
        return [.. rows.Where(m => m.Payload.Contains(PluginReadyNotice.ReadyField, StringComparison.Ordinal))];
    }

    /// <summary>Waits until the Manager the failure woke has run and is idle again.</summary>
    private async Task ManagerRanAsync(Message failed)
    {
        var manager = new ContainerId(_team, TeamRegistry.DefaultManagerName);
        var log = Services.GetRequiredService<IMessageLog>();
        var host = Services.GetRequiredService<ContainerHost>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var ended = (await log.ReadAfterAsync(failed.Seq, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct))
                .Any(m => m.Source == manager.ToString() && m.CorrelationId == failed.CorrelationId);
            if (ended && host.Find(manager)!.State == ContainerState.Idle) return;
            await Task.Delay(50, Ct);
        }

        Assert.Fail("The Manager was not woken by the failure, or its run did not end.");
    }

    // ---- Done when 3 -----------------------------------------------------------------------------------

    /// <summary>The Web Credentials plugin's entries table and settings, as its manifest declares them.</summary>
    private const string WebCredentialsConfig = """
        {
          "languages": { "type": "list", "default": ["en-US"], "advanced": true, "setBy": "person" },
          "timeZone": { "type": "string", "default": "UTC", "advanced": true, "setBy": "person" },
          "entries": {
            "type": "table",
            "key": "name",
            "columns": [
              { "name": "name", "type": "string", "required": true },
              { "name": "siteUrl", "type": "string", "format": "url", "required": true },
              { "name": "hosts", "type": "string" },
              { "name": "method", "type": "enum", "enum": ["password", "token", "interactive"], "required": true },
              { "name": "active", "type": "bool", "default": true },
              { "name": "signInUrl", "type": "string", "format": "url" },
              { "name": "checkUrl", "type": "string", "format": "url" },
              { "name": "checkText", "type": "string" },
              { "name": "username", "type": "string" },
              { "name": "password", "type": "secret" },
              { "name": "totpSecret", "type": "secret" },
              { "name": "token", "type": "secret" },
              { "name": "tokenHeader", "type": "string", "default": "Authorization" },
              { "name": "session", "type": "secret", "readOnly": true },
              { "name": "status", "type": "string", "readOnly": true }
            ],
            "rowActions": [ { "action": "sign-in", "label": "Sign in", "when": { "column": "method", "equals": "interactive" } } ]
          }
        }
        """;

    [Fact]
    public async Task An_existing_instances_Web_Credentials_entries_become_website_connections_assigned_to_their_teams_and_work_through_Team_Connections()
    {
        Assert.SkipWhen(SignInBrowserChromiumTests.Chromium() is null, SignInBrowserChromiumTests.NoChrome);

        // AN INSTANCE FROM BEFORE THIS STEP: a Web Credentials member with a password entry and an entry a
        // person signed in to with its Sign in link.
        PluginInstall.Write(_dataRoot, WebCredentialsCarryOver.PluginId, "1.0.0", manifest: PluginInstall.Manifest(WebCredentialsCarryOver.PluginId, "1.0.0",
            m => m["config"] = JsonNode.Parse(WebCredentialsConfig)));
        await RestartAsync();
        var hired = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name = "Creds", agent = $"plugin:{WebCredentialsCarryOver.PluginId}" }, Ct);
        Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));
        var rows = $$"""
            [
              {"name":"portal-admin","siteUrl":"{{_origin}}","method":"password","signInUrl":"/login","checkUrl":"/dashboard","checkText":"Signed in as","username":"{{SiteUser}}"},
              {"name":"saved","siteUrl":"{{_origin}}","method":"interactive","checkUrl":"/dashboard","checkText":"Signed in as"}
            ]
            """;
        var saved = await _person.PutAsJsonAsync($"/api/teams/{_team}/members/Creds/plugin-settings", new
        {
            config = new Dictionary<string, JsonElement>
            {
                ["entries"] = JsonDocument.Parse(rows).RootElement.Clone(),
                ["languages"] = JsonDocument.Parse("""["en-gb"]""").RootElement.Clone(),
            },
        }, Ct);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(Ct));

        _live.Add(Session);
        var session = SignInSessionValue.Write(
            $$"""{"cookies":[{"name":"sess","value":"{{Session}}","domain":"127.0.0.1","path":"/","expires":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}},"httpOnly":false,"secure":false,"sameSite":"Lax"}],"origins":[]}""",
            new SignInBrowserIdentity("chrome", "156.0.0.0", "Mozilla/5.0", new SignInUserAgentMetadata([], [], "Linux", "", "x86", "", false, "64"),
                "en-GB", ["en-GB", "en"], "Europe/London", new SignInBrowserSize(1920, 1080), new SignInBrowserSize(1280, 800), 1));
        var cells = Services.GetRequiredService<PluginMemberSecretStore>();
        var creds = new ContainerId(_team, "Creds");
        var actor = new CredentialActor(null, Email);
        await cells.SetCellAsync(creds, "Creds", WebCredentialsCarryOver.PluginId, "entries", "portal-admin", "password", SitePassword, actor, Ct);
        await cells.SetCellAsync(creds, "Creds", WebCredentialsCarryOver.PluginId, "entries", "saved", "session", session, actor, Ct);

        // The starts so far had no Web Credentials member, so wrote no row: this is an instance from before the step.
        Assert.Empty(await CarriedOverDetailAsync());

        // THE FIRST START WITH THIS STEP.
        await RestartAsync();

        var connections = JsonNode.Parse(await _person.GetStringAsync("/api/connections", Ct))!.AsArray();
        var admin = connections.Single(c => (string?)c!["name"] == "Alpha portal-admin")!;
        var kept = connections.Single(c => (string?)c!["name"] == "Alpha saved")!;
        Assert.Equal(["website", "website"], new[] { (string?)admin["kind"], (string?)kept["kind"] });
        string? Field(JsonNode c, string name) => (string?)c["fields"]!.AsArray().Single(f => (string?)f!["name"] == name)!["value"];
        bool Set(JsonNode c, string name) => c["fields"]!.AsArray().Single(f => (string?)f!["name"] == name)!["set"]!.GetValue<bool>();
        Assert.Equal(("password", "/login", "en-GB", "false"), (Field(admin, "method"), Field(admin, "signInUrl"), Field(admin, "languages"), Field(admin, "mayAct")));
        Assert.True(Set(admin, "password"));
        Assert.True(Set(kept, "session"));
        var assigned = (await Services.GetRequiredService<IConnectionStore>().AssignedAsync(_team, Ct)).Select(a => a.ConnectionId).ToHashSet();
        Assert.Contains((string)admin["id"]!, assigned);
        Assert.Contains((string)kept["id"]!, assigned);

        // ONE ROW naming how many, and never a value; the plugin's own member stays.
        var detail = await CarriedOverDetailAsync();
        Assert.Single(detail);
        Assert.Equal(2, JsonNode.Parse(detail[0])!["count"]!.GetValue<int>());
        Assert.NotNull(Services.GetRequiredService<ContainerHost>().Find(creds));

        // THEY WORK THROUGH TEAM CONNECTIONS: the password one signs in, the saved one uses its session.
        using var dev = await KeyClientAsync(new ContainerId(_team, "Dev").ToString());
        var signedIn = Output(await TellAndAwaitAsync(dev, "check Alpha portal-admin"));
        Assert.Contains("Signed in as Pat", signedIn, StringComparison.Ordinal);
        var used = Output(await TellAndAwaitAsync(dev, "check Alpha saved"));
        Assert.Contains("Signed in as Pat", used, StringComparison.Ordinal);

        // ONCE: a later start makes nothing again.
        await RestartAsync();
        Assert.Single(await CarriedOverDetailAsync());
        Assert.Single(JsonNode.Parse(await _person.GetStringAsync("/api/connections", Ct))!.AsArray(), c => (string?)c!["name"] == "Alpha portal-admin");

        foreach (var row in detail) Assert.DoesNotContain(SitePassword, row, StringComparison.Ordinal);
        await AssertNowhereAsync(SitePassword, Session);
    }

    private async Task<IReadOnlyList<string>> CarriedOverDetailAsync()
    {
        await using var db = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")}");
        await db.OpenAsync(Ct);
        var read = db.CreateCommand();
        read.CommandText = "SELECT detail FROM tenant_events WHERE action = $action";
        read.Parameters.AddWithValue("$action", TenantActions.ConnectionsCarriedOver);
        var rows = new List<string>();
        await using var reader = await read.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) rows.Add(reader.GetString(0));
        return rows;
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private async Task<string> WebsiteAsync(string name, params (string Name, string Value)[] fields)
    {
        var added = await _person.PostAsJsonAsync("/api/connections/credentials", new
        {
            kind = "website", name, fields = fields.Select(f => new { name = f.Name, value = f.Value }),
        }, Ct);
        var body = await added.Content.ReadAsStringAsync(Ct);
        Assert.True(added.IsSuccessStatusCode, body);
        return (string)JsonNode.Parse(body)!["connection"]!["id"]!;
    }

    private async Task AssignAsync(string connection)
    {
        var assigned = await _person.PostAsJsonAsync($"/api/teams/{_team}/connections", new { connection }, Ct);
        Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync(Ct));
    }

    private async Task<JsonNode> ConnectionAsync(string id) =>
        JsonNode.Parse(await _person.GetStringAsync("/api/connections", Ct))!.AsArray().Single(c => (string?)c!["id"] == id)!;

    private async Task<JsonElement> TestAsync(string id)
    {
        var tested = await _person.PostAsync($"/api/connections/{id}/test", null, Ct);
        var body = await tested.Content.ReadAsStringAsync(Ct);
        Assert.True(tested.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>A person's Sign in on the connection: the page's socket, the site opened, Done pressed.</summary>
    private async Task<JsonElement> SignInAsync(string id)
    {
        using var login = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        var answer = await login.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct);
        answer.EnsureSuccessStatusCode();
        var cookie = string.Join("; ", answer.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Cookie = cookie;
        using var socket = await client.ConnectAsync(new Uri(_factory.Server.BaseAddress, $"/api/connections/{id}/sign-in?lang=en-gb"), Ct);
        var buffer = new byte[4 * 1024 * 1024];
        var texts = new List<string>();

        async Task<JsonElement> Until(string kind, Func<JsonElement, bool>? when = null)
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            bound.CancelAfter(TimeSpan.FromSeconds(60));
            while (true)
            {
                var length = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), bound.Token);
                    length += result.Count;
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Assert.Fail($"The socket closed before '{kind}': {string.Join(" | ", texts)}");
                }
                if (result.MessageType != WebSocketMessageType.Text) continue;
                var text = Encoding.UTF8.GetString(buffer, 0, length);
                texts.Add(text);
                var message = JsonDocument.Parse(text).RootElement;
                if (message.GetProperty("kind").GetString() == kind && (when is null || when(message))) return message.Clone();
            }
        }

        await Until("started");
        await Until("page", m => m.TryGetProperty("title", out var t) && t.GetString() == "Fake portal");
        await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"done"}"""), WebSocketMessageType.Text, true, Ct);
        var saved = await Until("saved");
        Assert.DoesNotContain(texts, t => t.Contains(Session, StringComparison.Ordinal));
        return saved;
    }

    private async Task<HttpClient> KeyClientAsync(string id)
    {
        var key = await Services.GetRequiredService<IPrincipalStore>().MintAsync(id, PrincipalKind.Container, _team, MemberPermits, ct: Ct);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private async Task<Message> TellAndAwaitAsync(HttpClient client, string instruction)
    {
        var who = new ContainerId(_team, Tc);
        var log = Services.GetRequiredService<IMessageLog>();

        async Task<IReadOnlyList<Message>> Rows() =>
            [.. (await log.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct)).Where(m => m.Source == who.ToString())];

        var after = (await Rows()).Select(m => m.Seq).DefaultIfEmpty(0).Max();
        var told = await client.PostAsJsonAsync($"/api/teams/{_team}/containers/{Tc}/tell", new { instruction }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        var deadline = DateTime.UtcNow.AddMinutes(4);
        while (DateTime.UtcNow < deadline)
        {
            if ((await Rows()).FirstOrDefault(m => m.Seq > after) is { } row)
            {
                var host = Services.GetRequiredService<ContainerHost>();
                while (host.Find(who)!.State != ContainerState.Idle && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
                return row;
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException($"Team Connections never finished '{instruction}'.");
    }

    private static string Output(Message row)
    {
        var payload = JsonNode.Parse(row.Payload)!;
        return ((string?)payload["output"] ?? "") + "\n" + ((string?)payload["error"] ?? "") + "\n" + ((string?)payload["reason"] ?? "");
    }

    private async Task<string> AnswersToAsync(string member, long told)
    {
        var log = Services.GetRequiredService<IMessageLog>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var answer = (await log.ReadAfterAsync(0, [MessageTypes.InstructionFor(new ContainerId(_team, member))], int.MaxValue, Ct))
                .FirstOrDefault(m => m.CausationSeq == told && m.Source == new ContainerId(_team, Tc).ToString());
            if (answer is not null) return (string?)JsonNode.Parse(answer.Payload)![PayloadFields.Instruction] ?? "";
            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"{member} was never answered.");
    }

    private async Task<IReadOnlyList<string>> TenantActionsAsync()
    {
        await using var db = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")}");
        await db.OpenAsync(Ct);
        var read = db.CreateCommand();
        read.CommandText = "SELECT action FROM tenant_events";
        var actions = new List<string>();
        await using var reader = await read.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) actions.Add(reader.GetString(0));
        return actions;
    }

    private async Task AssertNowhereAsync(params string[] secrets)
    {
        foreach (var file in Directory.EnumerateFiles(_dataRoot, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, Ct);
                bytes = memory.ToArray();
            }
            catch (IOException)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(bytes);
            foreach (var secret in secrets) Assert.False(text.Contains(secret, StringComparison.Ordinal), $"{secret} is in {file}");
        }
    }
}
