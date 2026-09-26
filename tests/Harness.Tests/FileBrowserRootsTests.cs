using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// THE DATA ROOT IS ALWAYS A FILE-BROWSER ROOT. A configured root adds to it and never replaces it.
/// </summary>
public sealed class FileBrowserRootsTests
{
    private static readonly string DataRoot =
        Path.Combine(Path.GetTempPath(), $"harness-roots-{Guid.NewGuid():N}");

    [Fact]
    public void With_nothing_configured_the_data_root_is_listed()
    {
        var roots = FileBrowserOptions.Effective(null, DataRoot);

        Assert.Contains(roots, r => r.Path == Path.GetFullPath(DataRoot));
    }

    [Fact]
    public void A_configured_root_adds_to_the_data_root_and_does_not_replace_it()
    {
        var other = Path.Combine(Path.GetTempPath(), $"harness-other-{Guid.NewGuid():N}");
        var configured = new FileBrowserOptions { Roots = [new FileBrowserRoot("other", other)] };

        var roots = FileBrowserOptions.Effective(configured, DataRoot);

        Assert.Contains(roots, r => r.Path == Path.GetFullPath(DataRoot));
        Assert.Contains(roots, r => r.Path == Path.GetFullPath(other));
    }
}
