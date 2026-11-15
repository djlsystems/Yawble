using Yawble.Core.Logs;
using Xunit;

namespace Yawble.Core.Tests.Logs;

/// <summary>The core's shutdown line, and the shell's log kept and listed beside the core's.</summary>
public class CoreStopTests
{
    [Fact]
    public void TheFirstWayOut_WritesCoreStoppingWithItsReason_Once()
    {
        var lines = new List<string>();
        var stop = new CoreStop(lines.Add);
        Assert.Null(stop.Reason);
        Assert.False(stop.Token.IsCancellationRequested);

        stop.Request("the window closed its connection (the app is quitting)");
        stop.Request("the process was asked to end (SIGTERM or the system shutting down)"); // the ProcessExit that follows

        Assert.True(stop.Token.IsCancellationRequested);
        Assert.Equal(["core stopping: the window closed its connection (the app is quitting)"], lines);
        Assert.Equal("the window closed its connection (the app is quitting)", stop.Reason);
    }

    [Fact]
    public void ALogThatThrows_StillStopsTheCore()
    {
        var stop = new CoreStop(_ => throw new IOException("disk full"));
        stop.Request("Ctrl+C");
        Assert.True(stop.Token.IsCancellationRequested);
    }

    [Fact]
    public void TheLineGoesToTheCoreLog_ThroughTheDiagnosticLog()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-stop-").FullName;
        try
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 11, 14, 9, 30, 0, TimeSpan.Zero));
            var diag = new DiagnosticLog(dir, "core", clock);
            new CoreStop(diag.Write).Request("Ctrl+C");
            var text = File.ReadAllText(Path.Combine(dir, "logs", "core-2026-11-14.log"));
            Assert.EndsWith("\tcore\tcore stopping: Ctrl+C\n", text);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheShellsLog_IsListedWithTheDiagnostics_AndKeptToTheSameRetention()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-shell-log-").FullName;
        try
        {
            var logs = Directory.CreateDirectory(Path.Combine(dir, "logs")).FullName;
            File.WriteAllText(Path.Combine(logs, "shell-2026-10-01.log"), "2026-10-01T08:00:00.000Z\tshell\tshell starting\n");
            File.WriteAllText(Path.Combine(logs, "shell-2026-11-14.log"), "2026-11-14T08:00:00.000Z\tshell\tshell starting\n");
            File.WriteAllText(Path.Combine(logs, "core-2026-11-14.log"), "2026-11-14T08:00:01.0000000+00:00\tcore\tYawble core starting\n");

            Assert.Equal(["core-2026-11-14.log", "shell-2026-10-01.log", "shell-2026-11-14.log"], FileLogs.Diagnostics.Files(dir).Select(Path.GetFileName));
            Assert.Contains(FileLogs.Diagnostics.Read(dir), e => e.Fields[1] == "shell" && e.Fields[2] == "shell starting");
            Assert.Contains("shell", FileLogs.Diagnostics.About);
            Assert.Equal(14, FileLogs.Diagnostics.DefaultDays);

            // the core's retention sweep: an old shell day goes whole, like a core day
            Assert.Equal(1, FileLogs.Diagnostics.Prune(dir, "2026-10-31T00:00:00Z"));
            Assert.False(File.Exists(Path.Combine(logs, "shell-2026-10-01.log")));
            Assert.True(File.Exists(Path.Combine(logs, "shell-2026-11-14.log")));
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
