using System.Text.Json.Nodes;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Yawble.Core.Tests.Guardrail;
using Xunit;
using PolicyMember = Yawble.Core.Policy.MemberRef;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// A .NET build inside a member's REAL sandbox (Windows ProcessContainer). A plain `dotnet test` can
/// hang: its MSBuild worker nodes (/nodemode:1) sit idle forever and hold the shell open. This probes
/// why (can the sandbox create the named pipes MSBuild's nodes talk over?) and pins what members get: a build that
/// finishes, with or without -m: the app's dotnet wrapper is first on the sandbox's PATH and builds with -m:1.
/// </summary>
[Collection(RealSandboxGuardrailCollection.Name)]
public class RealSandboxDotnetBuildTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Probe(string powershell) : ILaunchProbe
    {
        public IReadOnlyList<PolicyMember> ExtraMembers => [];
        public LaunchMember? Synthetic { get; } = new(0, "_dotnet", "probe", AgentCli.Claude);
        public TimeSpan MaxWall => TimeSpan.FromMinutes(6);

        public JsonObject Rewrite(LaunchProbeContext context)
        {
            var request = (JsonObject)context.Request.DeepClone();
            request["process"]!["commandLine"] = RealSandboxHost.Script(context.Layout.Os, "true", powershell);
            return request;
        }
    }

    private static Dictionary<string, string> Lines(string stdout) => stdout.Split('\n').Select(l => l.TrimEnd('\r'))
        .Where(l => l.StartsWith("@", StringComparison.Ordinal) && l.Contains('='))
        .Select(l => l[1..]).GroupBy(l => l[..l.IndexOf('=')]).ToDictionary(g => g.Key, g => g.Last()[(g.Last().IndexOf('=') + 1)..]);

    /// <summary>Runs dotnet with a deadline; reports its exit code, or "hung" (then ends its whole tree).</summary>
    private const string Timed = """
        function Timed($name, $argList, $seconds) {
          $p = Start-Process dotnet -ArgumentList $argList -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $env:TEMP "$name.out") -RedirectStandardError (Join-Path $env:TEMP "$name.err")
          $null = $p.Handle
          if ($p.WaitForExit($seconds * 1000)) { $p.WaitForExit(); "@${name}=exit $($p.ExitCode)" }
          else {
            "@${name}=hung"
            & taskkill /T /F /PID $p.Id *> $null
          }
          $o = (Get-Content (Join-Path $env:TEMP "$name.out") -Raw); if ($o) { "@${name}_tail=" + (($o -replace "`r?`n", ' | ') -replace '.*(.{400})$', '$1') }
          Get-Process dotnet -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID } | ForEach-Object { "@${name}_left=$($_.Id)" }
          Get-Process dotnet -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        }
        """;

    /// <summary>
    /// MSBuild's out-of-proc nodes: the node creates \\.\pipe\MSBuild&lt;pid&gt; and the parent connects to it. Here: create
    /// such a pipe, connect to it from a second process in the same sandbox, and check the owner MSBuild compares.
    /// </summary>
    private const string PipeCheck = """
        function PipeCheck($label, $name) {
          $srv = $null
          try { $srv = New-Object System.IO.Pipes.NamedPipeServerStream($name, [System.IO.Pipes.PipeDirection]::InOut, 1, [System.IO.Pipes.PipeTransmissionMode]::Byte, [System.IO.Pipes.PipeOptions]::Asynchronous); "@${label}_create=ok" }
          catch { "@${label}_create=" + $_.Exception.Message }
          if (-not $srv) { return }
          $wait = $srv.WaitForConnectionAsync()
          $child = "`$c = New-Object System.IO.Pipes.NamedPipeClientStream('.', '$name', [System.IO.Pipes.PipeDirection]::InOut); try { `$c.Connect(5000); exit 0 } catch { [Console]::Error.WriteLine(`$_.Exception.Message); exit 1 }"
          $p = Start-Process powershell -ArgumentList '-NoProfile', '-NonInteractive', '-Command', $child -NoNewWindow -PassThru -Wait -RedirectStandardError (Join-Path $env:TEMP 'pipe.err')
          "@${label}_child_connect=" + $p.ExitCode + ' ' + ((Get-Content (Join-Path $env:TEMP 'pipe.err') -Raw) -replace "`r?`n", ' ')
          $self = New-Object System.IO.Pipes.NamedPipeClientStream('.', $name, [System.IO.Pipes.PipeDirection]::InOut)
          try { if (-not $wait.IsCompleted) { $self.Connect(3000) }; "@${label}_same_process_connect=ok" } catch { "@${label}_same_process_connect=" + $_.Exception.Message }
          $srv.Dispose()
        }
        PipeCheck 'pipe_global' ('MSBuild' + $PID)
        PipeCheck 'pipe_local' ('LOCAL\yawble' + $PID)
        """;

    [Fact]
    public async Task ADotnetBuildFinishesInAMemberSandbox()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows ProcessContainer");
        if (Environment.GetEnvironmentVariable("YAWBLE_REAL_DOTNET") != "1") Assert.Skip("a real build in the sandbox (a few minutes): set YAWBLE_REAL_DOTNET=1");
        RealSandboxHost.SkipUnlessAvailable();
        await using var kit = GuardrailKit.Create(realHome: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), clis: Yawble.Core.Agents.CliDetectorRegistry.ForThisHost(), realGateway: true);
        var script = string.Join("\n",
        [
            "$ErrorActionPreference = 'Continue'",
            "Set-Location $env:HOME",
            "'@path=' + $env:PATH",
            "'@shim_exists=' + (Test-Path (Join-Path ($env:PATH.Split(';')[0]) 'dotnet.exe'))",
            Timed,
            PipeCheck,
            """
            "@env=" + ((Get-ChildItem env: | Where-Object { $_.Name -like 'MSBUILD*' -or $_.Name -like 'DOTNET_*' } | ForEach-Object { $_.Name + '=' + $_.Value }) -join ';')
            & dotnet new sln -n Two -o two *> $null; Set-Location two
            & dotnet new classlib -n A -o A *> $null; & dotnet new classlib -n B -o B *> $null
            & dotnet sln add A/A.csproj B/B.csproj *> $null
            "@projects=" + ((Get-ChildItem -Recurse -Filter *.csproj).Name -join ',')
            Timed 'build_m1' @('build', '-m:1', '-nologo') 150
            Timed 'build_default' @('build', '--no-incremental', '-nologo') 90
            Timed 'test_default' @('test', '-nologo') 150
            "@which_dotnet=" + (Get-Command dotnet).Source
            """,
        ]);
        var outcome = await kit.Launcher(RealSandboxHost.Backend())
            .RunAsync(new LaunchRequest("_dotnet", "probe", RunKind.Headless, "probe", null, Probe: new Probe(script)), Ct);
        var stdout = outcome.ProbeOutput?.Stdout ?? "";
        output.WriteLine($"end {outcome.End} {outcome.Reason}\n{stdout}\nstderr: {outcome.ProbeOutput?.Stderr}");
        RealSandbox.Report($"dotnet build in the sandbox: end {outcome.End}\n{stdout}");
        var o = Lines(stdout);
        // What MSBuild trips over: an ordinary pipe name cannot be opened again inside the sandbox.
        Assert.NotEqual("ok", o.GetValueOrDefault("pipe_global_same_process_connect"));
        Assert.Equal("ok", o.GetValueOrDefault("pipe_local_same_process_connect"));
        // The app's dotnet wrapper: a plain build or test (no -m) finishes and leaves no MSBuild worker behind.
        Assert.Contains(SandboxRequestCompiler.DotnetShimFolder, o.GetValueOrDefault("which_dotnet") ?? "");
        Assert.NotEqual("hung", o.GetValueOrDefault("test_default"));
        Assert.False(o.ContainsKey("test_default_left"), "MSBuild workers were left behind by dotnet test");
        Assert.Equal("exit 0", o.GetValueOrDefault("build_m1"));
        Assert.Equal("exit 0", o.GetValueOrDefault("build_default"));
        Assert.False(o.ContainsKey("build_default_left"), "MSBuild workers were left behind");
    }
}
