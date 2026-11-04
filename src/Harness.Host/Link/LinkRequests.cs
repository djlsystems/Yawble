using System.Text.Json;

namespace Harness.Host.Link;

/// <summary>
/// THE OPERATOR CLI'S <c>attach</c> AND <c>detach</c>: the same exchange <see cref="ConnectRequests"/> keeps
/// for <c>connect</c> - through the container engine, a request file for a report file - never over HTTP.
///
/// <para>
/// The CLI writes <c>&lt;dataRoot&gt;/link/.attach</c> on stdin, never on a command line:
/// <c>{ request: nonce, op: attach|detach|status, url, token }</c>. Twice a second this service answers it in
/// <c>.attach-report.json</c> with that nonce, a status, the sentence the person reads and, for
/// <c>status</c>, the link as it stands - never the token, the credential or its ciphertext - then removes
/// the request. Whoever can write in that folder can ask, so it is held to the same checks as the connect
/// folder: the Host's own, 0700, not a link.
/// </para>
///
/// <para>
/// At start, <c>HARNESS_LINK_URL</c> and <c>HARNESS_LINK_TOKEN</c> attach an instance a container platform
/// started with no CLI: once, only while no link is stored, never over one.
/// </para>
/// </summary>
public sealed class LinkRequests(
    LinkAttach attach, ILinkStore store, string dataRoot, ILogger<LinkRequests> logger, ILinkSignal? signal = null,
    string? environmentUrl = null, string? environmentToken = null)
    : BackgroundService
{
    public const string Folder = "link";

    public const string RequestFile = ".attach";

    public const string ReportFile = ".attach-report.json";

    public const string UrlVariable = "HARNESS_LINK_URL";

    public const string TokenVariable = "HARNESS_LINK_TOKEN";

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string? _answered;

    private string? _warned;

    public string Root => Path.Combine(dataRoot, Folder);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Prepare();

        try
        {
            await AttachFromEnvironmentAsync(environmentUrl, environmentToken, stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("The attach named by {Variable} could not be made: {Kind}.", UrlVariable, exception.GetType().Name);
        }

        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await AnswerAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("An attach request could not be answered: {Kind}.", exception.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// The attach <c>HARNESS_LINK_URL</c> and <c>HARNESS_LINK_TOKEN</c> ask for: made only when both are set
    /// and no link is stored, so a restart with them still set, or a link made since, is left alone. Null
    /// when it was not made.
    /// </summary>
    public async Task<AttachOutcome?> AttachFromEnvironmentAsync(string? url, string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token)) return null;
        if (await store.GetAsync(ct) is not null) return null;

        var outcome = await attach.AttachAsync(url, token, LinkAttach.ViaEnvironment, ct);

        // The sentence names the service or the URL, never the token.
        if (outcome.Ok)
        {
            logger.LogInformation("{Variable}: {Sentence}", UrlVariable, outcome.Sentence);
            signal?.Changed();
        }
        else
        {
            logger.LogWarning("{Variable}: {Sentence}", UrlVariable, outcome.Sentence);
        }

        return outcome;
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
            logger.LogWarning("The link folder could not be made: {Message}", exception.Message);
        }
    }

    /// <summary>Answers a pending request, if there is one. Returns whether it did.</summary>
    public async Task<bool> AnswerAsync(CancellationToken ct)
    {
        var request = Path.Combine(Root, RequestFile);

        if (!File.Exists(request)) return false;

        // THE FOLDER IS THE CREDENTIAL: one another user could write in, or own, answers nobody.
        if (ConnectRequests.FolderRefusal(Root) is { } refusal)
        {
            if (_warned != refusal) logger.LogWarning("Attach requests are not answered: {Refusal}", refusal);
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
            "attach" => await AttachAsync(nonce, root, ct),
            "detach" => await DetachAsync(nonce, ct),
            "status" => await StatusAsync(nonce, ct),
            var other => new { request = nonce, status = 400, error = $"'{other}' is not an op: attach, detach or status." },
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

    private async Task<object> AttachAsync(string nonce, JsonElement root, CancellationToken ct)
    {
        var outcome = await attach.AttachAsync(Text(root, "url") ?? "", Text(root, "token") ?? "", ct);

        if (!outcome.Ok) return new { request = nonce, status = 400, error = outcome.Sentence, sentence = outcome.Sentence };

        signal?.Changed();
        return new { request = nonce, status = 200, error = (string?)null, sentence = outcome.Sentence, link = View(outcome.Link) };
    }

    private async Task<object> DetachAsync(string nonce, CancellationToken ct)
    {
        var sentence = await attach.DetachAsync(ct);

        if (sentence != LinkAttach.NotAttached) signal?.Changed();
        return new { request = nonce, status = 200, error = (string?)null, sentence };
    }

    private async Task<object> StatusAsync(string nonce, CancellationToken ct)
    {
        var link = await store.GetAsync(ct);
        return new { request = nonce, status = 200, error = (string?)null, sentence = link is null ? LinkAttach.NotAttached : null, link = View(link) };
    }

    /// <summary>The link as the CLI shows it: no credential, sealed or not.</summary>
    private static object? View(StoredLink? link) => link is null
        ? null
        : new
        {
            serviceUrl = link.ServiceUrl,
            serviceName = link.ServiceName,
            instanceName = link.InstanceName,
            attachedAt = link.AttachedAt,
            state = link.State.ToString().ToLowerInvariant(),
            stateSince = link.StateSince,
            reason = link.Reason,
            lastReportAt = link.LastReportAt,
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
}
