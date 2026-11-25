using Xunit;

namespace Yawble.Core.Tests;

public class TestTempTests
{
    private static readonly string Home = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "home", "member");

    [Fact]
    public void TheVariable_WinsOverEverything()
    {
        var chosen = TestTemp.Choose(Path.Combine(Home, "t"), Path.Combine(Home, new string('d', 200)), Home);
        Assert.Equal(Path.Combine(Home, "t"), chosen);
    }

    [Fact]
    public void ADeepTemp_MovesToAShortFolderInTheHome()
    {
        var deep = Path.Combine(Home, "AppData", "Local", "Packages", "sandbox.{" + Guid.NewGuid() + "}", "AC", "Temp", new string('x', 60));
        Assert.True(deep.Length > TestTemp.DeepTemp);
        Assert.Equal(Path.Combine(Home, TestTemp.HomeFolderName), TestTemp.Choose(null, deep, Home));
    }

    [Fact]
    public void AShortTemp_StaysWhereItIs()
    {
        Assert.Null(TestTemp.Choose(null, Path.Combine(Home, "Temp"), Home));
        Assert.Null(TestTemp.Choose("  ", Path.Combine(Home, "Temp") + Path.DirectorySeparatorChar, Home));
    }

    [Fact]
    public void ADeepTemp_WithNoHome_OrAHomeAsDeep_StaysWhereItIs()
    {
        var deep = Path.Combine(Home, new string('d', 150));
        Assert.Null(TestTemp.Choose(null, deep, null));
        Assert.Null(TestTemp.Choose(null, deep, deep));
    }

    [Fact]
    public void ThisProcessUsesTheRoot_ForItsTempFolders()
    {
        Assert.Equal(TestTemp.Root, Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
        var made = Directory.CreateTempSubdirectory("yawble-temp-");
        try { Assert.StartsWith(TestTemp.Root, made.FullName, StringComparison.OrdinalIgnoreCase); }
        finally { made.Delete(); }
    }
}
