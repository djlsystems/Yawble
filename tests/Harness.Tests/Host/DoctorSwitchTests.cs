using System.Diagnostics;
using System.Text.Json;
using Harness.Host;

namespace Harness.Tests.Host;

/// <summary>
/// `--doctor` NEVER SERVES, exits 0, and its report is the LAST LINE of stdout even when the
/// Host prints lines before its operator commands run (it removes FORCE_COLOR out loud). The
/// operator CLI runs exactly this with an exec in the container and reads the last line - while
/// the Host is serving the same data root, so the second test holds the data-root lock from THIS
/// process and expects the spawned doctor to answer anyway.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DoctorSwitchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"harness-doctor-switch-{Guid.NewGuid():N}");

    [Fact]
    public async Task The_switch_exits_zero_with_the_report_last_and_does_not_serve()
    {
        var (exitCode, lines, stderr) = await RunDoctorAsync(forceColor: true);

        Assert.True(exitCode == 0, stderr);
        Assert.Contains(lines, l => l.StartsWith("Removed FORCE_COLOR", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(lines[^1]);
        Assert.Equal(_root, document.RootElement.GetProperty("dataRoot").GetProperty("path").GetString());
    }

    [Fact]
    public async Task The_switch_answers_from_another_process_while_this_one_holds_the_data_root()
    {
        Directory.CreateDirectory(_root);
        // What a serving Host holds. Program.cs must run operator commands BEFORE taking this, or
        // the doctor refuses against every live instance, which is the only place the CLI runs it.
        using var held = DataRootLock.Acquire(_root);

        var (exitCode, lines, stderr) = await RunDoctorAsync(forceColor: false);

        Assert.True(exitCode == 0, stderr);
        using var document = JsonDocument.Parse(lines[^1]);
        Assert.Equal(_root, document.RootElement.GetProperty("dataRoot").GetProperty("path").GetString());
    }

    private async Task<(int ExitCode, string[] Lines, string Stderr)> RunDoctorAsync(bool forceColor)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Harness.Host.dll"));
        start.ArgumentList.Add("--DataRoot=" + _root);
        start.ArgumentList.Add("--doctor");
        start.Environment.Remove("HARNESS_DATA_ROOT");
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        // Makes the Host print a line BEFORE the operator command runs.
        if (forceColor) start.Environment["FORCE_COLOR"] = "1";

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The Host kept running on --doctor instead of reporting and exiting.");
        }

        var lines = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();

        return (process.ExitCode, lines, await stderr);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
