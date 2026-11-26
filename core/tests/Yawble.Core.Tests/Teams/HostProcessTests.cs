using System.Diagnostics;
using Yawble.Core.Teams;
using Xunit;

namespace Yawble.Core.Tests.Teams;

/// <summary>
/// A program the core runs on the host for a provider call or a push is stopped, with the processes it started, when
/// its time runs out and when the caller cancels. The program here is a script that starts a child and sleeps; it
/// touches nothing but its own temp folder.
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

    /// <summary>The shell and arguments of a script that writes its own and its child's process id, then sleeps for minutes.</summary>
    private (string Program, string[] Args) Sleeper()
    {
        if (OperatingSystem.IsWindows())
        {
            var ps = Path.Combine(_dir, "sleeper.ps1");
            File.WriteAllText(ps,
                "$p = Start-Process -FilePath powershell -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 300' -PassThru -WindowStyle Hidden\r\n" +
                $"\"$PID $($p.Id)\" | Set-Content -Path '{PidFile}'\r\nStart-Sleep -Seconds 300\r\n");
            return ("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ps]);
        }
        var sh = Path.Combine(_dir, "sleeper.sh");
        File.WriteAllText(sh, $"sleep 300 &\necho \"$$ $!\" > '{PidFile}'\nsleep 300\n");
        return ("sh", [sh]);
    }

    private async Task<int[]> PidsAsync()
    {
        await Bus.Wait.UntilAsync(() => File.Exists(PidFile) && ReadPids().Length == 2, "the script wrote its process ids", TimeSpan.FromSeconds(60));
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

        var call = PullRequestProviders.RunOnHostAsync(program, _dir, args, TimeSpan.FromSeconds(15), Ct);
        var pids = await PidsAsync();
        var result = await call;

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("did not finish within 15 s and was stopped", result.Stderr);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(120));
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
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell") { ArgumentList = { "-NoProfile", "-Command", "exit 3" } }
            : new ProcessStartInfo("sh") { ArgumentList = { "-c", "exit 3" } };
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
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
