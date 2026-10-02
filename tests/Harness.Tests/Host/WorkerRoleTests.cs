using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// The Host runs as control, worker or all. All is today's single process with its own run worker;
/// control composes no launcher and no worker host; a worker runs before anything of control's is
/// composed - or compiled - and needs no data root at all.
/// </summary>
public sealed class WorkerRoleTests
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_role_is_all_by_default()
    {
        Assert.Equal((HostRole.All, (string?)null), HostRoles.Resolve([], null));
        Assert.Equal((HostRole.All, (string?)null), HostRoles.Resolve(["--DataRoot", "/x"], ""));
    }

    [Fact]
    public void The_role_is_read_from_role_or_harness_role()
    {
        Assert.Equal(HostRole.Worker, HostRoles.Resolve(["--Role", "worker"], null).Role);
        Assert.Equal(HostRole.Control, HostRoles.Resolve(["--Role=control"], null).Role);
        Assert.Equal(HostRole.Control, HostRoles.Resolve(["--urls", "http://x"], "Control").Role);
        Assert.Equal(HostRole.All, HostRoles.Resolve(["--Role", "all"], "worker").Role);
    }

    [Fact]
    public void An_unknown_role_is_refused_with_a_sentence()
    {
        Assert.Equal(
            "--Role must be control, worker or all (HARNESS_ROLE); 'boss' is none of them.",
            HostRoles.Resolve(["--Role", "boss"], null).Refusal);
    }

    [Fact]
    public async Task Control_composes_no_launcher_and_no_worker_host()
    {
        var root = Directory.CreateTempSubdirectory("harness-control-role-").FullName;
        try
        {
            await using var control = Host(root, "control");
            var services = control.Services;

            Assert.Null(services.GetService<InProcessWorker>());
            Assert.IsType<RunWorkers>(services.GetRequiredService<IRunWorker>());
            Assert.Empty(services.GetRequiredService<WorkerPool>().Workers);

            var report = await services.GetRequiredService<ProcessAgentRunner>().CheckLaunchAsync(
                services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless && d.LaunchCheck is { Count: > 0 }).Name, Ct);
            Assert.Equal(AgentLaunchReport.NotChecked, report.Result);

            var limit = services.GetRequiredService<TenantSettings>().RunLimit();
            Assert.Equal("workers", limit.Bound);
            Assert.Equal(0, limit.Limit);

            Assert.Null(services.GetRequiredService<WipLedger>().TryEnter(new ContainerId("alpha", "Developer")));
            Assert.Equal(WipLedger.WorkerReason, Assert.Single(services.GetRequiredService<WipLedger>().View().Waiting).Reason);
        }
        finally
        {
            Clean(root);
        }
    }

    [Fact]
    public async Task All_composes_todays_in_process_worker()
    {
        var root = Directory.CreateTempSubdirectory("harness-all-role-").FullName;
        try
        {
            await using var all = Host(root, null);
            var services = all.Services;

            Assert.NotNull(services.GetService<InProcessWorker>());
            Assert.IsType<InProcessTransport>(services.GetRequiredService<IRunWorker>());
            Assert.Equal([WorkerId.Local], services.GetRequiredService<WorkerPool>().Workers);
            Assert.Contains(services.GetRequiredService<TenantSettings>().RunLimit().Bound, new[] { "cpu", "memory", "configuration" });
        }
        finally
        {
            Clean(root);
        }
    }

    /// <summary>
    /// A WORKER RUNS BEFORE THE HOST IS COMPILED. The role is read in a module initializer, which runs
    /// when the Host's assembly loads; Program's top-level statements are one method, and compiling it
    /// loads every assembly it names. So the initializer is the branch, and it may name nothing of a
    /// store: it calls the worker and exits.
    /// </summary>
    [Fact]
    public void The_worker_role_returns_before_any_store_is_composed()
    {
        var initializer = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Single(m => m.GetCustomAttribute<ModuleInitializerAttribute>() is not null);

        var calls = IlScan.Decode(initializer).Select(i => i.Method).OfType<MethodBase>().ToList();
        Assert.Contains(calls, m => m.DeclaringType == typeof(WorkerProcess) && m.Name == nameof(WorkerProcess.RunAsync));
        Assert.Contains(calls, m => m.DeclaringType == typeof(Environment) && m.Name == nameof(Environment.Exit));
        Assert.Empty(StoreReferences(initializer));
    }

    [Fact]
    public void The_role_order_scan_catches_a_planted_store_before_the_branch()
    {
        var probe = typeof(WorkerRoleTests).GetMethod(nameof(PlantsAStoreBeforeTheBranch), BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.Contains(nameof(DataRootLock), StoreReferences(probe));
    }

    [Fact]
    public async Task A_worker_starts_with_no_data_root()
    {
        var closed = ClosedPort();
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Harness.Host.dll"));
        start.ArgumentList.Add("--Role");
        start.ArgumentList.Add("worker");
        start.Environment.Remove("HARNESS_DATA_ROOT");
        start.Environment.Remove("DataRoot");
        start.Environment.Remove(HostRoles.Variable);
        start.Environment["HARNESS_CONTROL_URL"] = $"http://127.0.0.1:{closed}";
        start.Environment["HARNESS_WORKER_KEY"] = "a-key-nobody-checks";
        start.Environment["HARNESS_WORKER_ID"] = "no-data-root";

        var output = new StringBuilder();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        Assert.True(process.Start());
        var pid = process.Id;
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            string Said()
            {
                lock (output) return output.ToString();
            }

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!Said().Contains("Could not reach control", StringComparison.Ordinal) && !process.HasExited && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, Ct);
            }

            Assert.False(process.HasExited, $"The worker exited ({(process.HasExited ? process.ExitCode : 0)}):\n{Said()}");
            Assert.Contains("Could not reach control", Said());
            Assert.DoesNotContain("No data root is set", Said());
            Assert.Equal(pid, process.Id);
        }
        finally
        {
            // The one process this test started, by its recorded PID.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    private static WebApplicationFactory<Program> Host(string root, string? role) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DataRoot", root).UseSetting("Logging:LogLevel:Default", "Warning");
            if (role is not null) builder.UseSetting("Role", role);
        });

    private static void Clean(string root)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>What a method names that belongs to control's composition: a store, the data root's lock, the key ring, an identity type.</summary>
    private static List<string> StoreReferences(MethodBase method)
    {
        string[] forbidden =
        [
            "Microsoft.Data.Sqlite", "SQLitePCLRaw", "Microsoft.AspNetCore.DataProtection", "Harness.Identity", "Harness.Messaging",
            "Harness.Containers", "Harness.Kanban", "Harness.Backlog", "Harness.Skills",
        ];
        string[] controlTypes = [nameof(DataRootLock), "SqlitePrincipalStore", "TenantSettings", "HostLog"];

        return [.. IlScan.Decode(method)
            .Select(i => (MemberInfo?)i.Method ?? i.Field)
            .OfType<MemberInfo>()
            .Select(m => m.DeclaringType!)
            .Where(t => forbidden.Any(a => t.Assembly.GetName().Name!.StartsWith(a, StringComparison.Ordinal)) || controlTypes.Contains(t.Name))
            .Select(t => t.Name)
            .Distinct()];
    }

    // Never called, only scanned.
    private static void PlantsAStoreBeforeTheBranch()
    {
        _ = DataRootLock.PathFor("/nowhere");
        StandIn();
    }

    private static Task<int> StandIn() => Task.FromResult(0);
}
