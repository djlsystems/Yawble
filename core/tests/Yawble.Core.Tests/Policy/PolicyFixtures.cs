using Yawble.Core.Agents;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Agents;

namespace Yawble.Core.Tests.Policy;

/// <summary>Fixed, fictional machines for each OS: the person "alice" with two teams.</summary>
internal static class PolicyFixtures
{
    public const string Token = "golden-run-token-0123456789abcdefghijklmnopqrstuv";
    public const int Port = 47001;

    /// <summary>A prompt with a quote and shell metacharacters: it must reach the CLI as one argument.</summary>
    public const string Prompt = "You have 1 instruction from Manager. Read it with the bus tools; don't push. $(id) `id`";

    public static readonly MemberRef Me = new("alpha", "dev1");

    public static readonly MemberRef[] Everyone =
    [
        new("alpha", "manager"), new("alpha", "dev1"), new("alpha", "dev2"), new("beta", "manager"),
    ];

    public static (string Home, string AppData, string Install, string Platform) Machine(TargetOs os) => os switch
    {
        TargetOs.Windows => (@"C:\Users\alice", @"C:\Users\alice\AppData\Local\Yawble", @"C:\Program Files\Yawble\resources", "win-x64"),
        TargetOs.MacOS => ("/Users/alice", "/Users/alice/Library/Application Support/Yawble", "/Applications/Yawble.app/Contents/Resources", "osx-arm64"),
        _ => ("/home/alice", "/home/alice/.local/share/yawble", "/opt/yawble/resources", "linux-x64"),
    };

    public static RunLayout Layout(TargetOs os, AgentCli cli, MemberRef? member = null, string? install = null, bool signIn = false)
    {
        var m = Machine(os);
        return RunLayout.Standard(os, m.Home, m.AppData, m.Install, member ?? Me, cli,
            Cli(os, cli, install ?? DefaultInstall(cli), signIn), Everyone);
    }

    /// <summary>
    /// Host sign-in: the CLI's own sign-in on the fictional machine, as SignInCatalog reports it (the file is present):
    /// Claude ~/.claude/.credentials.json via CLAUDE_SECURESTORAGE_CONFIG_DIR (macOS: the Keychain, not read), Grok
    /// ~/.grok/auth.json via GROK_AUTH_PATH.
    /// </summary>
    public static CliSignIn SignIn(TargetOs os, AgentCli cli)
    {
        var (home, _, _, _) = Machine(os);
        var sep = os == TargetOs.Windows ? '\\' : '/';
        string H(params string[] parts) => home + sep + string.Join(sep, parts);
        if (cli == AgentCli.Claude && os == TargetOs.MacOS)
            return new CliSignIn(SignInStatus.Pending, SignInCatalog.MacClaudePending, [], new Dictionary<string, string>());
        if (cli == AgentCli.Codex)
            return new CliSignIn(SignInStatus.SignedIn, "signed in", [new CliSignInFile(H(".codex", "auth.json"), SignInAccess.CopyIn, true, "Codex")],
                new Dictionary<string, string>());
        if (cli == AgentCli.Copilot)
            return new CliSignIn(SignInStatus.SignedIn, "signed in", [new CliSignInFile("https://github.com:alice.copilot-cli", SignInAccess.HostStore, true, "Copilot")],
                new Dictionary<string, string>());
        if (cli == AgentCli.Antigravity)
            return new CliSignIn(SignInStatus.SignedIn, "signed in", [new CliSignInFile(SignInCatalog.AntigravityTarget, SignInAccess.HostStore, true, "Antigravity")],
                new Dictionary<string, string>());
        return cli == AgentCli.Claude
            ? new CliSignIn(SignInStatus.SignedIn, "signed in", [new CliSignInFile(H(".claude", ".credentials.json"), SignInCatalog.ClaudeCredentialsAccess, true, "Claude")],
                new Dictionary<string, string> { [SignInCatalog.ClaudeStoreVariable] = H(".claude") })
            : new CliSignIn(SignInStatus.SignedIn, "signed in", [new CliSignInFile(H(".grok", "auth.json"), SignInCatalog.GrokAuthAccess, true, "Grok")],
                new Dictionary<string, string> { [SignInCatalog.GrokAuthVariable] = H(".grok", "auth.json") });
    }

    public static string DefaultInstall(AgentCli cli) => cli switch
    {
        AgentCli.Claude => "native",
        AgentCli.Grok => "grok",
        var other => other.Id(),
    };

    /// <summary>A stand-in for Copilot's token from the host's credential store (never a real one).</summary>
    public const string CopilotToken = "gho_FICTIONALfictionalFICTIONALfiction1";

    /// <summary>
    /// The member's CLI as the detector reports it on the fictional machine, per install layout (the app runs
    /// the CLI where the person installed it). The host's folders around it (for the config denials) are fixed too.
    /// <list type="bullet">
    /// <item>native: Anthropic's installer (~/.local/bin/claude -> ~/.local/share/claude/versions/&lt;v&gt; on Linux and
    /// macOS; %USERPROFILE%\.local\bin\claude.exe on Windows).</item>
    /// <item>npm: npm -g, whose postinstall hard-links the native program into bin/claude.exe (2.1.x): no node at run.</item>
    /// <item>npm-node: npm -g where the entry point is a JavaScript file (cli.js; --ignore-scripts or an older
    /// package): node's own folder is granted too, and the command is node &lt;script&gt;.</item>
    /// <item>homebrew (macOS): the cask's versioned Caskroom folder.</item>
    /// <item>grok: xAI's installer, ~/.grok/bin/grok -> ~/.grok/downloads/grok-&lt;v&gt;-&lt;platform&gt; (Windows: a copy in
    /// ~\\.grok\\bin plus MinGit in %LOCALAPPDATA%\\grok\\git): the program lives INSIDE the config folder ~/.grok,
    /// so ~/.grok's other entries are denied one by one.</item>
    /// </list>
    /// </summary>
    public static CliInstall Cli(TargetOs os, AgentCli cli, string install, bool signIn = false)
    {
        var (home, _, _, _) = Machine(os);
        var w = os == TargetOs.Windows;
        var sep = w ? '\\' : '/';
        string H(params string[] parts) => home + sep + string.Join(sep, parts);
        var id = cli.Id();
        var config = cli switch
        {
            AgentCli.Claude => new[] { H(".claude"), H(".claude.json") },
            AgentCli.Grok => [H(".grok")],
            AgentCli.Codex => [H(".codex")],
            AgentCli.Copilot => [H(".copilot")],
            _ => [H(".gemini")],
        };
        string exe;
        string[] ro;
        string kind = install;
        string[] extra = [];
        switch (install)
        {
            case "native" when w:
                // Windows: claude.exe is a plain copy in ~\.local\bin, with the versions folder beside it.
                exe = H(".local", "bin", "claude.exe");
                ro = [H(".local", "bin"), H(".local", "share", "claude", "versions")];
                break;
            case "native":
                exe = H(".local", "share", "claude", "versions", "2.1.318");
                ro = [H(".local", "share", "claude", "versions")];
                extra = [H(".local", "share", "claude", "versions", "2.1.317")];
                break;
            case "npm" when w:
                exe = H("AppData", "Roaming", "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
                ro = [H("AppData", "Roaming", "npm", "node_modules", "@anthropic-ai", "claude-code")];
                break;
            case "npm":
                exe = "/usr/local/lib/node_modules/@anthropic-ai/claude-code/bin/claude.exe";
                ro = ["/usr/local/lib/node_modules/@anthropic-ai/claude-code"];
                break;
            case "npm-node" when w:
                exe = H("AppData", "Roaming", "npm", "node_modules", "@anthropic-ai", "claude-code", "cli.js");
                ro = [H("AppData", "Roaming", "npm", "node_modules", "@anthropic-ai", "claude-code"), @"C:\Program Files\nodejs"];
                extra = [@"C:\Program Files\nodejs\node.exe"];
                kind = "npm";
                break;
            case "npm-node":
                exe = H(".nvm", "versions", "node", "v22.12.0", "lib", "node_modules", "@anthropic-ai", "claude-code", "cli.js");
                ro = [H(".nvm", "versions", "node", "v22.12.0", "lib", "node_modules", "@anthropic-ai", "claude-code"),
                      H(".nvm", "versions", "node", "v22.12.0", "bin")];
                extra = [H(".nvm", "versions", "node", "v22.12.0", "bin", "node")];
                kind = "npm";
                break;
            case "homebrew":
                exe = "/opt/homebrew/Caskroom/claude-code/2.1.318/claude";
                ro = ["/opt/homebrew/Caskroom/claude-code/2.1.318"];
                break;
            case "grok" when w:
                // xAI's install.ps1: grok.exe COPIED to ~\.grok\bin (inside the config folder), MinGit in %LOCALAPPDATA%.
                exe = H(".grok", "bin", "grok.exe");
                ro = [H(".grok", "bin"), H("AppData", "Local", "grok", "git", "2.47.1")];
                kind = "native";
                extra = [H(".grok", "auth.json"), H(".grok", "config.toml"), H(".grok", "sessions", "s1.jsonl"),
                         H("AppData", "Local", "grok", "git", "2.47.1", "cmd", "git.exe")];
                break;
            case "grok":
                var file = os == TargetOs.MacOS ? "grok-1.0.77-macos-aarch64" : "grok-1.0.77-linux-x86_64";
                exe = H(".grok", "downloads", file);
                ro = [H(".grok", "downloads")];
                kind = "native";
                extra = [H(".grok", "auth.json"), H(".grok", "config.toml"), H(".grok", "bin", "grok"),
                         H(".grok", "bundled", "rg"), H(".grok", "sessions", "s1.jsonl")];
                break;
            case "codex" when w:
                exe = H("AppData", "Local", "Programs", "OpenAI", "Codex", "bin", "codex.exe");
                ro = [H("AppData", "Local", "Programs", "OpenAI", "Codex", "bin")];
                kind = "native";
                break;
            case "codex":
                exe = "/usr/local/lib/node_modules/@openai/codex/bin/codex";
                ro = ["/usr/local/lib/node_modules/@openai/codex"];
                kind = "npm";
                break;
            case "copilot" when w:
                exe = H("AppData", "Roaming", "npm", "node_modules", "@github", "copilot", "npm-loader.js");
                ro = [H("AppData", "Roaming", "npm", "node_modules", "@github", "copilot"), @"C:\Program Files\nodejs"];
                extra = [@"C:\Program Files\nodejs\node.exe"];
                kind = "npm";
                break;
            case "copilot":
                exe = "/usr/local/lib/node_modules/@github/copilot/npm-loader.js";
                ro = ["/usr/local/lib/node_modules/@github/copilot", "/usr/local/bin"];
                extra = ["/usr/local/bin/node"];
                kind = "npm";
                break;
            case "antigravity" when w:
                exe = H("AppData", "Local", "agy", "bin", "agy.exe");
                ro = [H("AppData", "Local", "agy", "bin")];
                kind = "native";
                break;
            case "antigravity":
                exe = H(".local", "share", "agy", "agy");
                ro = [H(".local", "share", "agy")];
                kind = "native";
                break;
            default:
                throw new ArgumentException(install);
        }
        // The host as it is: the config folders exist (with their secrets), the install exists.
        var signInState = signIn ? SignIn(os, cli) : null;
        var folders = new FakeFolders(os, [.. config.Select(c => c.EndsWith(".json") ? c : c + sep + "settings.json"), exe, .. extra,
            .. signInState?.Files.Select(f => f.Path) ?? []]);
        return CliInstall.FromDetected(os, home, TestClis.Found(id, exe, ro, config, kind) with { SignIn = signInState }, folders);
    }

    public static RunSpec Run(TargetOs os, CredentialMode mode, RunKind kind = RunKind.Headless, string? install = null) => new()
    {
        Layout = WithSecret(Layout(os, AgentPresets.CliOf(mode), install: install, signIn: AgentPresets.IsHostSignIn(mode)), mode),
        Credential = mode,
        Kind = kind,
        Prompt = kind == RunKind.Headless ? Prompt : null,
        RunId = "run-0001",
        SessionId = "11111111-2222-3333-4444-555555555555",
        RunToken = Token,
        GatewayPort = Port,
    };

    /// <summary>A Copilot run gets its token from the host's credential store at launch (the launcher's job).</summary>
    private static RunLayout WithSecret(RunLayout l, CredentialMode mode) => mode == CredentialMode.CopilotHostSignIn
        ? l with { CliInstall = l.CliInstall with { SecretEnvironment = new Dictionary<string, string> { [AgentPresets.CopilotTokenVariable] = CopilotToken } } }
        : l;

    /// <summary>The golden scenario's global policy: the defaults plus two allowed hosts.</summary>
    public static GlobalPolicy Global => GlobalPolicy.Default with
    {
        Network = new NetworkPolicy(["github.com", "registry.npmjs.org"]),
    };

    /// <summary>The golden scenario's override: tightens hosts and the wall clock, adds a read-only folder.</summary>
    public static PolicyPatch Override => PolicyPatch.Parse("""
        { "network": { "allowHosts": ["registry.npmjs.org"] },
          "compute": { "wallTimeoutSec": 900 },
          "filesystem": { "readonly": ["~/src/reference"] } }
        """, PatchKind.Override);

    public static PolicyPatch Loosen => PolicyPatch.Parse("""
        { "network": { "allowHosts": ["pypi.org"] },
          "compute": { "wallTimeoutSec": 14400 } }
        """, PatchKind.Loosen);
}
