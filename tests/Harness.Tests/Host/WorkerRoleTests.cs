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
using Microsoft.Extensions.Logging;

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
    public async Task In_control_a_plugins_runtime_is_the_workers_and_never_looked_up_on_controls_own_path()
    {
        // Control's image carries no runtime; a python3 plugin was refused on a split instance for it.
        var controlRoot = Directory.CreateTempSubdirectory("harness-control-runtime-").FullName;
        var allRoot = Directory.CreateTempSubdirectory("harness-all-runtime-").FullName;
        try
        {
            await using (var control = Host(controlRoot, "control"))
            {
                Assert.Equal<Func<string, bool>>(PluginCatalog.OnWorkers, control.Services.GetRequiredService<PluginCatalog>().RuntimeCheck);
            }

            await using var all = Host(allRoot, "all");
            Assert.NotEqual<Func<string, bool>>(PluginCatalog.OnWorkers, all.Services.GetRequiredService<PluginCatalog>().RuntimeCheck);
        }
        finally
        {
            Clean(controlRoot);
            Clean(allRoot);
        }
    }

    [Fact]
    public async Task The_launch_check_in_control_goes_to_a_connected_worker()
    {
        var catalog = new AgentCatalog(
        [
            new AgentDefinition("checked", AgentMode.Headless, new AgentLaunch("sh", ["-p", "{userPrompt}"]), LaunchCheck: ["-c", "exit 0"]),
        ]);
        var heartbeat = new RunHeartbeat();
        var directory = new RunDirectory();
        var worker = InProcessWorker.Connect(
            new WorkerId("w1"),
            events => new WorkerHost(new WorkerId("w1"), events, new RunLauncher(heartbeat, reports: false, lookup: LaunchLookup.Once), heartbeat),
            directory.HandleAsync);
        var workers = new OneOfMany(worker.Worker);

        // This process has no launcher: the check is sent to a worker, and its answer is the report.
        var runner = new ProcessAgentRunner(catalog, workers, directory, null, () => null, null);
        var report = await runner.CheckLaunchAsync("checked", Ct);

        Assert.Equal(AgentLaunchReport.Ok, report.Result);
        Assert.Single(workers.Sent.OfType<CheckLaunch>());

        // With no worker there is nobody to check it.
        var none = new ProcessAgentRunner(catalog, new OneOfMany(null), directory, null, () => null, null);
        Assert.Equal(ProcessAgentRunner.NotCheckedHere, (await none.CheckLaunchAsync("checked", Ct)).Detail);
        worker.Close();
    }

    /// <summary>Control's handle on its workers, with one worker or none.</summary>
    private sealed class OneOfMany(IRunWorker? only) : IRunWorker, IRunWorkerRouter
    {
        public System.Collections.Concurrent.ConcurrentQueue<ControlMessage> Sent { get; } = new();

        public WorkerId Id { get; } = new("control");

        public Task Closed { get; } = new TaskCompletionSource().Task;

        public IRunWorker For(ContainerId member) => Any();

        public IRunWorker Any() => only is null ? throw new InvalidOperationException("No worker is connected.") : this;

        public Task SendAsync(ControlMessage message, CancellationToken ct = default)
        {
            Sent.Enqueue(message);
            return only!.SendAsync(message, ct);
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
    /// ONLY CONTROL RETRIES THE UNFINISHED REMOVALS WHEN A WORKER JOINS: the Host wires the retry in
    /// control and not in all, where its start's own retry runs with its own worker already there.
    /// </summary>
    [Theory]
    [InlineData("control", true)]
    [InlineData("all", false)]
    public async Task Only_control_retries_unfinished_removals_when_a_worker_joins(string role, bool wired)
    {
        var root = Directory.CreateTempSubdirectory($"harness-{role}-join-retry-").FullName;
        var logged = new Captured();
        try
        {
            await using var host = Host(root, role, logged);
            _ = host.Services;

            Assert.Equal(wired, logged.Messages.Contains(RetryWhenAWorkerJoins.WiredText));
        }
        finally
        {
            Clean(root);
        }
    }

    /// <summary>
    /// IN CONTROL ONLY, EVERY CHANGE OF WORKERS MEASURES THE AGENT CLIS AGAIN: the pass is wired to the
    /// real worker connections in control, and not in all, where no worker joins.
    /// </summary>
    [Theory]
    [InlineData("control", true)]
    [InlineData("all", false)]
    public async Task Only_control_remeasures_when_the_workers_change(string role, bool wired)
    {
        var root = Directory.CreateTempSubdirectory($"harness-{role}-remeasure-").FullName;
        var logged = new Captured();
        try
        {
            await using var host = Host(root, role, logged);
            _ = host.Services;

            Assert.Equal(wired, logged.Messages.Contains(RemeasureWhenWorkersChange.WiredText));
        }
        finally
        {
            Clean(root);
        }
    }

    /// <summary>
    /// ALL'S OWN WORKER REMOVES ITS RUN HOMES WITH THE WORKER'S RULE: every run home the composed Host's
    /// worker holds - its launcher's and its agent CLIs' - is removed by <see cref="RunHomeRemoval"/>,
    /// never looped back through control's <see cref="FolderRemoval"/>.
    /// </summary>
    [Fact]
    public async Task All_removes_its_run_homes_with_the_workers_run_home_removal()
    {
        var root = Directory.CreateTempSubdirectory("harness-all-run-homes-").FullName;
        try
        {
            await using var all = Host(root, null);
            var worker = all.Services.GetRequiredService<InProcessWorker>();

            var homes = Reachable<RunHomes>(worker.Host);
            // The Host hands its launcher and its agent CLIs one set of homes; every one reachable is judged.
            Assert.NotEmpty(homes);
            Assert.All(homes, h =>
            {
                var remove = typeof(RunHomes).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Select(f => f.GetValue(h)).OfType<Func<string, string, Task>>().Single();
                Assert.Equal(typeof(RunHomeRemoval), remove.Method.DeclaringType!.DeclaringType ?? remove.Method.DeclaringType);
            });
        }
        finally
        {
            Clean(root);
        }
    }

    /// <summary>Every <typeparamref name="T"/> reachable from <paramref name="from"/> through the instance fields of the Harness's own types.</summary>
    private static List<T> Reachable<T>(object from) where T : class
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var found = new List<T>();
        var next = new Queue<object>([from]);
        while (next.TryDequeue(out var item))
        {
            if (!seen.Add(item)) continue;
            if (item is T t) found.Add(t);

            for (var type = item.GetType(); type is not null && type.Namespace?.StartsWith("Harness", StringComparison.Ordinal) == true; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType.IsValueType) continue;
                    if (field.GetValue(item) is { } value && value.GetType().Namespace?.StartsWith("Harness", StringComparison.Ordinal) == true) next.Enqueue(value);
                }
            }
        }

        return found;
    }

    /// <summary>What the Host logs, at Information and above, whatever its configured level.</summary>
    private sealed class Captured : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(Messages);

        public void Dispose() { }

        private sealed class Logger(System.Collections.Concurrent.ConcurrentBag<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Add(formatter(state, exception));
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
        var state = Directory.CreateTempSubdirectory("harness-worker-state-").FullName;
        start.Environment["HARNESS_WORKER_STATE_DIR"] = state;

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
            Directory.Delete(state, recursive: true);
        }
    }

    private static WebApplicationFactory<Program> Host(string root, string? role, Captured? logged = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DataRoot", root).UseSetting("Logging:LogLevel:Default", "Warning");
            if (role is not null) builder.UseSetting("Role", role);
            if (logged is not null)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(logged).AddFilter<Captured>(null, LogLevel.Information));
            }
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
