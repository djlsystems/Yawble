using System.Diagnostics;

namespace Harness.Tests.Host;

/// <summary>
/// <c>scripts/prepare-volume.sh</c> is what the entrypoint runs as root before it drops to
/// <c>harness</c>: the ownership map on the volume and the "System packages" install. It runs here
/// against a temporary directory with <c>chown</c> and <c>apt-get</c> replaced by recorders, so the
/// map, the modes, idempotence and the package-name check are pinned without an image. What still
/// needs a built image - real users, setpriv dropping to them, apt-get reaching a mirror - is listed
/// in the Containerfile, not here.
/// </summary>
public sealed class PrepareVolumeTests : IDisposable
{
    private const string HostOwner = "10001:10001";
    private const string AgentOwner = "10002:10002";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"harness-volume-{Guid.NewGuid():N}");
    private readonly string _data;
    private readonly string _chownLog;
    private readonly string _aptLog;
    private readonly string _chownStub;
    private readonly string _aptStub;

    public PrepareVolumeTests()
    {
        _data = Path.Combine(_root, "data");
        Directory.CreateDirectory(_data);
        _chownLog = Path.Combine(_root, "chown.log");
        _aptLog = Path.Combine(_root, "apt.log");
        _chownStub = Stub("chown-stub", $"printf '%s\\n' \"$*\" >> '{_chownLog}'");
        _aptStub = Stub("apt-stub", $"printf '%s\\n' \"$*\" >> '{_aptLog}'; [ \"$1\" != install ] || [ ! -f '{_root}/apt-fails' ]");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void An_all_root_volume_gets_the_host_files_for_harness_and_everything_else_for_agent()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");
        RootOrSkip();
        SeedExistingVolume();

        var output = Run("ownership");

        var chowned = ChownedPaths();
        Assert.Equal("10001:10002", chowned[_data]);

        foreach (var host in new[]
                 {
                     "messages.db", "messages.db-wal", "messages.db-shm", "keys", "keys/key-1.xml", "agents.json",
                     "agents.json.before-builtins", "agent-launch.json", "agent-launch.json.tmp", "logs", "logs/host.log",
                     "backups", "backups/old.db", "host.lock", "system-packages",
                     "connections", "connections/.connect-report.json",
                     "agent-credentials", "agent-credentials/request-1.json", "agent-auth.json", "agent-auth.json.tmp",
                     "agent-tools.json", "agent-launch-checks.json", "wip.json", "workers.json", "workers.json.tmp",
                 })
        {
            Assert.True(chowned.TryGetValue(Path.Combine(_data, host), out var owner), $"{host} was not handed over. {output}");
            Assert.Equal(HostOwner, owner);
        }

        foreach (var agent in new[]
                 {
                     "teams", "teams/alpha/workspaces/Ines/notes.md", "documents/spec.md", "agent-home",
                     "agent-home/.claude/.credentials.json", "agent-home/.ssh/id_ed25519", "agent-home/.grok",
                     "agent-home/.grok/config.toml", "npm-global/bin/claude",
                     "bin/tool", "go", "ms-playwright", "steering", "cli-versions.jsonl",
                     "tenant-interactive-agent-workspaces",
                 })
        {
            Assert.True(chowned.TryGetValue(Path.Combine(_data, agent), out var owner), $"{agent} was not handed over. {output}");
            Assert.Equal(AgentOwner, owner);
        }

        // Host files: owner-only, all the way down.
        Assert.Equal("700", Mode("keys"));
        Assert.Equal("600", Mode("keys/key-1.xml"));
        Assert.Equal("600", Mode("messages.db"));
        Assert.Equal("600", Mode("agents.json"));
        // What --doctor reports as agentLaunch: an agent that could write it could make the doctor lie.
        Assert.Equal("600", Mode("agent-launch.json"));
        Assert.Equal("600", Mode("agent-launch.json.tmp"));
        Assert.Equal("700", Mode("logs"));
        Assert.Equal("600", Mode("logs/host.log"));
        // The operator CLI's `connect` exchange: the host answers only while nobody else can write here.
        Assert.Equal("700", Mode("connections"));
        Assert.Equal("600", Mode("connections/.connect-report.json"));
        Assert.Equal("750", Mode("."));

        // Agent's trees: the host writes through the group, and what it makes stays in the group.
        foreach (var directory in new[]
                 {
                     "teams", "teams/alpha/workspaces", "documents", "tenant-interactive-agent-workspaces", "steering",
                     "agent-home", "npm-global", "npm-global/bin", "bin", "go", "ms-playwright",
                 })
        {
            Assert.Equal("2775", Mode(directory));
        }

        Assert.Equal("664", Mode("teams/alpha/workspaces/Ines/notes.md"));
        Assert.Equal("664", Mode("documents/spec.md"));
        Assert.Equal("775", Mode("npm-global/bin/claude"));

        // agent-home is not group-writable all the way down: the owner changes and the modes of the
        // Claude login and the ssh key do not - ssh refuses a key the group can read.
        Assert.Equal("600", Mode("agent-home/.claude/.credentials.json"));
        Assert.Equal("700", Mode("agent-home/.claude"));
        Assert.Equal("600", Mode("agent-home/.ssh/id_ed25519"));
        Assert.Equal("700", Mode("agent-home/.ssh"));
        Assert.Equal("600", Mode("agent-home/.gitconfig"));
        Assert.Equal(AgentOwner, ChownedPaths()[Path.Combine(_data, "agent-home/.ssh/id_ed25519")]);

        // The one exception: .grok, so the host (in group agent) can rewrite grok's config.toml with
        // its MCP entry. The directory is setgid and group rwx, files directly in it group rw, and
        // grok's own bin below keeps its modes.
        var grok = Convert.ToInt32(Mode("agent-home/.grok"), 8);
        Assert.True((grok & Convert.ToInt32("2770", 8)) == Convert.ToInt32("2770", 8), $".grok is {Mode("agent-home/.grok")}");
        Assert.Equal("660", Mode("agent-home/.grok/config.toml"));
        Assert.Equal("700", Mode("agent-home/.grok/bin"));
    }

    [Fact]
    public void A_file_browser_root_outside_the_volume_is_agents_and_a_system_directory_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");
        RootOrSkip();
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(Path.Combine(elsewhere, "teams", "beta"));
        File.WriteAllText(Path.Combine(elsewhere, "teams", "beta", "README.md"), "x");
        File.SetUnixFileMode(Path.Combine(elsewhere, "teams", "beta", "README.md"), (UnixFileMode)Convert.ToInt32("644", 8));

        // Only paths inside this test's temp folder ever reach `ownership`, which chmods for real.
        var output = Run("ownership", new Dictionary<string, string>
        {
            ["FileBrowser__Roots__0__Path"] = elsewhere + "/",
            ["FileBrowser__Roots__1__Path"] = Path.Combine(_root, "missing"),
        });

        var chowned = ChownedPaths();
        Assert.Equal(AgentOwner, chowned[elsewhere]);
        Assert.Equal(AgentOwner, chowned[Path.Combine(elsewhere, "teams", "beta", "README.md")]);
        Assert.Equal("2775", Exec("stat", ["-c", "%a", Path.Combine(elsewhere, "teams", "beta")]).Output.Trim());
        Assert.Equal("664", Exec("stat", ["-c", "%a", Path.Combine(elsewhere, "teams", "beta", "README.md")]).Output.Trim());
        Assert.Contains("missing' left alone: it does not resolve to an existing path", output);

        // System directories go to `roots`, which only lists: a regressed check must fail this test,
        // never chmod or chown /etc on the machine running it. Other spellings, and a symlink,
        // resolve to the system directory first.
        var toEtc = Path.Combine(_root, "looks-harmless");
        File.CreateSymbolicLink(toEtc, "/etc");
        // A folder under /etc that exists here: the path must resolve before it is judged, and the
        // SDK image has no /etc/ssh, so a fixed name would test "does not resolve" instead.
        var underEtc = Directory.EnumerateDirectories("/etc")
            .Where(d => new DirectoryInfo(d).LinkTarget is null).Order(StringComparer.Ordinal).First();
        var refused = new[] { "/usr", underEtc, "/", "//etc", "/./etc", "/var/log", "/var/cache/", toEtc };
        var environment = new Dictionary<string, string> { ["FileBrowser__Roots__0__Path"] = elsewhere };
        for (var i = 0; i < refused.Length; i++) environment[$"FileBrowser__Roots__{i + 1}__Path"] = refused[i];

        var (code, listed) = Exec("/bin/sh", [Path.Combine(RepoRoot(), "scripts", "prepare-volume.sh"), "roots"],
            new Dictionary<string, string>(environment) { ["HARNESS_DATA_ROOT"] = _data });
        Assert.True(code == 0, listed);
        Assert.Equal([elsewhere], listed.Split('\n').Where(line => line.StartsWith('/')));
        Assert.Contains("'/usr' left alone: a system directory", listed);
        Assert.Contains($"'{underEtc}' left alone: a system directory ({underEtc})", listed);
        Assert.Contains("'/' left alone: a system directory", listed);
        Assert.Contains("'//etc' left alone: a system directory (/etc)", listed);
        Assert.Contains("'/./etc' left alone: a system directory (/etc)", listed);
        Assert.Contains("'/var/log' left alone: a system directory (/var/log)", listed);
        Assert.Contains("'/var/cache/' left alone: a system directory (/var/cache)", listed);
        Assert.Contains($"'{toEtc}' left alone: a system directory (/etc)", listed);
    }

    [Fact]
    public void A_missing_plugins_directory_is_created_for_harness_and_readable_by_agent()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");

        var output = Run("ownership");

        Assert.True(Directory.Exists(Path.Combine(_data, "plugins")), output);
        Assert.Equal("10001:10002", ChownedPaths()[Path.Combine(_data, "plugins")]);
        Assert.Equal("750", Mode("plugins"));
    }

    [Fact]
    public void An_installed_plugin_is_harness_agent_with_its_executable_kept_and_nothing_writable_by_agent()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");
        var version = Path.Combine(_data, "plugins", "sample-echo", "0.1.0");
        Directory.CreateDirectory(Path.Combine(version, "lib"));
        File.WriteAllText(Path.Combine(version, "plugin.json"), "{}");
        File.WriteAllText(Path.Combine(version, "sample-echo"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(version, "lib", "SampleEcho.dll"), "x");
        File.WriteAllText(Path.Combine(_data, "plugins", "sample-echo", "active"), "0.1.0\n");
        File.SetUnixFileMode(Path.Combine(version, "plugin.json"), (UnixFileMode)Convert.ToInt32("666", 8));
        File.SetUnixFileMode(Path.Combine(version, "sample-echo"), (UnixFileMode)Convert.ToInt32("4777", 8));
        File.SetUnixFileMode(Path.Combine(version, "lib"), (UnixFileMode)Convert.ToInt32("2777", 8));
        File.SetUnixFileMode(Path.Combine(version, "lib", "SampleEcho.dll"), (UnixFileMode)Convert.ToInt32("600", 8));
        File.WriteAllText(Path.Combine(_data, "plugins", ".rescan-report.json"), "{}");
        File.SetUnixFileMode(Path.Combine(_data, "plugins", ".rescan-report.json"), (UnixFileMode)Convert.ToInt32("600", 8));

        var output = Run("ownership");

        var chowned = ChownedPaths();
        foreach (var path in new[] { "plugins", "plugins/sample-echo/0.1.0/lib/SampleEcho.dll", "plugins/sample-echo/active" })
        {
            Assert.True(chowned.TryGetValue(Path.Combine(_data, path), out var owner), $"{path} was not handed over. {output}");
            Assert.Equal("10001:10002", owner);
        }

        Assert.Equal("750", Mode("plugins"));
        Assert.Equal("750", Mode("plugins/sample-echo"));
        Assert.Equal("750", Mode("plugins/sample-echo/0.1.0/lib"));
        Assert.Equal("640", Mode("plugins/sample-echo/0.1.0/plugin.json"));
        Assert.Equal("640", Mode("plugins/sample-echo/0.1.0/lib/SampleEcho.dll"));
        Assert.Equal("640", Mode("plugins/sample-echo/active"));
        // The host's rescan answer names members of every team: it stays the host's alone.
        Assert.Equal("600", Mode("plugins/.rescan-report.json"));
        // The launcher keeps its execute bits and loses setuid and the write bits.
        Assert.Equal("750", Mode("plugins/sample-echo/0.1.0/sample-echo"));

        // And a second start, as this process's own ids, finds nothing more to change.
        Assert.Contains("ownership set: 0 change(s)", Run("ownership", new Dictionary<string, string>
        {
            ["HARNESS_HOST_UID"] = Id("-u"), ["HARNESS_HOST_GID"] = Id("-g"),
            ["HARNESS_AGENT_UID"] = Id("-u"), ["HARNESS_AGENT_GID"] = Id("-g"), ["HARNESS_CHOWN"] = "chown",
        }));
    }

    [Fact]
    public void A_local_repository_is_harness_agent_readable_by_agent_and_nothing_in_it_writable_by_agent()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");
        var repos = Path.Combine(_data, "repos");
        var bare = Path.Combine(repos, "widget.git");
        Directory.CreateDirectory(repos);
        Exec("git", ["init", "--quiet", "--bare", "-b", "main", bare]);
        var content = Path.Combine(_root, "content.txt");
        File.WriteAllText(content, "widget\n");
        var blob = Exec("git", ["--git-dir", bare, "hash-object", "-w", content]).Output.Trim();
        var loose = Path.Combine("repos", "widget.git", "objects", blob[..2], blob[2..]);
        // What a careless umask or a hand copy may leave: group- and world-writable, setuid.
        File.SetUnixFileMode(Path.Combine(bare, "refs"), (UnixFileMode)Convert.ToInt32("2777", 8));
        File.SetUnixFileMode(Path.Combine(bare, "config"), (UnixFileMode)Convert.ToInt32("4666", 8));
        File.SetUnixFileMode(Path.Combine(bare, "HEAD"), (UnixFileMode)Convert.ToInt32("600", 8));

        var output = Run("ownership");

        var chowned = ChownedPaths();
        foreach (var path in new[] { "repos", "repos/widget.git", "repos/widget.git/config", loose })
        {
            Assert.True(chowned.TryGetValue(Path.Combine(_data, path), out var owner), $"{path} was not handed over. {output}");
            Assert.Equal("10001:10002", owner);
        }

        Assert.Equal("2750", Mode("repos"));
        Assert.Equal("2750", Mode("repos/widget.git"));
        Assert.Equal("2750", Mode("repos/widget.git/refs"));
        Assert.Equal("640", Mode("repos/widget.git/config"));
        Assert.Equal("640", Mode("repos/widget.git/HEAD"));
        // git's own read-only object keeps its owner bits and gains group read, nothing else.
        Assert.Equal("440", Mode(loose));

        // NOTHING under repos is writable by agent (the group) or by anybody else.
        var writable = Exec("find", [repos, "(", "-perm", "/022", "-o", "-perm", "/6000", "!", "-type", "d", ")", "-print"]).Output.Trim();
        Assert.True(writable.Length == 0, $"writable by agent or others: {writable}");

        Assert.Contains("ownership set: 0 change(s)", Run("ownership", new Dictionary<string, string>
        {
            ["HARNESS_HOST_UID"] = Id("-u"), ["HARNESS_HOST_GID"] = Id("-g"),
            ["HARNESS_AGENT_UID"] = Id("-u"), ["HARNESS_AGENT_GID"] = Id("-g"), ["HARNESS_CHOWN"] = "chown",
        }));
    }

    [Fact]
    public void A_missing_repos_directory_is_created_for_harness_and_readable_by_agent()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");

        var output = Run("ownership");

        Assert.True(Directory.Exists(Path.Combine(_data, "repos")), output);
        Assert.Equal("10001:10002", ChownedPaths()[Path.Combine(_data, "repos")]);
        Assert.Equal("2750", Mode("repos"));
    }

    /// <summary>
    /// Members' TMPDIRs live in <c>&lt;data&gt;/tmp</c> (MemberTemp), on the volume rather than a /tmp
    /// the engine may hold in memory: made when missing, and the agent's, which makes them there.
    /// </summary>
    [Fact]
    public void A_missing_tmp_directory_is_created_for_agent_on_the_volume()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");

        var output = Run("ownership");

        Assert.True(Directory.Exists(Path.Combine(_data, "tmp")), output);
        // Handed to agent, or already agent's when this process is agent (nothing to chown).
        Assert.Equal(AgentOwner, ChownedPaths().GetValueOrDefault(Path.Combine(_data, "tmp")) ?? Owner("tmp"));
        Assert.StartsWith("27", Mode("tmp"));
    }

    [Fact]
    public void A_second_start_changes_nothing()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");
        SeedExistingVolume();

        // As root the real chown runs, so ownership itself is shown to settle. Otherwise both users
        // are this process's own ids, and the modes are what is shown to settle.
        var root = Environment.UserName == "root" || Id("-u") == "0";
        var environment = root
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>
            {
                ["HARNESS_HOST_UID"] = Id("-u"), ["HARNESS_HOST_GID"] = Id("-g"),
                ["HARNESS_AGENT_UID"] = Id("-u"), ["HARNESS_AGENT_GID"] = Id("-g"),
            };
        environment["HARNESS_CHOWN"] = "chown";

        var first = Run("ownership", environment);
        Assert.DoesNotContain("ownership set: 0 change(s)", first);

        var second = Run("ownership", environment);
        Assert.Contains("ownership set: 0 change(s)", second);

        if (root)
        {
            Assert.Equal("10001:10002", Owner("."));
            Assert.Equal(HostOwner, Owner("keys/key-1.xml"));
            Assert.Equal(AgentOwner, Owner("agent-home/.claude/.credentials.json"));
        }
    }

    [Fact]
    public void Only_valid_package_names_reach_apt_get_and_nothing_else_is_run()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");
        var pwned = Path.Combine(_root, "pwned");
        File.WriteAllText(Path.Combine(_data, "system-packages"),
            "# Written by the host\n"
            + "htop\r\n"
            + "  postgresql-client  \n"
            + "\n"
            + "vim; touch " + pwned + "\n"
            + "$(touch " + pwned + ")\n"
            + "`touch " + pwned + "`\n"
            + "-o=APT::Get::AllowUnauthenticated=true\n"
            + "Vim\n"
            + "x\n"
            + "* \n"
            + "libstdc++6\n"
            + "g++-12");

        var output = Run("packages");

        Assert.Equal(
            ["update", "install -y --no-install-recommends -- htop postgresql-client libstdc++6 g++-12"],
            File.ReadAllLines(_aptLog));
        Assert.False(File.Exists(pwned), "A line of the file was run.");
        Assert.Contains("refused 'vim; touch", output);
        Assert.Contains("refused '-o=APT::Get::AllowUnauthenticated=true'", output);
        Assert.Contains("system packages: installed", output);
    }

    [Fact]
    public void No_list_runs_no_apt_get()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");

        Assert.Contains("no system packages to install", Run("packages"));
        File.WriteAllText(Path.Combine(_data, "system-packages"), "# Written by the host\n");
        Assert.Contains("no system packages to install", Run("packages"));

        Assert.False(File.Exists(_aptLog));
    }

    [Fact]
    public void A_failed_install_is_logged_and_the_entrypoint_starts_the_host_anyway()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");
        File.WriteAllText(Path.Combine(_data, "system-packages"), "no-such-package\n");
        File.WriteAllText(Path.Combine(_root, "apt-fails"), "");

        var (code, output) = RunWithCode("packages", new Dictionary<string, string>());

        Assert.NotEqual(0, code);
        Assert.Contains("FAILED to install", output);

        // The entrypoint runs under `set -e`: the call must carry its own `||`, or a failed install
        // stops the container before the host.
        var entrypoint = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "container-entrypoint.sh"));
        Assert.Matches(@"prepare-volume\.sh packages \|\| ", entrypoint);
    }

    [Fact]
    public void The_entrypoint_sets_ownership_and_installs_packages_as_root_then_drops_to_harness()
    {
        var entrypoint = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "container-entrypoint.sh"));
        var branches = Branches(entrypoint);
        var prepare = PrepareVolumeFunction(entrypoint);

        // `all`: the volume, the packages, the CLIs, then the drop to harness - in that order.
        var all = branches["all"];
        var ownership = all.IndexOf("prepare_volume", StringComparison.Ordinal);
        var packages = all.IndexOf("prepare-volume.sh packages", StringComparison.Ordinal);
        var clis = all.IndexOf("ensure-agent-clis.sh", StringComparison.Ordinal);
        var drop = all.IndexOf("exec /usr/bin/setpriv --reuid=harness", StringComparison.Ordinal);
        Assert.True(ownership >= 0 && ownership < packages && packages < clis && clis < drop, all);
        Assert.Matches(@"exec /usr/bin/setpriv --reuid=agent[^\n]*\n?[^\n]*ensure-agent-clis\.sh", all);
        Assert.Contains("-- /usr/bin/dotnet /app/Harness.Host.dll", all[drop..]);
        Assert.Contains("prepare-volume.sh ownership", prepare);

        // control: the volume, then the drop to harness; no package and no CLI step.
        var control = branches["control"];
        var controlOwnership = control.IndexOf("prepare_volume", StringComparison.Ordinal);
        var controlDrop = control.IndexOf("exec /usr/bin/setpriv --reuid=harness", StringComparison.Ordinal);
        Assert.True(controlOwnership >= 0 && controlOwnership < controlDrop, control);
        Assert.DoesNotContain("prepare-volume.sh packages", control);
        Assert.DoesNotContain("ensure-agent-clis", control);
        Assert.Contains("-- /usr/bin/dotnet /app/Harness.Host.dll", control[controlDrop..]);

        // worker: packages, the CLIs, then the drop to worker; no ownership.
        var worker = branches["worker"];
        var workerPackages = worker.IndexOf("prepare-volume.sh packages", StringComparison.Ordinal);
        var workerClis = worker.IndexOf("ensure-agent-clis.sh", StringComparison.Ordinal);
        var workerDrop = worker.IndexOf("exec /usr/bin/setpriv --reuid=worker", StringComparison.Ordinal);
        Assert.True(workerPackages >= 0 && workerPackages < workerClis && workerClis < workerDrop, worker);
        Assert.Contains("-- /usr/bin/dotnet /app/Harness.Host.dll", worker[workerDrop..]);
        Assert.DoesNotContain("exec dotnet", entrypoint);

        // What the host and both users need from root before the drop, in every branch.
        foreach (var (name, branch) in branches)
        {
            Assert.Matches(@"(?m)^\s*umask 0007\n\s*exec /usr/bin/setpriv --reuid=(harness|worker)", branch);
        }
        Assert.Matches(@"(?m)^\s+git config --system --add safe\.directory '\*'", entrypoint);
        Assert.Matches(@"(?m)^chmod 1777 /tmp$", entrypoint);

        // Nothing as root writes into the shared HOME: git settings go to the system config.
        Assert.DoesNotContain("git config --global", entrypoint);

        // The agent CLIs' install step and the host see the same tool paths as the worker image's ENV.
        var path = System.Text.RegularExpressions.Regex.Match(entrypoint, @"(?m)^tool_path=""([^""]+)""").Groups[1].Value.Split(':');
        AssertSystemFoldersFirst(path);
        // Both steps that leave root run on it, with agent-home as HOME, the npm prefix, and none of
        // the root section's git and Python settings: the CLI install as agent, and the host.
        const string AgentSide = @"export PATH=""\$tool_path"" HOME=/data/agent-home NPM_CONFIG_PREFIX=/data/npm-global";
        Assert.Matches(AgentSide + @" \\\n\s+&& unset GIT_CONFIG_GLOBAL PYTHONNOUSERSITE && exec /usr/bin/setpriv --reuid=agent", all);
        Assert.Matches(AgentSide + @" HARNESS_UPDATE_AGENTS=0 \\\n\s+&& unset GIT_CONFIG_GLOBAL PYTHONNOUSERSITE && exec /usr/bin/flock ", worker);
        foreach (var branch in new[] { all, worker })
        {
            Assert.Matches("(?m)^\\s*" + AgentSide + @"\n\s*unset GIT_CONFIG_GLOBAL PYTHONNOUSERSITE\n\s*umask 0007\n\s*exec /usr/bin/setpriv --reuid=", branch);
        }
        // Control runs no tool an agent installs: its host's PATH is the system folders alone.
        Assert.Matches(@"(?m)^\s*export PATH=" + SystemPath + @" HOME=/data/agent-home\n\s*unset GIT_CONFIG_GLOBAL PYTHONNOUSERSITE\n\s*umask 0007\n\s*exec /usr/bin/setpriv --reuid=harness", control);

        // HOME=/data/agent-home once per step that leaves root, and nowhere else: root's HOME is /root from the top.
        static int Homes(string text) => text.Split('\n').Count(line => !line.TrimStart().StartsWith('#') && line.Contains("HOME=/data/agent-home", StringComparison.Ordinal));
        Assert.Equal((2, 1, 2), (Homes(all), Homes(control), Homes(worker)));
        Assert.Equal(5, Homes(entrypoint));
    }

    /// <summary>
    /// The entrypoint installs and records the agent CLIs for a worker and for all, and not for
    /// control, which starts no agent CLI. The role is read once, as the host reads it.
    /// </summary>
    [Fact]
    public void The_entrypoint_installs_agent_clis_for_a_worker_and_all_and_not_for_control()
    {
        var entrypoint = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "container-entrypoint.sh"));
        var branches = Branches(entrypoint);

        Assert.Equal(["all", "control", "worker"], branches.Keys.Order(StringComparer.Ordinal));
        Assert.Matches(@"(?m)^case ""\$role"" in$", entrypoint);
        Assert.Contains("echo \"agent cli: control role, no agent CLI is installed or recorded here\"", branches["control"]);
        Assert.DoesNotContain("ensure-agent-clis", branches["control"]);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(branches["all"], @"/opt/harness/ensure-agent-clis\.sh"));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(branches["worker"], @"/opt/harness/ensure-agent-clis\.sh"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(entrypoint, @"/opt/harness/ensure-agent-clis\.sh"));

        // The role line itself, run by sh: trimmed and any case; unset and empty are all.
        var line = RoleLine(entrypoint);
        foreach (var (role, expected) in new (string?, string)[] { ("control", "control"), (" Control ", "control"), ("worker", "worker"), ("all", "all"), (null, "all"), ("", "all") })
        {
            var variables = role is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["HARNESS_ROLE"] = role };
            var (code, output) = Exec("/bin/sh", ["-c", line + "\nprintf '%s' \"$role\""], variables);
            Assert.Equal((0, expected), (code, output));
        }
    }

    /// <summary>
    /// Root finds commands in system folders only. The entrypoint's first command sets that PATH,
    /// and the only other PATH it sets is the host's, in the exec's last lines. prepare-volume.sh sets
    /// the same one before its first command, whatever PATH it was called with.
    /// Before their first command both scripts also move root off agent-home: HOME=/root, no
    /// global git config, no Python user site.
    /// </summary>
    [Fact]
    public void Root_runs_on_system_folders_only()
    {
        foreach (var script in new[] { "container-entrypoint.sh", "prepare-volume.sh" })
        {
            var lines = File.ReadAllLines(Path.Combine(RepoRoot(), "scripts", script));
            var commands = lines.Where(line => line.Trim() is { Length: > 0 } text && !text.StartsWith('#')
                && text is not "set -e" and not "set -u" and not "#!/bin/sh").ToList();
            Assert.True(
                commands[0] is "export PATH=" + SystemPath or "PATH=" + SystemPath,
                $"{script}: the first command is '{commands[0]}', not the system-only PATH.");

            var settings = RootEnvironmentBlock(lines);
            foreach (var setting in new[] { "HOME=/root", "GIT_CONFIG_GLOBAL=/dev/null", "PYTHONNOUSERSITE=1" })
            {
                var name = setting.Split('=')[0];
                Assert.True(settings.Contains(setting) || settings.Contains("export " + setting),
                    $"{script}: {setting} is not set before the first command.\n{string.Join('\n', settings)}");
                Assert.True(settings.Contains("export " + setting) || settings.Any(line => line.StartsWith("export ", StringComparison.Ordinal) && line.Split(' ').Contains(name)),
                    $"{script}: {name} is not exported before the first command.");
            }
            Assert.Contains(settings, line => line.StartsWith("unset XDG_CONFIG_HOME ", StringComparison.Ordinal));
        }

        // Apart from the first, each PATH the entrypoint sets is a host's, three lines above that
        // branch's exec, and each branch sets exactly one.
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "container-entrypoint.sh"));
        Assert.Equal(4, text.Split('\n').Count(line => line.TrimStart().StartsWith("export PATH=", StringComparison.Ordinal)));
        foreach (var (name, branch) in Branches(text))
        {
            var lines = branch.Split('\n').Select(line => line.Trim()).ToArray();
            var paths = lines.Select((line, at) => (line, at)).Where(l => l.line.StartsWith("export PATH=", StringComparison.Ordinal)).ToList();
            var exec = Array.FindIndex(lines, line => line.StartsWith("exec /usr/bin/setpriv --reuid=", StringComparison.Ordinal));
            Assert.True(paths.Count == 1, $"{name}: {paths.Count} PATH lines");
            Assert.Equal(exec - 3, paths[0].at);
        }
    }

    /// <summary>
    /// Root reads no user config, as behaviour. An agent-owned HOME holding a malformed .gitconfig and a usercustomize.py
    /// that writes a marker: first shown to bite (git exits 128, python3 imports the file), then the
    /// entrypoint's own root lines - its environment block and its git config --system step - and
    /// prepare-volume.sh's packages step, whose apt-get is a stub that does what a maintainer script
    /// does (python3, git), run with that HOME and neither bites. GIT_CONFIG_SYSTEM is a temp file and
    /// apt-get is never run, so nothing outside the temp root is written.
    /// </summary>
    [Fact]
    public void Root_reads_no_gitconfig_or_python_user_site_from_the_agents_home()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Exec("sh", ["-c", "command -v python3 && command -v git"]).Code != 0,
            "Needs a POSIX shell, python3 and git.");
        var systemBefore = File.Exists("/etc/gitconfig") ? File.ReadAllText("/etc/gitconfig") : null;

        var home = Path.Combine(_root, "agent-home");
        var marker = Path.Combine(_root, "usercustomize-ran");
        var version = Exec("python3", ["-I", "-c", "import sys; print(f'{sys.version_info[0]}.{sys.version_info[1]}')"]).Output.Trim();
        var site = Path.Combine(home, ".local", "lib", "python" + version, "site-packages");
        Directory.CreateDirectory(site);
        File.WriteAllText(Path.Combine(site, "usercustomize.py"),
            $"import os\nopen('{marker}', 'a').write('usercustomize ran uid=%d\\n' % os.getuid())\n");
        File.WriteAllText(Path.Combine(home, ".gitconfig"), "[[[ this is not git config\n");
        // As root, the home is another user's, as agent-home is on the image.
        if (Id("-u") == "0") Assert.Equal(0, Exec("chown", ["-R", "65534:65534", home]).Code);

        var systemConfig = Path.Combine(_root, "gitconfig-system");
        var environment = new Dictionary<string, string> { ["HOME"] = home, ["GIT_CONFIG_SYSTEM"] = systemConfig };

        // The plant works: without the scripts' settings, root's git fails and python3 runs the agent's code.
        var bare = Exec("git", ["config", "--system", "--get-all", "safe.directory"], environment);
        Assert.True(bare.Code == 128, bare.Output);
        Exec("python3", ["-c", "pass"], environment);
        Assert.True(File.Exists(marker), "The planted usercustomize.py was not imported even without the fix.");
        File.Delete(marker);

        // The entrypoint's lines as they are, from the top to the role line, then its git step.
        var entrypoint = File.ReadAllLines(Path.Combine(RepoRoot(), "scripts", "container-entrypoint.sh"));
        var header = entrypoint.TakeWhile(line => !line.StartsWith("role=", StringComparison.Ordinal)).ToArray();
        var gitStart = Array.FindIndex(entrypoint, line => line.StartsWith("if ! git config --system", StringComparison.Ordinal));
        var gitStep = entrypoint[gitStart..(Array.IndexOf(entrypoint, "fi", gitStart) + 1)];
        var section = string.Join('\n', header.Concat(gitStep)) + "\npython3 -c pass\necho root-section-done\n";
        // Safety: the section must not choose its own system config, or the step below would write the real one.
        Assert.DoesNotContain("GIT_CONFIG_SYSTEM", section);

        var root = Exec("/bin/sh", ["-e", "-c", section], environment);
        Assert.True(root.Code == 0 && root.Output.Contains("root-section-done"), root.Output);
        Assert.Matches(@"(?m)^\s*directory = \*$", File.ReadAllText(systemConfig));

        // prepare-volume.sh reaching apt-get: the stub is what a python3-* postinst does.
        var maintainer = Stub("maintainer-stub",
            $"python3 -c pass; git config --system --get-all safe.directory >/dev/null || {{ echo 'git failed' >&2; exit 1; }}");
        File.WriteAllText(Path.Combine(_data, "system-packages"), "python3-yaml\n");
        var packages = RunWithCode("packages", new Dictionary<string, string>
        {
            ["HOME"] = home, ["GIT_CONFIG_SYSTEM"] = systemConfig, ["HARNESS_APT_GET"] = maintainer,
        });

        Assert.False(File.Exists(marker), $"usercustomize.py ran: {(File.Exists(marker) ? File.ReadAllText(marker) : "")}");
        Assert.True(packages.Code == 0, packages.Output);
        Assert.Contains("system packages: installed", packages.Output);
        Assert.Equal(systemBefore, File.Exists("/etc/gitconfig") ? File.ReadAllText("/etc/gitconfig") : null);
    }

    /// <summary>A script run from these tests sees only what the test passes, not the runner's environment.</summary>
    [Fact]
    public void A_script_run_by_these_tests_sees_only_the_environment_it_is_given()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");
        var leak = $"HARNESS_TEST_RUNNER_ONLY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(leak, "x");
        try
        {
            var names = Exec("/usr/bin/env", [], new Dictionary<string, string> { ["GIVEN"] = "y" }).Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('=')[0]).Order().ToArray();
            Assert.Equal(["GIVEN", "PATH"], names);
        }
        finally
        {
            Environment.SetEnvironmentVariable(leak, null);
        }
    }

    /// <summary>The lines before a script's first command: its settings, backslash continuations joined.</summary>
    private static List<string> RootEnvironmentBlock(string[] lines)
    {
        var joined = string.Join('\n', lines).Replace("\\\n", " ", StringComparison.Ordinal).Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && line is not "set -e" and not "set -u");
        return joined.TakeWhile(line => System.Text.RegularExpressions.Regex.IsMatch(line,
            @"^(export \w+(=\S*)?( \w+(=\S*)?)*|unset [\w ]+|\w+=\S*)$")).ToList();
    }

    /// <summary>
    /// System folders first, as behaviour: find, chown, chmod and the rest planted in an agent-writable folder at the
    /// FRONT of the caller's PATH do not run - prepare-volume.sh resets PATH before anything.
    /// </summary>
    [Fact]
    public void A_command_planted_on_the_callers_path_is_not_run()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");
        SeedExistingVolume();
        var planted = Path.Combine(_data, "bin");
        var marker = Path.Combine(_root, "planted-ran");
        foreach (var name in new[] { "find", "chown", "chmod", "stat", "sed", "grep", "wc", "basename", "tr", "env", "realpath", "apt-get", "git" })
        {
            var path = Path.Combine(planted, name);
            File.WriteAllText(path, $"#!/bin/sh\necho {name} >> '{marker}'\n");
            File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("755", 8));
        }
        File.WriteAllText(Path.Combine(_data, "system-packages"), "jq\n");

        // As root, chown by bare name like the image does, so its lookup is on trial too; apt-get
        // stays the recorder, so nothing is installed on this machine.
        var environment = new Dictionary<string, string>
        {
            ["PATH"] = planted + ":" + Environment.GetEnvironmentVariable("PATH"),
        };
        if (Id("-u") == "0") environment["HARNESS_CHOWN"] = "chown";

        var output = Run("ownership", environment);
        Run("packages", environment);

        Assert.False(File.Exists(marker), $"Ran from the planted folder: {(File.Exists(marker) ? File.ReadAllText(marker) : "")} {output}");
        Assert.Contains("change(s)", output);
    }

    /// <summary>
    /// /tmp stays 1777: prompt, system-prompt and usage files go there from both users. The only line
    /// in either script that changes it is the entrypoint's own chmod 1777, and the ownership step
    /// refuses it as a file-browser root.
    /// </summary>
    [Fact]
    public void Nothing_but_chmod_1777_touches_tmp()
    {
        var entrypoint = File.ReadAllLines(Path.Combine(RepoRoot(), "scripts", "container-entrypoint.sh"));
        var prepare = File.ReadAllLines(Path.Combine(RepoRoot(), "scripts", "prepare-volume.sh"));
        var touching = new System.Text.RegularExpressions.Regex(@"^(?!\s*#).*\b(chmod|chown|chgrp|rm|mv|mount)\b.*\s/tmp(\s|/|$)");

        Assert.Equal(["chmod 1777 /tmp"], entrypoint.Where(line => touching.IsMatch(line)));
        Assert.DoesNotContain(prepare, line => touching.IsMatch(line));
        Assert.Contains(prepare, line => line.Contains("|/tmp|", StringComparison.Ordinal));
    }

    /// <summary>
    /// The host keeps SETUID, SETGID and KILL and nothing else - not in its bounding set either - and
    /// a child it starts with the switch AgentLaunchUser prefixes ends with no capability at all. Run
    /// for real when this suite runs as root: the entrypoint's own exec line, with <c>harness</c>
    /// replaced by uid 65534 and the host by a shell that reads its capabilities. Every branch's exec
    /// line is today's, for its own user.
    /// </summary>
    [Fact]
    public void The_host_keeps_only_setuid_setgid_and_kill_and_its_agent_children_keep_nothing()
    {
        var branches = Branches(File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "container-entrypoint.sh")));
        Assert.Equal(HostExec("harness"), ExecLine(branches["all"]));
        Assert.Equal(HostExec("harness"), ExecLine(branches["control"]));
        Assert.Equal(HostExec("worker"), ExecLine(branches["worker"]));

        AssertThreeCapsForReal(ExecLine(branches["all"]), "harness");
    }

    /// <summary>The worker branch's exec line, run for real as root: the same three capabilities, and none for its agent children.</summary>
    [Fact]
    public void The_worker_exec_line_keeps_only_setuid_setgid_and_kill()
    {
        var line = ExecLine(Branches(File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "container-entrypoint.sh")))["worker"]);
        Assert.Equal(HostExec("worker"), line);

        AssertThreeCapsForReal(line, "worker");
    }

    /// <summary>
    /// Control starts no agent CLI, but runs git and gh as the agent (Fetch, Merge, Push, a team's
    /// local repository), so its host keeps exactly the three capabilities and setpriv is in its image.
    /// </summary>
    [Fact]
    public void The_control_entrypoint_keeps_setuid_setgid_and_kill_for_git_as_the_agent()
    {
        var control = Branches(File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "container-entrypoint.sh")))["control"];

        Assert.Equal(HostExec("harness"), ExecLine(control));
        var stages = ContainerImageTests.Stages();
        Assert.Equal("base", stages["control"].From);
        Assert.Contains(stages["base"].Instructions, line => line.Contains("test -x /usr/bin/setpriv", StringComparison.Ordinal));
    }

    private static string[] HostExec(string user) =>
        ["exec", "/usr/bin/setpriv", $"--reuid={user}", $"--regid={user}", "--init-groups",
         "--inh-caps=-all,+setuid,+setgid,+kill", "--ambient-caps=-all,+setuid,+setgid,+kill",
         "--bounding-set=-all,+setuid,+setgid,+kill", "--", "/usr/bin/dotnet", "/app/Harness.Host.dll"];

    /// <summary>A branch's exec of the host, continuation lines joined, split into words.</summary>
    private static string[] ExecLine(string branch)
    {
        var at = System.Text.RegularExpressions.Regex.Match(branch, @"exec /usr/bin/setpriv --reuid=(harness|worker) ").Index;
        Assert.True(at > 0, branch);
        return branch[at..].Replace("\\\n", " ", StringComparison.Ordinal).Split('\n')[0]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static void AssertThreeCapsForReal(string[] arguments, string user)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Id("-u") != "0" || Exec("sh", ["-c", "command -v setpriv"]).Code != 0,
            "Needs root and setpriv to switch users for real.");

        var host = arguments[1..^2].Select(a => a.Replace(user, "65534", StringComparison.Ordinal)).ToArray();
        const string Child = "setpriv --reuid=65533 --regid=65533 --clear-groups --inh-caps=-all --ambient-caps=-all -- grep ^Cap /proc/self/status";
        var result = Exec(host[0], [.. host[1..], "sh", "-c", "grep ^Cap /proc/self/status; echo child; " + Child]);
        Assert.True(result.Code == 0, result.Output);

        var (parent, child) = (result.Output.Split("child")[0], result.Output.Split("child")[1]);
        // CAP_KILL is bit 5, CAP_SETGID 6, CAP_SETUID 7.
        const string ThreeCaps = "00000000000000e0";
        foreach (var set in new[] { "CapEff", "CapPrm", "CapAmb", "CapBnd" }) Assert.Contains($"{set}:\t{ThreeCaps}", parent);
        foreach (var set in new[] { "CapInh", "CapPrm", "CapEff", "CapAmb" }) Assert.Contains($"{set}:\t0000000000000000", child);
    }

    [Fact]
    public void The_image_carries_both_users_at_fixed_ids_and_the_tools_to_switch()
    {
        var containerfile = File.ReadAllText(Path.Combine(RepoRoot(), "Containerfile"));
        var worker = ContainerImageTests.Stages()["worker"];

        Assert.Contains("groupadd --gid 10001 harness", containerfile);
        Assert.Contains("groupadd --gid 10002 agent", containerfile);
        Assert.Matches("useradd --uid 10001 --gid harness --groups agent ", containerfile);
        Assert.Matches("useradd --uid 10002 --gid agent ", containerfile);
        Assert.Contains("util-linux", containerfile);
        Assert.Contains("command -v setpriv && command -v runuser", containerfile);
        Assert.Contains("scripts/prepare-volume.sh", containerfile);

        // An agent's `npm -g` and the tools it installs for itself land on the volume, on the worker's PATH.
        Assert.Equal("/data/npm-global", ContainerImageTests.Env(worker, "NPM_CONFIG_PREFIX"));
        AssertSystemFoldersFirst(ContainerImageTests.Env(worker, "PATH")!.Split(':'));
        Assert.Contains("test -x /usr/bin/setpriv && test -x /usr/bin/dotnet", containerfile);
    }

    /// <summary>
    /// A tarball unpacked as root keeps its own owner unless told otherwise, which would leave
    /// /usr/local/bin, node and npm - first on every PATH - to uid 1001, a user the image does not
    /// have. Every tarball unpacked into a system folder is root's.
    /// </summary>
    [Fact]
    public void Every_tarball_the_image_unpacks_is_owned_by_root()
    {
        var containerfile = File.ReadAllText(Path.Combine(RepoRoot(), "Containerfile"));
        var extractions = System.Text.RegularExpressions.Regex.Matches(containerfile, @"\btar\s[^\n\\]*?-[A-Za-z]*x[A-Za-z]*\b[^\n\\]*")
            .Select(m => m.Value).ToList();

        Assert.NotEmpty(extractions);
        Assert.All(extractions, line => Assert.Contains("--no-same-owner", line));
    }

    private const string SystemPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    private static readonly string[] AgentFolders =
    [
        "/data/agent-home/.grok/bin", "/data/npm-global/bin", "/data/bin", "/data/go/bin",
        "/data/agent-home/.local/bin", "/data/agent-home/.dotnet/tools",
    ];

    /// <summary>Every system folder comes before every agent-writable one, and nothing else is on it.</summary>
    private static void AssertSystemFoldersFirst(string[] path)
    {
        var system = SystemPath.Split(':');
        Assert.Equal(system, path[..system.Length]);
        foreach (var folder in AgentFolders) Assert.Contains(folder, path);
        Assert.All(path[system.Length..], entry => Assert.True(
            entry == "/usr/local/go/bin" || AgentFolders.Contains(entry), $"Unexpected PATH entry {entry}"));
        Assert.DoesNotContain(path, entry => entry.Contains('$'));
    }

    // ---- Control and workers ----

    /// <summary>
    /// Every entry the Host keeps for itself goes to harness alone, 700 or 600 all the way down, and
    /// none to group agent - the doctor's records and the credential exchange included, which an agent
    /// that could read or write them would turn into a credential or a lying doctor. Run as a non-root
    /// user the recorder only proves the host side: what is handed to harness. What agent and worker
    /// then cannot read is the root-only test below.
    /// </summary>
    [Fact]
    public void Every_host_entry_is_left_to_harness_alone_and_none_to_group_agent()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");
        SeedExistingVolume();

        var output = Run("ownership");

        var chowned = ChownedPaths();
        foreach (var file in new[]
                 {
                     "agent-credentials/request-1.json", "agent-auth.json", "agent-auth.json.tmp", "agent-tools.json",
                     "agent-launch-checks.json", "wip.json", "workers.json", "workers.json.tmp",
                 })
        {
            Assert.Equal(HostOwner, chowned.GetValueOrDefault(Path.Combine(_data, file)) ?? Owner(file));
            Assert.True(Mode(file) == "600", $"{file} is {Mode(file)}. {output}");
        }

        Assert.Equal(HostOwner, chowned.GetValueOrDefault(Path.Combine(_data, "agent-credentials")) ?? Owner("agent-credentials"));
        Assert.Equal("700", Mode("agent-credentials"));
        Assert.DoesNotContain(chowned, entry => entry.Key.Contains("/agent-credentials", StringComparison.Ordinal) && entry.Value != HostOwner);
    }

    /// <summary>
    /// The worker key is the first thing the entrypoint deals with: straight after the root environment
    /// block, before any other command, it is written to a file and unset.
    /// </summary>
    [Fact]
    public void The_worker_key_leaves_the_environment_before_anything_else_runs()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), "scripts", "container-entrypoint.sh"));
        var commands = lines.Select(line => line.Trim())
            .Where(text => text.Length > 0 && !text.StartsWith('#') && text is not "set -e" and not "#!/bin/sh").ToList();
        var settings = RootEnvironmentBlock(lines).Count;

        Assert.Equal(@"if [ -n ""${HARNESS_WORKER_KEY:-}"" ]; then", commands[settings]);
        var block = KeyBlock(lines).Where(line => !line.TrimStart().StartsWith('#')).ToArray();
        Assert.Equal("unset HARNESS_WORKER_KEY", block[^1]);
        Assert.Equal(commands[settings + block.Length - 1], block[^1]);
        Assert.StartsWith("role=", commands[settings + block.Length]);
        Assert.Contains(block, line => line.Trim() == @"export HARNESS_WORKER_KEY_FILE=""$key_dir/worker-key""");
    }

    /// <summary>
    /// The key block, run by sh with its folder and chown pointed at this test: the key lands in a file
    /// only the container's Host user may read, and what runs after the block has the file's name and
    /// not the key.
    /// </summary>
    [Fact]
    public void The_worker_key_file_is_the_host_users_alone()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");
        var block = string.Join('\n', KeyBlock(File.ReadAllLines(Path.Combine(RepoRoot(), "scripts", "container-entrypoint.sh"))));
        const string Key = "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0";

        foreach (var (role, user) in new[] { ("all", "harness"), ("control", "harness"), ("worker", "worker"), (" Worker ", "worker") })
        {
            var folder = Path.Combine(_root, $"run-{role.Trim()}-{Guid.NewGuid():N}", "harness");
            File.Delete(_chownLog);
            var (code, output) = Exec("/bin/sh", ["-e", "-c", block + "\nenv"], new Dictionary<string, string>
            {
                ["HARNESS_WORKER_KEY"] = Key, ["HARNESS_ROLE"] = role,
                ["HARNESS_WORKER_KEY_DIR"] = folder, ["HARNESS_CHOWN"] = _chownStub,
            });
            Assert.True(code == 0, output);

            var file = Path.Combine(folder, "worker-key");
            Assert.Equal("700", Exec("stat", ["-c", "%a", folder]).Output.Trim());
            Assert.Equal("400", Exec("stat", ["-c", "%a", file]).Output.Trim());
            Assert.Equal(Key + "\n", File.ReadAllText(file));
            Assert.Equal([$"{user}:{user} {folder} {file}"], File.ReadAllLines(_chownLog));

            var environment = output.Split('\n');
            Assert.DoesNotContain(environment, line => line.StartsWith("HARNESS_WORKER_KEY=", StringComparison.Ordinal));
            Assert.Contains($"HARNESS_WORKER_KEY_FILE={file}", environment);
            Assert.DoesNotContain(Key, output);
        }

        // No key, or an empty one: no file, and no name of one.
        foreach (var variables in new[] { new Dictionary<string, string>(), new Dictionary<string, string> { ["HARNESS_WORKER_KEY"] = "" } })
        {
            var folder = Path.Combine(_root, $"run-none-{Guid.NewGuid():N}", "harness");
            variables["HARNESS_WORKER_KEY_DIR"] = folder;
            variables["HARNESS_CHOWN"] = _chownStub;
            var (code, output) = Exec("/bin/sh", ["-e", "-c", block + "\nenv"], variables);
            Assert.True(code == 0, output);
            Assert.False(Directory.Exists(folder));
            Assert.DoesNotContain(output.Split('\n'), line => line.StartsWith("HARNESS_WORKER_KEY=", StringComparison.Ordinal)
                || line.StartsWith("HARNESS_WORKER_KEY_FILE=", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A worker changes no ownership: it waits until control has prepared the volume (the data root
    /// host:agent 0750, tmp and agent-home there), and after the limit starts anyway with a warning.
    /// </summary>
    [Fact]
    public void The_worker_waits_for_a_prepared_volume_then_starts()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");
        var worker = Branches(File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "container-entrypoint.sh")))["worker"];
        var lines = worker.Split('\n');
        var start = Array.FindIndex(lines, line => line.TrimStart().StartsWith("wait_root=", StringComparison.Ordinal));
        var end = Array.FindIndex(lines, start, line => line.Trim() == "done");
        var wait = string.Join('\n', lines[start..(end + 1)]);

        Dictionary<string, string> Volume(string root, int limit) => new()
        {
            ["HARNESS_DATA_ROOT"] = root, ["HARNESS_VOLUME_WAIT_SECONDS"] = "1", ["HARNESS_VOLUME_WAIT_LIMIT"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["HARNESS_HOST_UID"] = Id("-u"), ["HARNESS_AGENT_GID"] = Id("-g"),
        };
        const string Waiting = "worker: waiting for control to prepare the volume";
        const string Anyway = "starting anyway";

        // Prepared: it goes straight on.
        var prepared = Path.Combine(_root, "prepared");
        Directory.CreateDirectory(Path.Combine(prepared, "tmp"));
        Directory.CreateDirectory(Path.Combine(prepared, "agent-home"));
        File.SetUnixFileMode(prepared, (UnixFileMode)Convert.ToInt32("750", 8));
        var straight = Exec("/bin/sh", ["-e", "-c", wait + "\necho started"], Volume(prepared, 30));
        Assert.Equal((0, "started"), (straight.Code, straight.Output.Trim()));

        // Never prepared: it waits to the limit, says so, and starts.
        var bare = Path.Combine(_root, "bare");
        Directory.CreateDirectory(bare);
        File.SetUnixFileMode(bare, (UnixFileMode)Convert.ToInt32("755", 8));
        var never = Exec("/bin/sh", ["-e", "-c", wait + "\necho started"], Volume(bare, 2));
        Assert.True(never.Code == 0, never.Output);
        Assert.Equal(2, never.Output.Split('\n').Count(line => line == Waiting));
        Assert.Contains("worker: the volume is still not prepared by control after 2 s; starting anyway", never.Output);
        Assert.EndsWith("started", never.Output.Trim());

        // Prepared while it waits: it goes on then, with no warning. The mode alone is not enough.
        var later = Path.Combine(_root, "later");
        Directory.CreateDirectory(later);
        File.SetUnixFileMode(later, (UnixFileMode)Convert.ToInt32("750", 8));
        var prepare = $"( sleep 2; mkdir -p '{later}/tmp' '{later}/agent-home' ) &\n";
        var waited = Exec("/bin/sh", ["-e", "-c", prepare + wait + "\nwait\necho started"], Volume(later, 30));
        Assert.True(waited.Code == 0, waited.Output);
        Assert.Contains(Waiting, waited.Output);
        Assert.DoesNotContain(Anyway, waited.Output);
        Assert.EndsWith("started", waited.Output.Trim());
    }

    /// <summary>
    /// A worker's start installs only the CLIs that are missing, one worker at a time: the CLIs are on
    /// the shared volume, and another worker may be running members from them.
    /// </summary>
    [Fact]
    public void The_worker_installs_missing_clis_only_under_the_volume_lock()
    {
        var worker = Branches(File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "container-entrypoint.sh")))["worker"]
            .Replace("\\\n", " ", StringComparison.Ordinal);
        var step = Assert.Single(worker.Split('\n'), line => line.Contains("ensure-agent-clis.sh", StringComparison.Ordinal));
        step = System.Text.RegularExpressions.Regex.Replace(step, @"\s+", " ");

        Assert.Contains("HARNESS_UPDATE_AGENTS=0", step);
        Assert.Matches(@"exec /usr/bin/flock /data/tmp/\.agent-cli-install\.lock /usr/bin/setpriv --reuid=agent --regid=agent --init-groups --inh-caps=-all --ambient-caps=-all -- /bin/sh /opt/harness/ensure-agent-clis\.sh \)", step);
        Assert.Contains("|| echo \"agent cli: setup hit an error; starting the host anyway\"", step);
    }

    /// <summary>The worker's own state - its run process groups, health and drain files - is in the container and the worker user's alone.</summary>
    [Fact]
    public void The_worker_state_folder_is_the_worker_users_alone()
    {
        var worker = Branches(File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "container-entrypoint.sh")))["worker"];
        var lines = worker.Split('\n').Select(line => line.Trim()).ToList();

        var made = lines.IndexOf("mkdir -p /var/lib/harness-worker");
        Assert.True(made > 0, worker);
        Assert.Equal("chown worker:worker /var/lib/harness-worker", lines[made + 1]);
        Assert.Equal("chmod 0700 /var/lib/harness-worker", lines[made + 2]);
        Assert.True(made < lines.FindIndex(line => line.StartsWith("exec /usr/bin/setpriv --reuid=worker", StringComparison.Ordinal)));
        Assert.Equal("/var/lib/harness-worker", ContainerImageTests.Env(ContainerImageTests.Stages()["worker"], "HARNESS_WORKER_STATE_DIR"));
    }

    /// <summary>The control image runs control and nothing else, however the role is spelled.</summary>
    [Fact]
    public void The_control_image_refuses_any_role_but_control()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), "scripts", "container-entrypoint.sh"));
        var start = Array.FindIndex(lines, line => line.StartsWith(@"if [ ""${HARNESS_IMAGE:-}"" = control ]", StringComparison.Ordinal));
        var check = RoleLine(lines) + "\n" + string.Join('\n', lines[start..(Array.IndexOf(lines, "fi", start) + 1)]) + "\necho runs";
        const string Refusal = "This is the control image; it has no agent CLI, so it runs only as control (HARNESS_ROLE=control).";

        foreach (var (image, role, runs) in new (string?, string?, bool)[]
                 {
                     ("control", "control", true), ("control", " Control ", true), ("control", "CONTROL", true),
                     ("control", "worker", false), ("control", "Worker", false), ("control", "all", false),
                     ("control", "", false), ("control", null, false), ("control", "controller", false),
                     ("worker", "worker", true), ("worker", "all", true), ("worker", null, true), (null, null, true),
                 })
        {
            var variables = new Dictionary<string, string>();
            if (image is not null) variables["HARNESS_IMAGE"] = image;
            if (role is not null) variables["HARNESS_ROLE"] = role;
            var (code, output) = Exec("/bin/sh", ["-e", "-c", check], variables);
            Assert.True(runs ? (code, output.Trim()) == (0, "runs") : code == 2 && output.Trim() == Refusal,
                $"image {image ?? "(none)"} role '{role ?? "(unset)"}': exit {code}, {output}");
        }
    }

    /// <summary>
    /// A worker shares the volume with control and every other worker: it creates nothing on it as root
    /// and changes no ownership there, which control's own pass does.
    /// </summary>
    [Fact]
    public void The_worker_entrypoint_changes_no_ownership_and_runs_no_mkdir_on_the_volume()
    {
        var worker = Branches(File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "container-entrypoint.sh")))["worker"];
        var touching = new System.Text.RegularExpressions.Regex(@"^(?!\s*#).*\b(mkdir|chown|chgrp|chmod|touch|rm|mv)\b[^\n]*\s/data(\s|/|$)");

        Assert.DoesNotContain("prepare_volume", worker);
        Assert.DoesNotContain("prepare-volume.sh ownership", worker);
        Assert.DoesNotContain(worker.Split('\n'), line => touching.IsMatch(line));
        // The function that does is called by control and `all` only.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "container-entrypoint.sh")), @"(?m)^\s+prepare_volume$"));
    }

    /// <summary>The worker's host runs as `worker`, keeping setuid, setgid and kill to start and stop agents, with umask 0007.</summary>
    [Fact]
    public void The_worker_entrypoint_drops_to_the_worker_user_keeping_setuid_setgid_and_kill()
    {
        var worker = Branches(File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "container-entrypoint.sh")))["worker"];

        Assert.Equal(HostExec("worker"), ExecLine(worker));
        Assert.Matches(@"(?m)^\s*umask 0007\n\s*exec /usr/bin/setpriv --reuid=worker", worker);
        Assert.DoesNotContain("--reuid=harness", worker);
    }

    /// <summary>
    /// What the ownership map means, for real: after a real pass over a seeded volume, the worker user
    /// (in group agent) and the agent user cannot read the database, the key ring or any record the Host
    /// keeps for itself - and both can still write where agents work, and the worker can read plugins,
    /// so the refusals come from the map and not from a path they cannot reach.
    /// </summary>
    [Fact]
    public void As_the_worker_and_as_the_agent_the_database_and_key_ring_cannot_be_read()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell script.");
        RootOrSkip();
        Assert.SkipWhen(Exec("sh", ["-c", "command -v setpriv"]).Code != 0, "Needs setpriv to switch users.");
        SeedExistingVolume();
        Directory.CreateDirectory(Path.Combine(_data, "plugins", "sample", "0.1.0"));
        File.WriteAllText(Path.Combine(_data, "plugins", "sample", "0.1.0", "plugin.json"), "{}");
        // Every folder above the data root can be passed through, so a refusal below is the map's.
        File.SetUnixFileMode(_root, (UnixFileMode)Convert.ToInt32("755", 8));

        var output = Run("ownership", new Dictionary<string, string> { ["HARNESS_CHOWN"] = "chown" });
        Assert.Equal("10001:10002", Owner("."));

        foreach (var uid in new[] { "10003", "10002" })
        {
            (int Code, string Output) As(string command) =>
                Exec("setpriv", [$"--reuid={uid}", $"--regid={uid}", "--groups=10002", "--", "/bin/sh", "-c", command]);

            foreach (var refused in new[]
                     {
                         "cat messages.db", "ls keys", "cat keys/key-1.xml", "cat agent-auth.json", "cat agent-tools.json",
                         "ls agent-credentials", "cat agent-credentials/request-1.json", "cat workers.json", "cat wip.json",
                     })
            {
                var (code, text) = As($"cd '{_data}' && {refused}");
                Assert.True(code != 0 && text.Contains("Permission denied", StringComparison.Ordinal),
                    $"uid {uid}: `{refused}` exited {code}: {text}\n{output}");
            }

            foreach (var allowed in new[] { $"touch tmp/probe-{uid}", $"touch teams/alpha/workspaces/probe-{uid}", "cat plugins/sample/0.1.0/plugin.json" })
            {
                var (code, text) = As($"cd '{_data}' && {allowed}");
                Assert.True(code == 0, $"uid {uid}: `{allowed}` exited {code}: {text}");
            }
        }
    }

    /// <summary>The entrypoint's role branches: <c>control</c>, <c>worker</c>, and <c>all</c> for the <c>*</c> branch.</summary>
    private static Dictionary<string, string> Branches(string entrypoint)
    {
        var branches = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     entrypoint.Replace("\r\n", "\n", StringComparison.Ordinal), @"(?ms)^  (?<name>control|worker|\*)\)\n(?<body>.*?)^    ;;$"))
        {
            branches.Add(match.Groups["name"].Value == "*" ? "all" : match.Groups["name"].Value, match.Groups["body"].Value);
        }

        Assert.Equal(3, branches.Count);
        return branches;
    }

    /// <summary>The one line that reads the role.</summary>
    private static string RoleLine(string entrypoint) => RoleLine(entrypoint.Split('\n'));

    private static string RoleLine(string[] lines) => Assert.Single(lines, line => line.StartsWith("role=", StringComparison.Ordinal));

    /// <summary>The worker key's block, from its <c>if</c> to the <c>unset</c> after it.</summary>
    private static string[] KeyBlock(string[] lines)
    {
        var start = Array.FindIndex(lines, line => line.StartsWith(@"if [ -n ""${HARNESS_WORKER_KEY:-}"" ]; then", StringComparison.Ordinal));
        var end = Array.IndexOf(lines, "unset HARNESS_WORKER_KEY", start);
        Assert.True(start >= 0 && end > start, string.Join('\n', lines));
        return lines[start..(end + 1)];
    }

    /// <summary>The function control and `all` prepare the volume with.</summary>
    private static string PrepareVolumeFunction(string entrypoint)
    {
        var start = entrypoint.IndexOf("prepare_volume() {", StringComparison.Ordinal);
        Assert.True(start >= 0, entrypoint);
        return entrypoint[start..entrypoint.IndexOf("\n}\n", start, StringComparison.Ordinal)];
    }

    // ---- The volume ----

    /// <summary>An existing volume holding every kind of file the ownership map sorts, as far as this
    /// process can make it.</summary>
    private void SeedExistingVolume()
    {
        foreach (var file in new[]
                 {
                     "messages.db", "messages.db-wal", "messages.db-shm", "keys/key-1.xml", "agents.json",
                     "agents.json.before-builtins", "agent-launch.json", "agent-launch.json.tmp", "logs/host.log",
                     "backups/old.db", "host.lock", "system-packages", "teams/alpha/workspaces/Ines/notes.md", "documents/spec.md", "agent-home/.claude/.credentials.json",
                     "agent-home/.ssh/id_ed25519", "agent-home/.gitconfig", "agent-home/.grok/config.toml",
                     "agent-home/.grok/bin/grok", "npm-global/bin/claude", "bin/tool",
                     "steering/user.txt", "cli-versions.jsonl", "tenant-interactive-agent-workspaces/u/AGENTS.md",
                     "connections/.connect-report.json",
                     "agent-credentials/request-1.json", "agent-auth.json", "agent-auth.json.tmp", "agent-tools.json",
                     "agent-launch-checks.json", "wip.json", "workers.json", "workers.json.tmp",
                 })
        {
            var path = Path.Combine(_data, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
            File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("644", 8));
        }

        Directory.CreateDirectory(Path.Combine(_data, "go"));
        Directory.CreateDirectory(Path.Combine(_data, "ms-playwright"));
        File.SetUnixFileMode(Path.Combine(_data, "npm-global/bin/claude"), (UnixFileMode)Convert.ToInt32("755", 8));
        File.SetUnixFileMode(Path.Combine(_data, "agent-home/.claude/.credentials.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(Path.Combine(_data, "agent-home/.ssh/id_ed25519"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(Path.Combine(_data, "agent-home/.ssh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(Path.Combine(_data, "agent-home/.gitconfig"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(Path.Combine(_data, "agent-home/.claude"), (UnixFileMode)Convert.ToInt32("700", 8));
        // What grok's installer may leave: owner-only, which the host could not rewrite.
        File.SetUnixFileMode(Path.Combine(_data, "agent-home/.grok"), (UnixFileMode)Convert.ToInt32("700", 8));
        File.SetUnixFileMode(Path.Combine(_data, "agent-home/.grok/config.toml"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(Path.Combine(_data, "agent-home/.grok/bin"), (UnixFileMode)Convert.ToInt32("700", 8));
        File.SetUnixFileMode(Path.Combine(_data, "agent-home/.grok/bin/grok"), (UnixFileMode)Convert.ToInt32("700", 8));
        // A directory an old start left setgid: an octal chmod would keep the bit and count it as a
        // change on every start.
        File.SetUnixFileMode(Path.Combine(_data, "keys"), (UnixFileMode)Convert.ToInt32("2755", 8));
        File.CreateSymbolicLink(Path.Combine(_data, "bin/claude-link"), Path.Combine(_data, "npm-global/bin/claude"));
    }

    private Dictionary<string, string> ChownedPaths()
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in File.ReadAllLines(_chownLog))
        {
            // "-h 10002:10002 /a /b ..." from find, or "10001:10002 /data" for the root.
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var at = parts[0] == "-h" ? 1 : 0;
            foreach (var path in parts[(at + 1)..]) owners[path] = parts[at];
        }

        return owners;
    }

    private string Mode(string relative) => Stat("%a", relative);

    private string Owner(string relative) => Stat("%u:%g", relative);

    private string Stat(string format, string relative) =>
        Exec("stat", ["-c", format, Path.Combine(_data, relative)]).Output.Trim();

    private static string Id(string flag) => Exec("id", [flag]).Output.Trim();

    /// <summary>
    /// The script hands over only what is not already its owner's. Files seeded as root are
    /// nobody's here, as on a fresh volume; seeded as another user they may already be agent's or
    /// harness's (as a team's own run, uid 10002, they are), and nothing is handed over to record.
    /// </summary>
    private static void RootOrSkip() =>
        Assert.SkipUnless(Id("-u") == "0",
            $"Needs root: the seeded volume must be root's, and this process is uid {Id("-u")}.");

    // ---- Running the script ----

    private string Run(string command, Dictionary<string, string>? environment = null)
    {
        var (code, output) = RunWithCode(command, environment ?? []);
        Assert.True(code == 0, output);
        return output;
    }

    private (int Code, string Output) RunWithCode(string command, Dictionary<string, string> environment)
    {
        var variables = new Dictionary<string, string>
        {
            ["HARNESS_DATA_ROOT"] = _data,
            ["HARNESS_CHOWN"] = _chownStub,
            ["HARNESS_APT_GET"] = _aptStub,
        };
        foreach (var (key, value) in environment) variables[key] = value;

        var result = Exec("/bin/sh", [Path.Combine(RepoRoot(), "scripts", "prepare-volume.sh"), command], variables);
        return (result.Code, result.Output);
    }

    private string Stub(string name, string body)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("755", 8));
        return path;
    }

    private static (int Code, string Output) Exec(string file, string[] arguments, Dictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Nothing from the runner's environment. A FileBrowser__Roots__* set where this suite
        // runs as root would otherwise reach the real chown; the script reads only what is passed here.
        start.Environment.Clear();
        start.Environment["PATH"] = SystemPath;
        foreach (var (key, value) in environment ?? []) start.Environment[key] = value;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("No Harness.slnx above the test output.");
    }
}
