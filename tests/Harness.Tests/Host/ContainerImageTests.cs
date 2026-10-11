using System.Text.RegularExpressions;

namespace Harness.Tests.Host;

/// <summary>
/// The two images one <c>Containerfile</c> builds, read from the file: <c>control</c>, which must
/// carry no Node and no agent CLI, and <c>worker</c>, which carries the toolchain. No image is built
/// here; the file is parsed into its stages and each target's <c>FROM</c> chain is followed, so a
/// tool that reaches control through a stage it derives from is found too. What only a built image
/// shows - its size, a command missing at run time - is the person's live check.
/// </summary>
public sealed class ContainerImageTests
{
    private static readonly string[] AgentTokens =
    [
        "node", "nodejs", "npm", "npx", "corepack", "nodejs.org", "playwright", "ensure-agent-clis", "npm-torn-install",
        "claude", "codex", "copilot", "grok", "agy",
    ];

    [Fact]
    public void The_control_image_has_no_node_and_no_agent_cli()
    {
        var stages = Stages();

        // The parse found what it must, or "nothing found" below would prove nothing.
        foreach (var name in new[] { "web", "build", "base", "control", "worker" }) Assert.Contains(name, stages.Keys);
        var control = Chain(stages, "control");
        Assert.Equal(["control", "base"], control.Select(s => s.Name));
        Assert.StartsWith("mcr.microsoft.com/dotnet/aspnet:", control[^1].From);
        Assert.Contains(control[0].Instructions, line => line.StartsWith("COPY --from=build /out ", StringComparison.Ordinal));

        Assert.Empty(Found(control, AgentTokens));

        // The same scan over the worker finds what the worker carries: the scan works.
        var worker = Found(Chain(stages, "worker"), AgentTokens);
        foreach (var token in new[] { "node", "ensure-agent-clis", "npm-torn-install" }) Assert.Contains(token, worker);

        // A whole folder copied in would carry the install scripts past the token scan.
        Assert.DoesNotContain(control.SelectMany(s => s.Instructions),
            line => Regex.IsMatch(line, @"^COPY\s+(--\S+\s+)*scripts/?(\s|$)"));
        Assert.Equal(
            ["/opt/harness/container-entrypoint.sh", "/opt/harness/prepare-volume.sh"],
            Copied(control[0]).Order(StringComparer.Ordinal));

        // Control runs no tool an agent installs: its PATH is the system folders alone.
        var path = Env(control[0], "PATH")!.Split(':');
        Assert.Equal("/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin".Split(':'), path);
    }

    [Fact]
    public void The_worker_image_carries_todays_toolchain_and_scripts()
    {
        var worker = Stages()["worker"];
        var text = string.Join('\n', worker.Instructions);

        foreach (var tool in new[] { "build-essential", "python3", "ripgrep", "fd-find", "nodejs.org/dist/v22", "corepack enable", "go.dev/dl/go", "dotnet-install.sh", "astral.sh/uv", "playwright install-deps chromium" })
        {
            Assert.Contains(tool, text);
        }

        Assert.Equal(
            ["/opt/harness/container-entrypoint.sh", "/opt/harness/ensure-agent-clis.sh", "/opt/harness/npm-torn-install.sh", "/opt/harness/prepare-volume.sh"],
            Copied(worker).Order(StringComparer.Ordinal));
        Assert.Equal("/data/npm-global", Env(worker, "NPM_CONFIG_PREFIX"));
        Assert.Equal("1", Env(worker, "IS_SANDBOX"));
        Assert.Contains("/data/npm-global/bin", Env(worker, "PATH")!.Split(':'));
        // A worker dials out to control and listens on nothing.
        Assert.Null(Env(worker, "ASPNETCORE_URLS"));
        Assert.DoesNotContain(worker.Instructions, line => line.StartsWith("EXPOSE", StringComparison.Ordinal));
    }

    [Fact]
    public void Both_images_run_the_one_host_from_the_one_build_stage()
    {
        var stages = Stages();
        foreach (var target in new[] { "control", "worker" })
        {
            var chain = Chain(stages, target);
            Assert.Single(chain.SelectMany(s => s.Instructions), line => line.StartsWith("COPY --from=", StringComparison.Ordinal));
            Assert.Contains(chain[0].Instructions, line => line == "COPY --from=build /out .");
            Assert.Contains(chain.SelectMany(s => s.Instructions), line => line == "WORKDIR /app");
            Assert.Contains(chain.SelectMany(s => s.Instructions), line => line == @"ENTRYPOINT [""/entrypoint.sh""]");
        }

        Assert.Contains(stages["build"].Instructions, line => line.StartsWith("RUN dotnet publish src/Harness.Host/Harness.Host.csproj ", StringComparison.Ordinal));
    }

    [Fact]
    public void Each_image_declares_its_role()
    {
        var stages = Stages();

        Assert.Equal("control", Env(stages["control"], "HARNESS_IMAGE"));
        Assert.Equal("control", Env(stages["control"], "HARNESS_ROLE"));
        Assert.Equal("http://0.0.0.0:8080", Env(stages["control"], "ASPNETCORE_URLS"));
        Assert.Contains(stages["control"].Instructions, line => line == "EXPOSE 8080");

        Assert.Equal("worker", Env(stages["worker"], "HARNESS_IMAGE"));
        Assert.Equal("worker", Env(stages["worker"], "HARNESS_ROLE"));
        Assert.Equal("120", Env(stages["worker"], "HARNESS_WORKER_GIVE_UP_SECONDS"));
    }

    [Fact]
    public void The_version_is_declared_last_in_both_images()
    {
        var stages = Stages();
        foreach (var target in new[] { "control", "worker" })
        {
            var instructions = stages[target].Instructions.ToArray();
            Assert.Equal(["ARG HARNESS_VERSION", "ARG HARNESS_COMMIT"], instructions[^3..^1]);
            Assert.StartsWith(@"LABEL org.opencontainers.image.version=""${HARNESS_VERSION}""", instructions[^1]);
            // Nothing before it in the stage or in base names the version, so no layer above depends on it.
            Assert.DoesNotContain(Chain(stages, target).SelectMany(s => s.Name == target ? s.Instructions.Take(s.Instructions.Count - 3) : s.Instructions),
                line => line.Contains("HARNESS_VERSION", StringComparison.Ordinal) || line.Contains("HARNESS_COMMIT", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_worker_is_the_last_stage()
    {
        // A build with no --target makes the last stage: the image that can run every role.
        Assert.Equal("worker", Stages().Values.Last().Name);
    }

    [Fact]
    public void Each_image_has_its_own_healthcheck()
    {
        var stages = Stages();

        var control = Healthcheck(stages["control"]);
        Assert.Contains("curl -fsS -o /dev/null http://127.0.0.1:8080/healthz", control);
        Assert.Contains("--start-period=1m", control);

        // A worker shares control's network namespace: 127.0.0.1:8080 is control, so its own check is
        // the file its host keeps while connected, and /healthz only when it runs as `all`.
        var worker = Healthcheck(stages["worker"]);
        Assert.Contains(@"test -n ""$(find /var/lib/harness-worker/connected -mmin -1 2>/dev/null)""", worker);
        Assert.Contains("--start-period=10m", worker);
        Assert.Matches(@"if \[ ""\$HARNESS_ROLE"" = all \]; then curl [^;]*/healthz; else test -n", worker);
        Assert.Equal("/var/lib/harness-worker", Env(stages["worker"], "HARNESS_WORKER_STATE_DIR"));
    }

    [Fact]
    public void Both_images_are_built_in_the_docker_format_so_their_healthchecks_survive()
    {
        var release = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "release.ps1"));
        var functions = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "release-functions.ps1"));
        var devUp = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "dev-up.ps1"));

        // Every build line in either script passes --format docker and a --target.
        var start = release.IndexOf("$build = @(", StringComparison.Ordinal);
        var end = release.IndexOf("podman @build", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, release);
        Assert.Contains("'build', '--format', 'docker',", release[start..end]);
        Assert.Contains("'--target', $b.Target,", release[start..end]);
        Assert.Single(Regex.Matches(release, @"podman @build"));
        Assert.Single(Regex.Matches(release, @"\$build = @\("));

        var devBuilds = Regex.Matches(devUp, @"(?m)^\s*podman build\b[^\n]*");
        Assert.NotEmpty(devBuilds);
        Assert.All(devBuilds, b => Assert.Contains("--format docker --target $build.Target", b.Value));

        // Both targets, under the one version.
        Assert.Contains("Target = 'control'; Ref = \"${Image}:$Version\"", functions);
        Assert.Contains("Target = 'worker'; Ref = \"${Image}:$Version-worker\"", functions);
        Assert.Contains("Target = 'control'; Ref = $tag", devUp);
        Assert.Contains("Target = 'worker'; Ref = \"$tag-worker\"", devUp);
        Assert.Single(Regex.Matches(devUp, @"--build-arg ""HARNESS_VERSION=\$version"""));
    }

    [Fact]
    public void Release_pushes_every_version_tag_before_either_latest()
    {
        var release = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "release.ps1"));

        var version = release.IndexOf(@"podman manifest push --all $b.Ref ""docker://$($b.Ref)""", StringComparison.Ordinal);
        var latest = release.IndexOf(@"podman manifest push --all $b.Ref ""docker://$($b.Latest)""", StringComparison.Ordinal);
        Assert.True(version > 0 && latest > version, release);
    }

    [Fact]
    public void Both_images_carry_the_three_users_and_setpriv()
    {
        var stages = Stages();
        var users = string.Join('\n', stages["base"].Instructions);

        Assert.Contains("groupadd --gid 10001 harness", users);
        Assert.Contains("groupadd --gid 10002 agent", users);
        Assert.Contains("groupadd --gid 10003 worker", users);
        Assert.Contains("useradd --uid 10001 --gid harness --groups agent ", users);
        Assert.Matches(@"useradd --uid 10002 --gid agent --no-create-home", users);
        Assert.Contains("useradd --uid 10003 --gid worker --groups agent ", users);
        Assert.Contains("util-linux", users);
        Assert.Contains("command -v setpriv && command -v runuser && command -v flock", users);
        Assert.Contains("test -x /usr/bin/setpriv && test -x /usr/bin/dotnet", users);
        // The backup helper runs sqlite3 in control's image; control's git and gh run as agent.
        foreach (var package in new[] { "sqlite3", "git", "openssh-client", "gh" }) Assert.Matches($@"\b{Regex.Escape(package)}\b", users);

        // Both images are built on base, so an id means the same user in each.
        foreach (var target in new[] { "control", "worker" }) Assert.Equal("base", stages[target].From);
        Assert.DoesNotContain(stages["control"].Instructions.Concat(stages["worker"].Instructions),
            line => line.Contains("useradd", StringComparison.Ordinal) || line.Contains("groupadd", StringComparison.Ordinal));
    }

    [Fact]
    public void The_worker_image_keeps_its_state_out_of_the_volume()
    {
        var state = Env(Stages()["worker"], "HARNESS_WORKER_STATE_DIR");

        Assert.Equal("/var/lib/harness-worker", state);
        Assert.False(state!.StartsWith("/data", StringComparison.Ordinal));
        Assert.False(state.StartsWith("/tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void No_temporary_folder_is_shared_between_containers()
    {
        var instructions = Stages().Values.SelectMany(s => s.Instructions).ToList();

        // The one volume is /data; /tmp is each container's own.
        Assert.Equal(["VOLUME /data"], instructions.Where(line => line.StartsWith("VOLUME", StringComparison.Ordinal)));
        Assert.DoesNotContain(instructions, line => Regex.IsMatch(line, @"--mount=type=(tmpfs|cache)[^ ]*target=/tmp"));
    }

    // ---- Parsing the Containerfile ----

    /// <summary>One stage: its name (or its index), what it is <c>FROM</c>, and its instructions with
    /// comments dropped and continuation lines joined.</summary>
    internal sealed record Stage(string Name, string From, List<string> Instructions);

    internal static Dictionary<string, Stage> Stages()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "Containerfile")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = text.Split('\n').Where(line => !line.TrimStart().StartsWith('#')).ToList();
        var joined = string.Join('\n', lines).Replace("\\\n", " ", StringComparison.Ordinal).Split('\n')
            .Select(line => Regex.Replace(line.Trim(), @"\s+", " ")).Where(line => line.Length > 0);

        var stages = new Dictionary<string, Stage>(StringComparer.Ordinal);
        Stage? current = null;
        foreach (var line in joined)
        {
            var from = Regex.Match(line, @"^FROM (--\S+ )*(?<from>\S+)( AS (?<name>\S+))?$", RegexOptions.IgnoreCase);
            if (from.Success)
            {
                var name = from.Groups["name"].Success ? from.Groups["name"].Value : stages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                current = new Stage(name, from.Groups["from"].Value, []);
                stages.Add(name, current);
                continue;
            }

            current?.Instructions.Add(line);
        }

        return stages;
    }

    /// <summary>The stage and every stage it is built <c>FROM</c>, nearest first.</summary>
    internal static List<Stage> Chain(Dictionary<string, Stage> stages, string target)
    {
        var chain = new List<Stage>();
        for (var at = stages[target]; ; at = stages[at.From])
        {
            chain.Add(at);
            if (!stages.ContainsKey(at.From)) return chain;
        }
    }

    /// <summary>An ENV value in a stage, from either form (<c>ENV A=b C=d</c>).</summary>
    internal static string? Env(Stage stage, string name)
    {
        string? value = null;
        foreach (var line in stage.Instructions.Where(l => l.StartsWith("ENV ", StringComparison.Ordinal)))
        {
            var match = Regex.Match(line, $@"(?:^ENV | ){Regex.Escape(name)}=(?<value>\S+)");
            if (match.Success) value = match.Groups["value"].Value;
        }

        return value;
    }

    private static string Healthcheck(Stage stage) =>
        Assert.Single(stage.Instructions, line => line.StartsWith("HEALTHCHECK", StringComparison.Ordinal));

    /// <summary>The files a stage copies from the build context into /opt/harness.</summary>
    private static IEnumerable<string> Copied(Stage stage) =>
        stage.Instructions.Where(l => l.StartsWith("COPY scripts/", StringComparison.Ordinal))
            .SelectMany(l =>
            {
                var parts = l.Split(' ')[1..];
                return parts[..^1].Select(source => parts[^1].TrimEnd('/') + "/" + Path.GetFileName(source));
            });

    /// <summary>Each agent token found as a word in a chain's instructions.</summary>
    private static List<string> Found(IEnumerable<Stage> chain, IEnumerable<string> tokens)
    {
        var text = string.Join('\n', chain.SelectMany(s => s.Instructions));
        return tokens.Where(token => Regex.IsMatch(text, $@"(?<![\w.-]){Regex.Escape(token)}(?![\w-])", RegexOptions.IgnoreCase)).ToList();
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
