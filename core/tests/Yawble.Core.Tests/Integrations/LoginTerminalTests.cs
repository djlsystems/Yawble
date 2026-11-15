using Yawble.Core.Integrations;
using Yawble.Core.Policy;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>
/// The sign-in terminal on each OS. Pure: every case builds the start for an explicit OS with a fake PATH lookup, so all
/// of them run on any host and no real terminal ever opens.
/// </summary>
public class LoginTerminalTests
{
    private const string WinHome = @"C:\Users\someone";
    private const string UnixHome = "/home/someone";
    private const string Temp = "/tmp/yawble-test";

    private static string? NoTerminals(string _) => null;
    private static Func<string, string?> Only(params string[] names) => n => names.Contains(n) ? "/usr/bin/" + n : null;

    /// <summary>A gh path, its folder and the home, as the host OS writes them.</summary>
    private static (string Path, string Dir, string Home) HostGh() => TargetOsExtensions.Host == TargetOs.Windows
        ? (@"C:\Program Files\GitHub CLI\gh.exe", @"C:\Program Files\GitHub CLI", WinHome)
        : ("/opt/github cli/bin/gh", "/opt/github cli/bin", UnixHome);

    [Fact]
    public void KnownCli_OpensItsLoginCommand_InItsFolder()
    {
        var (path, dir, home) = HostGh();
        var start = LoginTerminal.Build(path, CliCatalog.Of("gh")!, home, TargetOsExtensions.Host, Only("xterm"), Temp);
        Assert.Equal(dir, start.WorkingDirectory);
        Assert.Equal("gh auth login", start.Command);
        switch (TargetOsExtensions.Host)
        {
            case TargetOs.Windows:
                Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), start.FileName);
                Assert.Contains($@"/D ""{dir}""", start.Arguments);
                Assert.Contains(@"/k """"C:\Program Files\GitHub CLI\gh.exe"" auth login""", start.Arguments);
                break;
            case TargetOs.MacOS:
                Assert.Equal(LoginTerminal.MacOpen, start.FileName);
                Assert.Contains($"cd -- '{dir}'", start.Script!.Text);
                Assert.Contains($"'{path}' 'auth' 'login'", start.Script.Text);
                break;
            default:
                Assert.Equal("/usr/bin/xterm", start.FileName);
                Assert.Equal(["-e", "sh", "-c"], start.ArgumentList.Take(3));
                Assert.Equal([dir, path, "auth", "login"], start.ArgumentList.Skip(4));
                break;
        }
    }

    [Fact]
    public void UnknownCli_OpensAPlainTerminal()
    {
        var (path, dir, home) = HostGh();
        var start = LoginTerminal.Build(path, CliCatalog.Generic("foo"), home, TargetOsExtensions.Host, Only("xterm"), Temp);
        Assert.Null(start.Command);
        Assert.Null(start.Script);
        Assert.Equal(dir, start.WorkingDirectory);
        switch (TargetOsExtensions.Host)
        {
            case TargetOs.Windows:
                Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), start.FileName);
                Assert.DoesNotContain("/k", start.Arguments);
                Assert.Contains($@"/D ""{dir}""", start.Arguments);
                break;
            case TargetOs.MacOS:
                Assert.Equal(["-a", "Terminal", dir], start.ArgumentList);
                break;
            default:
                Assert.Empty(start.ArgumentList);
                break;
        }
    }

    // ---- Windows -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Windows_KnownCli_RunsItsLoginInCmd_OnAnyHost()
    {
        var gh = CliCatalog.Of("gh")!;
        var start = LoginTerminal.Build(@"C:\Program Files\GitHub CLI\gh.exe", gh, WinHome, TargetOs.Windows, NoTerminals, Temp);
        Assert.EndsWith("cmd.exe", start.FileName);
        Assert.Equal($@"/c start ""{gh.Label} sign-in"" /D ""C:\Program Files\GitHub CLI"" cmd.exe /k """"C:\Program Files\GitHub CLI\gh.exe"" auth login""", start.Arguments);
        Assert.Equal(@"C:\Program Files\GitHub CLI", start.WorkingDirectory);
        Assert.Empty(start.ArgumentList);
    }

    [Fact]
    public void NoPath_StartsInHome()
    {
        var start = LoginTerminal.Build(null, CliCatalog.Generic("foo"), WinHome, TargetOs.Windows, NoTerminals, Temp);
        Assert.Equal(WinHome, start.WorkingDirectory);
        Assert.Contains($@"/D ""{WinHome}""", start.Arguments);
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("&")]
    [InlineData("|")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("^")]
    [InlineData("%")]
    public void APathWithShellCharactersIsRefused(string bad)
    {
        var e = Assert.Throws<ArgumentException>(() => LoginTerminal.Build($@"C:\Program Files\a{bad}b\gh.exe", CliCatalog.Of("gh")!, WinHome, TargetOs.Windows, NoTerminals, Temp));
        Assert.Contains("cannot be opened in a terminal safely", e.Message);
    }

    [Theory]
    [InlineData(TargetOs.MacOS)]
    [InlineData(TargetOs.Linux)]
    public void Unix_APathWithANewlineIsRefused(TargetOs os)
    {
        var e = Assert.Throws<ArgumentException>(() => LoginTerminal.Build("/opt/a\nrm -rf ~/gh", CliCatalog.Of("gh")!, UnixHome, os, Only("xterm"), Temp));
        Assert.Contains("cannot be opened in a terminal safely", e.Message);
    }

    // ---- macOS ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Mac_KnownCli_OpensTerminalOnAOneTimeCommandScript()
    {
        var start = LoginTerminal.Build("/opt/homebrew/bin/gh", CliCatalog.Of("gh")!, UnixHome, TargetOs.MacOS, NoTerminals, Temp);
        Assert.Equal("/usr/bin/open", start.FileName);
        var script = start.Script!;
        Assert.StartsWith(Temp, script.Path);
        Assert.EndsWith(".command", script.Path);
        Assert.Equal(["-a", "Terminal", script.Path], start.ArgumentList);
        var lines = script.Text.Split('\n');
        Assert.Equal("#!/bin/sh", lines[0]);
        Assert.Equal("rm -f -- \"$0\"", lines[1]);
        Assert.Equal("cd -- '/opt/homebrew/bin' || exit 1", lines[2]);
        Assert.Equal("'/opt/homebrew/bin/gh' 'auth' 'login'", lines[3]);
        Assert.StartsWith("exec ", lines[4]);
        Assert.Equal("/opt/homebrew/bin", start.WorkingDirectory);
        Assert.Equal("gh auth login", start.Command);
    }

    [Fact]
    public void Mac_AQuoteInThePath_StaysInsideItsWord()
    {
        var start = LoginTerminal.Build("/Users/o'brien/bin/gh", CliCatalog.Of("gh")!, UnixHome, TargetOs.MacOS, NoTerminals, Temp);
        Assert.Contains(@"cd -- '/Users/o'\''brien/bin' || exit 1", start.Script!.Text);
        Assert.Contains(@"'/Users/o'\''brien/bin/gh' 'auth' 'login'", start.Script.Text);
    }

    [Fact]
    public void Mac_UnknownCli_OpensTerminalInItsFolder_WithNoScript()
    {
        var start = LoginTerminal.Build("/usr/local/bin/foo", CliCatalog.Generic("foo"), UnixHome, TargetOs.MacOS, NoTerminals, Temp);
        Assert.Equal(["-a", "Terminal", "/usr/local/bin"], start.ArgumentList);
        Assert.Null(start.Script);
        Assert.Null(start.Command);
    }

    // ---- Linux ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Linux_TakesTheFirstTerminalFound_InOrder()
    {
        var gh = CliCatalog.Of("gh")!;
        Assert.Equal("/usr/bin/x-terminal-emulator", LoginTerminal.Build("/usr/bin/gh", gh, UnixHome, TargetOs.Linux, Only("xterm", "x-terminal-emulator", "konsole"), Temp).FileName);
        Assert.Equal("/usr/bin/gnome-terminal", LoginTerminal.Build("/usr/bin/gh", gh, UnixHome, TargetOs.Linux, Only("xterm", "gnome-terminal", "konsole"), Temp).FileName);
        Assert.Equal("/usr/bin/konsole", LoginTerminal.Build("/usr/bin/gh", gh, UnixHome, TargetOs.Linux, Only("xterm", "konsole"), Temp).FileName);
        Assert.Equal("/usr/bin/xterm", LoginTerminal.Build("/usr/bin/gh", gh, UnixHome, TargetOs.Linux, Only("xterm"), Temp).FileName);
    }

    [Fact]
    public void Linux_EachTerminal_RunsTheLoginInTheFolder_WordForWord()
    {
        var gh = CliCatalog.Of("gh")!;
        var path = "/home/someone/my tools/gh";
        var dir = "/home/someone/my tools";
        var gnome = LoginTerminal.Build(path, gh, UnixHome, TargetOs.Linux, Only("gnome-terminal"), Temp).ArgumentList;
        Assert.Equal($"--working-directory={dir}", gnome[0]);
        Assert.Equal(["--", "sh", "-c"], gnome.Skip(1).Take(3));
        Assert.Equal([dir, path, "auth", "login"], gnome.Skip(5));

        var konsole = LoginTerminal.Build(path, gh, UnixHome, TargetOs.Linux, Only("konsole"), Temp).ArgumentList;
        Assert.Equal(["--workdir", dir, "-e", "sh", "-c"], konsole.Take(5));
        Assert.Equal([dir, path, "auth", "login"], konsole.Skip(6));

        var x = LoginTerminal.Build(path, gh, UnixHome, TargetOs.Linux, Only("x-terminal-emulator"), Temp);
        Assert.Equal(["-e", "sh", "-c"], x.ArgumentList.Take(3));
        Assert.Equal(dir, x.WorkingDirectory);
        // the shell script is fixed text: the folder and the program are its arguments, never part of it
        Assert.DoesNotContain("my tools", x.ArgumentList[3]);
        Assert.Contains("cd -- \"$0\"", x.ArgumentList[3]);
    }

    [Fact]
    public void Linux_UnknownCli_OpensAPlainTerminalInItsFolder()
    {
        var start = LoginTerminal.Build("/opt/foo/foo", CliCatalog.Generic("foo"), UnixHome, TargetOs.Linux, Only("gnome-terminal"), Temp);
        Assert.Equal(["--working-directory=/opt/foo"], start.ArgumentList);
        Assert.Null(start.Command);
    }

    [Fact]
    public void Linux_NoTerminal_SaysSoAndShowsTheCommand()
    {
        var e = Assert.Throws<NoTerminalException>(() => LoginTerminal.Build("/usr/bin/gh", CliCatalog.Of("gh")!, UnixHome, TargetOs.Linux, NoTerminals, Temp));
        Assert.Contains("No terminal program was found", e.Message);
        Assert.Contains("x-terminal-emulator, gnome-terminal, konsole, xterm", e.Message);
        Assert.Contains("run: gh auth login", e.Message);
        Assert.Contains("/usr/bin", e.Message);
    }

    [Fact]
    public void Linux_NoPath_StartsInHome()
    {
        var start = LoginTerminal.Build(null, CliCatalog.Of("gh")!, UnixHome, TargetOs.Linux, Only("xterm"), Temp);
        Assert.Equal(UnixHome, start.WorkingDirectory);
        Assert.Null(start.Command); // no detected program: nothing to run, a plain terminal
    }
}
