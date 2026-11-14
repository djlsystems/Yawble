using System.Text.Json.Nodes;
using Yawble.Core.Ipc;

namespace Yawble.Core.Companion;

/// <summary>
/// yawble-companion in the running core: its control endpoint, the user-only control.json that tells the client where it
/// listens (removed on exit, only while it still names this core's address), and what <c>companion.status</c> shows the
/// window. Never fatal: a companion home that cannot be secured (a populated YAWBLE_HOME, a folder the user may not change)
/// or an endpoint that cannot be opened leaves the core running without the endpoint; the reason is logged and shown.
/// </summary>
public sealed class CompanionHost : IAsyncDisposable
{
    private readonly CompanionServer? _server;
    private readonly string? _createdRuntimeDir;
    private int _disposed;

    private CompanionHost(string? home, string? binaryPath, CompanionServer? server, string? problem, string? createdRuntimeDir)
    {
        Home = home;
        BinaryPath = binaryPath;
        _server = server;
        Problem = problem;
        _createdRuntimeDir = createdRuntimeDir;
    }

    /// <summary>The companion home; null when YAWBLE_HOME does not name a usable path.</summary>
    public string? Home { get; }
    public string? BinaryPath { get; }

    /// <summary>Where the control endpoint listens; null when it is off.</summary>
    public string? Address => _server?.Address;

    /// <summary>Why the endpoint is off (as the person reads it; never a key); null while it listens.</summary>
    public string? Problem { get; }

    /// <summary>As <see cref="Start(string, string, CompanionService, string?, Action{string}?)"/>, resolving the home first (YAWBLE_HOME or the default); a malformed one is said, never thrown.</summary>
    public static CompanionHost Start(Func<string, string?> env, string runtimeDir, CompanionService service, string? binaryPath, Action<string>? log)
    {
        if (ResolveHome(env, out var problem) is { } home) return Start(home, runtimeDir, service, binaryPath, log);
        log?.Invoke($"companion: {problem}");
        return new CompanionHost(null, binaryPath, null, problem, null);
    }

    /// <summary>The companion turned off for this launch (--no-control), or not started yet.</summary>
    public static CompanionHost Off(Func<string, string?> env, string? binaryPath, string reason) =>
        new(ResolveHome(env, out _), binaryPath, null, reason, null);

    private static string? ResolveHome(Func<string, string?> env, out string? problem)
    {
        try
        {
            problem = null;
            return ControlDiscovery.Home(env);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException)
        {
            problem = $"yawble-companion is off: {ControlDiscovery.HomeVariable} does not name a usable folder ({e.GetType().Name}); fix or unset it and restart the app";
            return null;
        }
    }

    /// <summary>
    /// Opens the control endpoint (a fresh address; on POSIX a socket in a 0700 folder) and writes control.json under
    /// <paramref name="home"/>. Any failure to secure the home, open the endpoint or write the file is caught: the result
    /// then has no endpoint and says why.
    /// </summary>
    public static CompanionHost Start(string home, string runtimeDir, CompanionService service, string? binaryPath, Action<string>? log)
    {
        CompanionServer? server = null;
        string? created = null;
        try
        {
            // Secured first: nothing listens unless its discovery file can be written user-only.
            UserOnlyFile.EnsureDirectory(home);
            var dir = OperatingSystem.IsWindows() ? runtimeDir : PrivateRuntimeDir(runtimeDir, out created);
            server = CompanionServer.Start(IpcEndpoint.Create(CompanionServer.NewAddress(dir)), service);
            ControlDiscovery.Write(home, new ControlFile(CompanionProtocol.Version, server.Address, Environment.ProcessId, CoreInfo.Version, DateTimeOffset.UtcNow));
            return new CompanionHost(home, binaryPath, server, null, created);
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or IOException or NotSupportedException
                                       or ArgumentException or System.Security.SecurityException or System.Net.Sockets.SocketException)
        {
            if (server is not null) server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            DeleteDir(created);
            var problem = $"yawble-companion is off: {e.Message}";
            log?.Invoke($"companion: {problem}");
            return new CompanionHost(home, binaryPath, null, problem, null);
        }
    }

    /// <summary>The companion turned off for this launch (--no-control).</summary>
    public static CompanionHost Off(string? home, string? binaryPath, string reason) => new(home, binaryPath, null, reason, null);

    /// <summary>companion.status: {endpoint, controlFile, binaryPath, claudeCommand, problem}.</summary>
    public JsonObject Status() => new()
    {
        ["endpoint"] = Address,
        ["controlFile"] = Home is null ? null : ControlDiscovery.FilePath(Home),
        ["binaryPath"] = BinaryPath,
        ["claudeCommand"] = BinaryPath is null ? null : $"claude mcp add --scope user yawble -- \"{BinaryPath}\" mcp",
        ["problem"] = Problem,
    };

    /// <summary>yawble-companion the installer ships: &lt;install&gt;/bin/yawble-companion(.exe); null without an install folder.</summary>
    public static string? BinaryPathFor(string? installDir) =>
        string.IsNullOrEmpty(installDir) ? null : Path.Combine(installDir, "bin", OperatingSystem.IsWindows() ? "yawble-companion.exe" : "yawble-companion");

    /// <summary>
    /// What the companion's calls reach: the Concierge's own route (which refuses companion.*), plus the backups' list and a
    /// restore (BackupRpc refuses it unless the person approved this exact call in the window). companion.* never.
    /// </summary>
    public static Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> Route(
        Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> concierge,
        Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> backups) =>
        (method, p, ct) => CompanionRpc.Handles(method) ? throw new RpcException("forbidden", CompanionRpc.WindowOnly)
            : method is "ops.restore" or "ops.backups" ? backups(method, p, ct)
            : concierge(method, p, ct);

    /// <summary>
    /// Needs You: the other counts plus the pairings and action requests waiting for the person, for the person at this
    /// computer's window only. Nothing companion reaches the Cloud Link, not even a count (app.overview is a remote method).
    /// </summary>
    public static Func<int> NeedsYou(Func<int> others, CompanionService service) =>
        () => others() + (Provenance.Actor.Current?.Via == Provenance.Actor.App ? service.PendingCount : 0);

    /// <summary>
    /// A folder only this user can open for the control socket: <paramref name="dir"/> itself when it is already 0700 (the
    /// shell's own mkdtemp folder), otherwise a fresh 0700 folder inside it (removed on exit).
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static string PrivateRuntimeDir(string dir, out string? created)
    {
        const UnixFileMode only = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        created = null;
        dir = Path.GetFullPath(dir);
        if (Directory.Exists(dir) && File.GetUnixFileMode(dir) == only) return dir;
        var mine = Path.Combine(dir, "yawble-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)));
        Directory.CreateDirectory(mine, only);
        File.SetUnixFileMode(mine, only); // the umask may have narrowed it; never widened
        if (File.GetUnixFileMode(mine) != only) throw new UnauthorizedAccessException($"the folder '{mine}' could not be made user-only");
        created = mine;
        return mine;
    }

    private static void DeleteDir(string? dir)
    {
        if (dir is null) return;
        try { Directory.Delete(dir, recursive: false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1 || _server is null) return;
        if (Home is not null) ControlDiscovery.RemoveIfOurs(Home, _server.Address);
        await _server.DisposeAsync().ConfigureAwait(false);
        DeleteDir(_createdRuntimeDir);
    }
}
