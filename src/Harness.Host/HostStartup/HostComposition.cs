using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using ModelContextProtocol.AspNetCore;
using Harness.Containers;
using Harness.Backlog;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Host.Capacity;
using Harness.Host.Solutions;
using Harness.Identity;
using Harness.Kanban;
using Harness.Messaging;
using Harness.Skills;

namespace Harness.Host;

/// <summary>
/// THE HOST'S COMPOSITION, read top to bottom in Program.cs: one phase per file, each a verbatim span
/// of what was one top-level program. The fields are the values a phase hands the ones after it; a
/// value one phase alone uses is its local.
/// </summary>
internal sealed partial class HostComposition(string[] args, HostLog hostLog) : IDisposable
{
    private WebApplicationBuilder builder = default!;
    private bool control = default!;
    private string dataRoot = default!;
    private string instance = default!;
    private string database = default!;
    private DatabaseOptions databaseOptions = default!;
    private BackupSettings backupSettings = default!;
    private DataRootLock held = default!;
    private SchemaMigrator migrator = default!;
    private LedgerStartResult? ledgerStart = default!;
    private SqliteMessageStore store = default!;
    private AdmissionHoldWriter admissionHoldWriter = default!;
    private SqliteUserStore users = default!;
    private WorkerKeyGate workerKeys = default!;
    private TenantSettings tenantSettings = default!;
    private DiagnosticRetention diagnosticsRetention = default!;
    private SqliteDiagnosticsLog diagnostics = default!;
    private AuthorizationPolicy policy = default!;
    private TeamPaths teamPaths = default!;
    private FileBrowserPolicy fileBrowser = default!;
    private List<AgentDefinition> loadedCatalog = default!;
    private IReadOnlyList<string> unmeasuredHeadlessPresets = default!;
    private PluginCatalog pluginCatalog = default!;
    private IDisposable pluginEvents = default!;
    private HeadroomGate headroom = default!;
    private WorkerPool workers = default!;
    private WipLedger wip = default!;
    private string memberBaseAddress = default!;
    private Action? recordConciergeSessions = default!;
    private WebApplication app = default!;
    private List<string> missingPrerequisites = default!;
    private string? schemaBackup = default!;
    private string? workerKey = default!;
    private IReadOnlyDictionary<int, int> previewPorts = default!;

    internal void Run()
    {
        app.Run();

        // An orderly close, and nothing rests on it: every write is already flushed, which is what makes
        // the file survive a KILL rather than only a shutdown. This is the tidy half of the same promise.
        hostLog.Dispose();
    }

    /// <summary>The data root's lock, held from the operator phase until the Host is gone.</summary>
    public void Dispose() => held?.Dispose();

    // SignalR has its OWN serialiser and does not read ConfigureHttpJsonOptions below. Configuring only
    // that one would let /api/overview answer `"state": "Idle"` while every containerChanged push
    // carried `"state": 1`, and the SPA's ContainerCard compares `snapshot.state === 'Running'`. A
    // number never equals that string, so a worker's badge would read `idle` through a whole run while
    // its own activity feed - fetched over HTTP - updated beside it. Nothing would fail anywhere; the
    // comparison would simply always be false.
    //
    // The two serialisers are the thing to remember here. Every future converter, naming policy or
    // option belongs on BOTH, and a browser reading one contract from two transports cannot tell you
    // which of them you forgot.
    static void ConfigureHostJson(JsonSerializerOptions options)
    {
        options.TypeInfoResolverChain.Insert(0, HostJsonContext.Default);
        options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
        options.Converters.Add(new JsonStringEnumConverter());
    }


    // The workers a terminal or a file read goes to. The run worker is resolved first: in one process
    // that is what connects the Host's own worker to the pool.
    static WorkerPool WorkersOf(IServiceProvider services)
    {
        _ = services.GetRequiredService<IRunWorker>();
        return services.GetRequiredService<WorkerPool>();
    }
}
