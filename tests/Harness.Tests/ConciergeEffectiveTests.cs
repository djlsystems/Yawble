using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Tests.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// CONCIERGE PRE-FLIGHT. <c>GET /api/concierge</c> says which agent and prompt the launcher
/// will actually run after defaults, and whether that agent can start, so the panel can say so
/// instead of opening onto a closed socket. <see cref="ConciergeView.ReadAsync"/> is the route's
/// whole body; the agent comes from the same resolution the launcher calls.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ConciergeEffectiveTests : IDisposable
{
    private readonly string _dataRoot =
        Path.Combine(Path.GetTempPath(), $"harness-concierge-effective-{Guid.NewGuid():N}");

    private readonly AgentCatalog _catalog;
    private readonly List<AgentDefinition> _interactive;

    public ConciergeEffectiveTests()
    {
        Directory.CreateDirectory(_dataRoot);
        _catalog = new AgentCatalog(AgentCatalogFile.BuiltIns());
        _interactive = [.. _catalog.Definitions
            .Where(d => d.Mode == AgentMode.Interactive && !d.Hidden && d.Launch.LanguageModel)];
        Assert.True(_interactive.Count >= 2, "The seed needs two interactive presets for this test to mean anything.");
        Assert.NotEqual(_interactive[0].Launch.FileName, _interactive[1].Launch.FileName);
    }

    [Fact]
    public async Task A_chosen_agent_is_reported_as_chosen_with_that_agents_auth()
    {
        // The first preset is installed and NOT signed in; the second is signed in. The chosen one
        // still wins, and its auth is what is reported.
        using var restore = await Machine(installed: [_interactive[0], _interactive[1]], signedIn: _interactive[1]);

        var view = await Read(new TenantConciergeSettings(_interactive[0].Name));

        Assert.Equal(_interactive[0].Name, view.Effective.Agent);
        Assert.Equal("chosen", view.Effective.AgentSource);
        Assert.True(view.Effective.Auth.Installed);
        Assert.False(view.Effective.Auth.SignedIn);
        Assert.False(string.IsNullOrWhiteSpace(view.Effective.Auth.Detail));
    }

    [Fact]
    public async Task With_nothing_configured_the_signed_in_agent_is_the_default()
    {
        // Installed-but-signed-out comes FIRST in the catalog, so "signed in" is what decides.
        using var restore = await Machine(installed: [_interactive[0], _interactive[1]], signedIn: _interactive[1]);

        var view = await Read(new TenantConciergeSettings(null));

        Assert.Equal(_interactive[1].Name, view.Effective.Agent);
        Assert.Equal("default", view.Effective.AgentSource);
        Assert.True(view.Effective.Auth.Installed);
        Assert.True(view.Effective.Auth.SignedIn);

        // The launcher starts the same agent the route named.
        Assert.Equal(
            view.Effective.Agent,
            await ConciergeAgentDefault.ResolveAsync(
                null, _catalog, new AgentAuthProbe(_catalog), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task With_no_agent_installed_or_signed_in_the_agent_is_null_and_the_launcher_refuses()
    {
        using var restore = await Machine(installed: [], signedIn: null);

        var view = await Read(new TenantConciergeSettings(null));

        Assert.Null(view.Effective.Agent);
        Assert.Equal("default", view.Effective.AgentSource);
        Assert.False(view.Effective.Auth.Installed);
        Assert.False(view.Effective.Auth.SignedIn);
        Assert.False(string.IsNullOrWhiteSpace(view.Effective.Auth.Detail));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ConciergeAgentDefault.ResolveAsync(
            null, _catalog, new AgentAuthProbe(_catalog), TestContext.Current.CancellationToken));
    }

    // ---- A PRESET BEING INSTALLED KEEPS ITS CATALOG PLACE (card 24363) ----
    // Nobody chose. An installing preset ranks where it stands in the catalog beside the installed
    // ones; only a signed-in preset goes before it. A later preset that finished first does not
    // take the panel away from an earlier one still installing.

    [Fact]
    public async Task Nobody_chose_and_the_first_preset_is_installing_while_a_later_one_is_installed_the_first_is_the_default()
    {
        using var restore = await Machine(installed: [_interactive[1]], signedIn: null);
        var gate = new AgentUpdateGate();
        var install = Install(gate, _interactive[0]);

        var effective = await Effective(null, gate);

        Assert.Equal(_interactive[0].Name, effective.Agent);
        Assert.Equal("default", effective.AgentSource);
        Assert.Equal(AgentUpdateKinds.Install, effective.Auth.HeldBy);
        Assert.False(effective.Auth.Installed);
        install.SetResult(true);
    }

    [Fact]
    public async Task Nobody_chose_and_the_first_preset_is_installed_while_a_later_one_is_installing_the_first_is_the_default_and_not_held()
    {
        using var restore = await Machine(installed: [_interactive[0]], signedIn: null);
        var gate = new AgentUpdateGate();
        var install = Install(gate, _interactive[1]);

        var effective = await Effective(null, gate);

        Assert.Equal(_interactive[0].Name, effective.Agent);
        Assert.Equal("default", effective.AgentSource);
        Assert.Null(effective.Auth.HeldBy);
        Assert.True(effective.Auth.Installed);
        install.SetResult(true);
    }

    [Fact]
    public async Task When_the_first_presets_install_ends_it_stays_the_default_and_is_no_longer_held()
    {
        // The first is installing, the second is installed, the third is installing too.
        Assert.True(_interactive.Count >= 3, "The seed needs three interactive presets for this test.");
        using var restore = await Machine(installed: [_interactive[1]], signedIn: null);
        var gate = new AgentUpdateGate();
        var first = Install(gate, _interactive[0], writes: true);
        var third = Install(gate, _interactive[2]);
        var probe = new AgentAuthProbe(_catalog, gate: gate);
        var ct = TestContext.Current.CancellationToken;

        var before = await ConciergeAgentDefault.EffectiveAsync(null, _catalog, probe, ct);
        Assert.Equal(_interactive[0].Name, before.Agent);
        Assert.Equal(AgentUpdateKinds.Install, before.Auth.HeldBy);

        first.SetResult(true);
        await Ended(gate, _interactive[0]);

        // The same agent, now neither held nor elsewhere: not the third, which is still installing.
        var after = await ConciergeAgentDefault.EffectiveAsync(null, _catalog, probe, ct);
        Assert.Equal(_interactive[0].Name, after.Agent);
        Assert.Equal("default", after.AgentSource);
        Assert.Null(after.Auth.HeldBy);
        Assert.True(after.Auth.Installed);
        third.SetResult(true);
    }

    [Fact]
    public async Task Nobody_chose_and_a_later_preset_is_signed_in_while_the_first_is_installing_the_signed_in_one_is_the_default()
    {
        using var restore = await Machine(installed: [_interactive[1]], signedIn: _interactive[1]);
        var gate = new AgentUpdateGate();
        var install = Install(gate, _interactive[0]);

        var effective = await Effective(null, gate);

        Assert.Equal(_interactive[1].Name, effective.Agent);
        Assert.Equal("default", effective.AgentSource);
        Assert.True(effective.Auth.SignedIn);
        install.SetResult(true);
    }

    [Fact]
    public async Task A_chosen_agent_is_returned_as_chosen_while_other_presets_are_installed_or_installing()
    {
        Assert.True(_interactive.Count >= 3, "The seed needs three interactive presets for this test.");
        using var restore = await Machine(installed: [_interactive[0], _interactive[1]], signedIn: null);
        var gate = new AgentUpdateGate();
        var install = Install(gate, _interactive[0]);

        var effective = await Effective(_interactive[2].Name, gate);

        Assert.Equal(_interactive[2].Name, effective.Agent);
        Assert.Equal("chosen", effective.AgentSource);
        Assert.False(effective.Auth.Installed);
        install.SetResult(true);
    }

    [Fact]
    public async Task When_the_first_presets_install_fails_the_default_falls_through_to_the_next_installed_preset()
    {
        using var restore = await Machine(installed: [_interactive[1]], signedIn: null);
        var gate = new AgentUpdateGate();
        var first = Install(gate, _interactive[0], writes: false);
        var probe = new AgentAuthProbe(_catalog, gate: gate);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(_interactive[0].Name, (await ConciergeAgentDefault.EffectiveAsync(null, _catalog, probe, ct)).Agent);

        first.SetResult(true);
        await Ended(gate, _interactive[0]);

        var after = await ConciergeAgentDefault.EffectiveAsync(null, _catalog, probe, ct);
        Assert.Equal(_interactive[1].Name, after.Agent);
        Assert.Equal("default", after.AgentSource);
        Assert.Null(after.Auth.HeldBy);
        Assert.True(after.Auth.Installed);
    }

    private Task<ConciergeEffective> Effective(string? chosen, AgentUpdateGate gate) =>
        ConciergeAgentDefault.EffectiveAsync(
            chosen, _catalog, new AgentAuthProbe(_catalog, gate: gate), TestContext.Current.CancellationToken);

    /// <summary>Holds <paramref name="preset"/>'s command for an install until the answer is set. With
    /// <paramref name="writes"/> the install puts the command on PATH (it succeeded); without, it failed.</summary>
    private TaskCompletionSource<bool> Install(AgentUpdateGate gate, AgentDefinition preset, bool writes = false)
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = preset.Launch.FileName;
        var (_, started) = gate.Request(command, preset.Name, null, async _ =>
        {
            await release.Task;
            if (writes)
            {
                await TestExecutable.WriteAsync(Path.Combine(_dataRoot, "bin", command), "#!/bin/sh\nexit 1\n");
            }

            return new AgentUpdateResult(preset.Name, command, writes, writes ? 0 : 1, null, writes ? "1" : null,
                DateTimeOffset.UtcNow, writes ? "installed" : "the vendor could not be reached");
        }, kind: AgentUpdateKinds.Install);
        Assert.True(started);
        return release;
    }

    private static async Task Ended(AgentUpdateGate gate, AgentDefinition preset)
    {
        var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (gate.Holding(preset.Launch.FileName) is not null)
        {
            Assert.True(DateTimeOffset.UtcNow < until, $"{preset.Name}'s install never ended.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private Task<ConciergeView> Read(TenantConciergeSettings settings) =>
        ConciergeView.ReadAsync(
            settings, _catalog, new AgentAuthProbe(_catalog), TestContext.Current.CancellationToken);

    /// <summary>A PATH holding exactly <paramref name="installed"/>, each exiting 1 to a status
    /// command, no saved logins, and only <paramref name="signedIn"/>'s credential variable set.</summary>
    private Task<EnvironmentScope> Machine(AgentDefinition[] installed, AgentDefinition? signedIn) =>
        Machine(_dataRoot, installed, signedIn);

    internal static async Task<EnvironmentScope> Machine(
        string root, AgentDefinition[] installed, AgentDefinition? signedIn)
    {
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        foreach (var definition in installed)
        {
            await TestExecutable.WriteAsync(Path.Combine(bin, definition.Launch.FileName), "#!/bin/sh\nexit 1\n");
        }

        var specs = JsonSerializer.Deserialize<Dictionary<string, AgentAuthProbeSpec>>(
            await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "auth-probes.json"), TestContext.Current.CancellationToken),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var values = new List<KeyValuePair<string, string>> { new("PATH", bin), new("HOME", root) };
        foreach (var (command, spec) in specs)
        {
            if (spec.CredentialVariable is not { Length: > 0 } variable) continue;
            var on = signedIn is not null && signedIn.Launch.FileName == command;
            values.Add(new(variable, on ? "test-key" : ""));
        }

        Assert.True(
            signedIn is null || specs.ContainsKey(signedIn.Launch.FileName),
            "The signed-in preset needs a credential variable in auth-probes.json.");

        return new EnvironmentScope(values);
    }

    [Fact]
    public async Task The_concierge_route_lists_each_session_with_its_worker_last_activity_last_viewer_and_would_end_at()
    {
        var ct = TestContext.Current.CancellationToken;
        var start = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTime(start);
        var root = Directory.CreateDirectory(Path.Combine(_dataRoot, "host")).FullName;

        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("DataRoot", root)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton(new ConciergeSessionStore(
                new FakePtyEngine(),
                (_, _, _, _) => Task.FromResult(new Harness.Pty.PtySpec("fixture-cli", root)),
                (_, _) => Task.CompletedTask,
                clock))));

        var person = await app.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", "correct horse battery", ct);
        using var client = app.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = "correct horse battery" }, ct))
            .EnsureSuccessStatusCode();

        var store = app.Services.GetRequiredService<ConciergeSessionStore>();
        var session = await store.AttachAsync(new ConciergeSessionKey(person.Id), "", 80, 24, ct);
        clock.Advance(TimeSpan.FromMinutes(10));
        ((FakePty)session.Terminal).Print(new byte[3000]);

        var listed = (await client.GetFromJsonAsync<JsonElement>("/api/concierge", ct)).GetProperty("sessions").EnumerateArray().Single();
        Assert.Equal(person.Id, listed.GetProperty("user").GetString());
        Assert.Equal("person@example.test", listed.GetProperty("email").GetString());
        // A terminal not on a worker names none.
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("worker").ValueKind);
        Assert.Equal(start, listed.GetProperty("startedAt").GetDateTimeOffset());
        Assert.False(listed.GetProperty("viewer").GetBoolean());
        Assert.Equal(start, listed.GetProperty("lastViewerAt").GetDateTimeOffset());
        Assert.Equal(start.AddMinutes(10), listed.GetProperty("lastActivityAt").GetDateTimeOffset());
        Assert.Equal("output", listed.GetProperty("lastActivity").GetString());
        Assert.Equal(0, listed.GetProperty("callsInFlight").GetInt32());
        Assert.Equal(2048, listed.GetProperty("outputFloor").GetProperty("bytesPerMinute").GetInt64());
        Assert.Equal("default", listed.GetProperty("outputFloor").GetProperty("source").GetString());
        Assert.Equal([0L, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3000], listed.GetProperty("outputPerMinute").EnumerateArray().Select(m => m.GetInt64()));
        // The window is the default hour, from the later of the two.
        Assert.Equal(start.AddMinutes(70), listed.GetProperty("wouldEndAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("memory").ValueKind);

        // While someone has it open there is no time it would end.
        using var evict = new CancellationTokenSource();
        session.Attachment.Attach(evict, 80, 24);
        var viewed = (await client.GetFromJsonAsync<JsonElement>("/api/concierge", ct)).GetProperty("sessions").EnumerateArray().Single();
        Assert.True(viewed.GetProperty("viewer").GetBoolean());
        Assert.Equal(JsonValueKind.Null, viewed.GetProperty("lastViewerAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, viewed.GetProperty("wouldEndAt").ValueKind);
        Assert.Equal("attached", viewed.GetProperty("lastActivity").GetString());
    }

    public void Dispose()
    {
        try
        {
            MemberTempCleanup.Remove(_dataRoot);
            Directory.Delete(_dataRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>The route itself: the stored fields stay, and <c>effective</c> is added in camelCase.</summary>
public sealed class ConciergeRouteTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task The_concierge_route_carries_the_agent_and_effective_and_no_prompt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = await host.PersonAsync();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/concierge", ct);

        // A fresh volume stores NO agent (auth-003): nobody has chosen one.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("agent").ValueKind);

        // The prompt is chosen by role: nothing about it is a setting.
        Assert.False(body.TryGetProperty("prompt", out _));
        Assert.False(body.TryGetProperty("promptName", out _));

        var effective = body.GetProperty("effective");
        Assert.True(effective.TryGetProperty("agent", out _));
        Assert.Contains(effective.GetProperty("agentSource").GetString(), new[] { "chosen", "default" });
        Assert.False(effective.TryGetProperty("prompt", out _));
        Assert.False(effective.TryGetProperty("promptSource", out _));

        var auth = effective.GetProperty("auth");
        Assert.Contains(auth.GetProperty("installed").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Contains(auth.GetProperty("signedIn").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.True(auth.TryGetProperty("detail", out _));
    }
}

/// <summary>
/// At the route: a FRESH VOLUME with nothing configured and only grok
/// signed in opens grok, reported as the default - not a seeded 'claude' reported as "chosen"
/// whether or not claude is installed.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ConciergeFreshVolumeTests : IDisposable
{
    private readonly string _machine =
        Path.Combine(Path.GetTempPath(), $"harness-concierge-fresh-{Guid.NewGuid():N}");

    public ConciergeFreshVolumeTests() => Directory.CreateDirectory(_machine);

    [Fact]
    public async Task A_fresh_volume_with_only_grok_signed_in_defaults_to_grok()
    {
        var ct = TestContext.Current.CancellationToken;
        var interactive = AgentCatalogFile.BuiltIns().Where(d => d.Mode == AgentMode.Interactive && !d.Hidden).ToList();
        var grok = interactive.First(d => d.Launch.FileName == "grok");
        var claude = interactive.First(d => d.Launch.FileName == "claude");

        // claude is installed too, and signed out: the seed row naming it is what this pins away.
        using var restore = await ConciergeEffectiveTests.Machine(_machine, [claude, grok], signedIn: grok);

        var host = new HostFixture();
        await host.InitializeAsync();
        try
        {
            using var client = await host.PersonAsync();

            var body = await client.GetFromJsonAsync<JsonElement>("/api/concierge", ct);

            Assert.Equal(JsonValueKind.Null, body.GetProperty("agent").ValueKind);
            var effective = body.GetProperty("effective");
            Assert.Equal(grok.Name, effective.GetProperty("agent").GetString());
            Assert.Equal("default", effective.GetProperty("agentSource").GetString());
            Assert.False(effective.TryGetProperty("prompt", out _));
            Assert.True(effective.GetProperty("auth").GetProperty("signedIn").GetBoolean());
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_machine, recursive: true); }
        catch (IOException) { }
    }
}
