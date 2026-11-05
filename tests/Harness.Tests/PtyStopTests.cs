using Harness.Pty;

namespace Harness.Tests;

/// <summary>
/// STOPPING A TERMINAL WHOSE CLI IGNORES SIGHUP. Porta.Pty's Kill sends SIGHUP only, and the pump's
/// blocking read of the PTY returns only when the child's side closes, so a child that ignores SIGHUP
/// kept a stop waiting forever: its session sweep, which runs after the stop, was never reached. A
/// child ignores SIGHUP when it says so itself, or when it inherits SIG_IGN, as everything does under
/// a worker (or a test run) started with <c>nohup</c>. Red before the SIGKILL fallback: the dispose never returned.
/// </summary>
public sealed class PtyStopTests
{
    [Fact]
    public async Task Stopping_a_terminal_whose_cli_ignores_sighup_ends_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "A Unix PTY and /proc.");
        var ct = TestContext.Current.CancellationToken;

        var output = new System.Text.StringBuilder();
        var session = await new PortaPtyEngine().SpawnAsync(
            new PtySpec("", Path.GetTempPath(), Argv: ["/bin/bash", "-c", "trap '' HUP; echo IGNORING; exec sleep 120"]), ct);
        var pid = ((IPtyProcess)session).ProcessId!.Value;
        try
        {
            session.Output += bytes => { lock (output) output.Append(System.Text.Encoding.UTF8.GetString(bytes)); };
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (output) if (output.ToString().Contains("IGNORING", StringComparison.Ordinal)) break;
                await Task.Delay(20, ct);
            }

            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);

            Assert.False(Alive(pid), $"pid {pid} outlived its terminal's stop");
        }
        finally
        {
            try { System.Diagnostics.Process.GetProcessById(pid).Kill(); } catch (ArgumentException) { } catch (InvalidOperationException) { }
        }
    }

    private static bool Alive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)] != 'Z';
        }
        catch (IOException)
        {
            return false;
        }
    }
}
