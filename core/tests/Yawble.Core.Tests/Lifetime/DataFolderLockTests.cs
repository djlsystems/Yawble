using Yawble.Core.Lifetime;
using Xunit;

namespace Yawble.Core.Tests.Lifetime;

/// <summary>One core per data folder: the lock on a temp folder, taken twice in this process (two open files, as two cores).</summary>
public sealed class DataFolderLockTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("yawble-lock-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ASecondLockOnTheSameFolderIsRefusedAndNamesTheHoldersPid()
    {
        var first = DataFolderLock.TryAcquire(_dir, 4242);
        using var held = first.Lock;
        Assert.True(first.Acquired);

        var second = DataFolderLock.TryAcquire(_dir, 5151);
        Assert.False(second.Acquired);
        Assert.Equal(4242, second.HeldByPid);
        Assert.Equal("another Yawble is using this data folder (pid 4242)", DataFolderLock.InUseMessage(second.HeldByPid));
    }

    [Fact]
    public void TheLockIsFreeAgainAfterDispose_AndCorePidGoesWithIt()
    {
        var first = DataFolderLock.TryAcquire(_dir, 4242);
        Assert.True(first.Acquired);
        first.Lock!.Dispose();
        Assert.False(File.Exists(Path.Combine(_dir, DataFolderLock.PidFileName)));
        Assert.True(File.Exists(Path.Combine(_dir, DataFolderLock.LockFileName))); // never deleted (see the class)

        var again = DataFolderLock.TryAcquire(_dir, 5151);
        using var held = again.Lock;
        Assert.True(again.Acquired);
        Assert.StartsWith("5151\n", File.ReadAllText(Path.Combine(_dir, DataFolderLock.PidFileName)));
    }

    [Fact]
    public void ACrashedCoresPidFileIsTakenOver()
    {
        File.WriteAllText(Path.Combine(_dir, DataFolderLock.PidFileName), "999\nold\n"); // no lock held: the process is gone
        var attempt = DataFolderLock.TryAcquire(_dir, 77);
        using var held = attempt.Lock;
        Assert.True(attempt.Acquired);
        Assert.StartsWith("77\n", File.ReadAllText(Path.Combine(_dir, DataFolderLock.PidFileName)));
    }

    [Fact]
    public void StillOurs_UntilThePidFileIsRemovedOrReplaced()
    {
        var attempt = DataFolderLock.TryAcquire(_dir, 4242);
        using var held = attempt.Lock!;
        Assert.True(held.StillOurs());

        var pidFile = Path.Combine(_dir, DataFolderLock.PidFileName);
        var mine = File.ReadAllText(pidFile);
        File.WriteAllText(pidFile, "4242\nsomeone-elses-folder\n"); // a folder put in its place, same pid by chance
        Assert.False(held.StillOurs());
        File.WriteAllText(pidFile, mine);
        Assert.True(held.StillOurs());
        File.Delete(pidFile); // the data was removed
        Assert.False(held.StillOurs());
    }

    [Fact]
    public void APidThatCannotBeReadIsSaidAsUnknown()
    {
        Assert.Equal("another Yawble is using this data folder (pid unknown)", DataFolderLock.InUseMessage(null));
        var first = DataFolderLock.TryAcquire(_dir, 4242);
        using var held = first.Lock;
        File.Delete(Path.Combine(_dir, DataFolderLock.PidFileName));
        var second = DataFolderLock.TryAcquire(_dir, 5151, new FakeClock().Wait(), DataFolderLock.OpenLockFile);
        Assert.False(second.Acquired);
        Assert.Null(second.HeldByPid);
    }

    // B033: a just-killed core's handle on core.lock can still be closing for a few ms after WaitForExit (Windows).

    [Fact]
    public void ALockThatFreesWithinTheBudgetIsTaken_AfterRetrying()
    {
        var clock = new FakeClock();
        var opens = 0;
        FileStream? FreesAfter300Ms(string path)
        {
            opens++;
            return clock.Now < TimeSpan.FromMilliseconds(300) ? null : DataFolderLock.OpenLockFile(path);
        }

        var attempt = DataFolderLock.TryAcquire(_dir, 77, clock.Wait(), FreesAfter300Ms);
        using var held = attempt.Lock;

        Assert.True(attempt.Acquired);
        Assert.Null(attempt.HeldByPid);
        Assert.Equal(TimeSpan.FromMilliseconds(300), clock.Now); // 6 steps of 50 ms, then the open succeeded
        Assert.Equal(7, opens);
        Assert.All(clock.Delays, d => Assert.Equal(LockWait.DefaultStep, d));
        Assert.StartsWith("77\n", File.ReadAllText(Path.Combine(_dir, DataFolderLock.PidFileName)));
    }

    [Fact]
    public void ALockStillHeldAfterTheBudgetIsRefusedWithTheHoldersPid_OnlyAfterTheBudget()
    {
        var first = DataFolderLock.TryAcquire(_dir, 4242);
        using var holder = first.Lock;
        Assert.True(first.Acquired);

        var clock = new FakeClock();
        var opens = 0;
        TimeSpan? lastOpen = null;
        FileStream? RealOpen(string path)
        {
            opens++;
            lastOpen = clock.Now;
            return DataFolderLock.OpenLockFile(path); // still held by `holder`
        }

        var second = DataFolderLock.TryAcquire(_dir, 5151, clock.Wait(), RealOpen);

        Assert.False(second.Acquired);
        Assert.Equal(4242, second.HeldByPid);
        Assert.Equal("another Yawble is using this data folder (pid 4242)", DataFolderLock.InUseMessage(second.HeldByPid));
        Assert.Equal(LockWait.DefaultBudget, lastOpen); // the last try was at the end of the budget, not before
        Assert.Equal(21, opens); // at 0, 50, ..., 1000 ms
        Assert.All(clock.Delays, d => Assert.True(d <= LockWait.DefaultStep));
    }

    [Fact]
    public void TheLastWaitIsCutToTheBudget()
    {
        var clock = new FakeClock();
        var wait = clock.Wait() with { Budget = TimeSpan.FromMilliseconds(120) };
        var attempt = DataFolderLock.TryAcquire(_dir, 5151, wait, _ => null);

        Assert.False(attempt.Acquired);
        Assert.Equal(new[] { 50, 50, 20 }, clock.Delays.Take(3).Select(d => (int)d.TotalMilliseconds));
    }

    /// <summary>A clock that moves only when the lock waits: no real sleeping, no Windows timing.</summary>
    private sealed class FakeClock
    {
        public TimeSpan Now { get; private set; }
        public List<TimeSpan> Delays { get; } = [];

        public LockWait Wait() => LockWait.Default with
        {
            Now = () => Now,
            Delay = d => { Delays.Add(d); Now += d; },
        };
    }
}
