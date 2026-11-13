using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Yawble.Core.Policy;

namespace Yawble.Core.Sandboxing;

internal static class SandboxJson
{
    // NewLine is pinned: the default is Environment.NewLine (CRLF on Windows), and these strings become
    // seed-file content inside the request, which must be byte-identical whatever the host OS.
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

/// <summary>Everything about one run that is not policy.</summary>
public sealed record RunSpec
{
    public required RunLayout Layout { get; init; }
    public required CredentialMode Credential { get; init; }
    public required RunKind Kind { get; init; }
    public string? Prompt { get; init; }
    public string? Model { get; init; }

    /// <summary>A custom agent's argument changes (null for a built-in agent).</summary>
    public ArgvCustomization? Custom { get; init; }
    public required string RunId { get; init; }
    public required string SessionId { get; init; }

    /// <summary>The per-run bearer token: bus identity and the CLI's credential placeholder.</summary>
    public required string RunToken { get; init; }

    /// <summary>The run's gateway, bound on 127.0.0.1 (IPv4 only: Linux rejects a [::1] proxy).</summary>
    public required int GatewayPort { get; init; }

    /// <summary>The team's environment variables: added under the app's own (a name the app sets, or reserves, is skipped).</summary>
    public IReadOnlyDictionary<string, string>? TeamEnv { get; init; }
}

/// <summary>The compute limits that do not go into the request.</summary>
public sealed record ComputeLimits(int IdleTimeoutSec, int WallTimeoutSec, int? MemoryMb, int? CpuPercent, int? MaxProcesses);

/// <summary>The compiler's output: the exact sandbox 1.0.0 request, plus what the core and the gateway enforce.</summary>
public sealed record CompiledRun(
    JsonObject Request,
    ComputeLimits Compute,
    GatewayPlan Gateway,
    IReadOnlyList<SeedFile> Seeds,
    EffectivePolicy Policy)
{
    /// <summary>The exact argv the request's command line was joined from (unredacted: record it through RunArgv.ForRecord).</summary>
    public IReadOnlyList<string> Argv { get; init; } = [];

    /// <summary>The exact JSON submitted to the sandbox.</summary>
    public string RequestJson => Request.ToJsonString(SandboxJson.Compact);

    /// <summary>Everything except the request, for the golden launch files and the terminal banner.</summary>
    public JsonObject LaunchJson() => new()
    {
        ["compute"] = new JsonObject
        {
            ["idleTimeoutSec"] = Compute.IdleTimeoutSec,
            ["wallTimeoutSec"] = Compute.WallTimeoutSec,
            ["memoryMb"] = Compute.MemoryMb,
            ["cpuPercent"] = Compute.CpuPercent,
            ["maxProcesses"] = Compute.MaxProcesses,
        },
        ["gateway"] = new JsonObject
        {
            ["port"] = Gateway.Port,
            ["allowHosts"] = new JsonArray(Gateway.AllowHosts.Select(h => (JsonNode?)h).ToArray()),
            ["vhosts"] = new JsonArray(Gateway.Vhosts.Select(v => (JsonNode?)new JsonObject
            {
                ["host"] = v.Host,
                ["kind"] = v.Kind,
                ["upstream"] = v.Upstream,
                ["credential"] = v.Credential,
                ["allowedPaths"] = new JsonArray(v.AllowedPaths.Select(p => (JsonNode?)p).ToArray()),
            }).ToArray()),
        },
        ["seeds"] = new JsonArray(Seeds.Select(s => (JsonNode?)new JsonObject { ["path"] = s.Path, ["content"] = s.Content }).ToArray()),
        ["loosened"] = new JsonArray(Policy.Loosened.Select(l => (JsonNode?)l).ToArray()),
        ["maxConcurrentAgents"] = Policy.MaxConcurrentAgents,
    };
}

/// <summary>
/// The sandbox request compiler: a pure function of (effective policy, run) that emits one
/// create-and-run request pinned to contract 1.0.0 with telemetry off. It never emits an option on the
/// never-emit list; <see cref="SandboxRequestInvariants"/> re-checks every request it builds and throws if one
/// slipped through.
/// </summary>
public static partial class SandboxRequestCompiler
{
    /// <summary>Windows ProcessContainer refuses an env without SYSTEMROOT (policy.md, "Process and filesystem").</summary>
    public const string WindowsSystemRoot = @"C:\Windows";

    /// <summary>
    /// Under the app's bin folder: dotnet.exe, the wrapper that runs the real dotnet with builds in one MSBuild process
    /// (bridge/src/Yawble.DotnetShim). First on PATH in Windows sandboxes.
    /// </summary>
    public const string DotnetShimFolder = "dotnet-shim";

    public static CompiledRun Compile(
        GlobalPolicy global, PolicyPatch? overridePatch, PolicyPatch? loosen, RunSpec run,
        Func<string, string>? canonicalize = null, long? machineMemoryMb = null)
    {
        var merged = PolicyMerger.Merge(global, overridePatch, loosen, run.Layout, canonicalize, machineMemoryMb);
        if (merged.Violations.Count > 0) throw new PolicyException(merged.Violations);
        return Compile(merged.Policy, run, canonicalize);
    }

    public static CompiledRun Compile(EffectivePolicy policy, RunSpec run, Func<string, string>? canonicalize = null)
    {
        var l = run.Layout;
        var os = l.Os;
        var paths = new PolicyPaths(os, l.Home, canonicalize);
        CheckRun(run);

        string R(string p) => paths.Resolve(p) ?? throw new ArgumentException($"not an absolute {os} path: {p}");
        List<string> Ordered(IEnumerable<string> items) => items.Select(R).Distinct(paths.Comparer).ToList();

        var readwrite = Ordered(l.ImplicitReadwrite.Concat(l.SignInReadwrite).Concat(policy.Readwrite));
        var readonlyPaths = Ordered(l.ImplicitReadonly.Concat(policy.Readonly))
            .Where(p => !readwrite.Any(rw => paths.Same(rw, p))).ToList();
        var denied = Ordered(l.ImplicitDenied.Concat(policy.Denied));

        var env = BaseEnvironment(run);
        foreach (var (k, v) in AgentPresets.Environment(l, run.Credential, run.RunToken)) env[k] = v;
        // HOME is the canonical workspace, like cwd and the grants: an app data root reached through a symlink
        // (macOS temp folders, /var -> /private/var) must not give the sandbox a HOME outside its own grants.
        env["HOME"] = R(l.Workspace);
        if (os == TargetOs.Windows) env["USERPROFILE"] = env["HOME"];
        foreach (var (k, v) in run.TeamEnv ?? new Dictionary<string, string>())
        {
            if (!env.ContainsKey(k) && !Teams.TeamEnv.Reserved(k)) env[k] = v;
        }

        var argv = AgentPresets.Arguments(l, run.Credential, run.Kind, run.Prompt, run.SessionId, run.Model, run.Custom);
        var request = new JsonObject
        {
            ["version"] = SandboxPin.RequestContract,
            ["containment"] = os.Containment(),
        };
        if (os == TargetOs.Windows) request["containerId"] = "yawble-" + run.RunId;
        request["process"] = new JsonObject
        {
            ["commandLine"] = CommandLine.Join(os, argv),
            ["cwd"] = R(l.ClonePath),
            // Always explicit: an omitted env gives Windows the user-profile env, and the sandbox strips and
            // re-injects the proxy variables itself, so none are set here.
            ["env"] = new JsonArray(env.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (JsonNode?)$"{e.Key}={e.Value}").ToArray()),
            ["timeout"] = (long)policy.Compute.WallTimeoutSec * 1000,
        };
        request["filesystem"] = new JsonObject
        {
            ["readonlyPaths"] = Array(readonlyPaths),
            ["readwritePaths"] = Array(readwrite),
            ["deniedPaths"] = Array(denied),
        };
        request["network"] = new JsonObject
        {
            ["egress"] = new JsonObject { ["default"] = "deny" },
            // Windows unpackaged accepts only the identity-less proxy: ingress allow + hostLoopback allow
            // (microsoft/mxc network_parser.rs:370-397; an accepted residual). Linux and macOS: both deny.
            ["ingress"] = os == TargetOs.Windows
                ? new JsonObject { ["default"] = "allow", ["hostLoopback"] = "allow" }
                : new JsonObject { ["default"] = "deny", ["hostLoopback"] = "deny" },
        };
        request["runtimeConfig"] = new JsonObject { ["networkProxy"] = $"http://127.0.0.1:{run.GatewayPort}" };
        // Windows keeps Win32k (ui.disable false): with it off, every program that imports USER32 (git, grok,
        // PowerShell) dies with STATUS_DLL_INIT_FAILED. Every other UI limit is then
        // explicit and strict in processContainer.ui. Linux and macOS: the UI stays disabled.
        request["ui"] = new JsonObject { ["disable"] = os != TargetOs.Windows, ["clipboard"] = policy.Clipboard, ["injection"] = false };
        if (os == TargetOs.Windows)
        {
            request["processContainer"] = new JsonObject
            {
                ["ui"] = WindowsUiLimits(),
                // The drive root is enumerable (recursive: names, never contents; deniedPaths stay closed): without it sandbox release 1.0.1 denies cmd del,
                // rmdir /s and Remove-Item inside a read/write grant (microsoft/mxc#1676) and Node cannot run a script (lstat C:\).
                ["filesystem"] = new JsonObject
                {
                    ["enumeratePaths"] = Array(WindowsEnumeratePaths(readonlyPaths.Concat(readwrite).ToList(), denied, paths)),
                },
            };
            // The host's ACLs are never changed: refuse the sandbox's Tier 3 host-DACL fallback.
            request["fallback"] = new JsonObject { ["allowDaclMutation"] = false };
        }
        if (os == TargetOs.MacOS)
        {
            // Explicitly off: no login keychain from the sandbox (credential stores).
            request["seatbelt"] = new JsonObject { ["keychainAccess"] = false };
        }
        request["telemetry"] = new JsonObject { ["enabled"] = SandboxPin.TelemetryEnabled };

        var compiled = new CompiledRun(
            request,
            new ComputeLimits(policy.Compute.IdleTimeoutSec, policy.Compute.WallTimeoutSec, policy.Compute.MemoryMb, policy.Compute.CpuPercent, policy.Compute.MaxProcesses),
            new GatewayPlan(run.GatewayPort, [.. policy.AllowHosts, .. AgentPresets.PassthroughHosts(run.Credential)],
                AgentPresets.Vhosts(run.Credential)),
            AgentPresets.Seeds(l, run.Credential),
            policy)
        { Argv = argv };

        var problems = SandboxRequestInvariants.Check(compiled, run, canonicalize);
        if (problems.Count > 0)
            throw new InvalidOperationException("compiler invariant violated: " + string.Join("; ", problems));
        return compiled;
    }

    /// <summary>The strict Windows UI limits that go with ui.disable false (microsoft/mxc UIPolicy_Schema.md).</summary>
    public static JsonObject WindowsUiLimits() => new()
    {
        ["isolation"] = "container",
        ["desktopSystemControl"] = false,
        ["systemSettings"] = "none",
        ["ime"] = false,
    };

    /// <summary>
    /// The drive root (upper-case, "C:\") of every drive-letter grant that is not itself denied. enumeratePaths is
    /// recursive in sandbox release 1.0.1, so the root already covers every ancestor: names anywhere on that drive
    /// become listable, file contents and deniedPaths stay closed. Sorted, distinct.
    /// </summary>
    public static List<string> WindowsEnumeratePaths(IReadOnlyList<string> grants, IReadOnlyList<string> denied, PolicyPaths paths)
    {
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var grant in grants)
            if (grant.Length >= 3 && char.IsAsciiLetter(grant[0]) && grant[1] == ':' && grant[2] == '\\')
            {
                var root = char.ToUpperInvariant(grant[0]) + @":\";
                if (!denied.Any(d => paths.IsWithin(root, d))) result.Add(root);
            }
        return [.. result];
    }

    private static Dictionary<string, string> BaseEnvironment(RunSpec run)
    {
        var l = run.Layout;
        string J(params string[] parts) => AgentPresets.Join(l, l.Workspace, parts);
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = l.Workspace,
            ["YAWBLE_RUN_TOKEN"] = run.RunToken,
            ["IS_SANDBOX"] = "1",
            ["TERM"] = "xterm-256color",
        };
        if (l.Os == TargetOs.Windows)
        {
            // Windows PowerShell's folder too, as on every Windows PATH: a CLI's shell tool finds powershell.exe by name
            // (Grok 1.0.77: "Terminal error: IO Error: program not found" without it).
            // The app's dotnet wrapper first: builds in one MSBuild process (DotnetShimFolder).
            env["PATH"] = string.Join(';', [new PolicyPaths(l.Os, l.Home).Combine(l.AppBinDir, DotnetShimFolder),
                .. l.CliInstall.PathFolders, .. l.HostToolFolders, l.AppBinDir, WindowsSystemRoot + @"\System32", WindowsSystemRoot,
                WindowsSystemRoot + @"\System32\WindowsPowerShell\v1.0"]);
            env["SYSTEMROOT"] = WindowsSystemRoot;
            env["WINDIR"] = WindowsSystemRoot;
            env["USERPROFILE"] = l.Workspace;
            env["LOCALAPPDATA"] = J("AppData", "Local");
            env["APPDATA"] = J("AppData", "Roaming");
            env["TEMP"] = l.Tmp;
            env["TMP"] = l.Tmp;
            env["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
            // The machine-wide folders (not the person's): NuGet, the .NET SDK and npm resolve their machine config
            // through them, and NuGet fails "Value cannot be null (path1)" without ProgramData.
            var drive = WindowsSystemRoot[..2];
            env["SystemDrive"] = drive;
            env["ProgramData"] = drive + @"\ProgramData";
            env["ALLUSERSPROFILE"] = drive + @"\ProgramData";
            env["ProgramFiles"] = drive + @"\Program Files";
            env["ProgramFiles(x86)"] = drive + @"\Program Files (x86)";
            env["CommonProgramFiles"] = drive + @"\Program Files\Common Files";
        }
        else
        {
            var system = l.Os == TargetOs.MacOS ? "/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin" : "/usr/local/bin:/usr/bin:/bin";
            env["PATH"] = string.Join(':', [.. l.CliInstall.PathFolders, l.AppBinDir, system]);
            env["TMPDIR"] = l.Tmp;
            env["LANG"] = "C.UTF-8";
            // Caches and config under the member's workspace.
            env["XDG_CONFIG_HOME"] = J(".config");
            env["XDG_CACHE_HOME"] = J(".cache");
            env["XDG_DATA_HOME"] = J(".local", "share");
            env["XDG_STATE_HOME"] = J(".local", "state");
        }
        // The .NET SDK's first-run state, NuGet's package folder and telemetry: all in the member's own home.
        env["DOTNET_CLI_HOME"] = l.Workspace;
        env["DOTNET_NOLOGO"] = "1";
        env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        env["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        env["NUGET_PACKAGES"] = J(".nuget", "packages");
        // Node's built-in fetch (undici) ignores HTTPS_PROXY unless told to, and connects directly, which the sandbox
        // refuses (EACCES): npm itself uses the proxy, but install scripts that fetch (Electron's download) failed.
        env["NODE_USE_ENV_PROXY"] = "1";
        // Windows ProcessContainer: a named pipe with an ordinary name cannot be opened again, not even by the process
        // that made it (only \\.\pipe\LOCAL\ names work; microsoft/mxc#1706). MSBuild's worker nodes and its build
        // server and the compiler server talk over such pipes, so a parallel build fails ("Build FAILED", 0 errors) and
        // leaves idle workers that keep the agent's shell open. The dotnet wrapper first on PATH (DotnetShimFolder) builds
        // with -m:1; these keep MSBuild from starting anything else that would wait on a pipe.
        if (l.Os == TargetOs.Windows)
        {
            env["MSBUILDDISABLENODEREUSE"] = "1";
            env["MSBUILDUSESERVER"] = "0";
            env["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            env["UseSharedCompilation"] = "false"; // MSBuild reads environment variables as properties
        }
        // The team's shared view (read-only). Members fetch a teammate's branch from its bundle:
        // `git fetch "$YAWBLE_TEAM_BUNDLE" members/<name>` (a path fetch needs sh.exe on Windows, microsoft/mxc#1061).
        if (l.TeamDocuments is not null) env["YAWBLE_TEAM_DOCS"] = l.TeamDocuments;
        if (l.SharedRepo is not null)
        {
            env["YAWBLE_TEAM_REPO"] = l.SharedRepo;
            env["YAWBLE_TEAM_BUNDLE"] = new PolicyPaths(l.Os, l.Home).Combine(l.SharedRepo, Teams.TeamRepos.SharedBundleFile);
        }
        return env;
    }

    private static void CheckRun(RunSpec run)
    {
        var errors = new List<string>();
        if (run.GatewayPort is < 1024 or > 65535) errors.Add("gateway port must be 1024..65535");
        if (!TokenPattern().IsMatch(run.RunToken)) errors.Add("run token must be 32-128 URL-safe characters");
        if (!IdPattern().IsMatch(run.RunId)) errors.Add("run id must be 1-64 of [A-Za-z0-9-]");
        if (!IdPattern().IsMatch(run.SessionId)) errors.Add("session id must be 1-64 of [A-Za-z0-9-]");
        if (run.Kind == RunKind.Headless && string.IsNullOrWhiteSpace(run.Prompt)) errors.Add("a headless run needs a prompt");
        if (run.Model is { } m && !ModelPattern().IsMatch(m)) errors.Add("model must be 1-128 of [A-Za-z0-9._:/@-]");
        if (AgentPresets.CliOf(run.Credential) != run.Layout.Cli)
            errors.Add("the credential mode does not match the layout's CLI");
        if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors));
    }

    private static JsonArray Array(IEnumerable<string> items) => new(items.Select(i => (JsonNode?)i).ToArray());

    [GeneratedRegex("^[A-Za-z0-9_-]{32,128}$")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z0-9._:/@-]{1,128}$")]
    private static partial Regex ModelPattern();
}
