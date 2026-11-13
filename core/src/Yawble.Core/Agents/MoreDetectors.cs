using Yawble.Core.Policy;

namespace Yawble.Core.Agents;

/// <summary>
/// OpenAI's Codex CLI. Layouts: the Windows installer (%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe, with
/// codex-code-mode-host.exe beside it), npm (@openai/codex, a native binary inside the
/// package), Homebrew. Config and sign-in: ~/.codex ($CODEX_HOME), blocked; the sign-in file is copied per run.
/// </summary>
public sealed class CodexDetector : AgentCliDetector
{
    public override string CliId => AgentClis.Codex;

    protected override string Command => "codex";

    protected override IReadOnlyDictionary<string, string> VersionEnvironment { get; } = new Dictionary<string, string>();

    protected override IEnumerable<string?> KnownLaunchers(DetectionHost h)
    {
        if (h.Os == TargetOs.Windows)
        {
            yield return h.FromVar("LOCALAPPDATA", "Programs", "OpenAI", "Codex", "bin", "codex.exe");
            yield return h.FromVar("APPDATA", "npm", "codex.cmd");
            yield break;
        }
        yield return h.Combine(h.Home, ".local", "bin", "codex");
        yield return h.Os == TargetOs.MacOS ? "/opt/homebrew/bin/codex" : "/home/linuxbrew/.linuxbrew/bin/codex";
        yield return "/usr/local/bin/codex";
    }

    public override CliSignIn? SignIn(DetectionHost h) => SignInCatalog.Codex(h);

    public override IReadOnlyList<string> ConfigFolders(DetectionHost h)
    {
        var list = new List<string> { h.Combine(h.Home, ".codex") };
        if (h.FromVar("CODEX_HOME") is { } custom && !list.Any(c => h.Paths.Same(c, custom))) list.Add(custom);
        return list;
    }

    protected override InstallLayout? NativeLayout(DetectionHost h, ResolvedProgram p)
    {
        var dir = h.Parent(p.Executable);
        var segs = h.Segments(p.Executable);
        // ...\Programs\OpenAI\Codex\bin\codex.exe: the bin folder holds everything the CLI runs.
        if (dir is not null && segs.Count >= 4 && segs[^2].Equals("bin", StringComparison.OrdinalIgnoreCase)
            && segs[^3].Equals("Codex", StringComparison.OrdinalIgnoreCase) && segs[^4].Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            return new InstallLayout("native", [dir]);
        return null;
    }

    protected override string? CheckVersionOutput(string stdout) =>
        stdout.TrimStart().StartsWith("codex", StringComparison.OrdinalIgnoreCase) ? null : "Its --version output does not look like OpenAI's Codex CLI ('codex-cli <version>').";

    public override string InstallHelp(TargetOs os) =>
        "Install OpenAI's Codex CLI yourself (https://developers.openai.com/codex/cli, or with Node.js: npm install -g @openai/codex), "
        + "then sign in once in your own terminal: run 'codex' and choose Sign in with ChatGPT (or 'codex login'). Then reopen this dialog.";
}

/// <summary>
/// GitHub Copilot CLI. Layouts: npm (@github/copilot, run by node: npm-loader.js), WinGet, Homebrew.
/// Config: ~/.copilot, blocked. Sign-in: the OS credential store (<see cref="SignInCatalog.Copilot"/>).
/// </summary>
public sealed class CopilotDetector : AgentCliDetector
{
    public override string CliId => AgentClis.Copilot;

    protected override string Command => "copilot";

    protected override IReadOnlyDictionary<string, string> VersionEnvironment { get; } = new Dictionary<string, string>
    {
        ["COPILOT_AUTO_UPDATE"] = "false",
    };

    protected override IEnumerable<string?> KnownLaunchers(DetectionHost h)
    {
        if (h.Os == TargetOs.Windows)
        {
            yield return h.FromVar("APPDATA", "npm", "copilot.cmd");
            yield return h.FromVar("LOCALAPPDATA", "Microsoft", "WinGet", "Links", "copilot.exe");
            yield break;
        }
        yield return h.Os == TargetOs.MacOS ? "/opt/homebrew/bin/copilot" : "/home/linuxbrew/.linuxbrew/bin/copilot";
        yield return "/usr/local/bin/copilot";
    }

    public override CliSignIn? SignIn(DetectionHost h) => SignInCatalog.Copilot(h);

    public override IReadOnlyList<string> ConfigFolders(DetectionHost h)
    {
        var list = new List<string> { h.Combine(h.Home, ".copilot") };
        if (h.FromVar("COPILOT_HOME") is { } custom && !list.Any(c => h.Paths.Same(c, custom))) list.Add(custom);
        return list;
    }

    protected override InstallLayout? NativeLayout(DetectionHost h, ResolvedProgram p) => null;

    protected override string? CheckVersionOutput(string stdout) =>
        stdout.Contains("Copilot", StringComparison.OrdinalIgnoreCase) ? null : "Its --version output does not look like GitHub Copilot CLI.";

    public override string InstallHelp(TargetOs os) =>
        "Install GitHub Copilot CLI yourself (with Node.js 22+: npm install -g @github/copilot; or winget install GitHub.Copilot), then "
        + "sign in once in your own terminal: run 'copilot' and type /login. Then reopen this dialog. Official page: https://github.com/features/copilot/cli";
}

/// <summary>
/// Google's Antigravity CLI (agy). Layout: %LOCALAPPDATA%\agy\bin\agy.exe, with its bundled tools
/// in %LOCALAPPDATA%\antigravity\bin. Config: ~/.gemini, blocked. Sign-in: Windows Credential Manager.
/// </summary>
public sealed class AntigravityDetector : AgentCliDetector
{
    public override string CliId => AgentClis.Antigravity;

    protected override string Command => "agy";

    protected override IReadOnlyDictionary<string, string> VersionEnvironment { get; } = new Dictionary<string, string>
    {
        ["AGY_CLI_DISABLE_AUTO_UPDATE"] = "1",
    };

    protected override IEnumerable<string?> KnownLaunchers(DetectionHost h)
    {
        if (h.Os == TargetOs.Windows)
        {
            yield return h.FromVar("LOCALAPPDATA", "agy", "bin", "agy.exe");
            yield break;
        }
        yield return h.Combine(h.Home, ".local", "bin", "agy");
        yield return "/usr/local/bin/agy";
    }

    public override CliSignIn? SignIn(DetectionHost h) => SignInCatalog.Antigravity(h);

    public override IReadOnlyList<string> ConfigFolders(DetectionHost h) => [h.Combine(h.Home, ".gemini")];

    protected override InstallLayout? NativeLayout(DetectionHost h, ResolvedProgram p)
    {
        var dir = h.Parent(p.Executable);
        if (dir is null) return null;
        var folders = new List<string> { dir };
        if (h.Os == TargetOs.Windows && h.FromVar("LOCALAPPDATA", "antigravity", "bin") is { } tools && h.Fs.DirectoryExists(tools)) folders.Add(tools);
        return new InstallLayout("native", folders);
    }

    public override string InstallHelp(TargetOs os) =>
        "Install Google's Antigravity CLI (agy) yourself from https://antigravity.google, then sign in once in your own terminal by running 'agy'.";
}
