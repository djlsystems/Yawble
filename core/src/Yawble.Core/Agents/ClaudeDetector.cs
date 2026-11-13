using Yawble.Core.Policy;

namespace Yawble.Core.Agents;

/// <summary>
/// Anthropic Claude Code. Layouts (the shared npm / Homebrew / WinGet rules are in <see cref="AgentCliDetector"/>):
/// <list type="bullet">
/// <item>Native installer, macOS/Linux (curl -fsSL https://claude.ai/install.sh | bash): ~/.local/bin/claude is a
/// symlink to ~/.local/share/claude/versions/&lt;version&gt; (one file per version, the program itself). Grant: the
/// versions folder only, not ~/.local/share.</item>
/// <item>Native installer, Windows (irm https://claude.ai/install.ps1 | iex): %USERPROFILE%\.local\bin\claude.exe, a
/// plain file (no symlinks without Developer Mode), with %USERPROFILE%\.local\share\claude\versions beside it. Grant:
/// ~\.local\bin and the versions folder.</item>
/// <item>Old "local" install (claude migrate-installer, 2025): ~/.claude/local/claude, an exec wrapper to
/// ~/.claude/local/node_modules/.bin/claude: an npm package INSIDE the config folder. It works (the package folder
/// alone is granted) but is reported as a problem.</item>
/// <item>npm: the package folder (+ node for a cli.js build). Homebrew: brew install --cask claude-code, the Caskroom
/// version folder. WinGet: winget install Anthropic.ClaudeCode, the package folder.</item>
/// </list>
/// Config/credentials (never opened, reported to be blocked): ~/.claude (with .credentials.json on Linux/Windows),
/// ~/.claude.json, and $CLAUDE_CONFIG_DIR when set. On macOS the login is in the Keychain, not a file.
/// Auto-update is off for the probe: DISABLE_AUTOUPDATER=1 (and no non-essential traffic).
/// </summary>
public sealed class ClaudeDetector : AgentCliDetector
{
    public override string CliId => "claude";

    protected override string Command => "claude";

    protected override IReadOnlyDictionary<string, string> VersionEnvironment { get; } = new Dictionary<string, string>
    {
        ["DISABLE_AUTOUPDATER"] = "1",
        ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
    };

    protected override IEnumerable<string?> KnownLaunchers(DetectionHost h)
    {
        yield return h.Combine(h.Home, ".local", "bin", h.Exe("claude"));
        if (h.Os == TargetOs.Windows)
        {
            yield return h.FromVar("APPDATA", "npm", "claude.cmd");
            yield return h.FromVar("LOCALAPPDATA", "Microsoft", "WinGet", "Links", "claude.exe");
            yield break;
        }
        yield return h.Combine(h.Home, ".claude", "local", "claude");
        if (h.Os == TargetOs.MacOS) yield return "/opt/homebrew/bin/claude";
        else yield return "/home/linuxbrew/.linuxbrew/bin/claude";
        yield return "/usr/local/bin/claude";
    }

    public override CliSignIn? SignIn(DetectionHost h) => SignInCatalog.Claude(h);

    public override IReadOnlyList<string> ConfigFolders(DetectionHost h)
    {
        var list = new List<string> { h.Combine(h.Home, ".claude"), h.Combine(h.Home, ".claude.json") };
        if (h.FromVar("CLAUDE_CONFIG_DIR") is { } custom && !list.Any(c => h.Paths.Same(c, custom))) list.Add(custom);
        return list;
    }

    protected override InstallLayout? NativeLayout(DetectionHost h, ResolvedProgram p)
    {
        // .../claude/versions/<version>  (a file: macOS/Linux)  or  .../claude/versions/<version>/claude.exe
        var segs = h.Segments(p.Executable);
        var root = h.RootOf(p.Executable)!;
        for (var i = segs.Count - 2; i >= 1; i--)
        {
            if (segs[i].Equals("versions", StringComparison.OrdinalIgnoreCase) && segs[i - 1].Equals("claude", StringComparison.OrdinalIgnoreCase)
                && i >= segs.Count - 3)
                return new InstallLayout("native", [h.Combine(root, [.. segs.Take(i + 1)])]);
        }
        var localBin = h.Combine(h.Home, ".local", "bin");
        if (h.Os == TargetOs.Windows && h.Paths.Same(p.Executable, h.Combine(localBin, "claude.exe")))
        {
            var versions = h.Combine(h.Home, ".local", "share", "claude", "versions");
            return new InstallLayout("native", h.Fs.DirectoryExists(versions) ? [localBin, versions] : [localBin]);
        }
        return null;
    }

    protected override IEnumerable<string> LayoutWarnings(DetectionHost h, ResolvedProgram p)
    {
        if (h.IsWithin(p.Executable, h.Combine(h.Home, ".claude")))
            yield return "This is Claude Code's old 'local' install inside ~/.claude, your config folder. It works, but please move to the native installer (run 'claude install') so the program lives outside your credentials.";
    }

    public override string InstallHelp(TargetOs os) => os == TargetOs.Windows
        ? "Install Claude Code yourself, then sign in once in your own terminal. In PowerShell: irm https://claude.ai/install.ps1 | iex "
          + "(or: winget install Anthropic.ClaudeCode, or with Node.js 18+: npm install -g @anthropic-ai/claude-code). "
          + "Open a new terminal, run 'claude' and sign in, then reopen this dialog. Official guide: https://docs.claude.com/en/docs/claude-code/setup"
        : "Install Claude Code yourself, then sign in once in your own terminal: curl -fsSL https://claude.ai/install.sh | bash "
          + (os == TargetOs.MacOS ? "(or: brew install --cask claude-code, " : "(or ")
          + "with Node.js 18+: npm install -g @anthropic-ai/claude-code). "
          + "Open a new terminal, run 'claude' and sign in, then reopen this dialog. Official guide: https://docs.claude.com/en/docs/claude-code/setup";
}
