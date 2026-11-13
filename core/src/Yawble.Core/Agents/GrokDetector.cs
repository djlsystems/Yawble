using Yawble.Core.Policy;

namespace Yawble.Core.Agents;

/// <summary>
/// xAI's Grok Build CLI (https://x.ai/cli), NOT the community grok-cli on npm (@vibe-kit/grok-cli), which also
/// installs a 'grok' command. Layouts, from xAI's own installers (install.sh / install.ps1):
/// <list type="bullet">
/// <item>macOS/Linux (curl -fsSL https://x.ai/cli/install.sh | bash): the binary is downloaded to
/// ~/.grok/downloads/grok-&lt;version&gt;-&lt;platform&gt;; ~/.grok/bin/grok and ~/.grok/bin/agent are RELATIVE symlinks
/// to ../downloads/... (absolute when $GROK_BIN_DIR is elsewhere); when ~/.grok/bin is not on PATH the installer
/// also links ~/.local/bin/grok or /usr/local/bin/grok -> ~/.grok/bin/grok. Grant: ~/.grok/downloads only.
/// E.g. ~/.grok/bin/grok -> ../downloads/grok-&lt;version&gt;-linux-x86_64.</item>
/// <item>Windows (irm https://x.ai/cli/install.ps1 | iex): grok.exe is COPIED to %USERPROFILE%\.grok\bin\grok.exe
/// (and agent.exe, with grove.exe, grove-fsmonitor.exe, grove-credential.exe beside it) and %USERPROFILE%\.grok\bin
/// is put on the user PATH; a bundled MinGit is unpacked to %LOCALAPPDATA%\grok\git\&lt;version&gt;. Grant:
/// ~\.grok\bin and %LOCALAPPDATA%\grok\git when present.</item>
/// <item>A plain copy in ~/.grok/bin or $GROK_BIN_DIR on macOS/Linux (the installer copies when the Cursor CLI owns
/// the names): that folder.</item>
/// </list>
/// Config/credentials (never opened, reported to be blocked): ~/.grok as a whole (auth.json, sessions, config.toml,
/// memory ...), and $GROK_HOME when set. The granted downloads/bin folder lies inside it: the launcher must block
/// ~/.grok and allow only that one folder read-only. Auto-update is off for the probe: GROK_DISABLE_AUTOUPDATER=1 and
/// --no-auto-update (grok --no-auto-update --version with an empty HOME writes nothing).
/// </summary>
public sealed class GrokDetector : AgentCliDetector
{
    public override string CliId => "grok";

    protected override string Command => "grok";

    protected override IReadOnlyDictionary<string, string> VersionEnvironment { get; } = new Dictionary<string, string>
    {
        ["GROK_DISABLE_AUTOUPDATER"] = "1",
    };

    protected override IReadOnlyList<string> VersionArguments => ["--no-auto-update", "--version"];

    protected override IEnumerable<string?> KnownLaunchers(DetectionHost h)
    {
        yield return h.FromVar("GROK_BIN_DIR", h.Exe("grok"));
        yield return h.Combine(h.Home, ".grok", "bin", h.Exe("grok"));
    }

    public override CliSignIn? SignIn(DetectionHost h) => SignInCatalog.Grok(h);

    public override IReadOnlyList<string> ConfigFolders(DetectionHost h)
    {
        var list = new List<string> { h.Combine(h.Home, ".grok") };
        if (h.FromVar("GROK_HOME") is { } custom && !list.Any(c => h.Paths.Same(c, custom))) list.Add(custom);
        return list;
    }

    protected override InstallLayout? NativeLayout(DetectionHost h, ResolvedProgram p)
    {
        var grokDir = h.Combine(h.Home, ".grok");
        var downloads = h.Combine(grokDir, "downloads");
        if (h.Paths.Same(h.Parent(p.Executable) ?? "", downloads)) return new InstallLayout("native", [downloads]);

        var bin = h.FromVar("GROK_BIN_DIR") ?? h.Combine(grokDir, "bin");
        var dir = h.Parent(p.Executable) ?? "";
        if (h.Paths.Same(dir, bin) || h.Paths.Same(dir, h.Combine(grokDir, "bin")))
        {
            var folders = new List<string> { dir };
            if (h.Os == TargetOs.Windows && h.FromVar("LOCALAPPDATA", "grok", "git") is { } git && h.Fs.DirectoryExists(git)) folders.Add(git);
            return new InstallLayout("native", folders);
        }
        return null;
    }

    protected override string? NotThisCli(DetectionHost h, ResolvedProgram p) =>
        h.Segments(p.Executable).Any(s => s.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
            ? $"The 'grok' at {p.Launcher} is the community grok-cli from npm, not xAI's Grok Build CLI. The app does not run it."
            : null;

    protected override string? CheckVersionOutput(string stdout) =>
        stdout.TrimStart().StartsWith("grok ", StringComparison.OrdinalIgnoreCase)
            ? null
            : "Its --version output does not look like xAI's Grok Build CLI ('grok <version> ...').";

    public override string InstallHelp(TargetOs os) => os == TargetOs.Windows
        ? "Install xAI's Grok Build CLI yourself, then sign in once in your own terminal. In PowerShell: irm https://x.ai/cli/install.ps1 | iex . "
          + "Open a new terminal, run 'grok login', then reopen this dialog. Official page: https://x.ai/cli"
        : "Install xAI's Grok Build CLI yourself, then sign in once in your own terminal: curl -fsSL https://x.ai/cli/install.sh | bash . "
          + "Open a new terminal, run 'grok login', then reopen this dialog. Official page: https://x.ai/cli "
          + "(not the community 'grok-cli' npm package).";
}
