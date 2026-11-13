using System.Text;
using Yawble.Core.Policy;

namespace Yawble.Core.Sandboxing;

/// <summary>
/// Builds the sandbox request's <c>process.commandLine</c> from an argument vector. Bubblewrap and Seatbelt run it
/// with <c>sh -c</c> (microsoft/mxc bwrap_command.rs COMMAND_TAIL), so POSIX shell quoting; ProcessContainer passes it to
/// CreateProcess, so the MSVC argv rules. Every argument is quoted so no value (a prompt, a model name) can
/// add a shell word or an option.
/// </summary>
public static class CommandLine
{
    public static string Join(TargetOs os, IEnumerable<string> argv) =>
        string.Join(' ', argv.Select(a => os == TargetOs.Windows ? QuoteWindows(a) : QuotePosix(a)));

    public static string QuotePosix(string arg)
    {
        if (arg.Length > 0 && arg.All(c => char.IsAsciiLetterOrDigit(c) || "_@%+=:,./-".Contains(c))) return arg;
        return "'" + arg.Replace("'", "'\\''") + "'";
    }

    public static string QuoteWindows(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
