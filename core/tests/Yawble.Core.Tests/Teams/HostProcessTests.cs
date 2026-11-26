using System.Diagnostics;
using Yawble.Core.Teams;
using Yawble.Core.Tests.Tasks;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// A program the core runs on the host for a provider call or a push is stopped, with the processes it started, when
/// its time runs out and when the caller cancels. The program here is the test program's own stand-in
/// (<see cref="StandInTask"/>): it starts a copy of itself and waits, touches nothing but its own temp folder, and
/// ends by itself after a minute when nothing stops it.
/// </summary>
public class HostProcessTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _dir = Directory.CreateTempSubdirectory("yawble-hostproc-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string PidFile => Path.Combine(_dir, "pids.txt");

    /// <summary>The program and arguments of a stand-in that starts a waiting copy, writes its own and the copy's process id, then waits a minute.</summary>
    private (string Program, string[] Args) Sleeper() => StandInTask.Script(_dir, $"spawn {PidFile} 60", "wait 60");

    private async Task<int[]> PidsAsync()
    {
        await Bus.Wait.UntilAsync(() => File.Exists(PidFile) && ReadPids().Length == 2, "the stand-in wrote its process ids", TimeSpan.FromSeconds(60));
        return ReadPids();
    }

    private int[] ReadPids()
    {
        try { return [.. File.ReadAllText(PidFile).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)]; }
        catch (IOException) { return []; }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static Task GoneAsync(int[] pids) =>
        Bus.Wait.UntilAsync(() => !pids.Any(IsAlive), "the program and its child are gone", TimeSpan.FromSeconds(20));

    [Fact]
    public async Task AProviderCliCall_ThatRunsOutOfTime_IsStoppedWithItsChild_AndSaysSo()
    {
        var (program, args) = Sleeper();
        var watch = Stopwatch.StartNew();

        var call = PullRequestProviders.RunOnHostAsync(program, _dir, args, TimeSpan.FromSeconds(10), Ct);
        var pids = await PidsAsync();
        var result = await call;

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("did not finish within 10 s and was stopped", result.Stderr);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(50), "the call ended with its program, not at the time limit");
        await GoneAsync(pids);
    }

    [Fact]
    public async Task AProviderCliCall_ThatIsCancelled_IsStoppedWithItsChild()
    {
        var (program, args) = Sleeper();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var call = PullRequestProviders.RunOnHostAsync(program, _dir, args, cancel.Token);
        var pids = await PidsAsync();
        Assert.All(pids, pid => Assert.True(IsAlive(pid)));
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await GoneAsync(pids);
    }

    [Fact]
    public async Task WaitOrStop_LetsAProgramThatEndsInTimeEnd()
    {
        var (program, args) = StandInTask.Script(_dir, "exit 3");
        var psi = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;

        Assert.True(await HostProcess.WaitOrStopAsync(process, TimeSpan.FromSeconds(60), Ct));
        Assert.Equal(3, process.ExitCode);
    }

    [Fact]
    public async Task WaitOrStop_StopsOnCancel_BeforeTheCancellationGoesOn()
    {
        var (program, args) = Sleeper();
        var psi = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _dir };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pids = await PidsAsync();

        var wait = HostProcess.WaitOrStopAsync(process, TimeSpan.FromMinutes(5), cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        await GoneAsync(pids);
    }
}
