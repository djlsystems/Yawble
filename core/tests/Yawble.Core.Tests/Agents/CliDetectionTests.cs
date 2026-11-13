using Yawble.Core.Agents;
using Yawble.Core.Policy;
using Xunit;

namespace Yawble.Core.Tests.Agents;

/// <summary>
/// Every install layout the detector knows, on every OS, as a fake host (from Linux). Each test checks the launcher,
/// the real program, the install kind, the EXACT read-only folders, the config folders reported for blocking, the
/// version probe (auto-update off) and that no config/credential file was read.
/// </summary>
public class CliDetectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<DetectedCli> Claude(FakeHost f) => new ClaudeDetector().DetectAsync(f.Host(), Ct);

    private static Task<DetectedCli> Grok(FakeHost f) => new GrokDetector().DetectAsync(f.Host(), Ct);

    private static void NoConfigReads(FakeHost f, DetectedCli cli, params string[] allowedUnder)
    {
        var paths = new PolicyPaths(f.Os, f.Home);
        foreach (var read in f.Reads)
            foreach (var config in cli.ConfigFolders)
                if (paths.IsWithin(read, config) && !allowedUnder.Any(a => paths.IsWithin(read, a)))
                    Assert.Fail($"read {read}, inside the config folder {config}");
    }

    // ================================================================ Linux

    [Fact]
    public async Task LinuxClaudeNativeInstallerGrantsOnlyTheVersionsFolder()
    {
        // curl -fsSL https://claude.ai/install.sh | bash
        var f = new FakeHost(TargetOs.Linux, "/home/u")
            .File("/home/u/.local/share/claude/versions/2.1.318", FakeHost.Elf)
            .File("/home/u/.local/share/claude/versions/2.1.300", FakeHost.Elf)
            .Link("/home/u/.local/bin/claude", "/home/u/.local/share/claude/versions/2.1.318")
            .File("/home/u/.local/bin/pipx-tool", "#!/bin/sh\n")
            .OnPath("/usr/bin", "/home/u/.local/bin");
        f.VersionOutput = "2.1.318 (Claude Code)";

        var c = await Claude(f);

        Assert.True(c.Found);
        Assert.Equal("claude", c.CliId);
        Assert.Equal("/home/u/.local/bin/claude", c.LauncherPath);
        Assert.Equal("/home/u/.local/share/claude/versions/2.1.318", c.ExecutablePath);
        Assert.Equal("native", c.InstallKind);
        Assert.Equal(["/home/u/.local/share/claude/versions"], c.ReadOnlyFolders);
        Assert.Equal(["/home/u/.claude", "/home/u/.claude.json"], c.ConfigFolders);
        Assert.Equal("2.1.318", c.Version);
        Assert.Empty(c.Problems);
        var probe = Assert.Single(f.Probes);
        Assert.Equal("/home/u/.local/share/claude/versions/2.1.318", probe.FileName);
        Assert.Equal(["--version"], probe.Arguments);
        Assert.Equal("1", probe.Environment["DISABLE_AUTOUPDATER"]);
        NoConfigReads(f, c);
    }

    [Fact]
    public async Task LinuxClaudeNpmNativePackageGrantsThePackageFolderOnly()
    {
        // This host: /data/npm-global/bin/claude -> ../lib/node_modules/@anthropic-ai/claude-code/bin/claude.exe (ELF).
        var f = new FakeHost(TargetOs.Linux, "/home/u")
            .File("/data/npm-global/lib/node_modules/@anthropic-ai/claude-code/bin/claude.exe", FakeHost.Elf)
            .File("/data/npm-global/lib/node_modules/@anthropic-ai/claude-code/node_modules/@anthropic-ai/claude-code-linux-x64/claude", FakeHost.Elf)
            .Link("/data/npm-global/bin/claude", "../lib/node_modules/@anthropic-ai/claude-code/bin/claude.exe")
            .OnPath("/data/npm-global/bin", "/usr/bin");

        var c = await Claude(f);

        Assert.Equal("npm", c.InstallKind);
        Assert.Equal("/data/npm-global/lib/node_modules/@anthropic-ai/claude-code/bin/claude.exe", c.ExecutablePath);
        Assert.Equal(["/data/npm-global/lib/node_modules/@anthropic-ai/claude-code"], c.ReadOnlyFolders);
        Assert.Equal(c.ExecutablePath, Assert.Single(f.Probes).FileName);
        Assert.Empty(c.Problems);
    }

    [Fact]
    public async Task LinuxClaudeNpmCliJsUnderNvmAddsTheNodeRuntimeFolder()
    {
        const string nvm = "/home/u/.nvm/versions/node/v22.11.0";
        var f = new FakeHost(TargetOs.Linux, "/home/u")
            .File($"{nvm}/lib/node_modules/@anthropic-ai/claude-code/cli.js", "#!/usr/bin/env node\nimport './x.js'\n")
            .Link($"{nvm}/bin/claude", "../lib/node_modules/@anthropic-ai/claude-code/cli.js")
            .File($"{nvm}/bin/node", FakeHost.Elf)
            .OnPath($"{nvm}/bin", "/usr/bin");

        var c = await Claude(f);

        Assert.Equal("npm", c.InstallKind);
        Assert.Equal([$"{nvm}/lib/node_modules/@anthropic-ai/claude-code", $"{nvm}/bin"], c.ReadOnlyFolders);
        var probe = Assert.Single(f.Probes);
        Assert.Equal($"{nvm}/bin/node", probe.FileName);
        Assert.Equal([$"{nvm}/lib/node_modules/@anthropic-ai/claude-code/cli.js", "--version"], probe.Arguments);
    }

    [Fact]
    public async Task LinuxClaudeNpmCliJsWithoutNodeIsFoundWithAProblem()
    {
        var f = new FakeHost(TargetOs.Linux, "/home/u")
            .File("/usr/local/lib/node_modules/@anthropic-ai/claude-code/cli.js", "#!/usr/bin/env node\n")
            .Link("/usr/local/bin/claude", "../lib/node_modules/@anthropic-ai/claude-code/cli.js")
            .OnPath("/usr/local/bin");

        var c = await Claude(f);

        Assert.True(c.Found);
        Assert.Null(c.Version);
        Assert.Contains(c.Problems, p => p.Contains("Node.js"));
        Assert.Equal(["/usr/local/lib/node_modules/@anthropic-ai/claude-code"], c.ReadOnlyFolders);
        Assert.Empty(f.Probes);
    }

    [Fact]
    public async Task LinuxbrewCaskGrantsTheOneVersionFolder()
    {
        var f = new FakeHost(TargetOs.Linux, "/home/u")
            .File("/home/linuxbrew/.linuxbrew/Caskroom/claude-code/2.1.318/claude", FakeHost.Elf)
            .Link("/home/linuxbrew/.linuxbrew/bin/claude", "../Caskroom/claude-code/2.1.318/claude")
            .OnPath("/home/linuxbrew/.linuxbrew/bin");

        var c = await Claude(f);

        Assert.Equal("homebrew", c.InstallKind);
        Assert.Equal(["/home/linuxbrew/.linuxbrew/Caskroom/claude-code/2.1.318"], c.ReadOnlyFolders);
    }

    [Fact]
    public async Task LinuxClaudeOldLocalInstallInsideDotClaudeIsFollowedAndFlagged()
    {
        // claude migrate-installer (2025): ~/.claude/local/claude = exec wrapper; not on PATH (a shell alias ran it).
        var f = new FakeHost(TargetOs.Linux, "/home/u")
            .File("/home/u/.claude/local/claude", "#!/bin/bash\nexec \"/home/u/.claude/local/node_modules/.bin/claude\" \"$@\"\n")
            .Link("/home/u/.claude/local/node_modules/.bin/claude", "../@anthropic-ai/claude-code/cli.js")
            .File("/home/u/.claude/local/node_modules/@anthropic-ai/claude-code/cli.js", "#!/usr/bin/env node\n")
            .File("/home/u/.claude/.credentials.json", "{}")
            .File("/usr/bin/node", FakeHost.Elf)
            .OnPath("/usr/bin");

        var c = await Claude(f);

        Assert.True(c.Found);
        Assert.Equal("/home/u/.claude/local/claude", c.LauncherPath);
        Assert.Equal("npm", c.InstallKind);
        Assert.Equal(["/home/u/.claude/local/node_modules/@anthropic-ai/claude-code", "/usr/bin"], c.ReadOnlyFolders);
        Assert.Contains(c.Problems, p => p.Contains("not on your PATH"));
        Assert.Contains(c.Problems, p => p.Contains("old 'local' install"));
        NoConfigReads(f, c, "/home/u/.claude/local");
        Assert.DoesNotContain(f.Reads, r => r.EndsWith(".credentials.json"));
    }

    [Fact]
    public async Task LinuxClaudeFromADistroFolderIsOtherWithItsFolder()
    {
        var f = new FakeHost(TargetOs.Linux, "/home/u").File("/usr/bin/claude", FakeHost.Elf).OnPath("/usr/bin");
        var c = await Claude(f);
        Assert.Equal("other", c.InstallKind);
        Assert.Equal(["/usr/bin"], c.ReadOnlyFolders);
    }

    [Fact]
    public async Task AProgramDirectlyInHomeIsNeverGranted()
    {
        var f = new FakeHost(TargetOs.Linux, "/home/u").File("/home/u/claude", FakeHost.Elf).OnPath("/home/u");
        var c = await Claude(f);
        Assert.True(c.Found);
        Assert.Empty(c.ReadOnlyFolders);
        Assert.Contains(c.Problems, p => p.Contains("whole home folder"));
    }

    [Fact]
    public async Task NotInstalledShowsOnlyInstallHelp()
    {
        var f = new FakeHost(TargetOs.Linux, "/home/u").OnPath("/usr/bin");
        var c = await Claude(f);
        Assert.False(c.Found);
        Assert.Null(c.LauncherPath);
        Assert.Null(c.Version);
        Assert.Empty(c.ReadOnlyFolders);
        Assert.Contains("https://claude.ai/install.sh", c.InstallHelp);
        Assert.Contains("https://docs.claude.com", c.InstallHelp);
        Assert.Empty(f.Probes);
    }

    [Fact]
    public async Task ABrokenSymlinkIsNotFoundAndSaysWhy()
    {
        var f = new FakeHost(TargetOs.Linux, "/home/u")
            .Link("/home/u/.local/bin/claude", "/home/u/.local/share/claude/versions/2.1.0")
            .OnPath("/home/u/.local/bin");
        var c = await Claude(f);
        Assert.False(c.Found);
        Assert.Contains(c.Problems, p => p.Contains("missing"));
    }

    [Fact]
    public async Task AVersionProbeThatHangsOrFailsIsAProblemNotAFailure()
    {
        var f = new FakeHost(TargetOs.Linux, "/home/u").File("/usr/bin/claude", FakeHost.Elf).OnPath("/usr/bin");
        f.Respond = _ => new CliProbeResult(null, "", "", true);
        var c = await Claude(f);
        Assert.True(c.Found);
        Assert.Null(c.Version);
        Assert.Contains(c.Problems, p => p.Contains("did not answer"));

        f.Respond = _ => new CliProbeResult(3, "", "error: cannot open shared object\n", false);
        c = await Claude(f);
        Assert.Contains(c.Problems, p => p.Contains("exit code 3") && p.Contains("shared object"));
    }

    [Fact]
    public async Task ClaudeConfigDirFromTheEnvironmentIsReportedForBlocking()
    {
        var f = new FakeHost(TargetOs.Linux, "/home/u").OnPath("/usr/bin");
        f.Vars["CLAUDE_CONFIG_DIR"] = "/srv/claude-config";
        var c = await Claude(f);
        Assert.Contains("/srv/claude-config", c.ConfigFolders);
    }

    [Fact]
    public async Task LinuxGrokInstallerLayoutGrantsOnlyDownloads()
    {
        // This host: ~/.grok/bin/grok -> ../downloads/grok-1.0.77-linux-x86_64; install.sh also links
        // ~/.local/bin/grok -> ~/.grok/bin/grok when ~/.grok/bin is not on PATH.
        var f = new FakeHost(TargetOs.Linux, "/home/u")
            .File("/home/u/.grok/downloads/grok-1.0.77-linux-x86_64", FakeHost.Elf)
            .File("/home/u/.grok/downloads/grok-1.0.70-linux-x86_64", FakeHost.Elf)
            .Link("/home/u/.grok/bin/grok", "../downloads/grok-1.0.77-linux-x86_64")
            .Link("/home/u/.local/bin/grok", "/home/u/.grok/bin/grok")
            .File("/home/u/.grok/auth.json", "{}")
            .OnPath("/home/u/.local/bin", "/usr/bin");
        f.VersionOutput = "grok 1.0.77 (50ca579fa996)";

        var g = await Grok(f);

        Assert.True(g.Found);
        Assert.Equal("/home/u/.local/bin/grok", g.LauncherPath);
        Assert.Equal("/home/u/.grok/downloads/grok-1.0.77-linux-x86_64", g.ExecutablePath);
        Assert.Equal("native", g.InstallKind);
        Assert.Equal(["/home/u/.grok/downloads"], g.ReadOnlyFolders);
        Assert.Equal(["/home/u/.grok"], g.ConfigFolders);
        Assert.Equal("1.0.77", g.Version);
        Assert.Empty(g.Problems);
        var probe = Assert.Single(f.Probes);
        Assert.Equal(["--no-auto-update", "--version"], probe.Arguments);
        Assert.Equal("1", probe.Environment["GROK_DISABLE_AUTOUPDATER"]);
        NoConfigReads(f, g, "/home/u/.grok/downloads");
        Assert.DoesNotContain(f.Reads, r => r.EndsWith("auth.json"));
    }

    [Fact]
    public async Task TheCommunityGrokCliOnPathIsSkippedForXaisInstall()
    {
        var f = new FakeHost(TargetOs.Linux, "/home/u")
            .File("/usr/local/lib/node_modules/@vibe-kit/grok-cli/dist/index.js", "#!/usr/bin/env node\n")
            .Link("/usr/local/bin/grok", "../lib/node_modules/@vibe-kit/grok-cli/dist/index.js")
            .File("/home/u/.grok/downloads/grok-1.0.77-linux-x86_64", FakeHost.Elf)
            .Link("/home/u/.grok/bin/grok", "../downloads/grok-1.0.77-linux-x86_64")
            .OnPath("/usr/local/bin");
        f.VersionOutput = "grok 1.0.77 (x)";

        var g = await Grok(f);

        Assert.True(g.Found);
        Assert.Equal("/home/u/.grok/bin/grok", g.LauncherPath);
        Assert.Contains(g.Problems, p => p.Contains("community grok-cli"));
        Assert.Contains(g.Problems, p => p.Contains("not on your PATH"));
    }

    [Fact]
    public async Task OnlyTheCommunityGrokCliIsNotFound()
    {
        var f = new FakeHost(TargetOs.Linux, "/home/u")
            .File("/usr/local/lib/node_modules/@vibe-kit/grok-cli/dist/index.js", "#!/usr/bin/env node\n")
            .Link("/usr/local/bin/grok", "../lib/node_modules/@vibe-kit/grok-cli/dist/index.js")
            .OnPath("/usr/local/bin");
        var g = await Grok(f);
        Assert.False(g.Found);
        Assert.Contains(g.Problems, p => p.Contains("community grok-cli"));
        Assert.Contains("https://x.ai/cli/install.sh", g.InstallHelp);
    }

    [Fact]
    public async Task AGrokThatIsNotXaisSaysSoFromItsVersion()
    {
        var f = new FakeHost(TargetOs.Linux, "/home/u").File("/usr/bin/grok", FakeHost.Elf).OnPath("/usr/bin");
        f.VersionOutput = "0.9.1";
        var g = await Grok(f);
        Assert.Contains(g.Problems, p => p.Contains("does not look like xAI"));
    }

    // ================================================================ macOS

    [Fact]
    public async Task MacClaudeNativeInstaller()
    {
        var f = new FakeHost(TargetOs.MacOS, "/Users/u")
            .File("/Users/u/.local/share/claude/versions/2.1.318", FakeHost.MachO)
            .Link("/Users/u/.local/bin/claude", "/Users/u/.local/share/claude/versions/2.1.318")
            .OnPath("/opt/homebrew/bin", "/Users/u/.local/bin");
        var c = await Claude(f);
        Assert.Equal("native", c.InstallKind);
        Assert.Equal(["/Users/u/.local/share/claude/versions"], c.ReadOnlyFolders);
        Assert.Contains("brew install --cask claude-code", new ClaudeDetector().InstallHelp(TargetOs.MacOS));
    }

    [Fact]
    public async Task MacHomebrewCask()
    {
        // brew install --cask claude-code: /opt/homebrew/bin/claude -> /opt/homebrew/Caskroom/claude-code/<v>/claude
        var f = new FakeHost(TargetOs.MacOS, "/Users/u")
            .File("/opt/homebrew/Caskroom/claude-code/2.1.318/claude", FakeHost.MachO)
            .Link("/opt/homebrew/bin/claude", "/opt/homebrew/Caskroom/claude-code/2.1.318/claude")
            .OnPath("/opt/homebrew/bin");
        var c = await Claude(f);
        Assert.Equal("homebrew", c.InstallKind);
        Assert.Equal(["/opt/homebrew/Caskroom/claude-code/2.1.318"], c.ReadOnlyFolders);
    }

    [Fact]
    public async Task MacIntelHomebrewFormula()
    {
        var f = new FakeHost(TargetOs.MacOS, "/Users/u")
            .File("/usr/local/Cellar/claude-code/2.1.318/bin/claude", FakeHost.MachO)
            .Link("/usr/local/bin/claude", "../Cellar/claude-code/2.1.318/bin/claude")
            .OnPath("/usr/local/bin");
        var c = await Claude(f);
        Assert.Equal("homebrew", c.InstallKind);
        Assert.Equal(["/usr/local/Cellar/claude-code/2.1.318"], c.ReadOnlyFolders);
    }

    [Fact]
    public async Task MacNpmUnderHomebrewNodeGrantsPackageAndTheNodeCellar()
    {
        var f = new FakeHost(TargetOs.MacOS, "/Users/u")
            .File("/opt/homebrew/lib/node_modules/@anthropic-ai/claude-code/cli.js", "#!/usr/bin/env node\n")
            .Link("/opt/homebrew/bin/claude", "../lib/node_modules/@anthropic-ai/claude-code/cli.js")
            .File("/opt/homebrew/Cellar/node/22.11.0/bin/node", FakeHost.MachO)
            .Link("/opt/homebrew/bin/node", "../Cellar/node/22.11.0/bin/node")
            .OnPath("/opt/homebrew/bin");
        var c = await Claude(f);
        Assert.Equal("npm", c.InstallKind);
        Assert.Equal(["/opt/homebrew/lib/node_modules/@anthropic-ai/claude-code", "/opt/homebrew/Cellar/node/22.11.0/bin"], c.ReadOnlyFolders);
        Assert.Equal("/opt/homebrew/Cellar/node/22.11.0/bin/node", Assert.Single(f.Probes).FileName);
    }

    [Fact]
    public async Task MacGrokInstaller()
    {
        var f = new FakeHost(TargetOs.MacOS, "/Users/u")
            .File("/Users/u/.grok/downloads/grok-1.0.77-macos-aarch64", FakeHost.MachO)
            .Link("/Users/u/.grok/bin/grok", "../downloads/grok-1.0.77-macos-aarch64")
            .OnPath("/Users/u/.grok/bin");
        f.VersionOutput = "grok 1.0.77 (x) [stable]";
        var g = await Grok(f);
        Assert.Equal("native", g.InstallKind);
        Assert.Equal(["/Users/u/.grok/downloads"], g.ReadOnlyFolders);
        Assert.Equal("1.0.77", g.Version);
    }

    [Fact]
    public async Task MacPathsCompareCaseInsensitively()
    {
        var f = new FakeHost(TargetOs.MacOS, "/Users/u")
            .File("/Users/u/.local/share/claude/versions/2.1.318", FakeHost.MachO)
            .Link("/Users/u/.local/bin/claude", "/Users/u/.local/share/claude/versions/2.1.318")
            .OnPath("/Users/u/.local/bin");
        f.Vars["CLAUDE_CONFIG_DIR"] = "/USERS/U/.CLAUDE";
        var c = await Claude(f);
        Assert.Equal(2, c.ConfigFolders.Count); // the same folder, not reported twice
    }

    // ================================================================ Windows

    private static FakeHost Win() => new FakeHost(TargetOs.Windows, @"C:\Users\u")
        .OnPath(@"C:\Windows\system32", @"C:\Windows");

    [Fact]
    public async Task WindowsClaudeNativeInstallerGrantsLocalBinAndVersions()
    {
        // irm https://claude.ai/install.ps1 | iex: %USERPROFILE%\.local\bin\claude.exe (a plain file).
        var f = Win()
            .File(@"C:\Users\u\.local\bin\claude.exe", FakeHost.Pe)
            .Dir(@"C:\Users\u\.local\share\claude\versions")
            .OnPath(@"C:\Users\u\.local\bin");
        f.VersionOutput = "2.1.318 (Claude Code)";

        var c = await Claude(f);

        Assert.Equal(@"C:\Users\u\.local\bin\claude.exe", c.LauncherPath);
        Assert.Equal("native", c.InstallKind);
        Assert.Equal([@"C:\Users\u\.local\bin", @"C:\Users\u\.local\share\claude\versions"], c.ReadOnlyFolders);
        Assert.Equal([@"C:\Users\u\.claude", @"C:\Users\u\.claude.json"], c.ConfigFolders);
        Assert.Equal("2.1.318", c.Version);
        Assert.Contains("https://claude.ai/install.ps1", c.InstallHelp);
    }

    [Fact]
    public async Task WindowsClaudeNpmCmdShimToTheNativeExe()
    {
        // npm 7+ cmd-shim for a .exe bin: "%dp0%\node_modules\@anthropic-ai\claude-code\bin\claude.exe"   %*
        var f = Win()
            .File(@"C:\Users\u\AppData\Roaming\npm\claude.cmd",
                "@ECHO off\r\nGOTO start\r\n:find_dp0\r\nSET dp0=%~dp0\r\nEXIT /b\r\n:start\r\nSETLOCAL\r\nCALL :find_dp0\r\n\"%dp0%\\node_modules\\@anthropic-ai\\claude-code\\bin\\claude.exe\"   %*\r\n")
            .File(@"C:\Users\u\AppData\Roaming\npm\claude.ps1", "#!/usr/bin/env pwsh\n")
            .File(@"C:\Users\u\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe", FakeHost.Pe)
            .OnPath(@"C:\Users\u\AppData\Roaming\npm");

        var c = await Claude(f);

        Assert.Equal(@"C:\Users\u\AppData\Roaming\npm\claude.cmd", c.LauncherPath);
        Assert.Equal(@"C:\Users\u\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe", c.ExecutablePath);
        Assert.Equal("npm", c.InstallKind);
        Assert.Equal([@"C:\Users\u\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code"], c.ReadOnlyFolders);
        Assert.Equal(c.ExecutablePath, Assert.Single(f.Probes).FileName);
    }

    [Fact]
    public async Task WindowsClaudeNpmCmdShimToCliJsUsesNodeFromPath()
    {
        // Older npm cmd-shim for a .js bin, node from PATH (no node.exe beside the shim).
        var f = Win()
            .File(@"C:\Users\u\AppData\Roaming\npm\claude.cmd",
                "@IF EXIST \"%~dp0\\node.exe\" (\r\n  \"%~dp0\\node.exe\"  \"%~dp0\\node_modules\\@anthropic-ai\\claude-code\\cli.js\" %*\r\n) ELSE (\r\n  @SETLOCAL\r\n  @SET PATHEXT=%PATHEXT:;.JS;=;%\r\n  node  \"%~dp0\\node_modules\\@anthropic-ai\\claude-code\\cli.js\" %*\r\n)\r\n")
            .File(@"C:\Users\u\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js", "#!/usr/bin/env node\n")
            .File(@"C:\Program Files\nodejs\node.exe", FakeHost.Pe)
            .OnPath(@"C:\Program Files\nodejs", @"C:\Users\u\AppData\Roaming\npm");

        var c = await Claude(f);

        Assert.Equal("npm", c.InstallKind);
        Assert.Equal([@"C:\Users\u\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code", @"C:\Program Files\nodejs"], c.ReadOnlyFolders);
        var probe = Assert.Single(f.Probes);
        Assert.Equal(@"C:\Program Files\nodejs\node.exe", probe.FileName);
        Assert.Equal([@"C:\Users\u\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js", "--version"], probe.Arguments);
    }

    [Fact]
    public async Task WindowsWingetPortablePackageBehindTheLinksFolder()
    {
        const string pkg = @"C:\Users\u\AppData\Local\Microsoft\WinGet\Packages\Anthropic.ClaudeCode_Microsoft.Winget.Source_8wekyb3d8bbwe";
        var f = Win()
            .File(pkg + @"\claude.exe", FakeHost.Pe)
            .Link(@"C:\Users\u\AppData\Local\Microsoft\WinGet\Links\claude.exe", pkg + @"\claude.exe")
            .OnPath(@"C:\Users\u\AppData\Local\Microsoft\WinGet\Links");

        var c = await Claude(f);

        Assert.Equal("winget", c.InstallKind);
        Assert.Equal(pkg + @"\claude.exe", c.ExecutablePath);
        Assert.Equal([pkg], c.ReadOnlyFolders);
    }

    [Fact]
    public async Task WindowsKnownLocationsAreFoundWhenPathMissesThem()
    {
        var f = Win().File(@"C:\Users\u\.local\bin\claude.exe", FakeHost.Pe);
        var c = await Claude(f);
        Assert.True(c.Found);
        Assert.Contains(c.Problems, p => p.Contains("not on your PATH"));
    }

    [Fact]
    public async Task WindowsGrokInstallerCopyGrantsBinAndBundledGit()
    {
        // irm https://x.ai/cli/install.ps1 | iex: grok.exe COPIED to ~\.grok\bin (grove*.exe beside it), MinGit in
        // %LOCALAPPDATA%\grok\git\<v>.
        var f = Win()
            .File(@"C:\Users\u\.grok\bin\grok.exe", FakeHost.Pe)
            .File(@"C:\Users\u\.grok\bin\grove.exe", FakeHost.Pe)
            .File(@"C:\Users\u\.grok\downloads\grok-windows-x86_64.exe", FakeHost.Pe)
            .Dir(@"C:\Users\u\AppData\Local\grok\git\2.47.1")
            .File(@"C:\Users\u\.grok\auth.json", "{}")
            .OnPath(@"C:\Users\u\.grok\bin");
        f.Vars["LOCALAPPDATA"] = @"C:\Users\u\AppData\Local";
        f.VersionOutput = "grok 1.0.77 (x) [stable]";

        var g = await Grok(f);

        Assert.Equal(@"C:\Users\u\.grok\bin\grok.exe", g.ExecutablePath);
        Assert.Equal("native", g.InstallKind);
        Assert.Equal([@"C:\Users\u\.grok\bin", @"C:\Users\u\AppData\Local\grok\git"], g.ReadOnlyFolders);
        Assert.Equal([@"C:\Users\u\.grok"], g.ConfigFolders);
        Assert.Contains("https://x.ai/cli/install.ps1", g.InstallHelp);
        NoConfigReads(f, g, @"C:\Users\u\.grok\bin");
    }

    [Fact]
    public async Task WindowsVerbatimLinkTargetsAreUnderstood()
    {
        var f = Win()
            .File(@"D:\tools\claude\claude.exe", FakeHost.Pe)
            .Link(@"C:\Users\u\bin\claude.exe", @"\\?\D:\tools\claude\claude.exe")
            .OnPath(@"C:\Users\u\bin");
        var c = await Claude(f);
        Assert.Equal(@"D:\tools\claude\claude.exe", c.ExecutablePath);
        Assert.Equal("other", c.InstallKind);
        Assert.Equal([@"D:\tools\claude"], c.ReadOnlyFolders);
    }

    // ================================================================ registry

    [Fact]
    public async Task TheRegistryDetectsEveryCliFreshOnEachCall()
    {
        var f = new FakeHost(TargetOs.Linux, "/home/u").OnPath("/usr/bin");
        var registry = new CliDetectorRegistry(CliDetectorRegistry.Default, f.Host);

        var first = await registry.DetectAllAsync(Ct);
        Assert.Equal(["claude", "grok", "codex", "copilot", "antigravity"], first.Select(d => d.CliId));
        Assert.All(first, d => Assert.False(d.Found));

        f.File("/usr/bin/claude", FakeHost.Elf); // installed while the app is open
        var second = await registry.DetectAllAsync(Ct);
        Assert.True(second.Single(d => d.CliId == "claude").Found);
    }

    [Fact]
    public async Task DetectingOneCliProbesOnlyThatCli()
    {
        var f = new FakeHost(TargetOs.Linux, "/home/u").OnPath("/usr/bin");
        f.File("/usr/bin/claude", FakeHost.Elf).File("/usr/bin/grok", FakeHost.Elf).File("/usr/bin/codex", FakeHost.Elf);
        var registry = new CliDetectorRegistry(CliDetectorRegistry.Default, f.Host);

        var one = await registry.DetectOneAsync("grok", Ct);
        Assert.Equal(("grok", true), (one!.CliId, one.Found));
        Assert.NotEmpty(f.Probes);
        Assert.All(f.Probes, p => Assert.Contains("grok", p.FileName));
        Assert.Null(await registry.DetectOneAsync("nobody", Ct));
    }
}
