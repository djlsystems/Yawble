using System.Text.Json;

namespace Harness.Host;

/// <summary>
/// THE OPERATOR CLI'S AGENT CREDENTIALS: the CLI reaches the Host the way <c>connect</c> does
/// (<see cref="ConnectRequests"/>), trading a request file for a report file through the container
/// engine - never over HTTP with a person's key, and never with the value on a command line.
///
/// <para>
/// The CLI writes <c>&lt;dataRoot&gt;/agent-credentials/.request</c> from its STANDARD INPUT:
/// <c>{ request: nonce, action: set|clear|source|status, agent, kind, value, source }</c>, where
/// <c>agent</c> is a preset or a command, as the routes take it. Twice a second this service reads
/// it, DELETES IT BEFORE ACTING - so the value sits on disk for one poll at most - and answers in
/// <c>.request-report.json</c>: <c>{ request, status, body }</c>, the status and body the route
/// would give. The value is never in the report, a log or a tenant row.
/// </para>
///
/// <para>
/// WHO CAN ASK: whoever can write in that folder, which is the Host's own, mode 0700 - root at the
/// engine and the Host. An agent can neither write a request nor read one: a folder that is a link,
/// is owned by another user, or that any other user can read or write is refused: a request in it is
/// deleted unanswered, and the CLI is told why when that is safe (<see cref="RefuseAsync"/>). The requester is the operator (<see cref="CredentialActor.Operator"/>), and its
/// rows say so. A request left from before a restart is deleted unanswered at start.
/// </para>
/// </summary>
public sealed class AgentCredentialRequests(AgentCredentials credentials, string dataRoot, ILogger<AgentCredentialRequests> logger)
    : BackgroundService
{
    public const string Folder = "agent-credentials";

    public const string RequestFile = ".request";

    public const string ReportFile = ".request-report.json";

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
                // The type alone: a message could quote what the request held.
                logger.LogWarning("An agent credential request could not be answered: {Kind}.", exception.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// The folder, the Host's alone, made at start so the CLI finds it owned right; and any request
    /// a stopped Host left in it deleted unanswered, so a value never outlives a restart.
    /// </summary>
    public void Prepare()
    {
        try
        {
            if (!Directory.Exists(Root))
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Root);
                else Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            foreach (var left in Directory.EnumerateFiles(Root, RequestFile + "*"))
            {
                if (Path.GetFileName(left) == ReportFile) continue;
                File.Delete(left);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The agent credentials folder could not be made or cleared: {Message}", exception.Message);
        }
    }

    /// <summary>Answers a pending request, if there is one. Returns whether it did.</summary>
    public async Task<bool> AnswerAsync(CancellationToken ct)
    {
        var request = Path.Combine(Root, RequestFile);

        if (!File.Exists(request)) return false;

        if (FolderRefusal() is { } refusal)
        {
            if (_warned != refusal) logger.LogWarning("Agent credential requests are not answered: {Refusal}", refusal);
            _warned = refusal;
            return await RefuseAsync(request, ct);
        }

        _warned = null;

        if (new FileInfo(request).LinkTarget is not null)
        {
            File.Delete(request);
            return false;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(request, ct);
        }
        finally
        {
            // BEFORE ACTING, read or not: the request may hold a value, and nothing below needs the file.
            File.Delete(request);
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        var nonce = Text(root, "request");
        if (string.IsNullOrWhiteSpace(nonce)) return false;

        var agent = Text(root, "agent") ?? "";
        var (status, body) = Text(root, "action") switch
        {
            "set" => await credentials.SetAsync(agent, Text(root, "kind"), Text(root, "value"), CredentialActor.Operator, ct),
            "clear" => await credentials.ClearAsync(agent, CredentialActor.Operator, ct),
            "source" => await credentials.SourceAsync(agent, Text(root, "source"), CredentialActor.Operator, ct),
            "status" => await credentials.StatusAsync(agent, ct),
            _ => (400, (object)new { error = "The action must be set, clear, source or status." }),
        };

        await WriteReportAsync(new { request = nonce, status, body }, ct);
        return true;
    }

    /// <summary>
    /// A request in a folder that cannot be trusted: DELETED UNANSWERED, never left on disk, since it
    /// may hold a value. When the folder is still the Host's own and only too open to read - not a
    /// link, not another user's, not writable by others, so nobody else can plant a file where the
    /// report is written - the CLI is also told why, in a report with status 503 and
    /// <see cref="FolderRefused"/>, which names nothing the request held. Otherwise nothing is written
    /// there, and the CLI sees its request gone with no report. Returns whether a report was written.
    /// </summary>
    private async Task<bool> RefuseAsync(string request, CancellationToken ct)
    {
        var reportable = ConnectRequests.FolderRefusal(Root) is null;

        string? nonce = null;
        try
        {
            if (reportable && new FileInfo(request).LinkTarget is null)
            {
                nonce = Text(JsonDocument.Parse(await File.ReadAllTextAsync(request, ct)).RootElement, "request");
            }
        }
        catch (JsonException)
        {
            // Unreadable as a request: deleted below, unanswered.
        }
        finally
        {
            File.Delete(request);
        }

        if (string.IsNullOrWhiteSpace(nonce)) return false;

        await WriteReportAsync(new { request = nonce, status = 503, body = new { error = FolderRefused } }, ct);
        return true;
    }

    /// <summary>The reason a refused folder's report gives: fixed, naming nothing the request held.</summary>
    public const string FolderRefused =
        "The Host did not act on this request and deleted it: its request folder can be read by other users, "
        + "and it must be the Host's alone (mode 0700). Nothing was changed; the Host's log says what is wrong.";

    /// <summary>Why the folder cannot be trusted, or null: <see cref="ConnectRequests.FolderRefusal(string)"/>,
    /// and nobody but the Host may read it either, because a request can hold a value.</summary>
    public string? FolderRefusal()
    {
        if (ConnectRequests.FolderRefusal(Root) is { } refusal) return refusal;
        if (OperatingSystem.IsWindows()) return null;

        return (File.GetUnixFileMode(Root) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0
            ? $"{Root} can be read by others; it must be 0700."
            : null;
    }

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
}
