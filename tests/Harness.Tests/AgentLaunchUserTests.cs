using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// Agent children run as a separate user when the Host can switch to one, and as the
/// Host's own user when it cannot. The rule is <see cref="AgentLaunchUser.Decide"/>. The launches
/// below switch for real, to <c>nobody</c>, where the test process holds CAP_SETUID and
/// CAP_SETGID (root in the SDK container), and are skipped with the reason where it does not.
/// </summary>
public sealed class AgentLaunchUserDecisionTests
{
    private const ulong SetuidAndSetgid = 0xc0;
    private static readonly (int, int) Agent = (1001, 1001);

    private static AgentLaunchUser Decide(
        (int Uid, int Gid)? target = null, int hostUid = 1000, ulong caps = SetuidAndSetgid,
        string? setpriv = "/usr/bin/setpriv", bool canShare = true) =>
        AgentLaunchUser.Decide("agent", target, hostUid, caps, setpriv, _ => canShare);

    [Fact]
    public void A_host_that_can_switch_prefixes_setpriv_and_drops_every_capability()
    {
        var runAs = Decide(Agent);

        Assert.True(runAs.Switches);
        Assert.Equal(
            ["/usr/bin/setpriv", "--reuid=1001", "--regid=1001", "--init-groups", "--inh-caps=-all", "--ambient-caps=-all", "--"],
            runAs.Prefix);
        Assert.Equal([.. runAs.Prefix, "claude", "-p"], runAs.Wrap(["claude", "-p"]));
        Assert.Contains("'agent'", runAs.Reason);
    }

    [Fact]
    public void With_no_agent_user_the_child_launches_as_the_host_does()
    {
        var runAs = Decide(target: null);

        Assert.False(runAs.Switches);
        Assert.Empty(runAs.Prefix);
        Assert.Equal(["claude"], runAs.Wrap(["claude"]));
        Assert.Contains("no user 'agent'", runAs.Reason);
    }

    [Fact]
    public void A_host_already_running_as_the_agent_user_does_not_switch()
    {
        var runAs = Decide(Agent, hostUid: 1001);

        Assert.False(runAs.Switches);
        Assert.Contains("already runs as", runAs.Reason);
    }

    [Theory]
    [InlineData(0x00UL)]
    [InlineData(0x80UL)] // CAP_SETUID alone
    [InlineData(0x40UL)] // CAP_SETGID alone
    public void A_host_without_both_capabilities_does_not_switch(ulong caps)
    {
        var runAs = Decide(Agent, caps: caps);

        Assert.False(runAs.Switches);
        Assert.Contains("CAP_SETUID and CAP_SETGID", runAs.Reason);
    }

    [Fact]
    public void Without_setpriv_on_path_the_host_does_not_switch()
    {
        var runAs = Decide(Agent, setpriv: null);

        Assert.False(runAs.Switches);
        Assert.Contains("setpriv", runAs.Reason);
    }

    [Fact]
    public void A_host_that_cannot_hand_its_files_to_the_agent_group_does_not_switch()
    {
        var runAs = Decide(Agent, canShare: false);

        Assert.False(runAs.Switches);
        Assert.Contains("member of that group", runAs.Reason);
    }

    [Fact]
    public void Falling_back_while_an_agent_user_exists_is_recorded_as_unreachable()
    {
        Assert.True(Decide(Agent, caps: 0).AgentUserUnreachable);
        Assert.True(Decide(Agent, setpriv: null).AgentUserUnreachable);
        Assert.True(Decide(Agent, canShare: false).AgentUserUnreachable);

        Assert.False(Decide(target: null).AgentUserUnreachable);
        Assert.False(Decide(Agent, hostUid: 1001).AgentUserUnreachable);
        Assert.False(Decide(Agent).AgentUserUnreachable);
    }

    [Fact]
    public void A_host_that_does_not_switch_but_holds_capabilities_clears_them_for_every_child()
    {
        var runAs = Decide(target: null) with { ClearsCapabilities = true, SetprivPath = "/usr/bin/setpriv" };

        Assert.False(runAs.Switches);
        Assert.Equal(["/usr/bin/setpriv", "--inh-caps=-all", "--ambient-caps=-all", "--"], runAs.Prefix);
        Assert.Equal([.. runAs.Prefix, "claude"], runAs.Wrap(["claude"]));
    }

    [Fact]
    public void The_user_is_read_from_a_passwd_file_by_name()
    {
        var passwd = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(passwd,
            [
                "root:x:0:0:root:/root:/bin/bash",
                "harness:x:1000:1000::/data/agent-home:/bin/sh",
                "agent:x:1001:1002::/data/agent-home:/bin/bash",
            ]);

            Assert.Equal((1001, 1002), AgentLaunchUser.LookUp("agent", passwd));
            Assert.Null(AgentLaunchUser.LookUp("agen", passwd));
            Assert.Null(AgentLaunchUser.LookUp("agent", passwd + ".missing"));
        }
        finally
        {
            File.Delete(passwd);
        }
    }

    [Fact]
    public void Not_switching_shares_nothing()
    {
        var directory = Directory.CreateTempSubdirectory("harness-noshare-").FullName;
        try
        {
            var before = File.GetUnixFileMode(directory);
            AgentLaunchUser.Same("agent", "test").Share(directory);
            Assert.Equal(before, File.GetUnixFileMode(directory));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }
}

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class AgentLaunchUserLaunchTests : IDisposable
{
    private const string Target = "nobody";

    private readonly string _workspace =
        Directory.CreateTempSubdirectory("harness-runas-").FullName;

    public AgentLaunchUserLaunchTests() =>
        // The workspace belongs to the agent in the image; here it only has to let `nobody` in.
        File.SetUnixFileMode(_workspace, (UnixFileMode)0b111_111_111);

    public void Dispose()
    {
        MemberTempCleanup.Remove(_workspace);
        Directory.Delete(_workspace, recursive: true);
    }

    private static AgentLaunchUser SwitchingOrSkip()
    {
        var runAs = AgentLaunchUser.Resolve(Target);
        if (!runAs.Switches) Assert.Skip($"This process cannot switch users: {runAs.Reason}.");
        return runAs;
    }

    private async Task<AgentResult> RunAsync(AgentLaunchUser? runAs, string script)
    {
        // A launch with HARNESS_URL also writes grok's config.toml under HOME; keep it out of
        // the real one.
        using var restore = new EnvironmentScope([new("HOME", _workspace), new("GROK_HOME", "")]);

        var catalog = new AgentCatalog(
        [
            new AgentDefinition("probe", AgentMode.Headless, new AgentLaunch("bash", ["-c", script], LanguageModel: false)),
        ]);

        return await new ProcessAgentRunner(catalog, new RunHeartbeat(), runAs: runAs).RunAsync(new AgentInvocation(
            new ContainerId("alpha", "worker"),
            "You are a probe.",
            "hello",
            _workspace,
            new Dictionary<string, string>
            {
                ["HARNESS_URL"] = "http://127.0.0.1:9",
                ["HARNESS_KEY"] = "test-key",
                ["HARNESS_MEMBER"] = "worker",
            },
            Agent: "probe"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_agent_child_can_read_its_own_mcp_config_as_launched()
    {
        var runAs = SwitchingOrSkip();

        var result = await RunAsync(runAs,
            """
            id -u; id -g
            stat -c '%g %a' "$(dirname "$HARNESS_MCP_CONFIG")"; stat -c '%g' "$HARNESS_MCP_CONFIG" "$HARNESS_MCP_CONFIG_TOML"
            cat "$HARNESS_MCP_CONFIG" "$HARNESS_MCP_CONFIG_TOML"
            grep -E '^Cap(Prm|Eff|Amb):' /proc/self/status
            """);

        Assert.True(result.ExitCode == 0, result.Output + result.LaunchError);
        var lines = result.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(runAs.Uid.ToString(), lines[0]);
        Assert.Equal(runAs.Gid.ToString(), lines[1]);

        // The directory and both files belong to the agent's group. The directory lets the group
        // in and no other user, so the files stay closed to everyone outside it.
        Assert.Equal($"{runAs.Gid} 750", lines[2]);
        Assert.Equal(runAs.Gid.ToString(), lines[3]);
        Assert.Equal(runAs.Gid.ToString(), lines[4]);

        Assert.Contains("\"mcpServers\"", result.Output);
        Assert.Contains("[mcp_servers.harness]", result.Output);

        // The child keeps no capability: it cannot switch again or signal the Host.
        Assert.Contains("CapPrm:\t0000000000000000", result.Output);
        Assert.Contains("CapEff:\t0000000000000000", result.Output);
        Assert.Contains("CapAmb:\t0000000000000000", result.Output);
    }

    /// <summary>
    /// The per-launch directory is the agent's group, 750, and its parent /tmp/harness-mcp is the
    /// Host's group - the Host runs with umask 0007 - so a parent the agent cannot get
    /// through leaves every Concierge failing with "permission denied" on its mcp.json. The tests
    /// above make the parent with the test process's umask, open to everyone, which cannot show
    /// that. Here the parent is closed the way the Host closes it. Shared, the parent lets the agent's group through and no further: it
    /// cannot list it or write in it, so another launch's directory stays out of reach by name.
    /// </summary>
    [Fact]
    public async Task An_agent_child_reaches_its_mcp_config_through_a_parent_the_host_closed()
    {
        var runAs = SwitchingOrSkip();
        var parent = Path.Combine(Path.GetTempPath(), "harness-mcp");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, (UnixFileMode)0b111_111_000); // 770, the Host's umask 0007

        var result = await RunAsync(runAs, "cat \"$HARNESS_MCP_CONFIG\" >/dev/null && echo read; ls \"$(dirname \"$(dirname \"$HARNESS_MCP_CONFIG\")\")\" >/dev/null 2>&1 && echo listed || echo not-listed");

        Assert.True(result.ExitCode == 0, result.Output + result.LaunchError);
        Assert.Contains("read", result.Output);
        Assert.Contains("not-listed", result.Output);
        Assert.Equal($"{runAs.Gid} 710", $"{Stat("%g", parent)} {Stat("%a", parent)}");
    }

    [Fact]
    public async Task A_child_that_does_not_switch_runs_as_the_host()
    {
        var result = await RunAsync(AgentLaunchUser.Same(Target, "test"), "id -u; cat \"$HARNESS_MCP_CONFIG\" >/dev/null && echo read");

        Assert.Equal(0, result.ExitCode);
        var lines = result.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(RunId("-u"), lines[0]);
        Assert.Equal("read", lines[1]);
    }

    [Fact]
    public async Task The_concierge_is_launched_as_the_agent_and_its_mcp_config_is_shared_with_it()
    {
        var runAs = SwitchingOrSkip();
        var ct = TestContext.Current.CancellationToken;
        var catalog = new AgentCatalog(AgentCatalogFile.BuiltIns());
        var agent = catalog.Definitions
            .First(d => d.Mode == AgentMode.Interactive && !d.Hidden && d.Launch.LanguageModel).Name;

        using var restore = new EnvironmentScope([new("HOME", _workspace), new("GROK_HOME", "")]);

        var factory = new ConciergeLaunchFactory(
            new TeamPaths(_workspace), "http://localhost:5000", new MintingPrincipals(), catalog, runAs: runAs);

        var spec = await factory.ForAsync(
            team: "", teamLabel: "this instance", user: "user-1", login: "person@example.com",
            agent, teamEnv: new Dictionary<string, string>(), ct: ct);

        Assert.Equal(runAs.Prefix, spec.Argv!.Take(runAs.Prefix.Count));
        Assert.Equal(catalog.Interactive(agent)!.FileName, spec.Argv![runAs.Prefix.Count]);

        var directory = Path.GetDirectoryName(spec.Env!["HARNESS_MCP_CONFIG"])!;
        Assert.Equal(runAs.Gid.ToString(), Stat("%g", directory));
        Assert.Equal("750", Stat("%a", directory));
        Assert.Equal(runAs.Gid.ToString(), Stat("%g", spec.Env["HARNESS_MCP_CONFIG"]));

        Directory.Delete(directory, recursive: true);
        foreach (var file in spec.TempFiles ?? []) File.Delete(file);
    }

    /// <summary>
    /// grok's config.toml lives in the shared agent-home, which the image gives
    /// to the agent's group with setgid. What the Host writes there - the .grok it creates and
    /// the file - must stay rewritable by grok running as the agent, as grok rewrites it.
    /// </summary>
    [Fact]
    public void The_agent_can_rewrite_the_grok_config_the_host_wrote()
    {
        var runAs = SwitchingOrSkip();

        // The image's agent-home: the agent's group, group rwx, setgid.
        var home = Path.Combine(_workspace, "agent-home");
        Directory.CreateDirectory(home);
        Run("chgrp", runAs.Gid.ToString(), home);
        Run("chmod", "2775", home);
        var path = Path.Combine(home, ".grok", "config.toml");

        McpLaunchConfig.EnsureGrokConfig("http://127.0.0.1:8080/mcp", path);

        Assert.Equal($"{runAs.Gid} 2775", Stat("%g %a", Path.GetDirectoryName(path)!));
        Assert.Equal(runAs.Gid.ToString(), Stat("%g", path));

        // An edit in place (the file is group-writable), then a sibling moved over it (the
        // directory is).
        var result = Run(runAs.Wrap(["bash", "-c",
            "cd \"$0\" && printf '[ui]\\n' >> config.toml && cp config.toml next.tmp && mv next.tmp config.toml && echo rewritten",
            Path.GetDirectoryName(path)!]));

        Assert.Equal("rewritten", result);
        Assert.EndsWith("[ui]\n", File.ReadAllText(path));
    }

    /// <summary>
    /// Through the prefix the Host really uses (<see cref="AgentLaunchUser.Resolve"/> and
    /// <see cref="ProcessAgentRunner"/>, no hand-written setpriv line): the child holds no
    /// capability, and a signal to a process of another uid - the Host's, in the image - is refused.
    /// </summary>
    [Fact]
    public async Task A_launched_agent_has_no_capability_and_cannot_signal_a_host_like_process()
    {
        var runAs = SwitchingOrSkip();

        // A stand-in for the Host: another uid that is neither root nor the agent.
        const int HostLikeUid = 65533;
        var victimStart = new System.Diagnostics.ProcessStartInfo(SystemCommand.Find("setpriv")!);
        foreach (var argument in new[] { $"--reuid={HostLikeUid}", $"--regid={HostLikeUid}", "--clear-groups", "--", "/bin/sleep", "60" })
        {
            victimStart.ArgumentList.Add(argument);
        }

        using var victim = System.Diagnostics.Process.Start(victimStart)!;
        try
        {
            var result = await RunAsync(runAs, $$"""
                grep -E '^Cap(Inh|Prm|Eff|Amb):' /proc/self/status
                kill -0 {{victim.Id}} 2>&1; echo "probe=$?"
                kill -KILL {{victim.Id}} 2>&1; echo "kill=$?"
                """);

            Assert.Contains("CapInh:\t0000000000000000", result.Output);
            Assert.Contains("CapPrm:\t0000000000000000", result.Output);
            Assert.Contains("CapEff:\t0000000000000000", result.Output);
            Assert.Contains("CapAmb:\t0000000000000000", result.Output);
            Assert.Contains("probe=1", result.Output);
            Assert.Contains("kill=1", result.Output);
            Assert.Contains("Operation not permitted", result.Output);
            Assert.False(victim.HasExited, "the agent child killed a process it does not own");
        }
        finally
        {
            // Only the process this test started, by its own handle.
            if (!victim.HasExited) victim.Kill();
            await victim.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
    }

    private static string Stat(string format, string path) => Run("stat", "-c", format, path);

    private static string RunId(string flag) => Run("id", flag);

    private static string Run(IReadOnlyList<string> argv) => Run(argv[0], [.. argv.Skip(1)]);

    private static string Run(string file, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(file) { RedirectStandardOutput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }
}

/// <summary>
/// The member skill tells an agent that it is not root, where user-space tools install,
/// and that an operating-system package is a request to the person, naming the setting.
/// </summary>
public sealed class NotRootSkillTests
{
    [Fact]
    public void The_member_skill_says_where_tools_install_and_names_the_system_packages_setting()
    {
        var body = BuiltInSkills.Find("member")!.Body;

        Assert.Contains("not as root", body);
        foreach (var place in new[] { "npm install -g", "/data/npm-global", "pip install --user", "go install", "dotnet tool install", "/data/bin" })
        {
            Assert.Contains(place, body);
        }

        Assert.Contains("\"System packages\"", body);
        Assert.Contains("do not work around it", body);
    }
}
