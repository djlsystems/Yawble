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
    [InlineData("msbuild a.proj", "msbuild -m:1 a.proj")]
    [InlineData("restore x.sln", "restore -m:1 x.sln")]
    [InlineData("publish -c Release -m:2", "publish -m:1 -c Release")]
    [InlineData("pack -o out", "pack -m:1 -o out")]
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

    // Fake files only: these paths are never touched on disk.
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "shim-fake", "repo");
    private static readonly string App = Path.Combine(Root, "app");
    private const string Mtp = """
        // comments are allowed, as the SDK reads it
        { "sdk": { "version": "10.0.100" }, "test": { "runner": "Microsoft.Testing.Platform" } }
        """;

    private static Yawble.DotnetShim.ShimFiles Fake(Dictionary<string, string>? files = null, params string[] dirs)
    {
        files ??= [];
        return new(p => files.ContainsKey(p), p => dirs.Contains(p), p => files.TryGetValue(p, out var t) ? t : null);
    }

    private static string[] Plan(string given, Yawble.DotnetShim.ShimFiles? files = null, string? cwd = null) =>
        [.. Yawble.DotnetShim.Program.Plan(given.Split(' ', StringSplitOptions.RemoveEmptyEntries), cwd ?? App, files ?? Fake())
            .Select(c => string.Join(" ", c))];

    [Theory]
    [InlineData("build App.sln", "build -m:1 App.sln")]
    [InlineData("restore", "restore -m:1")]
    [InlineData("publish -r linux-x64 /m", "publish -m:1 -r linux-x64")]
    [InlineData("pack", "pack -m:1")]
    [InlineData("test --filter A", "test -m:1 --filter A")]
    [InlineData("test -- RunConfiguration.X=1", "test -m:1 -- RunConfiguration.X=1")]
    public void WithoutTestingPlatform_OneCommand_WithM1ForMSBuild(string given, string expected)
    {
        Assert.Equal([expected], Plan(given));
        // VSTest named in global.json is the same as none.
        var vstest = Fake(new() { [Path.Combine(Root, "global.json")] = """{ "test": { "runner": "VSTest" } }""" });
        Assert.Equal([expected], Plan(given, vstest));
    }

    [Fact]
    public void Run_BuildsWithM1_ThenRunsWithNoBuild()
    {
        Assert.Equal(["build -m:1 -v:q --nologo", "run --no-build"], Plan("run"));
        Assert.Equal(["-d build -m:1 -v:q --nologo", "-d run --no-build"], Plan("-d run"));
    }

    [Fact]
    public void Run_TheProgramGetsExactlyItsOwnArguments()
    {
        var plan = Plan("run --project App -c Release -- hi -m:4 --x");
        Assert.Equal(["build -m:1 App -c Release -v:q --nologo", "run --no-build --project App -c Release -- hi -m:4 --x"], plan);
        Assert.DoesNotContain("-m:1", plan[1].Split(' '));
        Assert.EndsWith("-- hi -m:4 --x", plan[1]);
    }

    [Fact]
    public void Run_OnlySharedBuildOptionsGoToTheBuild()
    {
        var plan = Plan("run hi -v:n -p:A=1 --property B=2 -lp web -e K=V --no-cache -f net10.0 --os linux -a x64 --no-restore --artifacts-path=out");
        Assert.Equal("build -m:1 -v:n -p:A=1 --property B=2 -f net10.0 --os linux -a x64 --no-restore --artifacts-path=out --nologo", plan[0]);
        Assert.Equal("run --no-build hi -v:n -p:A=1 --property B=2 -lp web -e K=V --no-cache -f net10.0 --os linux -a x64 --no-restore --artifacts-path=out", plan[1]);
        Assert.DoesNotContain("-m:1", plan[1].Split(' '));
    }

    [Fact]
    public void Run_OptionalBoolOptions_TakeOnlyTrueOrFalse()
    {
        Assert.Equal("build -m:1 --interactive true -v:q --nologo", Plan("run --interactive true hi")[0]);
        Assert.Equal("build -m:1 --sc -v:q --nologo", Plan("run --sc hi")[0]);
        Assert.Equal("build -m:1 -bl:x.binlog -v:q --nologo", Plan("run -bl:x.binlog")[0]);
    }

    [Fact]
    public void Run_AFileBasedApp_IsBuiltAsItself()
    {
        var files = Fake(new() { [Path.Combine(App, "tool.cs")] = "" });
        Assert.Equal(["build -m:1 tool.cs -v:q --nologo", "run --no-build tool.cs a b"], Plan("run tool.cs a b", files));
        // Not a file that exists: an ordinary argument for the program, not for the build.
        Assert.Equal("build -m:1 -v:q --nologo", Plan("run other.cs a")[0]);
        // Only the first argument can be the app.
        Assert.Equal("build -m:1 -v:q --nologo", Plan("run a tool.cs", files)[0]);
    }

    [Theory]
    [InlineData("run --no-build -- x")]
    [InlineData("run --help")]
    [InlineData("run --list-devices")]
    [InlineData("run -")]
    [InlineData("run -c Release --no-build")]
    public void Run_ThatDoesNotBuild_PassesThroughUnchanged(string given) =>
        Assert.Equal([given], Plan(given));

    [Fact]
    public void Run_NoBuildAfterDoubleDash_IsTheProgramsAndStillBuilds() =>
        Assert.Equal(["build -m:1 -v:q --nologo", "run --no-build -- --no-build"], Plan("run -- --no-build"));

    [Fact]
    public void TestingPlatformTest_BuildsWithM1_ThenTestsWithNoBuild()
    {
        var files = Fake(new() { [Path.Combine(Root, "global.json")] = Mtp });
        var plan = Plan("test --project T.csproj -c Debug --filter-class X --results-directory r -p:A=1 -- --foo -m:2", files);
        Assert.Equal(["build -m:1 T.csproj -c Debug -p:A=1 -v:q",
            "test --no-build --project T.csproj -c Debug --filter-class X --results-directory r -p:A=1 -- --foo -m:2"], plan);
        Assert.DoesNotContain("-m:1", plan[1].Split(' '));
        Assert.Equal(["build -m:1 -v:q", "test --no-build --no-ansi --list-tests"], Plan("test --no-ansi --list-tests", files));
        Assert.Equal(["build -m:1 -v:d", "test --no-build -v:d"], Plan("test -v:d", files));
    }

    [Fact]
    public void TestingPlatformTest_FirstPositional_AsTheSdkReadsIt()
    {
        var files = Fake(new() { [Path.Combine(Root, "global.json")] = Mtp, [Path.Combine(App, "My.slnx")] = "", [Path.Combine(App, "T.dll")] = "" },
            Path.Combine(App, "tests"));
        Assert.Equal(["build -m:1 My.slnx -v:q", "test --no-build My.slnx --report-trx"], Plan("test My.slnx --report-trx", files));
        Assert.Equal(["build -m:1 tests -v:q", "test --no-build tests"], Plan("test tests", files));
        Assert.Equal(["test T.dll"], Plan("test T.dll", files)); // a built test module: nothing to build
        Assert.Equal(["test --test-modules **/*.dll"], Plan("test --test-modules **/*.dll", files));
        Assert.Equal(["test -c Release --no-build"], Plan("test -c Release --no-build", files));
        Assert.Equal("build -m:1 -v:q", Plan("test Missing.sln", files)[0]); // not a file: the test app's argument
    }

    [Fact]
    public void TestingPlatform_IsReadFromTheNearestGlobalJson()
    {
        var outer = Path.Combine(Root, "global.json");
        var inner = Path.Combine(App, "global.json");
        Assert.True(Yawble.DotnetShim.Program.IsTestingPlatform(App, Fake(new() { [outer] = Mtp })));
        Assert.True(Yawble.DotnetShim.Program.IsTestingPlatform(App, Fake(new() { [inner] = """{"test":{"runner":"microsoft.testing.platform"}}""" })));
        // The first global.json found decides, as in the SDK: one without "test" means VSTest.
        Assert.False(Yawble.DotnetShim.Program.IsTestingPlatform(App, Fake(new() { [outer] = Mtp, [inner] = """{"sdk":{}}""" })));
        Assert.False(Yawble.DotnetShim.Program.IsTestingPlatform(App, Fake()));
        Assert.False(Yawble.DotnetShim.Program.IsTestingPlatform(App, Fake(new() { [outer] = "{ not json" })));
        Assert.False(Yawble.DotnetShim.Program.IsTestingPlatform(App, Fake(new() { [outer] = """{"Test":{"Runner":"Microsoft.Testing.Platform"}}""" })));
        Assert.False(Yawble.DotnetShim.Program.IsTestingPlatform(App, Fake(new() { [outer] = """{"test":{"runner":1}}""" })));
        Assert.False(Yawble.DotnetShim.Program.IsTestingPlatform(App, Fake(new() { [outer] = "[]" })));
        var throwing = new Yawble.DotnetShim.ShimFiles(_ => true, _ => false, _ => throw new IOException("locked"));
        Assert.False(Yawble.DotnetShim.Program.IsTestingPlatform(App, throwing));
    }

    [Fact]
    public void OnMacOS_TheRealDotnetIsTheFirstOnPathThatIsNotTheWrapper()
    {
        var files = new HashSet<string> { "/app/bin/dotnet-shim/dotnet", "/usr/local/share/dotnet/dotnet" };
        Assert.Equal("/usr/local/share/dotnet/dotnet", Yawble.DotnetShim.Program.RealDotnet(
            "/app/bin/dotnet-shim:/usr/local/share/dotnet:/usr/bin", "/app/bin/dotnet-shim/dotnet", windows: false, files.Contains));
    }

    [Fact]
    public void OnMacOS_WithoutDotnetOnPath_TheInstallersFolderIsTried()
    {
        var files = new HashSet<string> { "/usr/local/share/dotnet/dotnet" };
        Assert.Equal("/usr/local/share/dotnet/dotnet", Yawble.DotnetShim.Program.RealDotnet("/usr/bin:/bin", "/app/bin/dotnet-shim/dotnet", windows: false, files.Contains));
        Assert.Null(Yawble.DotnetShim.Program.RealDotnet("/usr/bin:/bin", "/app/bin/dotnet-shim/dotnet", windows: false, _ => false));
    }
}
