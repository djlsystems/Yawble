using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>Tests that change this process's own environment - PATH, HOME, provider keys - run
/// alone, because every other test in the assembly shares that environment.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}

/// <summary>
/// WHAT A CHILD IS HANDED. A catalog or team env entry beginning `HARNESS_` is dropped, never merely
/// overwritten: a hand-edited file must not be able to hand a member another principal's credential.
/// And a child is handed its own provider's key and no other provider's.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class AgentEnvironmentTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"harness-env-test-{Guid.NewGuid():N}");

    public AgentEnvironmentTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Harness_entries_in_a_catalog_or_team_env_are_dropped()
    {
        var principals = new MintingPrincipals();
        var catalog = new AgentCatalog(
        [
            new AgentDefinition(
                "grok", AgentMode.Headless, new AgentLaunch("grok", []),
                Env: new Dictionary<string, string>
                {
                    ["HARNESS_KEY"] = "someone-elses-key",
                    ["harness_causation"] = "9",
                    ["XAI_API_KEY"] = "own",
                }),
        ]);
        var teamEnv = new Dictionary<string, string>
        {
            ["HARNESS_SHARED"] = "/another/teams/folder",
            ["Harness_Member"] = "Imposter",
            ["TEST_ADMIN_EMAIL"] = "admin@alpha.local",
        };
        var environment = new AgentEnvironment(
            principals, catalog, "http://localhost:5000",
            new TeamDocuments(new TeamPaths(_directory)));
        var id = new ContainerId("Alpha", "DeveloperRowan");

        // A permit-less worker returns before the platform's own assignments, so nothing later
        // overwrites what the catalog set: the filter is the only thing between it and the key.
        var permitless = await environment.ForContainerAsync(
            id, "grok", new HashSet<string>(), teamEnv, [], TestContext.Current.CancellationToken);

        Assert.DoesNotContain(permitless.Keys, IsReserved);
        Assert.Equal("own", permitless["XAI_API_KEY"]);

        var member = await environment.ForContainerAsync(
            id, "grok", new HashSet<string> { Permits.Progress }, teamEnv, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(principals.Minted, member["HARNESS_KEY"]);
        Assert.Equal(id.Name, member["HARNESS_MEMBER"]);
        Assert.NotEqual("/another/teams/folder", member[AgentEnvironment.SharedVariable]);
        Assert.DoesNotContain(member.Keys, key => IsReserved(key) && !PlatformAssigned.Contains(key));
        Assert.Equal("admin@alpha.local", member["TEST_ADMIN_EMAIL"]);
    }

    [Fact]
    public async Task A_child_receives_only_its_own_provider_key()
    {
        // A real child process. What a child inherits is decided at the spawn site, from the Host's
        // own environment, and only a process that prints its environment can show it.
        var bin = Path.Combine(_directory, "bin");
        Directory.CreateDirectory(bin);
        var grok = Path.Combine(bin, "grok");
        await TestExecutable.WriteAsync(grok, "#!/bin/sh\nenv\n");

        // The provider keys the container is started with (scripts/ensure-agent-clis.sh), keyed by
        // the command whose key each is (auth-probes.json).
        var keys = new Dictionary<string, string>
        {
            ["XAI_API_KEY"] = "xai-own",
            ["ANTHROPIC_API_KEY"] = "anthropic-foreign",
            ["OPENAI_API_KEY"] = "openai-foreign",
            ["GH_TOKEN"] = "github-foreign",
            ["GEMINI_API_KEY"] = "gemini-foreign",
        };

        using var restore = new EnvironmentScope(
            keys.Append(new("PATH", bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"))));

        var catalog = new AgentCatalog(
            [new AgentDefinition("grok", AgentMode.Headless, new AgentLaunch("grok", []))]);
        var runner = new ProcessAgentRunner(catalog, new RunHeartbeat());

        var result = await runner.RunAsync(
            new AgentInvocation(
                new ContainerId("Alpha", "DeveloperRowan"), "You are a member.", "print env",
                _directory, new Dictionary<string, string>(), Agent: "grok"),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        var received = result.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2)[0])
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("XAI_API_KEY", received);
        var foreign = keys.Keys.Where(key => key != "XAI_API_KEY" && received.Contains(key)).ToList();
        Assert.True(foreign.Count == 0, "grok's child was handed " + string.Join(", ", foreign));
    }

    [Fact]
    public async Task A_team_with_a_GitHub_remote_hands_its_member_GH_TOKEN_and_no_other_foreign_key()
    {
        using var restore = new EnvironmentScope(
        [
            new("GH_TOKEN", "github-token"),
            new("OPENAI_API_KEY", "openai-foreign"),
        ]);
        var catalog = new AgentCatalog(
            [new AgentDefinition("grok", AgentMode.Headless, new AgentLaunch("grok", []))]);
        var environment = new AgentEnvironment(
            new MintingPrincipals(), catalog, "http://localhost:5000",
            new TeamDocuments(new TeamPaths(_directory)));
        var id = new ContainerId("Alpha", "DeveloperRowan");

        var onGitHub = await environment.ForContainerAsync(
            id, "grok", new HashSet<string> { Permits.Progress }, new Dictionary<string, string>(),
            ["https://github.com/example/repo.git"], TestContext.Current.CancellationToken);
        var elsewhere = await environment.ForContainerAsync(
            id, "grok", new HashSet<string> { Permits.Progress }, new Dictionary<string, string>(),
            ["https://gitlab.example/o/r.git"], TestContext.Current.CancellationToken);

        Assert.Equal("github-token", onGitHub["GH_TOKEN"]);
        Assert.False(elsewhere.ContainsKey("GH_TOKEN"));

        // At the spawn site the handed-in GH_TOKEN survives; the other foreign key is removed.
        var inherited = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = "github-token", ["GITHUB_TOKEN"] = "alias",
            ["OPENAI_API_KEY"] = "openai-foreign", ["XAI_API_KEY"] = "xai-own", ["PATH"] = "/bin",
        };
        AgentEnvironment.ScopeProviderKeys(inherited, "grok", onGitHub);

        Assert.Equal(
            ["GH_TOKEN", "GITHUB_TOKEN", "PATH", "XAI_API_KEY"],
            inherited.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Copilot_keeps_GH_TOKEN_and_loses_every_other_provider_key()
    {
        var inherited = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = "own", ["ANTHROPIC_API_KEY"] = "a", ["OPENAI_API_KEY"] = "o",
            ["XAI_API_KEY"] = "x", ["GEMINI_API_KEY"] = "g", ["GIT_AUTHOR_NAME"] = "Rowan",
        };

        AgentEnvironment.ScopeProviderKeys(inherited, "/usr/local/bin/copilot", new Dictionary<string, string>());

        Assert.Equal(["GH_TOKEN", "GIT_AUTHOR_NAME"], inherited.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_codex_member_is_launched_with_the_mcp_flags_and_its_key_in_HARNESS_KEY()
    {
        // The built-in preset through the real launch path: the environment a member is minted,
        // then the runner. codex reads the key from the variable its `env_http_headers` flag
        // names, so the variable reaching the child is half of the wiring.
        var bin = Path.Combine(_directory, "bin");
        Directory.CreateDirectory(bin);
        await TestExecutable.WriteAsync(
            Path.Combine(bin, "codex"),
            "#!/bin/sh\nfor a in \"$@\"; do echo \"ARG $a\"; done\necho \"KEY $HARNESS_KEY\"\necho 'tokens used 1'\n");

        using var restore = new EnvironmentScope(
            [new("PATH", bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"))]);

        var principals = new MintingPrincipals();
        var catalog = new AgentCatalog(AgentCatalogFile.BuiltIns());
        var environment = new AgentEnvironment(
            principals, catalog, "http://127.0.0.1:5391",
            new TeamDocuments(new TeamPaths(_directory)));
        var id = new ContainerId("Alpha", "DeveloperTomas");
        var variables = await environment.ForContainerAsync(
            id, "codex-headless", new HashSet<string> { Permits.Progress },
            new Dictionary<string, string>(), [], TestContext.Current.CancellationToken);

        var workspace = Path.Combine(_directory, "workspace");
        Directory.CreateDirectory(workspace);
        var result = await new ProcessAgentRunner(catalog, new RunHeartbeat()).RunAsync(
            new AgentInvocation(id, "You are a member.", "hello", workspace, variables, Agent: "codex-headless"),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("ARG mcp_servers.harness.url=\"http://127.0.0.1:5391/mcp\"", lines);
        Assert.Contains("ARG mcp_servers.harness.env_http_headers={\"X-Api-Key\"=\"HARNESS_KEY\"}", lines);
        Assert.Contains("KEY " + principals.Minted, lines);
    }

    [Fact]
    public async Task A_member_gets_its_own_short_TMPDIR_and_two_members_never_share_one()
    {
        // A real child printing what it was handed. A handed-in TMPDIR naming the shared /tmp is
        // there to show it cannot win: the member's own folder is set after every merge.
        var bin = Path.Combine(_directory, "bin");
        Directory.CreateDirectory(bin);
        await TestExecutable.WriteAsync(Path.Combine(bin, "grok"), "#!/bin/sh\necho \"TMPDIR=$TMPDIR\"\n");

        using var restore = new EnvironmentScope(
            [new("PATH", bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"))]);

        var catalog = new AgentCatalog(
            [new AgentDefinition("grok", AgentMode.Headless, new AgentLaunch("grok", []))]);
        var runner = new ProcessAgentRunner(catalog, new RunHeartbeat());

        // A workspace path as long as a real team's, well past what a socket path can hold once a
        // tool puts its pipe in TMPDIR.
        var workspaces = Path.Combine(_directory, "teams", "a-team-with-a-long-descriptive-name", "workspaces");

        async Task<string> TmpdirOf(string member)
        {
            var workspace = Path.Combine(workspaces, member);
            Directory.CreateDirectory(workspace);
            var result = await runner.RunAsync(
                new AgentInvocation(
                    new ContainerId("Alpha", member), "You are a member.", "print TMPDIR",
                    workspace, new Dictionary<string, string> { ["TMPDIR"] = "/tmp" }, Agent: "grok"),
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            var tmpdir = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("TMPDIR=", StringComparison.Ordinal))["TMPDIR=".Length..];
            Assert.True(Directory.Exists(tmpdir), $"{tmpdir} was not created");
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(Path.Combine(workspace, MemberTemp.WindowsFolderName), tmpdir);
                return tmpdir;
            }

            // A real folder, owner-only, and the one the workspace's link names.
            Assert.Null(new DirectoryInfo(tmpdir).LinkTarget);
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(tmpdir) & (UnixFileMode)0x1FF);
            Assert.Equal(tmpdir, new FileInfo(MemberTemp.LinkFor(workspace)).LinkTarget);

            // In /tmp, never the Host's own TMPDIR: when the Host switches users the agent makes
            // this folder, and the Host's TMPDIR may be a folder only root can write (the release
            // suite's is), where a member would never start.
            Assert.Equal("/tmp", MemberTemp.Root);
            Assert.Equal(MemberTemp.Root, Path.GetDirectoryName(tmpdir));

            // The .NET runtime's named pipes, which test platforms, build servers and the compiler
            // server use, are Unix sockets at TMPDIR/CoreFxPipe_<name>, and a socket path holds 103
            // bytes. The name here is as long as the test platform's.
            var socket = Path.Combine(tmpdir, "CoreFxPipe_" + new string('p', 36));
            Assert.True(
                System.Text.Encoding.UTF8.GetByteCount(socket) <= 103,
                $"'{socket}' is {System.Text.Encoding.UTF8.GetByteCount(socket)} bytes, over a socket path's 103");

            return tmpdir;
        }

        var rowan = await TmpdirOf("DeveloperRowan");
        var tomas = await TmpdirOf("DeveloperTomas");

        Assert.NotEqual(rowan, tomas);
        Assert.Equal(rowan, await TmpdirOf("DeveloperRowan"));

        // Not a folder by path: a member made again under the same name starts empty.
        var link = MemberTemp.LinkFor(Path.Combine(workspaces, "DeveloperRowan"));
        File.Delete(link);
        var successor = await TmpdirOf("DeveloperRowan");
        Assert.NotEqual(rowan, successor);

        foreach (var folder in new[] { rowan, tomas, successor })
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>What the platform itself sets, spelled exactly. Any other HARNESS_ key, in any
    /// case, came from a catalog or team entry that should have been dropped.</summary>
    private static readonly HashSet<string> PlatformAssigned = new(StringComparer.Ordinal)
    {
        "HARNESS_URL", "HARNESS_TEAM", "HARNESS_MEMBER", "HARNESS_KEY", AgentEnvironment.SharedVariable,
    };

    private static bool IsReserved(string key) =>
        key.StartsWith("HARNESS_", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        try
        {
            MemberTempCleanup.Remove(_directory);
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A shell script standing in for an agent CLI. The Host runs in a Linux container.</summary>
public static class TestExecutable
{
    public static async Task WriteAsync(string path, string script)
    {
        await File.WriteAllTextAsync(path, script, TestContext.Current.CancellationToken);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

/// <summary>Sets process environment variables for one test and puts the previous values back.</summary>
public sealed class EnvironmentScope : IDisposable
{
    private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

    public EnvironmentScope(IEnumerable<KeyValuePair<string, string>> values)
    {
        foreach (var (name, value) in values)
        {
            _previous.TryAdd(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous) Environment.SetEnvironmentVariable(name, value);
    }
}

/// <summary>Mints one fixed credential and answers nothing else. The environment and the Concierge
/// launch only ever mint.</summary>
public sealed class MintingPrincipals : IPrincipalStore
{
    public string Minted { get; } = "minted-" + Guid.NewGuid().ToString("N");

    public Task<string> MintAsync(
        string id, PrincipalKind kind, string? team, IReadOnlySet<string> permits,
        string? credential = null, string? ownerUserId = null, string? label = null,
        CancellationToken ct = default) => Task.FromResult(Minted);

    public Task<Principal?> ResolveAsync(string credential, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<string?> TeamForAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task RevokeAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task<int> RevokeForTeamAsync(string team, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<ApiKeySummary>> ListApiKeysForAsync(
        string ownerUserId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<ApiKeySummary>> ListAllApiKeysAsync(CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<string?> OwnerOfApiKeyAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<DateTimeOffset?> LastUsedAtAsync(string id, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
