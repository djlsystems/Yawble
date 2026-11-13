using Xunit;

namespace Yawble.Bridge.Tests;

/// <summary>The sandbox's dotnet wrapper: building commands get -m:1, everything else passes through untouched.</summary>
public class DotnetShimTests
{
    [Theory]
    [InlineData("build", "build -m:1")]
    [InlineData("build App.sln -c Release", "build -m:1 App.sln -c Release")]
    [InlineData("test --filter A|B", "test -m:1 --filter A|B")]
    [InlineData("build -m", "build -m:1")]
    [InlineData("build -maxcpucount:8 x.csproj", "build -m:1 x.csproj")]
    [InlineData("build /m:4", "build -m:1")]
    [InlineData("BUILD -M:2", "BUILD -m:1")]
    [InlineData("run --project App -- -m:4 --x", "run -m:1 --project App -- -m:4 --x")]
    [InlineData("msbuild a.proj", "msbuild -m:1 a.proj")]
    [InlineData("--version", "--version")]
    [InlineData("new console -n x", "new console -n x")]
    [InlineData("App.dll -m", "App.dll -m")]
    [InlineData("build-server shutdown", "build-server shutdown")]
    [InlineData("", "")]
    [InlineData("--diagnostics build x.sln", "--diagnostics build -m:1 x.sln")]
    [InlineData("-d test -m:8", "-d test -m:1")]
    [InlineData("-d --version", "-d --version")]
    [InlineData("-d", "-d")]
    public void RewritesOnlyBuildingCommands(string given, string expected)
    {
        var args = given.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expected, string.Join(" ", Yawble.DotnetShim.Program.Rewrite(args)));
    }
}
