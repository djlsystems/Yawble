using System.Text;
using Harness.Pty;

namespace Harness.Tests;

/// <summary>
/// A terminal the Concierge runs in must start the way a real one does: output processing on, so
/// a bare newline also returns to the left edge. With `-opost`, the Copilot Concierge draws a
/// staircase (each line starting where the last ended); Claude, Codex and Grok send the carriage
/// return themselves and do not show it.
/// </summary>
public sealed class PtyTerminalModeTests
{
    [Fact]
    public async Task A_pty_child_starts_with_output_processing_on()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A Unix PTY.");
        var ct = TestContext.Current.CancellationToken;

        var output = new StringBuilder();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = await new PortaPtyEngine().SpawnAsync(
            new PtySpec("", Path.GetTempPath(), Argv: ["/bin/sh", "-c", "stty -a"]), ct);
        session.Output += bytes => { lock (output) output.Append(Encoding.UTF8.GetString(bytes)); };
        session.Exited += code => exited.TrySetResult(code);
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        string modes;
        lock (output) modes = output.ToString();
        var flags = modes.Split([' ', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.True(flags.Contains("opost") && flags.Contains("onlcr"), modes);
    }

    /// <summary>The shell that sets the modes hands the argv on untouched: a system prompt with
    /// spaces, quotes and a dollar sign arrives as the one element it was.</summary>
    [Fact]
    public async Task The_mode_setting_shell_passes_every_argument_through_whole()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A Unix PTY.");
        var ct = TestContext.Current.CancellationToken;
        const string prompt = "You are \"the Concierge\"; say $HOME and 'hi'  twice";

        var output = new StringBuilder();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = await new PortaPtyEngine().SpawnAsync(
            new PtySpec("", Path.GetTempPath(), Argv: ["/bin/sh", "-c", "printf '<%s>' \"$#\" \"$1\"", "zero", prompt]), ct);
        session.Output += bytes => { lock (output) output.Append(Encoding.UTF8.GetString(bytes)); };
        session.Exited += code => exited.TrySetResult(code);
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        lock (output) Assert.Contains($"<1><{prompt}>", output.ToString());
    }
}
