using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// A RUN WHOSE PROGRAM CRASHED, IN ITS OWN WORDS. A run that ended by a signal, or exited non-zero
/// with no usable output, and that nothing else classed, fails <see cref="FailureClasses.Crashed"/>
/// with the last lines it wrote to stderr - bounded, and redacted by <see cref="DiagnosticRedaction"/>
/// - rather than as a failure nobody could explain. A program that aborts usually says why on
/// stderr; that sentence is the one a person needs on the card. Generic over every agent CLI: it
/// reads only the exit code and stderr.
/// </summary>
public static class AgentCrash
{
    /// <summary>At most this many of stderr's last lines are carried.</summary>
    public const int TailLines = 20;

    /// <summary>And at most this many characters of them.</summary>
    public const int TailChars = 2_000;

    /// <summary>
    /// Whether a run that exited <paramref name="exitCode"/> with <paramref name="stdout"/> crashed:
    /// ended by a signal (an exit code of 128 + the signal, as the process API and every shell report
    /// it), or exited non-zero having written nothing to stdout.
    /// </summary>
    public static bool Crashed(int exitCode, string stdout) =>
        exitCode != 0 && (Signal(exitCode) is not null || string.IsNullOrWhiteSpace(stdout));

    /// <summary>The signal an exit code of 128 + N stands for, or null.</summary>
    public static int? Signal(int exitCode) => exitCode is > 128 and <= 128 + 64 ? exitCode - 128 : null;

    /// <summary>
    /// The last lines of <paramref name="stderr"/>, redacted first (so a cut cannot split a secret
    /// past the redactor), then bounded to <see cref="TailLines"/> lines and <see cref="TailChars"/>
    /// characters. Empty when it wrote nothing.
    /// </summary>
    public static string StderrTail(string? stderr)
    {
        var redacted = DiagnosticRedaction.RedactWithoutLimit(stderr);
        if (string.IsNullOrWhiteSpace(redacted)) return string.Empty;

        var lines = redacted.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd().Split('\n');
        var tail = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - TailLines)));
        return tail.Length <= TailChars ? tail : "..." + tail[^(TailChars - 3)..];
    }

    /// <summary>
    /// The run's error: how it ended, the last lines of its stderr, and - when the run had a memory
    /// limit - that limit in MB, how it was applied, and the setting that moves it. With
    /// <see cref="RunMemoryMechanism.None"/> nothing was applied, so a figure in hand is not named.
    /// </summary>
    public static string Sentence(int exitCode, string? stderr, RunMemoryLimit? limit, RunMemoryMechanism mechanism)
    {
        var ended = Signal(exitCode) is { } signal
            ? $"The agent's program ended by signal {signal}{SignalName(signal)} (exit {exitCode}) without finishing."
            : $"The agent's program exited {exitCode} without an answer.";

        var tail = StderrTail(stderr);
        var said = tail.Length == 0
            ? " It wrote nothing to stderr."
            : $" The last of what it wrote to stderr:\n{tail}\n";

        var memory = mechanism != RunMemoryMechanism.None && limit?.Mb is { } mb
            ? $" This run had a memory limit of {mb} MB "
              + (mechanism == RunMemoryMechanism.Cgroup ? "for the whole run" : "on each of its processes")
              + $" ({limit.Source}); a program that cannot start or work inside it can end like this. If that is the "
              + $"cause, raise {SettingNames.RunsMemoryLimitMb} in the Tenant Settings."
            : string.Empty;

        return ended + said + memory + " Classed crashed, not an agent fault.";
    }

    private static string SignalName(int signal) => signal switch
    {
        4 => " (SIGILL)",
        5 => " (SIGTRAP)",
        6 => " (SIGABRT)",
        7 => " (SIGBUS)",
        8 => " (SIGFPE)",
        9 => " (SIGKILL)",
        11 => " (SIGSEGV)",
        13 => " (SIGPIPE)",
        15 => " (SIGTERM)",
        _ => string.Empty,
    };
}
