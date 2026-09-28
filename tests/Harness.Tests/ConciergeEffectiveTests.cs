using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Harness.Tests.Host;

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
