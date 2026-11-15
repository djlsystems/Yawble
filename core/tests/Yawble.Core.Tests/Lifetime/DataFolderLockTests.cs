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
        var second = DataFolderLock.TryAcquire(_dir, 5151);
        Assert.False(second.Acquired);
        Assert.Null(second.HeldByPid);
    }
}
