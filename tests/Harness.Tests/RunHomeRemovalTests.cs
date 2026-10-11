using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A worker in a process of its own removes a run's home with nothing of control's: only strictly
/// inside its parent, with no link on the way, links removed and never followed.
/// </summary>
public sealed class RunHomeRemovalTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harness-run-home-removal-").FullName;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var link in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Where(p => new FileInfo(p).LinkTarget is not null))
        {
            File.Delete(link);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_run_home_is_removed_inside_its_parent_only()
    {
        var parent = Directory.CreateDirectory(Path.Combine(_root, "temp")).FullName;
        var home = Directory.CreateDirectory(Path.Combine(parent, RunHomes.Prefix + "abc")).FullName;
        Directory.CreateDirectory(Path.Combine(home, ".config", "cli"));
        await File.WriteAllTextAsync(Path.Combine(home, ".config", "cli", "settings.json"), "{}", Ct);
        var beside = Path.Combine(parent, "kept.txt");
        await File.WriteAllTextAsync(beside, "kept", Ct);

        var left = await RunHomeRemoval.RemoveAsync(parent, home, null, Ct);

        Assert.Empty(left);
        Assert.False(Directory.Exists(home));
        Assert.True(File.Exists(beside));
        Assert.True(Directory.Exists(parent));
    }

    [Fact]
    public async Task A_link_on_the_way_is_refused()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName;
        var home = Directory.CreateDirectory(Path.Combine(real, RunHomes.Prefix + "abc")).FullName;
        await File.WriteAllTextAsync(Path.Combine(home, "file"), "x", Ct);
        var linked = Path.Combine(_root, "linked");
        Directory.CreateSymbolicLink(linked, real);

        var left = await RunHomeRemoval.RemoveAsync(linked, Path.Combine(linked, RunHomes.Prefix + "abc"), null, Ct);

        // Left alone, and said so: the parent is a link.
        Assert.Equal([Path.Combine(linked, RunHomes.Prefix + "abc")], left);
        Assert.True(File.Exists(Path.Combine(home, "file")));

        // A home outside its parent is left alone too.
        Assert.Equal([home], await RunHomeRemoval.RemoveAsync(Path.Combine(_root, "elsewhere"), home, null, Ct));
        Assert.True(Directory.Exists(home));
    }

    [Fact]
    public async Task A_link_inside_a_home_is_removed_and_never_followed()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var precious = Path.Combine(outside, "precious.txt");
        await File.WriteAllTextAsync(precious, "keep me", Ct);

        var parent = Directory.CreateDirectory(Path.Combine(_root, "temp")).FullName;
        var home = Directory.CreateDirectory(Path.Combine(parent, RunHomes.Prefix + "abc")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(home, "to-outside"), outside);
        File.CreateSymbolicLink(Path.Combine(home, "to-file"), precious);

        var left = await RunHomeRemoval.RemoveAsync(parent, home, null, Ct);

        Assert.Empty(left);
        Assert.False(Directory.Exists(home));
        Assert.Equal("keep me", await File.ReadAllTextAsync(precious, Ct));
    }

    [Fact]
    public async Task The_removal_a_worker_gives_its_run_homes_is_this_one()
    {
        var parent = Directory.CreateDirectory(Path.Combine(_root, "temp")).FullName;
        var home = Directory.CreateDirectory(Path.Combine(parent, RunHomes.Prefix + "abc")).FullName;

        await RunHomeRemoval.For(null)(parent, home);

        Assert.False(Directory.Exists(home));
    }
}
