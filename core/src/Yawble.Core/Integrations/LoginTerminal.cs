namespace Yawble.Core.Integrations;

/// <summary>What to start: a program, its one-line arguments (cmd.exe's own quoting, not CommandLineToArgvW's), the folder, and the command shown to the person.</summary>
public sealed record TerminalStart(string FileName, string Arguments, string WorkingDirectory, string? Command);

/// <summary>
/// A terminal window for the person to sign a CLI in (its login is a floor: an agent never runs it). Pure: it builds the
/// command line from catalog values and the stored, detected path only, never from anything an agent supplied.
/// </summary>
public static class LoginTerminal
{
    private static readonly char[] Unsafe = ['"', '&', '|', '<', '>', '^', '%'];

    /// <summary>The system's own cmd.exe by full path (never one a PATH or the working folder could put first).</summary>
    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    public static TerminalStart Build(string? path, CliEntry entry, string home)
    {
        var dir = path is null ? home : Path.GetDirectoryName(path) is { Length: > 0 } d ? d : home;
        foreach (var value in new[] { path, dir })
            if (value is not null && value.IndexOfAny(Unsafe) >= 0)
                throw new ArgumentException("that path cannot be opened in a terminal safely");
        // cmd /c start "<title>" /D "<dir>" cmd.exe /k ""<path>" <args>"  (the doubled quotes: /k strips the outer pair)
        if (entry.LoginArgs is { Length: > 0 } login && path is not null)
        {
            var words = string.Join(' ', login);
            return new TerminalStart(Cmd, $"/c start \"{entry.Label} sign-in\" /D \"{dir}\" cmd.exe /k \"\"{path}\" {words}\"", dir, $"{entry.Name} {words}");
        }
        return new TerminalStart(Cmd, $"/c start \"{entry.Label}\" /D \"{dir}\" cmd.exe", dir, null);
    }
}
