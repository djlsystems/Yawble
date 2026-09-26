using System.Diagnostics;
using Harness.Host;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// DATA PROTECTION KEYS LIVE IN `&lt;dataRoot&gt;/keys`. Anywhere else, a restart with the same
/// volume signs everybody out.
/// </summary>
public sealed class DataProtectionKeysTests(HostFixture host) : IClassFixture<HostFixture>
{
    [Fact]
    public void Keys_are_written_under_the_data_root()
    {
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("probe").Protect("x");

        var keys = Path.Combine(host.Services.GetRequiredService<TeamPaths>().DataRoot, "keys");
        Assert.NotEmpty(Directory.EnumerateFiles(keys, "key-*.xml"));
    }
}

/// <summary>
/// ONE DATA ROOT: `--DataRoot` WINS, THEN `HARNESS_DATA_ROOT`; WITH NEITHER SET THE HOST REFUSES TO
/// START, and says so with a non-zero exit so a supervisor does not restart it into the same state.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class DataRootTests : IDisposable
{
    private readonly string _setting = Path.Combine(Path.GetTempPath(), $"harness-root-setting-{Guid.NewGuid():N}");
    private readonly string _environment = Path.Combine(Path.GetTempPath(), $"harness-root-env-{Guid.NewGuid():N}");

    [Fact]
    public async Task The_setting_wins_over_the_environment_variable()
    {
        using var scope = new EnvironmentScope([new("HARNESS_DATA_ROOT", _environment)]);
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(h => h
                .UseSetting("DataRoot", _setting)
                .UseSetting("Logging:LogLevel:Default", "Warning"));

        Assert.Equal(_setting, factory.Services.GetRequiredService<TeamPaths>().DataRoot);
    }

    [Fact]
    public async Task With_neither_set_the_host_refuses_to_start()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Harness.Host.dll"));
        start.Environment.Remove("HARNESS_DATA_ROOT");
        start.Environment.Remove("DataRoot");
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";

        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The Host started with no data root instead of refusing.");
        }

        Assert.Equal(1, process.ExitCode);
        Assert.Contains("No data root is set", await stderr, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _setting, _environment })
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
