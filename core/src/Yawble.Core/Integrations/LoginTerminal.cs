using Yawble.Core.Policy;

namespace Yawble.Core.Integrations;

/// <summary>
/// What to start: a program and its arguments, the folder, and the command shown to the person. On Windows
/// <see cref="Arguments"/> is one line in cmd.exe's own quoting (not CommandLineToArgvW's); on macOS and Linux
/// <see cref="ArgumentList"/> is passed word for word with no shell in between. <see cref="Script"/>, when set, is a
/// file to write (owner-only, executable) before starting: macOS's Terminal runs a .command file.
/// </summary>
public sealed record TerminalStart(string FileName, string Arguments, string WorkingDirectory, string? Command)
{
    public IReadOnlyList<string> ArgumentList { get; init; } = [];
    public TerminalScript? Script { get; init; }
}

public sealed record TerminalScript(string Path, string Text);

/// <summary>No terminal program could be found on this computer: the message says so and shows the command to run.</summary>
public sealed class NoTerminalException(string message) : Exception(message);

/// <summary>
/// A terminal window for the person to sign a CLI in (its login is a floor: an agent never runs it). Pure: it builds the
/// command line from catalog values and the stored, detected path only, never from anything an agent supplied.
/// Windows: cmd.exe. macOS: Terminal.app through `open -a Terminal` (a generated .command script for a login).
/// Linux: the first of x-terminal-emulator, gnome-terminal, konsole and xterm found on the PATH.
/// </summary>
public static class LoginTerminal
{
    private static readonly char[] Unsafe = ['"', '&', '|', '<', '>', '^', '%'];

    /// <summary>The Linux terminals tried, in order.</summary>
    public static readonly IReadOnlyList<string> LinuxTerminals = ["x-terminal-emulator", "gnome-terminal", "konsole", "xterm"];

    /// <summary>macOS's own `open`, by full path.</summary>
    public const string MacOpen = "/usr/bin/open";

    /// <summary>Runs the login in the folder, then leaves the person in their own shell (the window stays open, as cmd /k does).</summary>
    private const string LinuxShell = "cd -- \"$0\" || exit 1; \"$@\"; exec \"${SHELL:-/bin/sh}\" -i";

    /// <summary>The system's own cmd.exe by full path (never one a PATH or the working folder could put first).</summary>
    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>The terminal for this computer's OS.</summary>
    public static TerminalStart Build(string? path, CliEntry entry, string home) =>
        Build(path, entry, home, TargetOsExtensions.Host, CliRunner.Find, Path.GetTempPath());

    /// <param name="find">Finds a program on the PATH (Linux terminals); a fake in tests.</param>
    /// <param name="temp">Where macOS's .command script is written.</param>
    public static TerminalStart Build(string? path, CliEntry entry, string home, TargetOs os, Func<string, string?> find, string temp)
    {
        var dir = path is null ? home : DirectoryOf(path, os) is { Length: > 0 } d ? d : home;
        foreach (var value in new[] { path, dir })
            if (value is not null && (value.Any(char.IsControl) || os == TargetOs.Windows && value.IndexOfAny(Unsafe) >= 0))
                throw new ArgumentException("that path cannot be opened in a terminal safely");
        var login = entry.LoginArgs is { Length: > 0 } words && path is not null ? words : null;
        var command = login is null ? null : $"{entry.Name} {string.Join(' ', login)}";
        return os switch
        {
            TargetOs.Windows => Windows(path, entry, dir, login, command),
            TargetOs.MacOS => Mac(path, entry, dir, login, command, temp),
            _ => Linux(path, entry, dir, login, command, find),
        };
    }

    private static TerminalStart Windows(string? path, CliEntry entry, string dir, string[]? login, string? command)
    {
        // cmd /c start "<title>" /D "<dir>" cmd.exe /k ""<path>" <args>"  (the doubled quotes: /k strips the outer pair)
        if (login is not null)
            return new TerminalStart(Cmd, $"/c start \"{entry.Label} sign-in\" /D \"{dir}\" cmd.exe /k \"\"{path}\" {string.Join(' ', login)}\"", dir, command);
        return new TerminalStart(Cmd, $"/c start \"{entry.Label}\" /D \"{dir}\" cmd.exe", dir, null);
    }

    private static TerminalStart Mac(string? path, CliEntry entry, string dir, string[]? login, string? command, string temp)
    {
        if (login is null)
            return new TerminalStart(MacOpen, "", dir, null) { ArgumentList = ["-a", "Terminal", dir] };
        var script = System.IO.Path.Combine(temp, $"yawble-{Safe(entry.Name)}-sign-in-{Guid.NewGuid():N}.command");
        var text = string.Join('\n',
            "#!/bin/sh",
            "rm -f -- \"$0\"", // a one-time script: gone once Terminal has read it
            $"cd -- {Quote(dir)} || exit 1",
            $"{Quote(path!)} {string.Join(' ', login.Select(Quote))}",
            "exec \"${SHELL:-/bin/zsh}\" -l",
            "");
        return new TerminalStart(MacOpen, "", dir, command) { ArgumentList = ["-a", "Terminal", script], Script = new TerminalScript(script, text) };
    }

    private static TerminalStart Linux(string? path, CliEntry entry, string dir, string[]? login, string? command, Func<string, string?> find)
    {
        var (name, terminal) = LinuxTerminals.Select(t => (t, find(t))).FirstOrDefault(t => t.Item2 is { Length: > 0 });
        if (terminal is null)
            throw new NoTerminalException(
                $"No terminal program was found on this computer (looked for {string.Join(", ", LinuxTerminals)}). " +
                (command is null ? $"Open your own terminal in {dir} to use {entry.Name}." : $"Open your own terminal in {dir} and run: {command}"));
        // The folder and words go in as arguments of a fixed shell script, never spliced into its text.
        var args = new List<string>();
        if (name == "gnome-terminal") args.Add($"--working-directory={dir}");
        else if (name == "konsole") args.AddRange(["--workdir", dir]);
        // x-terminal-emulator and xterm have no folder option: the working directory is the folder
        if (login is not null)
        {
            args.Add(name == "gnome-terminal" ? "--" : "-e");
            args.AddRange(["sh", "-c", LinuxShell, dir, path!, .. login]);
        }
        return new TerminalStart(terminal, "", dir, command) { ArgumentList = args };
    }

    /// <summary>The folder of a path as the target OS writes it (so a Windows path splits the same on any host).</summary>
    private static string DirectoryOf(string path, TargetOs os)
    {
        var cut = os == TargetOs.Windows ? path.LastIndexOfAny(['\\', '/']) : path.LastIndexOf('/');
        if (cut < 0) return "";
        if (cut == 0) return path[..1];                                        // "/gh" is in "/"
        if (os == TargetOs.Windows && cut == 2 && path[1] == ':') return path[..3]; // "C:\gh.exe" is in "C:\"
        return path[..cut];
    }

    /// <summary>A POSIX shell word in single quotes: nothing inside is special except the quote itself.</summary>
    internal static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static string Safe(string name) => new(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
}
