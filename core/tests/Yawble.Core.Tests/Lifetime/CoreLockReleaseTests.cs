using System.Diagnostics;
using Yawble.Core.Ipc;
using Yawble.Core.Lifetime;
using Xunit;

namespace Yawble.Core.Tests.Lifetime;

/// <summary>
/// The data folder's lock goes with the process that holds it, as real core processes. A killed
/// core (a crash) and a core that exits cleanly both leave the folder free for the next core, which then owns
/// core.lock and core.pid.
/// </summary>
public class CoreLockReleaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Process StartCore(string dataDir, string token)
    {
        // The core's own apphost, so Process.Id is the core's pid: a "dotnet" on PATH may be a shim that starts the core as
        // its child (as in the agents' sandbox). Without an apphost, dotnet yawble-core.dll.
        var apphost = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "yawble-core.exe" : "yawble-core");
        var viaDotnet = !File.Exists(apphost);
        var psi = new ProcessStartInfo(viaDotnet ? "dotnet" : apphost)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment = { ["YAWBLE_IPC_TOKEN"] = token },
        };
        if (viaDotnet) psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "yawble-core.dll"));
        foreach (var a in new[] { "--ipc", IpcEndpoint.NewAddress(dataDir), "--data-dir", dataDir, "--no-control" }) psi.ArgumentList.Add(a);
        CoreLaunch.IsolateCompanionHome(psi);
        return Process.Start(psi)!;
    }

    private static async Task ReadyAsync(Process core)
    {
        Assert.Equal("YAWBLE-STARTING", await core.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct));
        var ready = await CoreLaunch.ReadReadyLineAsync(core.StandardOutput, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.StartsWith("YAWBLE-READY ", ready);
    }

    private static string FirstLine(string path)
    {
        // core.lock is held with FileShare.None while its core runs; read it only once that core is gone. On Windows a killed
        // process's handles can still be closing for a few ms after WaitForExit returns (seen ~1 run in 5 under load), so a
        // refused read is tried again for up to 2 s.
        for (var waited = 0; ; waited += 20)
        {
            try { return File.ReadAllText(path).Split('\n', 2)[0].Trim(); }
            catch (IOException) when (waited < 2000) { Thread.Sleep(20); }
        }
    }

    [Fact]
    public async Task TheLockIsFreeAfterTheHolderIsKilledAndAfterACleanExit_AndTheNextCoreOwnsIt()
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var dataDir = Directory.CreateTempSubdirectory("yawble-data-").FullName;
        var pidFile = Path.Combine(dataDir, DataFolderLock.PidFileName);
        var lockFile = Path.Combine(dataDir, DataFolderLock.LockFileName);
        var cores = new List<Process>();
        try
        {
            // 1. The first core holds the folder and names itself in core.pid.
            var crashed = StartCore(dataDir, token);
            cores.Add(crashed);
            await ReadyAsync(crashed);
            Assert.Equal(crashed.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), FirstLine(pidFile));

            // 2. It is killed: no Dispose ran, its core.pid stays, yet the kernel let the lock go.
            crashed.Kill(entireProcessTree: true);
            await crashed.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(15), Ct);
            Assert.True(File.Exists(pidFile), "a killed core cannot remove its core.pid");
            Assert.Equal(crashed.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), FirstLine(lockFile));

            var next = StartCore(dataDir, token);
            cores.Add(next);
            await ReadyAsync(next); // not refused with exit code 5
            Assert.Equal(next.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), FirstLine(pidFile));

            // 3. A clean exit frees it too, and takes core.pid with it.
            next.StandardInput.Close();
            await next.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(15), Ct);
            Assert.Equal(0, next.ExitCode);
            Assert.False(File.Exists(pidFile));
            Assert.Equal(next.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), FirstLine(lockFile));
            Assert.True(File.Exists(lockFile), "core.lock itself is never deleted");

            var third = StartCore(dataDir, token);
            cores.Add(third);
            await ReadyAsync(third);
            third.StandardInput.Close();
            await third.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(15), Ct);
            Assert.Equal(0, third.ExitCode);
        }
        finally
        {
            foreach (var c in cores)
            {
                if (!c.HasExited) c.Kill(entireProcessTree: true);
                c.Dispose();
            }
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    // A real core whose core.lock is still held when it starts (as by a just-killed core whose handle
    // is still closing) waits for it within the ~1 s budget instead of being refused at once.
    [Fact]
    public async Task ACoreLockLetGoWithinTheBudget_IsTakenByTheStartingCore()
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var dataDir = Directory.CreateTempSubdirectory("yawble-data-").FullName;
        Process? core = null;
        try
        {
            var held = new FileStream(Path.Combine(dataDir, DataFolderLock.LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                core = StartCore(dataDir, token);
                await Task.Delay(300, Ct);
                Assert.False(core.HasExited, "the starting core must still be waiting for the lock, not refused");
            }
            finally
            {
                held.Dispose();
            }
            await ReadyAsync(core);
            Assert.Equal(core.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), FirstLine(Path.Combine(dataDir, DataFolderLock.PidFileName)));
            core.StandardInput.Close();
            await core.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(15), Ct);
            Assert.Equal(0, core.ExitCode);
        }
        finally
        {
            if (core is { HasExited: false }) core.Kill(entireProcessTree: true);
            core?.Dispose();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    // A genuine second core on a folder a running core holds is still refused, after the retry
    // budget (about 1 s) and not much longer: exit 5, the holder's pid on stderr, nothing on stdout.
    [Fact]
    public async Task AGenuineSecondCore_IsRefusedWithExit5AfterAboutTheBudget()
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var dataDir = Directory.CreateTempSubdirectory("yawble-data-").FullName;
        var cores = new List<Process>();
        try
        {
            var holder = StartCore(dataDir, token);
            cores.Add(holder);
            await ReadyAsync(holder);

            var started = Stopwatch.StartNew();
            var second = StartCore(dataDir, token);
            cores.Add(second);
            var stdout = second.StandardOutput.ReadToEndAsync(Ct);
            var stderr = second.StandardError.ReadToEndAsync(Ct);
            await second.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            var took = started.Elapsed;

            Assert.Equal(5, second.ExitCode);
            Assert.Equal("", await stdout);
            Assert.Equal($"fatal: another Yawble is using this data folder (pid {holder.Id})", (await stderr).Trim());
            Assert.InRange(took, TimeSpan.FromMilliseconds(950), TimeSpan.FromSeconds(4));

            holder.StandardInput.Close();
            await holder.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(15), Ct);
            Assert.Equal(0, holder.ExitCode);
            Assert.False(File.Exists(Path.Combine(dataDir, DataFolderLock.PidFileName)));
        }
        finally
        {
            foreach (var c in cores)
            {
                if (!c.HasExited) c.Kill(entireProcessTree: true);
                c.Dispose();
            }
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }
}
