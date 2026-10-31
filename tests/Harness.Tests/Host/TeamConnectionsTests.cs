using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// TEAM CONNECTIONS (<see cref="TeamConnectionsMember"/>), the member every team has, running the built-in
/// plugin of the same name from the platform's own <c>plugins/team-connections/</c>, on the real Host with
/// the real plugin process. Shown only while its team has a connection; nobody hires, removes, renames,
/// repoints or sets it up; a member's tell of <c>list</c> answers its team's connections by name, kind and
/// health, and <c>test &lt;name&gt;</c> runs that connection's own Test connection, with no value anywhere
/// and nothing of another team's. Stripe is a fake in the test process.
/// </summary>
public sealed class TeamConnectionsTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";
    private const string StripeKey = "sk_test_teamconn_51Hxyz";
    private const string OtherKey = "sk_test_otherteam_77Qrs";

    /// <summary>What a member hired today holds: never Tell.</summary>
    private static readonly HashSet<string> MemberPermits =
        new(StringComparer.Ordinal) { Permits.Progress, Permits.Skills, Permits.Read, Permits.Sites };

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-team-conn-{Guid.NewGuid():N}");
    private readonly FakeAgent _agents = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _alpha = "";
    private string _beta = "";
    private string _userId = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private static readonly string Tc = TeamConnectionsMember.Name;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        _agents.Behaviour = invocation => Task.FromResult(new AgentResult(0, $"noted by {invocation.Container.Name}"));
        _factory = Factory();

        var teams = Services.GetRequiredService<TeamRegistry>();
        _alpha = (await teams.CreateAsync("Alpha", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        _beta = (await teams.CreateAsync("Beta", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        _userId = (await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password)).Id;
        _person = await SignInAsync(_factory);

        foreach (var team in new[] { _alpha, _beta })
        {
            Assert.Equal(HttpStatusCode.OK, (await _person.PostAsJsonAsync(
                $"/api/teams/{team}/containers", new { name = "Dev", agent = "claude-headless" }, Ct)).StatusCode);
        }
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(_agents);
                services.AddSingleton(new ConnectionTester(new SocketMailLogin(), new FakeStripe()));
            }));

    private static async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    // ---- Done when 1: shown only while the team has a connection ---------------------------------------

    [Fact]
    public async Task A_team_with_no_connection_shows_no_team_connections_assigning_one_shows_it_and_removing_the_last_hides_it_keeping_its_runs()
    {
        // EVERY TEAM HAS ONE, made with it - and hidden: no card, not in status, not in the Manager's roster.
        Assert.NotNull(Services.GetRequiredService<ContainerHost>().Find(new ContainerId(_alpha, Tc)));
        Assert.False(await ShownAsync(_alpha));
        Assert.False(await InStatusAsync(_alpha));
        Assert.DoesNotContain(Tc, ManagerPrompt(_alpha), StringComparison.Ordinal);

        // ASSIGNING ONE SHOWS IT, on the board, in status and in the roster.
        var stripe = await StripeAsync("Billing", StripeKey);
        await AssignAsync(_alpha, stripe);
        Assert.True(await ShownAsync(_alpha));
        Assert.True(await InStatusAsync(_alpha));
        Assert.Contains(Tc, ManagerPrompt(_alpha), StringComparison.Ordinal);
        Assert.False(await ShownAsync(_beta));

        // It runs: a person's list is answered.
        var run = await TellAndAwaitAsync(_person, _alpha, "list");
        Assert.Equal(MessageTypes.Completed, run.Type);

        // REMOVING THE LAST HIDES IT AGAIN; its runs and history are kept.
        Assert.Equal(HttpStatusCode.NoContent, (await _person.DeleteAsync($"/api/teams/{_alpha}/connections/{stripe}", Ct)).StatusCode);
        Assert.False(await ShownAsync(_alpha));
        Assert.False(await InStatusAsync(_alpha));
        Assert.DoesNotContain(Tc, ManagerPrompt(_alpha), StringComparison.Ordinal);
        Assert.Contains(await Services.GetRequiredService<IMessageLog>().ReadAfterAsync(0, [MessageTypes.Completed], int.MaxValue, Ct),
            m => m.Seq == run.Seq);
        var runs = await _person.GetAsync($"/api/teams/{_alpha}/members/{Tc}/runs", Ct);
        var body = await runs.Content.ReadAsStringAsync(Ct);
        Assert.True(runs.IsSuccessStatusCode, body);
        Assert.Contains("Billing", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_team_made_before_team_connections_existed_gets_one_at_start_and_a_clone_gets_its_own()
    {
        // A team from before: its member is taken out under the Host, as an older volume never had one.
        await _person.DisposeAsyncSafe();
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await using (var db = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "messages.db")}"))
        {
            await db.OpenAsync(Ct);
            var delete = db.CreateCommand();
            delete.CommandText = "DELETE FROM team_members WHERE name = $name";
            delete.Parameters.AddWithValue("$name", Tc);
            Assert.True(await delete.ExecuteNonQueryAsync(Ct) >= 2);
        }

        SqliteConnection.ClearAllPools();
        _factory = Factory();
        _person = await SignInAsync(_factory);

        var host = Services.GetRequiredService<ContainerHost>();
        foreach (var team in new[] { _alpha, _beta })
        {
            var member = host.Find(new ContainerId(team, Tc));
            Assert.NotNull(member);
            Assert.Equal((TeamConnectionsMember.Label, TeamConnectionsMember.Agent), (member!.Snapshot().Name, member.Snapshot().Agent));
        }

        // A CLONE has its own, exactly one, shown when the original's assignments came with it.
        var stripe = await StripeAsync("Billing", StripeKey);
        await AssignAsync(_alpha, stripe);
        var clone = await Services.GetRequiredService<TeamRegistry>().CloneAsync(_alpha, "Alpha Copy", Ct);
        Assert.Empty(clone.Failures);
        Assert.Single(clone.Team.Containers, c => c.Id == Tc);
        Assert.True(await ShownAsync(clone.Team.Id));
    }

    // ---- Done when 2: nobody hires, removes, renames, repoints, uninstalls or takes its name -------------

    [Fact]
    public async Task No_route_lets_a_person_a_manager_or_the_concierge_hire_remove_rename_repoint_set_up_uninstall_or_take_its_name()
    {
        var manager = await KeyClientAsync(new ContainerId(_alpha, TeamRegistry.DefaultManagerName).ToString(), PrincipalKind.Container, _alpha,
            new HashSet<string>(StringComparer.Ordinal) { Permits.CreateContainer, Permits.Tell, Permits.Read, Permits.Progress });
        var concierge = await KeyClientAsync(ConciergeLaunchFactory.PrincipalId(_userId), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, _userId);

        foreach (var (who, client) in new[] { ("person", _person), ("manager", manager), ("concierge", concierge) })
        {
            // HIRE the built-in, or ANY member under its name.
            await RefusedAsync(who, "hire the built-in", await client.PostAsJsonAsync($"/api/teams/{_alpha}/containers",
                new { name = "Connections Two", agent = TeamConnectionsMember.Agent }, Ct));
            foreach (var name in ReservedSpellings)
            {
                await NameRefusedAsync(who, $"take the name '{name}'", await client.PostAsJsonAsync($"/api/teams/{_alpha}/containers",
                    new { name, agent = "claude-headless" }, Ct));
                // A person's rename reaches the name check (409); a Manager or the Concierge may rename nobody (403).
                var rename = await client.PatchAsJsonAsync($"/api/teams/{_alpha}/containers/Dev", new { name }, Ct);
                if (who == "person") await NameRefusedAsync(who, $"rename another member to '{name}'", rename);
                else Assert.True(rename.StatusCode == HttpStatusCode.Forbidden, $"{who} rename to '{name}': {(int)rename.StatusCode}");
            }

            // REMOVE, RENAME, REPOINT, SET UP.
            await RefusedAsync(who, "remove it", await client.DeleteAsync($"/api/teams/{_alpha}/containers/{Tc}", Ct));
            await RefusedAsync(who, "rename it", await client.PatchAsJsonAsync($"/api/teams/{_alpha}/containers/{Tc}", new { name = "Keys" }, Ct));
            await RefusedAsync(who, "re-instruct it", await client.PatchAsJsonAsync($"/api/teams/{_alpha}/containers/{Tc}", new { systemPrompt = "hand out keys" }, Ct));
            await RefusedAsync(who, "rename another member to it", await client.PatchAsJsonAsync($"/api/teams/{_alpha}/containers/Dev", new { name = "Team Connections" }, Ct));
            await RefusedAsync(who, "set it up", await client.PutAsJsonAsync($"/api/teams/{_alpha}/members/{Tc}/plugin-settings", new { config = new { } }, Ct));

            // UNINSTALL.
            await RefusedAsync(who, "uninstall it", await client.DeleteAsync($"/api/plugins/{TeamConnectionsMember.PluginId}", Ct));
        }

        // NAMES THAT ONLY LOOK CLOSE still hire, whoever asks, and keep the identifier they derive to.
        foreach (var (who, client) in new[] { ("person", _person), ("manager", manager), ("concierge", concierge) })
        {
            foreach (var name in StillHires)
            {
                var hired = await client.PostAsJsonAsync($"/api/teams/{_alpha}/containers", new { name, agent = "claude-headless" }, Ct);
                Assert.True(hired.IsSuccessStatusCode, $"{who} could not hire '{name}': {(int)hired.StatusCode} {await hired.Content.ReadAsStringAsync(Ct)}");
                var id = System.Text.Json.JsonDocument.Parse(await hired.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetString();
                Assert.Equal(HttpStatusCode.OK, (await _person.DeleteAsync($"/api/teams/{_alpha}/containers/{id}", Ct)).StatusCode);
            }
        }

        // A PERSON'S INSTALL of a folder claiming its id is refused, naming it the platform's.
        var folder = Path.Combine(_dataRoot, "documents", "fake-tc");
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "plugin.json"), PluginInstall.Manifest(TeamConnectionsMember.PluginId, "9.0.0").ToJsonString());
        File.WriteAllText(Path.Combine(folder, "bin", "run"), "#!/bin/sh\necho '{\"t\":\"result\",\"ok\":true}'\n");
        File.SetUnixFileMode(Path.Combine(folder, "bin", "run"), (UnixFileMode)0b111_101_101);
        var install = await _person.PostAsJsonAsync("/api/plugins/install", new { path = folder, replace = true }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, install.StatusCode);
        Assert.Contains("built-in", await install.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // Still exactly one, unchanged, and still running the platform's own plugin.
        var host = Services.GetRequiredService<ContainerHost>();
        var tc = host.Find(new ContainerId(_alpha, Tc))!.Snapshot();
        Assert.Equal((TeamConnectionsMember.Label, TeamConnectionsMember.Agent), (tc.Name, tc.Agent));
        Assert.Equal("Dev", host.Find(new ContainerId(_alpha, "Dev"))!.Snapshot().Name);
        Assert.True(Services.GetRequiredService<PluginCatalog>().For(TeamConnectionsMember.PluginId) is { BuiltIn: true, Manifest.Version: "1.0.0" });
    }

    // ---- Done when 3: list and test, with no secret anywhere ------------------------------------------------

    [Fact]
    public async Task A_members_tell_of_list_answers_its_teams_connections_by_name_kind_and_health_and_test_runs_the_connections_own_test_with_no_secret()
    {
        var billing = await StripeAsync("Billing", StripeKey);
        var shop = await CredentialAsync("website", null, "Supplier portal", []);
        await AssignAsync(_alpha, billing);
        await AssignAsync(_alpha, shop);

        using var dev = await KeyClientAsync(new ContainerId(_alpha, "Dev").ToString(), PrincipalKind.Container, _alpha, MemberPermits);

        // LIST, told by a member WITHOUT Tell, answers each by name, kind and health - and reaches Dev.
        var list = await TellAndAwaitAsync(dev, _alpha, "list");
        Assert.Equal(MessageTypes.Completed, list.Type);
        var listed = Output(list);
        Assert.Contains("Billing (apikey): ok", listed, StringComparison.Ordinal);
        Assert.Contains("Supplier portal (website): ok (no test)", listed, StringComparison.Ordinal);
        Assert.Contains(listed, await AnswersToAsync(_alpha, "Dev", list.CausationSeq!.Value), StringComparison.Ordinal);

        // TEST BY NAME runs CON 3's test: Stripe's balance read, the health recorded, a connections.tested row.
        var test = await TellAndAwaitAsync(dev, _alpha, "test billing");
        var tested = Output(test);
        Assert.Contains("Test connection 'Billing' (apikey): ok. Connected: Stripe accepted the secret key.", tested, StringComparison.Ordinal);
        Assert.Contains(tested, await AnswersToAsync(_alpha, "Dev", test.CausationSeq!.Value), StringComparison.Ordinal);
        Assert.Contains(await TenantActionsAsync(), a => a == TenantActions.ConnectionTested);

        // A WEBSITE STILL HAS NO TEST, as CON 3 says, in CON 3's own sentence.
        var website = Output(await TellAndAwaitAsync(dev, _alpha, "test Supplier portal"));
        Assert.Contains("is a website: its test is its own sign-in, which is not here yet.", website, StringComparison.Ordinal);
        Assert.DoesNotContain(": ok.", website, StringComparison.Ordinal);

        // A REFUSED KEY is CON 3's refusal and marks the connection, still with no value said.
        await _person.PutAsJsonAsync($"/api/connections/{billing}/credentials", new { fields = new[] { new { name = "key", value = "sk_test_wrong_00000000" } } }, Ct);
        var refused = Output(await TellAndAwaitAsync(dev, _alpha, "test Billing"));
        Assert.Contains("Test connection 'Billing' (apikey): needs reconnect.", refused, StringComparison.Ordinal);
        Assert.Equal(ConnectionRecord.NeedsReconnect, (await Services.GetRequiredService<IConnectionStore>().GetAsync(billing, Ct))!.Status);
        Assert.Contains("Billing (apikey): needs reconnect", Output(await TellAndAwaitAsync(dev, _alpha, "list")), StringComparison.Ordinal);

        // NO SECRET in any answer, any row, or any file under the data root.
        await AssertNowhereAsync(StripeKey, "sk_test_wrong_00000000");
    }

    // ---- Done when 4: never another team's ---------------------------------------------------------------

    [Fact]
    public async Task A_member_cannot_read_or_test_a_connection_assigned_only_to_another_team()
    {
        var ours = await StripeAsync("Billing", StripeKey);
        var theirs = await StripeAsync("Beta Books", OtherKey);
        await AssignAsync(_alpha, ours);
        await AssignAsync(_beta, theirs);

        using var dev = await KeyClientAsync(new ContainerId(_alpha, "Dev").ToString(), PrincipalKind.Container, _alpha, MemberPermits);

        var listed = Output(await TellAndAwaitAsync(dev, _alpha, "list"));
        Assert.Contains("Billing", listed, StringComparison.Ordinal);
        Assert.DoesNotContain("Beta Books", listed, StringComparison.Ordinal);

        var log = Services.GetRequiredService<IMessageLog>();
        var testsBefore = (await TenantActionsAsync()).Count(a => a == TenantActions.ConnectionTested);
        var tested = Output(await TellAndAwaitAsync(dev, _alpha, "test Beta Books"));
        Assert.Contains("no connection of that name is assigned to this team", tested, StringComparison.Ordinal);
        Assert.Equal(testsBefore, (await TenantActionsAsync()).Count(a => a == TenantActions.ConnectionTested));

        // AND THE OTHER TEAM'S Team Connections is as far away as any other team's plugin.
        var across = await dev.PostAsJsonAsync($"/api/teams/{_beta}/containers/{Tc}/tell", new { instruction = "list" }, Ct);
        Assert.NotEqual(HttpStatusCode.OK, across.StatusCode);
        Assert.Empty(await log.ReadAfterAsync(0, [MessageTypes.InstructionFor(new ContainerId(_beta, Tc))], int.MaxValue, Ct));

        await AssertNowhereAsync(StripeKey, OtherKey);
    }

    // ---- Done when 5: built-in, locked, in the image, not offered -----------------------------------------

    [Fact]
    public async Task The_plugin_is_in_the_catalog_built_in_and_locked_and_absent_from_hiring_and_the_marketplace()
    {
        var plugins = (await _person.GetFromJsonAsync<JsonObject>("/api/plugins", Ct))!["plugins"]!.AsArray();
        var tc = Assert.Single(plugins, p => (string?)p!["id"] == TeamConnectionsMember.PluginId)!;
        Assert.True((bool)tc["builtIn"]!);
        Assert.True((bool)tc["locked"]!);
        Assert.Null((string?)tc["marketplaceUrl"]);
        Assert.Empty(tc["members"]!.AsArray());

        // HIRING (the Manager's list) offers it nowhere.
        var hiring = await _person.GetFromJsonAsync<JsonObject>($"/api/teams/{_alpha}/hiring", Ct);
        Assert.DoesNotContain(hiring!["plugins"]!.AsArray(), p => (string?)p!["id"] == TeamConnectionsMember.PluginId);
    }

    [Fact]
    public void The_plugin_ships_beside_the_host_and_the_image_build_copies_it()
    {
        var shipped = Path.Combine(PluginCatalog.BuiltInFolder, TeamConnectionsMember.PluginId);
        Assert.True(File.Exists(Path.Combine(shipped, PluginManifest.FileName)));
        Assert.NotEqual(UnixFileMode.None, File.GetUnixFileMode(Path.Combine(shipped, "bin", "run")) & UnixFileMode.OtherExecute);

        var repo = RepoRoot();
        var containerfile = File.ReadAllText(Path.Combine(repo, "Containerfile"));
        Assert.Contains("COPY plugins/ plugins/", containerfile, StringComparison.Ordinal);
        Assert.Contains("!plugins/*/bin", File.ReadAllText(Path.Combine(repo, ".dockerignore")), StringComparison.Ordinal);
        Assert.Contains(@"..\..\plugins\**\*", File.ReadAllText(Path.Combine(repo, "src", "Harness.Host", "Harness.Host.csproj")), StringComparison.Ordinal);
    }

    /// <summary>Spellings of the reserved name: its letters, whatever separates or decorates them.</summary>
    private static readonly string[] ReservedSpellings =
    [
        "Team Connections", "team connections", " TEAM CONNECTIONS ", "TeamConnections", "TEAM-CONNECTIONS", "Team-Connections",
        "Team_Connections", "Team.Connections", "Team--Connections", "Team - Connections", "Team Connections!", "Team Connections 2",
        // Look-alike letters: Cyrillic ie, a, o, es, i; Greek omicron; Latin e acute; fullwidth Latin.
        "T\u0435am Connections", "Te\u0430m Connections", "Team C\u043Ennections", "Team \u0441onnections", "Team C\u03BFnnections",
        "Team Connect\u0456ons", "T\u00E9am Connections", "\uFF34eam Connections",
        "\uFF34\uFF45\uFF41\uFF4D \uFF23\uFF4F\uFF4E\uFF4E\uFF45\uFF43\uFF54\uFF49\uFF4F\uFF4E\uFF53",
        "T\u0435\u0430m C\u03BFnn\u0435\u0441t\u0456\u043Ens", "Te\u0301am Connections",
    ];

    /// <summary>Names that only look close, or are in another script, and still hire.</summary>
    private static readonly string[] StillHires =
        ["Connections", "Team", "Team Connection", "Team Connectionz", "Developer Rowan", "Developer Ren\u00E9e", "\u041A\u043E\u043C\u0430\u043D\u0434\u0430"];

    [Fact]
    public void The_reserved_name_is_its_letters_whatever_separates_them_and_hyphenated_names_still_derive_as_before()
    {
        foreach (var name in ReservedSpellings) Assert.True(TeamConnectionsMember.IsReserved(name), name);
        foreach (var name in StillHires.Concat(["Team Connections Two", "My Team Connections", "T\u4E00am Connections", "", null]))
            Assert.False(TeamConnectionsMember.IsReserved(name), name ?? "null");

        // The real member keeps its name and id; DeriveName is unchanged for a hyphen or underscore.
        Assert.Equal(("Team Connections", "TeamConnections"), (TeamConnectionsMember.Label, TeamConnectionsMember.Name));
        Assert.Equal("Team-Connections", ContainerId.DeriveName("Team-Connections"));
        Assert.Equal("Team_Connections", ContainerId.DeriveName("Team_Connections"));
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx"))) directory = directory.Parent;
        return directory!.FullName;
    }

    private async Task<bool> ShownAsync(string team) =>
        (await _person.GetFromJsonAsync<JsonArray>("/api/teams", Ct))!
            .Single(t => (string?)t!["id"] == team)!["containers"]!.AsArray()
            .Any(c => (string?)c!["id"] == Tc);

    private async Task<bool> InStatusAsync(string team) =>
        (await _person.GetFromJsonAsync<JsonObject>("/api/overview", Ct))!["teams"]!.AsArray()
            .Single(t => (string?)t!["id"] == team)!["containers"]!.AsArray()
            .Any(c => (string?)c!["id"] == Tc);

    private string ManagerPrompt(string team) =>
        Services.GetRequiredService<ContainerHost>().Find(new ContainerId(team, TeamRegistry.DefaultManagerName))!.SystemPrompt;

    private async Task<HttpClient> KeyClientAsync(string id, PrincipalKind kind, string? team, IReadOnlySet<string> permits, string? owner = null)
    {
        var key = await Services.GetRequiredService<IPrincipalStore>().MintAsync(id, kind, team, permits, ownerUserId: owner, ct: Ct);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static async Task NameRefusedAsync(string who, string what, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{who} could {what}: {(int)response.StatusCode} {body}");
        Assert.Equal(TeamConnectionsMember.NameRefused, System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
    }

    private static async Task RefusedAsync(string who, string what, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(
            response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest,
            $"{who} could {what}: {(int)response.StatusCode} {body}");
    }

    private async Task<string> StripeAsync(string name, string key) =>
        await CredentialAsync("apikey", "stripe", name, [("key", key)]);

    private async Task<string> CredentialAsync(string kind, string? preset, string name, (string Name, string Value)[] fields)
    {
        var added = await _person.PostAsJsonAsync("/api/connections/credentials", new
        {
            kind, preset, name, fields = fields.Select(f => new { name = f.Name, value = f.Value }),
        }, Ct);
        var body = await added.Content.ReadAsStringAsync(Ct);
        Assert.True(added.IsSuccessStatusCode, body);
        return (string)JsonNode.Parse(body)!["connection"]!["id"]!;
    }

    private async Task AssignAsync(string team, string connection)
    {
        var assigned = await _person.PostAsJsonAsync($"/api/teams/{team}/connections", new { connection }, Ct);
        Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Tells Team Connections <paramref name="instruction"/> as <paramref name="client"/> and waits for its run's terminal row.</summary>
    private async Task<Message> TellAndAwaitAsync(HttpClient client, string team, string instruction)
    {
        var who = new ContainerId(team, Tc);
        var log = Services.GetRequiredService<IMessageLog>();

        async Task<IReadOnlyList<Message>> Rows() =>
            [.. (await log.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct)).Where(m => m.Source == who.ToString())];

        var after = (await Rows()).Select(m => m.Seq).DefaultIfEmpty(0).Max();

        var told = await client.PostAsJsonAsync($"/api/teams/{team}/containers/{Tc}/tell", new { instruction }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if ((await Rows()).FirstOrDefault(m => m.Seq > after) is { } row)
            {
                var host = Services.GetRequiredService<ContainerHost>();
                while (host.Find(who)!.State != ContainerState.Idle && DateTime.UtcNow < deadline) await Task.Delay(20, Ct);
                return row;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"Team Connections never finished '{instruction}'.");
    }

    private static string Output(Message row) => (string?)JsonNode.Parse(row.Payload)!["output"] ?? "";

    /// <summary>The answer Team Connections sent to <paramref name="member"/>, caused by its tell <paramref name="told"/>.</summary>
    private async Task<string> AnswersToAsync(string team, string member, long told)
    {
        var log = Services.GetRequiredService<IMessageLog>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var answer = (await log.ReadAfterAsync(0, [MessageTypes.InstructionFor(new ContainerId(team, member))], int.MaxValue, Ct))
                .FirstOrDefault(m => m.CausationSeq == told && m.Source == new ContainerId(team, Tc).ToString());
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

    /// <summary>No value in any file under the data root - the database's rows included - as the connections tests scan.</summary>
    private async Task AssertNowhereAsync(params string[] secrets)
    {
        // The log's rows and the tenant rows are messages.db (and its -wal), so the file scan covers them.
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

    private sealed class FakeStripe : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.Host == "api.stripe.com"
                && request.Headers.Authorization?.ToString() is $"Bearer {StripeKey}" or $"Bearer {OtherKey}"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"object":"balance"}""", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":{"message":"Invalid API Key"}}""", Encoding.UTF8, "application/json") });
    }
}

internal static class HttpClientDisposal
{
    public static ValueTask DisposeAsyncSafe(this HttpClient client)
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}
