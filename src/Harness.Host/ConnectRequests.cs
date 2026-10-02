using System.Runtime.InteropServices;
using System.Text.Json;

namespace Harness.Host;

/// <summary>
/// THE OPERATOR CLI'S <c>connect</c>: the CLI reaches the Host the way every command that talks to
/// a running instance does - through the container engine, trading a request file for a report file
/// (as <see cref="PluginRescanRequests"/> does for installs) - never over HTTP with a person's key.
///
/// <para>
/// The CLI writes <c>&lt;dataRoot&gt;/connections/.connect</c>:
/// <c>{ request: nonce, op: start|complete|list|remove, ...the route's body }</c>. Once a second this
/// service answers it in <c>.connect-report.json</c> carrying that nonce and the status and body the
/// route would give, then removes the request. The fields per op are the bodies of
/// <c>POST /api/connections/start</c>, <c>POST /api/connections/complete</c> and the id of
/// <c>DELETE /api/connections/{id}</c>.
/// </para>
///
/// <para>
/// WHO CAN ASK: whoever can write in that folder, which is the Host's own, mode 0700 - root at the
/// engine and the Host. An agent cannot: a folder that is a link, is owned by another user, or that
/// any other user can write in is refused and nothing in it is answered. The requester is the fixed operator principal; a state it started is
/// finished only through this same exchange, and no report carries a token or a secret.
/// </para>
/// </summary>
public sealed class ConnectRequests(Connections connections, string dataRoot, ILogger<ConnectRequests> logger, TeamRegistry? teams = null)
    : BackgroundService
{
    public const string Folder = "connections";

    public const string RequestFile = ".connect";

    public const string ReportFile = ".connect-report.json";

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string? _answered;

    private string? _warned;

    public string Root => Path.Combine(dataRoot, Folder);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Prepare();

        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await AnswerAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("A connect request could not be answered: {Kind}.", exception.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>The folder, the Host's alone. Made at start so the CLI finds it already owned right.</summary>
    public void Prepare()
    {
        try
        {
            if (!Directory.Exists(Root))
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Root);
                else Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The connections folder could not be made: {Message}", exception.Message);
        }
    }

    /// <summary>Answers a pending request, if there is one. Returns whether it did.</summary>
    public async Task<bool> AnswerAsync(CancellationToken ct)
    {
        var request = Path.Combine(Root, RequestFile);

        if (!File.Exists(request)) return false;

        // THE FOLDER IS THE CREDENTIAL: one another user could write in, or own, answers nobody.
        if (FolderRefusal() is { } refusal)
        {
            // Said once, not every tick; said again if it is put right and then goes wrong.
            if (_warned != refusal) logger.LogWarning("Connect requests are not answered: {Refusal}", refusal);
            _warned = refusal;
            return false;
        }

        _warned = null;

        if (new FileInfo(request).LinkTarget is not null) return false;

        JsonElement root;

        try
        {
            root = JsonDocument.Parse(await File.ReadAllTextAsync(request, ct)).RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        var nonce = Text(root, "request");
        if (string.IsNullOrWhiteSpace(nonce) || nonce == _answered) return false;

        object report = Text(root, "op") switch
        {
            "start" => await StartAsync(nonce, root, ct),
            "complete" => await CompleteAsync(nonce, root, ct),
            "list" => await ListAsync(nonce, ct),
            "remove" => await RemoveAsync(nonce, root, ct),
            var other => new { request = nonce, status = 400, error = $"'{other}' is not an op: start, complete, list or remove." },
        };

        await WriteReportAsync(report, ct);

        _answered = nonce;

        try
        {
            File.Delete(request);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The nonce is remembered, so a request that stays is not answered twice.
        }

        return true;
    }

    /// <summary>Why the folder cannot be trusted, or null: it must be a real folder (not a link), owned
    /// by the Host's own user, that no group or other user can write in.</summary>
    public string? FolderRefusal() => FolderRefusal(Root);

    /// <summary><see cref="FolderRefusal()"/> for any request folder the Host answers from.</summary>
    public static string? FolderRefusal(string root)
    {
        if (OperatingSystem.IsWindows()) return null;

        if (new DirectoryInfo(root).LinkTarget is not null) return $"{root} is a symbolic link; it must be the Host's own folder.";

        if ((File.GetUnixFileMode(root) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
        {
            return $"{root} is writable by others; it must be 0700.";
        }

        var owner = OwnerOf(root);
        if (owner is null) return $"{root}'s owner could not be read.";
        if (owner != geteuid()) return $"{root} is owned by uid {owner}, not the Host's uid {geteuid()}.";

        return null;
    }

    /// <summary>The owner's uid, from <c>statx</c> (its layout is the same on every architecture),
    /// not following a link.</summary>
    private static uint? OwnerOf(string path)
    {
        const int AtFdCwd = -100;
        const int AtSymlinkNoFollow = 0x100;
        const uint StatxUid = 0x8;
        const int UidOffset = 20;

        var buffer = new byte[256];
        if (statx(AtFdCwd, path, AtSymlinkNoFollow, StatxUid, buffer) != 0) return null;
        if ((BitConverter.ToUInt32(buffer, 0) & StatxUid) == 0) return null;

        return BitConverter.ToUInt32(buffer, UidOffset);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int dirfd, string pathname, int flags, uint mask, byte[] statxbuf);

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    private async Task<object> StartAsync(string nonce, JsonElement root, CancellationToken ct)
    {
        var (start, error) = await connections.StartAsync(
            ConnectionActor.Operator,
            new StartConnection(
                Text(root, "provider"), Strings(root, "scopes"), Text(root, "name"), Text(root, "reconnectId"), Text(root, "redirectUri")),
            origin: null, ct);

        return error is not null
            ? new { request = nonce, status = 400, error }
            : new
            {
                request = nonce,
                status = 200,
                error = (string?)null,
                start = new { authorizationUrl = start!.AuthorizationUrl, state = start.State, redirectUri = start.RedirectUri, expiresAt = start.ExpiresAt },
            };
    }

    private async Task<object> CompleteAsync(string nonce, JsonElement root, CancellationToken ct)
    {
        var (connection, _, error) = await connections.CompleteAsync(
            Text(root, "state"), Text(root, "code"), ConnectionActor.Operator, viaCallback: false, ct: ct);

        return error is not null
            ? new { request = nonce, status = 400, error }
            : new { request = nonce, status = 200, error = (string?)null, connection = await ViewAsync(connection!, ct) };
    }

    private async Task<object> ListAsync(string nonce, CancellationToken ct)
    {
        var uses = await connections.Store.AllUsesAsync(ct);
        var list = await connections.Store.ListAsync(ct);

        return new
        {
            request = nonce,
            status = 200,
            error = (string?)null,
            connections = list.Select(c => teams is null ? (object)Bare(c, uses[c.Id]) : ConnectionEndpoints.View(c, uses[c.Id], teams)),
        };
    }

    private async Task<object> RemoveAsync(string nonce, JsonElement root, CancellationToken ct)
    {
        var id = Text(root, "id") ?? "";

        // `remove <name>`: the CLI may send the name; an id wins, then a unique name.
        if (await connections.Store.GetAsync(id, ct) is null)
        {
            var named = (await connections.Store.ListAsync(ct)).Where(c => string.Equals(c.Name, id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count == 1) id = named[0].Id;
            else if (named.Count > 1) return new { request = nonce, status = 409, error = $"More than one connection is called '{id}'; remove it by id." };
        }

        var (status, body) = teams is null
            ? (404, (object?)new { error = "Connections cannot be removed here." })
            : await ConnectionEndpoints.DisconnectAsync(connections, teams, id, ConnectionActor.Operator, ct);

        if (body is null) return new { request = nonce, status, error = (string?)null };

        var element = JsonSerializer.SerializeToElement(body, Json);
        return new
        {
            request = nonce,
            status,
            error = Text(element, "error"),
            usedBy = element.TryGetProperty("usedBy", out var usedBy) ? usedBy : (JsonElement?)null,
        };
    }

    private async Task<object> ViewAsync(ConnectionRecord connection, CancellationToken ct)
    {
        var uses = await connections.Store.UsedByAsync(connection.Id, ct);
        return teams is null ? Bare(connection, uses) : ConnectionEndpoints.View(connection, uses, teams);
    }

    private static object Bare(ConnectionRecord c, IEnumerable<ConnectionUse> uses) => new
    {
        id = c.Id, name = c.Name, provider = c.Provider, account = c.Account, scopes = c.Scopes,
        connectedAt = c.ConnectedAt, refreshedAt = c.RefreshedAt, status = c.Status, statusReason = c.StatusReason,
        usedBy = uses.Select(u => new { team = u.Team, member = u.Member, label = u.Member, slot = u.Slot }),
    };

    /// <summary>A .tmp then a rename, so the CLI never reads half a report; the Host's alone (0600).</summary>
    private async Task WriteReportAsync(object report, CancellationToken ct)
    {
        var target = Path.Combine(Root, ReportFile);
        var temporary = target + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, Json) + "\n", ct);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, target, overwrite: true);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string>? Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)]
            : null;
}
