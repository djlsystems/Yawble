using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Pty;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// IN CONTROL, THE AGENTS BADGE, <c>GET /api/agents</c>' installations and the warnings on team create,
/// hire, agent change and the member-agent allowlist all carry the WORKERS' answer: installed on the
/// workers that said so, not installed on a named worker, both named when two disagree, and not
/// measured - never a warning - when no worker that counts has answered. In <c>all</c> they carry
/// this machine's PATH, byte for byte as before.
///
/// The real <c>Program</c> composition with <c>Role=control</c>; only <see cref="WorkerInstalls"/> is
/// replaced, by one over a fixed list of worker ids seeded with recorded answers. No worker joins and
/// no CLI runs; updates are off and the image is not control's, so no update is ever asked.
///
/// EVERY CUSTOM PRESET INVERTS CONTROL'S OWN PATH: a command a worker measured installed is on no PATH
/// (<see cref="Absent"/>), and one it measured missing is <c>sh</c>, on every PATH.
/// </summary>
public sealed class AgentInstallControlRouteTests : IDisposable
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    /// <summary>On no PATH, here or as root.</summary>
    private const string Absent = "no-such-cli-7f3a9c";

    /// <summary>A second command on no PATH, for an agent change.</summary>
    private const string AbsentOther = "no-such-cli-other-5b1e";

    private static readonly WorkerId W1 = new("worker-1");
    private static readonly WorkerId W2 = new("worker-2");

    private readonly string _root = Directory.CreateTempSubdirectory("harness-install-control-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void PathIsAsAssumed()
    {
        Assert.NotNull(PathSearch.Find("sh"));
        Assert.Null(PathSearch.Find(Absent));
        Assert.Null(PathSearch.Find(AbsentOther));
    }

    // ---------------------------------------------------------------------------------------------
    // GET /api/agents
    // ---------------------------------------------------------------------------------------------

    /// <summary>Also the check that the composed probe reads the <see cref="WorkerInstalls"/> registered.</summary>
    [Fact]
    public async Task In_control_with_every_cli_measured_installed_on_a_worker_no_preset_is_missing()
    {
        PathIsAsAssumed();
        await using var bed = await Bed.StartAsync(_root, control: true, W1);
        bed.RecordEveryCommand(W1, installed: true);

        var installations = await bed.InstallationsAsync(person: true);

        Assert.All(installations.Values, i => Assert.Equal(JsonValueKind.Null, i.GetProperty("state").ValueKind));
        Assert.All(installations.Values, i => Assert.Equal(JsonValueKind.Null, i.GetProperty("resolvedPath").ValueKind));
        Assert.Equal($"{Absent} is installed on worker-1.", installations["absent-headless"].GetProperty("message").GetString());
        Assert.Equal("sh is installed on worker-1.", installations["sh-headless"].GetProperty("message").GetString());
        Assert.Equal([("worker-1", true)], MeasuredOn(installations["absent-headless"]));

        var redacted = await bed.InstallationsAsync(person: false);
        Assert.All(redacted.Values, i => Assert.Equal(JsonValueKind.Null, i.GetProperty("state").ValueKind));
        Assert.Equal([("worker-1", true)], MeasuredOn(redacted["absent-headless"]));
        Assert.False(redacted["absent-headless"].TryGetProperty("resolvedPath", out _));
    }

    [Fact]
    public async Task In_control_a_cli_a_worker_measured_missing_reads_not_installed_on_that_worker()
    {
        PathIsAsAssumed();
        await using var bed = await Bed.StartAsync(_root, control: true, W1);
        bed.Installs.Record(W1, [("sh", false), (Absent, true)]);

        var installations = await bed.InstallationsAsync(person: true);

        var sh = installations["sh-headless"];
        Assert.Equal(AgentInstallStates.NotInstalled, sh.GetProperty("state").GetString());
        Assert.Equal("sh is not installed on worker-1.", sh.GetProperty("message").GetString());
        Assert.Equal([("worker-1", false)], MeasuredOn(sh));
        Assert.Equal(JsonValueKind.Null, installations["absent-headless"].GetProperty("state").ValueKind);
    }

    [Fact]
    public async Task In_control_with_no_worker_every_preset_is_not_measured_never_missing()
    {
        PathIsAsAssumed();
        await using var bed = await Bed.StartAsync(_root, control: true);
        // An answer from a worker that has gone counts for nothing.
        bed.Installs.Record(W1, [("sh", false), (Absent, false)]);

        var installations = await bed.InstallationsAsync(person: true);

        Assert.All(installations.Values, i =>
        {
            Assert.Equal(JsonValueKind.Null, i.GetProperty("state").ValueKind);
            Assert.Equal(JsonValueKind.Null, i.GetProperty("resolvedPath").ValueKind);
            Assert.Empty(MeasuredOn(i));
            Assert.EndsWith(" has not been measured: no worker is connected.", i.GetProperty("message").GetString());
        });
    }

    [Fact]
    public async Task In_control_with_a_worker_connected_that_has_not_answered_every_preset_is_not_measured()
    {
        PathIsAsAssumed();
        await using var bed = await Bed.StartAsync(_root, control: true, W1);

        var installations = await bed.InstallationsAsync(person: true);

        Assert.All(installations.Values, i =>
        {
            Assert.Equal(JsonValueKind.Null, i.GetProperty("state").ValueKind);
            Assert.Empty(MeasuredOn(i));
            Assert.EndsWith(" has not been measured: no worker has reported whether it is installed.", i.GetProperty("message").GetString());
        });

        var team = await bed.TeamAsync();
        Assert.Empty(await bed.HireAsync(team, "Dev", "absent-headless"));
    }

    [Fact]
    public async Task In_control_two_workers_that_disagree_are_both_named_in_installations_and_in_the_hire_warning()
    {
        PathIsAsAssumed();
        await using var bed = await Bed.StartAsync(_root, control: true, W2, W1);
        bed.RecordEveryCommand(W1, installed: true);
        bed.RecordEveryCommand(W2, installed: true);
        bed.Installs.Record(W2, [("sh", false)]);
        const string said = "sh is installed on worker-1 but not on worker-2: the workers disagree.";

        var sh = (await bed.InstallationsAsync(person: true))["sh-headless"];
        Assert.Equal(AgentInstallStates.NotInstalled, sh.GetProperty("state").GetString());
        Assert.Equal(said, sh.GetProperty("message").GetString());
        Assert.Equal([("worker-1", true), ("worker-2", false)], MeasuredOn(sh));

        var team = await bed.TeamAsync();
        var warning = Assert.Single(await bed.HireAsync(team, "Dev", "sh-headless"));
        Assert.Equal(said, warning.GetProperty("message").GetString());
    }

    /// <summary>
    /// THE ANSWER NEVER FOLLOWS CONTROL'S OWN PATH, IN EITHER DIRECTION. `sh` is on it and the other
    /// command is not; with no worker both are not measured, and once a worker measured `sh` missing and
    /// the other installed they read the workers' way round - for a person and for a machine principal.
    /// This is the runtime half of the guard's "all only" entries, which a scan of compiled code cannot
    /// tell apart by role.
    /// </summary>
    [Fact]
    public async Task In_control_the_answer_never_follows_controls_own_path_in_either_direction()
    {
        PathIsAsAssumed();
        await using var bed = await Bed.StartAsync(_root, control: true);

        foreach (var person in new[] { true, false })
        {
            var none = await bed.InstallationsAsync(person);
            foreach (var name in new[] { "sh-headless", "absent-headless" })
            {
                Assert.Equal(JsonValueKind.Null, none[name].GetProperty("state").ValueKind);
                Assert.Empty(MeasuredOn(none[name]));
                Assert.False(none[name].TryGetProperty("resolvedPath", out var path) && path.ValueKind != JsonValueKind.Null);
            }

            Assert.Equal("sh has not been measured: no worker is connected.", none["sh-headless"].GetProperty("message").GetString());
            Assert.Equal($"{Absent} has not been measured: no worker is connected.", none["absent-headless"].GetProperty("message").GetString());
        }

        bed.Connected = [W1];
        bed.Installs.Record(W1, [("sh", false), (Absent, true)]);

        foreach (var person in new[] { true, false })
        {
            var measured = await bed.InstallationsAsync(person);
            Assert.Equal(AgentInstallStates.NotInstalled, measured["sh-headless"].GetProperty("state").GetString());
            Assert.Equal("sh is not installed on worker-1.", measured["sh-headless"].GetProperty("message").GetString());
            Assert.Equal(JsonValueKind.Null, measured["absent-headless"].GetProperty("state").ValueKind);
            Assert.Equal($"{Absent} is installed on worker-1.", measured["absent-headless"].GetProperty("message").GetString());
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The warnings: team create, hire, agent change and the member-agent allowlist.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task In_control_creating_a_team_on_presets_a_worker_measured_installed_warns_of_nothing()
    {
        PathIsAsAssumed();
        await using var bed = await Bed.StartAsync(_root, control: true, W1);
        bed.RecordEveryCommand(W1, installed: true);

        var created = await bed.Person.PostAsJsonAsync("/api/teams", new
        {
            name = "Omega", agent = "absent-headless", memberAgents = new[] { "absent-headless", "absent-other" },
        }, Ct);

        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Ct));
        Assert.Empty(Unresolved(await created.Content.ReadFromJsonAsync<JsonElement>(Ct)));
    }

    [Fact]
    public async Task In_control_hiring_a_member_on_a_measured_preset_warns_of_nothing()
    {
        PathIsAsAssumed();
        await using var bed = await Bed.StartAsync(_root, control: true, W1);
        bed.RecordEveryCommand(W1, installed: true);
        var team = await bed.TeamAsync();

        Assert.Empty(await bed.HireAsync(team, "Dev", "absent-headless"));
    }

    [Fact]
    public async Task In_control_changing_a_members_agent_warns_of_nothing()
    {
        PathIsAsAssumed();
        await using var bed = await Bed.StartAsync(_root, control: true, W1);
        bed.RecordEveryCommand(W1, installed: true);
        var team = await bed.TeamAsync();
        await bed.HireAsync(team, "Dev", "absent-headless");

        var changed = await bed.Person.PatchAsJsonAsync($"/api/teams/{team}/containers/Dev", new { agent = "absent-other" }, Ct);

        Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync(Ct));
        Assert.Empty(Unresolved(await changed.Content.ReadFromJsonAsync<JsonElement>(Ct)));
    }

    [Fact]
    public async Task In_control_setting_the_member_agents_warns_of_nothing()
    {
        PathIsAsAssumed();
        await using var bed = await Bed.StartAsync(_root, control: true, W1);
        bed.RecordEveryCommand(W1, installed: true);
        var team = await bed.TeamAsync();

        var set = await bed.Person.PutAsJsonAsync(
            $"/api/teams/{team}/member-agent", new { agents = new[] { "absent-headless", "absent-other" } }, Ct);

        Assert.True(set.IsSuccessStatusCode, await set.Content.ReadAsStringAsync(Ct));
        Assert.Empty(Unresolved(await set.Content.ReadFromJsonAsync<JsonElement>(Ct)));
    }

    [Fact]
    public async Task In_control_with_no_worker_hiring_warns_of_nothing()
    {
        PathIsAsAssumed();
        await using var bed = await Bed.StartAsync(_root, control: true);
        var team = await bed.TeamAsync();

        Assert.Empty(await bed.HireAsync(team, "Dev", "absent-headless"));
    }

    [Fact]
    public async Task In_control_hiring_on_a_preset_measured_missing_warns_with_the_worker_named()
    {
        PathIsAsAssumed();
        await using var bed = await Bed.StartAsync(_root, control: true, W1);
        bed.RecordEveryCommand(W1, installed: true);
        bed.Installs.Record(W1, [("sh", false)]);
        var team = await bed.TeamAsync();

        var warning = Assert.Single(await bed.HireAsync(team, "Dev", "sh-headless"));
        Assert.Equal("sh-headless", warning.GetProperty("agent").GetString());
        Assert.Equal("sh is not installed on worker-1.", warning.GetProperty("message").GetString());
    }

    // ---------------------------------------------------------------------------------------------
    // The all role, unchanged.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task In_all_the_installations_are_todays_this_machine_answers()
    {
        PathIsAsAssumed();
        await using var bed = await Bed.StartAsync(_root, control: false);

        var installations = await bed.InstallationsAsync(person: true);

        Assert.Equal(
            "{\"agent\":\"sh-headless\",\"command\":\"sh\",\"state\":null,\"resolvedPath\":" + JsonSerializer.Serialize(PathSearch.Find("sh"))
            + ",\"referenced\":false,\"message\":\"sh resolves on this machine's PATH.\",\"install\":null}",
            installations["sh-headless"].GetRawText());
        Assert.Equal(
            "{\"agent\":\"absent-headless\",\"command\":\"" + Absent + "\",\"state\":\"AgentNotInstalled\",\"resolvedPath\":null"
            + ",\"referenced\":false,\"message\":\"" + Absent + " was not found on this machine's PATH.\",\"install\":null}",
            installations["absent-headless"].GetRawText());
        Assert.All(installations.Values, i => Assert.False(i.TryGetProperty("measuredOn", out _)));
    }

    [Fact]
    public async Task In_all_the_redacted_installations_carry_no_measured_on_key()
    {
        PathIsAsAssumed();
        await using var bed = await Bed.StartAsync(_root, control: false);

        var installations = await bed.InstallationsAsync(person: false);

        Assert.Equal(
            "{\"agent\":\"absent-headless\",\"state\":\"AgentNotInstalled\",\"referenced\":false,\"message\":\""
            + Absent + " was not found on this machine's PATH.\",\"install\":null}",
            installations["absent-headless"].GetRawText());
        Assert.All(installations.Values, i => Assert.False(i.TryGetProperty("measuredOn", out _)));
    }

    [Fact]
    public async Task In_all_hiring_on_a_command_missing_from_this_machines_path_warns_as_before()
    {
        PathIsAsAssumed();
        await using var bed = await Bed.StartAsync(_root, control: false);
        var team = await bed.TeamAsync();

        var warning = Assert.Single(await bed.HireAsync(team, "Dev", "absent-headless"));
        Assert.Equal($"{Absent} was not found on this machine's PATH.", warning.GetProperty("message").GetString());
        Assert.Empty(await bed.HireAsync(team, "Ops", "sh-headless"));
    }

    // ---------------------------------------------------------------------------------------------
    // While the platform's update holds a command.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AgentUpdatePhases.Waiting)]
    [InlineData(AgentUpdatePhases.Updating)]
    public async Task In_control_a_held_command_reads_updating_in_the_installations_never_not_installed(string phase)
    {
        PathIsAsAssumed();
        await using var bed = await Bed.StartAsync(_root, control: true, W1);
        bed.RecordEveryCommand(W1, installed: true);
        bed.Installs.Record(W1, [("sh", false)]);
        Assert.Equal("AgentNotInstalled", (await bed.InstallationsAsync(person: true))["sh-headless"].GetProperty("state").GetString());

        await using var held = await Held.StartAsync(bed, "sh", phase);

        foreach (var person in new[] { true, false })
        {
            var sh = (await bed.InstallationsAsync(person))["sh-headless"];
            Assert.Equal(AgentInstallStates.Updating, sh.GetProperty("state").GetString());
            Assert.StartsWith("Updating sh", sh.GetProperty("message").GetString());
            Assert.Equal(phase, sh.GetProperty("updating").GetProperty("phase").GetString());
            Assert.DoesNotContain("AgentNotInstalled", sh.GetRawText(), StringComparison.Ordinal);
        }

        var auth = await bed.Person.GetFromJsonAsync<JsonElement>("/api/agents/auth", Ct);
        var shAuth = auth.EnumerateArray().First(r => r.GetProperty("agent").GetString() == "sh-headless");
        Assert.Equal(JsonValueKind.Null, shAuth.GetProperty("installed").ValueKind);
        Assert.StartsWith("Updating sh", shAuth.GetProperty("updating").GetString());
        Assert.Equal(AgentLaunchReport.Updating, shAuth.GetProperty("launch").GetProperty("result").GetString());

        // Outside a hold the answer carries no updating key at all.
        await held.DisposeAsync();
        Assert.False((await bed.InstallationsAsync(person: true))["sh-headless"].TryGetProperty("updating", out _));
    }

    /// <summary>
    /// A held run waits and never fails, so nothing warns of an unresolved agent while its update holds
    /// it - and the same action with no hold, on a command a worker measured missing, still warns.
    /// </summary>
    [Theory]
    [InlineData("create", true)]
    [InlineData("create", false)]
    [InlineData("hire", true)]
    [InlineData("hire", false)]
    [InlineData("change", true)]
    [InlineData("change", false)]
    [InlineData("allowlist", true)]
    [InlineData("allowlist", false)]
    public async Task In_control_hiring_on_a_held_preset_warns_of_nothing(string site, bool held)
    {
        PathIsAsAssumed();
        await using var bed = await Bed.StartAsync(_root, control: true, W1);
        bed.RecordEveryCommand(W1, installed: true);
        bed.Installs.Record(W1, [("sh", false)]);
        var team = site == "create" ? null : await bed.TeamAsync();
        if (site == "change") await bed.HireAsync(team!, "Dev", "absent-headless");

        await using var hold = held ? await Held.StartAsync(bed, "sh", AgentUpdatePhases.Updating) : null;

        var response = site switch
        {
            "create" => await bed.Person.PostAsJsonAsync("/api/teams", new { name = "Omega", agent = "sh-headless", memberAgents = new[] { "sh-headless" } }, Ct),
            "hire" => await bed.Person.PostAsJsonAsync($"/api/teams/{team}/containers", new { name = "Dev", agent = "sh-headless" }, Ct),
            "change" => await bed.Person.PatchAsJsonAsync($"/api/teams/{team}/containers/Dev", new { agent = "sh-headless" }, Ct),
            _ => await bed.Person.PutAsJsonAsync($"/api/teams/{team}/member-agent", new { agents = new[] { "sh-headless" } }, Ct),
        };

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        var warnings = Unresolved(await response.Content.ReadFromJsonAsync<JsonElement>(Ct));
        if (held)
        {
            Assert.Empty(warnings);
        }
        else
        {
            // Team create warns once for the team's agent and once for its member agents.
            Assert.NotEmpty(warnings);
            Assert.All(warnings, w => Assert.Equal("sh is not installed on worker-1.", w.GetProperty("message").GetString()));
        }
    }

    /// <summary>A hold of a command's update on the composed Host's own gate, by a task the test completes: nothing runs.</summary>
    private sealed class Held : IAsyncDisposable
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IDisposable? _share;
        private Task<bool> _update = Task.FromResult(true);

        public static async Task<Held> StartAsync(Bed bed, string command, string phase)
        {
            var gate = bed.Services.GetRequiredService<AgentUpdateGate>();
            var held = new Held();
            if (phase == AgentUpdatePhases.Waiting) held._share = await gate.EnterRunAsync(command, null, Ct);
            held._update = gate.UpdateAsync(command, _ => held._release.Task, Ct);
            if (phase == AgentUpdatePhases.Updating) gate.RunsOn(command, "worker-1");
            Assert.Equal(phase, gate.Holding(command)?.Phase);
            return held;
        }

        public async ValueTask DisposeAsync()
        {
            _share?.Dispose();
            _release.TrySetResult(true);
            await _update.WaitAsync(TimeSpan.FromSeconds(60));
        }
    }

    // ---------------------------------------------------------------------------------------------

    private static List<(string Worker, bool Installed)> MeasuredOn(JsonElement installation) =>
        installation.TryGetProperty("measuredOn", out var measured) && measured.ValueKind == JsonValueKind.Array
            ? [.. measured.EnumerateArray().Select(m => (m.GetProperty("worker").GetString()!, m.GetProperty("installed").GetBoolean()))]
            : throw new Xunit.Sdk.XunitException($"No measuredOn list in {installation.GetRawText()}");

    private static List<JsonElement> Unresolved(JsonElement body) =>
        [.. body.GetProperty("unresolvedAgents").EnumerateArray()];

    /// <summary>One Host, a person signed in, a machine principal, and the custom presets.</summary>
    private sealed class Bed : IAsyncDisposable
    {
        private WebApplicationFactory<Program> _factory = null!;

        public IReadOnlyList<WorkerId> Connected { get; set; } = [];

        public WorkerInstalls Installs { get; private set; } = null!;

        public HttpClient Person { get; private set; } = null!;

        public HttpClient Machine { get; private set; } = null!;

        public IServiceProvider Services => _factory.Services;

        public static async Task<Bed> StartAsync(string root, bool control, params WorkerId[] connected)
        {
            var bed = new Bed { Connected = connected };
            bed.Installs = new WorkerInstalls(() => bed.Connected);
            bed._factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("DataRoot", root)
                    .UseSetting("Logging:LogLevel:Default", "Warning")
                    .UseSetting("HARNESS_UPDATE_AGENTS", "0")
                    .UseSetting("HARNESS_IMAGE", "");
                if (control) builder.UseSetting("Role", "control");
                builder.ConfigureTestServices(services => services.AddSingleton(bed.Installs));
            });

            var services = bed._factory.Services;
            var agent = services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
            var alpha = (await services.GetRequiredService<TeamRegistry>().CreateAsync("Alpha", agent, memberAgent: agent)).Id;
            var key = await services.GetRequiredService<IPrincipalStore>().MintAsync(
                new ContainerId(alpha, "Worker").ToString(), PrincipalKind.Container, alpha, Permits.All);
            await services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);

            bed.Person = bed._factory.CreateClient();
            (await bed.Person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
            bed.Machine = bed._factory.CreateClient();
            bed.Machine.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);

            var saved = await bed.Person.PutAsJsonAsync("/api/agents", new
            {
                agents = new[]
                {
                    Headless("sh-headless", "sh"),
                    Headless("absent-headless", Absent),
                    Headless("absent-other", AbsentOther),
                },
            }, Ct);
            Assert.True(saved.StatusCode == HttpStatusCode.NoContent, await saved.Content.ReadAsStringAsync(Ct));

            return bed;
        }

        private static AgentDefinition Headless(string name, string command) =>
            new(name, AgentMode.Headless, new AgentLaunch(command, []));

        /// <summary>Every command the catalog launches, answered by <paramref name="worker"/>.</summary>
        public void RecordEveryCommand(WorkerId worker, bool installed) =>
            Installs.Record(worker, _factory.Services.GetRequiredService<AgentCatalog>().Definitions
                .Where(d => d.Launch is not null)
                .Select(d => (d.Launch.FileName, installed))
                .Distinct());

        public async Task<Dictionary<string, JsonElement>> InstallationsAsync(bool person)
        {
            var body = await (person ? Person : Machine).GetFromJsonAsync<JsonElement>("/api/agents", Ct);
            return body.GetProperty("installations").EnumerateArray()
                .ToDictionary(i => i.GetProperty("agent").GetString()!, i => i.Clone());
        }

        /// <summary>A new team on the custom presets, by a person.</summary>
        public async Task<string> TeamAsync()
        {
            var created = await Person.PostAsJsonAsync("/api/teams", new
            {
                name = "Team" + Guid.NewGuid().ToString("N")[..6],
                agent = "absent-headless",
                memberAgents = new[] { "absent-headless", "absent-other", "sh-headless" },
            }, Ct);
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Ct));
            return (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetString()!;
        }

        /// <summary>Hires a member and answers the warnings the hire carried.</summary>
        public async Task<List<JsonElement>> HireAsync(string team, string name, string agent)
        {
            var hired = await Person.PostAsJsonAsync($"/api/teams/{team}/containers", new { name, agent }, Ct);
            Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));
            return Unresolved(await hired.Content.ReadFromJsonAsync<JsonElement>(Ct));
        }

        public async ValueTask DisposeAsync()
        {
            Person?.Dispose();
            Machine?.Dispose();
            await _factory.DisposeAsync();
        }
    }
}
