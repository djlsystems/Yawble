using System.Text.RegularExpressions;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>Body of <c>POST /api/mcp-servers</c> and <c>PUT /api/mcp-servers/{id}</c>.</summary>
public sealed record SaveMcpServer(
    string? Name,
    string? Kind,
    string? Url,
    string? Connection,
    string? Field,
    string? Header,
    string? Template,
    string? Command,
    string? Package,
    string? Version,
    IReadOnlyList<string?>? Args,
    IReadOnlyList<SaveMcpServerEnv?>? Env);

/// <summary>One environment variable of an entry the platform runs: a connection's id and one of its fields.</summary>
public sealed record SaveMcpServerEnv(string? Name, string? Connection, string? Field);

/// <summary>How a check went: <c>ok</c> with the tools listed, <c>refused</c> or <c>failed</c>, in one sentence.</summary>
public sealed record McpServerCheckAnswer(string Outcome, string Sentence, IReadOnlyList<string>? Tools)
{
    public object View => new { outcome = Outcome, sentence = Sentence, tools = Tools, toolCount = Tools?.Count };
}

/// <summary>
/// THE INSTANCE'S MCP SERVERS (MCP 1). An administrator keeps the list; a person gives entries to teams in
/// Team settings. An entry is a WEB ADDRESS, checked on every save and by its Test - initialize, then list
/// the tools - on a worker, as Test connection is; or one THE PLATFORM RUNS (a command, a package at a pinned
/// version, fixed arguments, environment variables each filled from a connection's field), stored and
/// validated here and started by MCP 3. A refused credential marks the entry and its connection Needs you;
/// a server not reached fails the check and marks neither. No answer, row or log line holds a value or the
/// URL's query. See <c>docs/mcp-servers.md</c>.
/// </summary>
public sealed partial class McpServers(IMcpServerStore store, Connections connections, TimeProvider clock)
{
    public IMcpServerStore Store => store;

    public Connections Connections => connections;

    public const int MaximumName = 20;

    public const string DefaultHeader = "Authorization";

    public const string DefaultTemplate = "Bearer {value}";

    public const string Placeholder = "{value}";

    /// <summary>What an entry the platform runs's health says until MCP 3 starts and checks it.</summary>
    public const string CheckedLater = "checked when MCP 3 is in place";

    public const string Where = "Admin → MCP servers";

    public static readonly IReadOnlyList<string> Commands = ["npx", "uvx"];

    [GeneratedRegex("^[A-Za-z0-9_-]{1,20}$")]
    private static partial Regex NameRule();

    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]+$")]
    private static partial Regex HeaderRule();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvRule();

    /// <summary>An npm version that names one release: major.minor.patch, with an optional pre-release and build.</summary>
    [GeneratedRegex(@"^v?\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex NpmExact();

    /// <summary>A Python (PEP 440) version that names one release, as <c>==</c> pins it.</summary>
    [GeneratedRegex(@"^\d+(\.\d+)*((a|b|rc)\d+)?(\.post\d+)?(\.dev\d+)?(\+[A-Za-z0-9.]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PythonExact();

    [GeneratedRegex(@"^(@[A-Za-z0-9._-]+/)?[A-Za-z0-9._-]+$")]
    private static partial Regex NpmPackage();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+(\[[A-Za-z0-9._,-]+\])?$")]
    private static partial Regex PythonPackage();

    public static string NameRuleSentence =>
        $"A name is 1 to {MaximumName} letters, digits, `-` or `_`, with no spaces: it prefixes the server's tools' names.";

    public static string NameTaken(string name) => $"There is already an MCP server named '{name}'. Choose another name.";

    public static string NotPinned(string package) =>
        $"A version must be pinned: give {package} one exact version, such as 1.4.2 - not `latest`, a range, or nothing.";

    // ---- saving ---------------------------------------------------------------------------------------

    /// <summary>
    /// <paramref name="request"/> as the record to store and the whole URL to seal (null keeps the stored
    /// one on an edit), or the sentence it is refused with. <paramref name="before"/> is the entry an edit
    /// changes.
    /// </summary>
    public async Task<(McpServerRecord? Server, string? Url, string? Error)> ReadAsync(
        SaveMcpServer request, McpServerRecord? before, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? before?.Name ?? "";
        if (!NameRule().IsMatch(name)) return (null, null, NameRuleSentence);

        var kind = request.Kind?.Trim() ?? before?.Kind;
        var now = clock.GetUtcNow();
        var id = before?.Id ?? $"mcp-{Guid.NewGuid():N}";

        var blank = new McpServerRecord(
            id, name, kind ?? "", null, null, null, null, null, null, null, null, [], [], McpServerRecord.Unchecked, null, null, null,
            before?.CreatedAt ?? now, now);

        return kind switch
        {
            McpServerRecord.Web => await WebAsync(request, before, blank, ct),
            McpServerRecord.Runs => await RunsAsync(request, blank, ct),
            _ => (null, null, "`kind` is `url` (a web address) or `runs` (the platform runs it)."),
        };
    }

    private async Task<(McpServerRecord?, string?, string?)> WebAsync(
        SaveMcpServer request, McpServerRecord? before, McpServerRecord blank, CancellationToken ct)
    {
        string? url = null;
        string? shown;

        if (!string.IsNullOrWhiteSpace(request.Url))
        {
            if (!Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return (null, null, "The address must be a whole web address starting https:// or http://.");
            }

            if (uri.UserInfo.Length > 0)
            {
                return (null, null, "Do not put a username or password in the address: keep the credential in a Connection and name it here.");
            }

            url = uri.AbsoluteUri;
            shown = Shown(uri);
        }
        else if (before is { Kind: McpServerRecord.Web, UrlShown: { } kept })
        {
            shown = kept;
        }
        else
        {
            return (null, null, "A web address entry needs its address (`url`).");
        }

        var connectionId = request.Connection?.Trim();
        if (string.IsNullOrEmpty(connectionId)) return (blank with { Kind = McpServerRecord.Web, UrlShown = shown }, url, null);

        if (await connections.Store.GetAsync(connectionId, ct) is not { } connection) return (null, null, $"There is no connection '{connectionId}'.");

        if (connection.Kind != ConnectionKinds.ApiKey || connection.Credential is not { } credential)
        {
            return (null, null, $"Connection {connection.Named} is not an API key. An MCP server at a web address is sent an API key connection's value.");
        }

        var masked = credential.Fields.Where(f => f.Masked).ToList();
        var field = request.Field?.Trim();
        if (string.IsNullOrEmpty(field))
        {
            if (masked.Count != 1)
            {
                return (null, null, $"Connection {connection.Named} holds {(masked.Count == 0 ? "no secret field" : "more than one secret field")}: name the field to send (`field`).");
            }

            field = masked[0].Name;
        }
        else if (!credential.Fields.Any(f => f.Name == field))
        {
            return (null, null, $"Connection {connection.Named} has no field `{field}`.");
        }

        var header = string.IsNullOrWhiteSpace(request.Header) ? DefaultHeader : request.Header.Trim();
        if (!HeaderRule().IsMatch(header)) return (null, null, "The header name may hold only letters, digits and `-`, as `Authorization` or `X-API-Key` do.");

        var template = string.IsNullOrWhiteSpace(request.Template) ? DefaultTemplate : request.Template.Trim();
        if (!template.Contains(Placeholder, StringComparison.Ordinal))
        {
            return (null, null, $"The value template must hold {Placeholder} where the connection's value goes, as `{DefaultTemplate}` does.");
        }

        if (template.Contains('\r') || template.Contains('\n')) return (null, null, "The value template is one line.");

        return (blank with
        {
            Kind = McpServerRecord.Web, UrlShown = shown, ConnectionId = connection.Id, Field = field, HeaderName = header, ValueTemplate = template,
        }, url, null);
    }

    private async Task<(McpServerRecord?, string?, string?)> RunsAsync(SaveMcpServer request, McpServerRecord blank, CancellationToken ct)
    {
        var command = request.Command?.Trim();
        if (command is null || !Commands.Contains(command, StringComparer.Ordinal)) return (null, null, "The command is `npx` or `uvx`.");

        var package = request.Package?.Trim() ?? "";
        if (package.Length == 0) return (null, null, "Name the package the platform runs.");

        // A VERSION WRITTEN INTO THE PACKAGE ("name@1.2.3", "name==1.2.3") is the version; written in both places they must agree.
        var (bare, inline) = Split(command, package);
        var version = request.Version?.Trim();
        if (string.IsNullOrEmpty(version)) version = inline;
        else if (inline is not null && inline != version) return (null, null, $"The package names version {inline} and the version says {version}: give one.");

        var npm = command == "npx";
        if (!(npm ? NpmPackage() : PythonPackage()).IsMatch(bare)) return (null, null, NotPinned(bare));
        if (string.IsNullOrEmpty(version) || !(npm ? NpmExact() : PythonExact()).IsMatch(version)) return (null, null, NotPinned(bare));

        var args = new List<string>();
        foreach (var arg in request.Args ?? [])
        {
            if (arg is null) return (null, null, "An argument may not be empty.");
            if (arg.Contains('\n') || arg.Contains('\r') || arg.Contains('\0')) return (null, null, "An argument is one line.");
            args.Add(arg);
        }

        var env = new List<McpServerEnv>();
        foreach (var given in request.Env ?? [])
        {
            var name = given?.Name?.Trim() ?? "";
            if (!EnvRule().IsMatch(name)) return (null, null, $"`{name}` is not an environment variable's name: letters, digits and `_`, not starting with a digit.");
            if (env.Any(e => e.Name == name)) return (null, null, $"Environment variable `{name}` is named twice.");

            var connectionId = given?.Connection?.Trim() ?? "";
            if (await connections.Store.GetAsync(connectionId, ct) is not { } connection)
            {
                return (null, null, $"Environment variable `{name}` names no connection that exists: choose one in {Connections.Where}.");
            }

            var field = given?.Field?.Trim() ?? "";
            if (connection.Credential is not { } credential || !credential.Fields.Any(f => f.Name == field))
            {
                return (null, null, $"Environment variable `{name}`: connection {connection.Named} has no field `{field}`.");
            }

            env.Add(new McpServerEnv(name, connection.Id, field));
        }

        return (blank with { Kind = McpServerRecord.Runs, Command = command, Package = bare, Version = version, Args = args, Env = env }, null, null);
    }

    /// <summary>A package as written, and the version written into it: <c>@scope/name@1.2.3</c> for npm,
    /// <c>name==1.2.3</c> or <c>name@1.2.3</c> for Python. A range written in is kept, to be refused.</summary>
    public static (string Package, string? Version) Split(string command, string package)
    {
        if (command == "uvx")
        {
            var pinned = package.IndexOf("==", StringComparison.Ordinal);
            if (pinned > 0) return (package[..pinned], package[(pinned + 2)..]);

            var range = package.IndexOfAny(['<', '>', '=', '~', '!', '@', ' ']);
            return range > 0 ? (package[..range], package[range..].TrimStart('@')) : (package, null);
        }

        var at = package.LastIndexOf('@');
        return at > 0 ? (package[..at], package[(at + 1)..]) : (package, null);
    }

    /// <summary>A URL as every answer and row shows it: no query, no fragment.</summary>
    public static string Shown(Uri uri) => uri.GetLeftPart(UriPartial.Path);

    // ---- checking -------------------------------------------------------------------------------------

    /// <summary>
    /// A WEB ADDRESS ENTRY'S CHECK, on a worker: initialize, then list the tools, with the connection's value
    /// in the named header. Recorded with an <c>mcp.server-checked</c> row: ok keeps the tools and clears a
    /// mark; refused marks the entry Needs you and its connection too; failed marks neither. A check no worker
    /// ran records nothing.
    /// </summary>
    public async Task<McpServerCheckAnswer> CheckAsync(ConnectionActor actor, McpServerRecord server, WorkerAsks asks, CancellationToken ct)
    {
        if (server.Kind != McpServerRecord.Web)
        {
            return new McpServerCheckAnswer(ConnectionTested.Failed, $"MCP server '{server.Name}' is run by the platform: it is {CheckedLater}.", null);
        }

        var (target, error) = await TargetAsync(server, ct);
        if (target is null)
        {
            return await RecordAsync(actor, server, ConnectionTested.Failed, error!.Sentence, null, error.Secrets, ct);
        }

        var (url, headers, secrets, connection) = (target.Url, target.Headers, target.Secrets, target.Connection);

        var probe = new McpProbe(server.Name, url, headers, secrets);
        var asked = await asks.AskAsync<ConnectionTested>(new TestConnection(WorkerAsks.NewRequest(), probe.Seal()), Connections.TestBound, ct);
        if (asked.Answer is not { } tested)
        {
            return new McpServerCheckAnswer(ConnectionTested.Failed, $"The check did not run: {asked.Why} Nothing was recorded; test it again.", null);
        }

        var sentence = ConnectionTester.Redact(tested.Sentence, secrets);

        if (tested.Outcome == ConnectionTested.Refused && connection is not null)
        {
            await connections.MarkFromMcpServerAsync(connection.Id, $"MCP server '{server.Name}' was refused: {sentence}", server.Name, ct);
        }

        // TWO TOOLS THAT WOULD SHARE A NAME on the harness server (MCP 2) refuse the save, naming both: this entry's
        // own, or one of this entry's and one another web address entry last listed. Nothing listed is kept.
        if (tested.Outcome == ConnectionTested.Ok && Clash(server, tested.Tools ?? [], await store.ListAsync(ct)) is { } clash)
        {
            return await RecordAsync(actor, server, ConnectionTested.Failed, clash, null, secrets, ct);
        }

        return await RecordAsync(actor, server, tested.Outcome, sentence, tested.Outcome == ConnectionTested.Ok ? tested.Tools ?? [] : null, secrets, ct);
    }

    /// <summary>The sentence naming two tools whose names on the harness server would be the same, or null.</summary>
    public static string? Clash(McpServerRecord server, IReadOnlyList<string> tools, IReadOnlyList<McpServerRecord> all)
    {
        if (tools.FirstOrDefault(t => McpToolNames.Platform.Contains(McpToolNames.For(server.Name, t))) is { } platform)
        {
            var name = McpToolNames.For(server.Name, platform);
            return $"Tool `{platform}` of {server.Name} and the platform's own tool `{name}` would both be named `{name}` for members. "
                + "Rename the entry or ask the server's owner to rename the tool; nothing listed was kept.";
        }

        var listed = tools.Select(t => (Server: server.Name, Tool: t))
            .Concat(all.Where(o => o.Id != server.Id && o.Kind == McpServerRecord.Web && o.Name != server.Name)
                .SelectMany(o => (o.Tools ?? []).Select(t => (Server: o.Name, Tool: t))));

        foreach (var clash in McpToolNames.Clashes(listed))
        {
            return $"Tool `{clash.First.Tool}` of {clash.First.Server} and tool `{clash.Second.Tool}` of {clash.Second.Server} would both be named "
                + $"`{clash.Name}` for members. Rename the entry or ask the server's owner to rename one; nothing listed was kept.";
        }

        return null;
    }

    /// <summary>A web address entry as a request reaches it: its whole URL, the header the template makes from its
    /// connection's value, and every value either holds (the query's parts, the field's value, the header sent).</summary>
    public sealed record McpTarget(string Url, IReadOnlyDictionary<string, string> Headers, IReadOnlyList<string> Secrets, ConnectionRecord? Connection);

    /// <summary>Why an entry cannot be reached from this Host, and the values the sentence must not carry.</summary>
    public sealed record McpTargetError(string Sentence, IReadOnlyList<string> Secrets);

    /// <summary>
    /// How a web address entry is reached (MCP 1's check and MCP 2's calls alike): the URL read back from its
    /// ciphertext and the connection's value in the named header. Null with the sentence when the address or the
    /// value cannot be read on this Host.
    /// </summary>
    public async Task<(McpTarget? Target, McpTargetError? Error)> TargetAsync(McpServerRecord server, CancellationToken ct)
    {
        if (await store.UrlAsync(server.Id, ct) is not { } url)
        {
            return (null, new McpTargetError(
                $"The address of MCP server '{server.Name}' could not be read on this Host. Edit it in {Where} and give the address again.", []));
        }

        var secrets = new List<string>();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Query.Length > 1)
        {
            secrets.Add(uri.Query);
            secrets.Add(uri.Query.TrimStart('?'));
            secrets.AddRange(uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2).ElementAtOrDefault(1) ?? "")
                .SelectMany(v => new[] { v, Uri.UnescapeDataString(v) }));
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ConnectionRecord? connection = null;

        if (server.ConnectionId is { } connectionId)
        {
            connection = await connections.Store.GetAsync(connectionId, ct);
            var values = connection is null ? null : await connections.Store.CredentialSecretsAsync(connection.Id, ct);
            var field = connection?.Credential?.Fields.FirstOrDefault(f => f.Name == server.Field);
            var value = field is null ? null : field.Masked ? values?.GetValueOrDefault(field.Name) : field.Value;

            if (connection is null || value is null)
            {
                return (null, new McpTargetError(
                    connection is null
                        ? $"The connection MCP server '{server.Name}' is sent no longer exists. Choose another in {Where}."
                        : $"The value of `{server.Field}` in connection {connection.Named} could not be read on this Host. Edit the connection in {Connections.Where}.",
                    secrets));
            }

            var sent = server.ValueTemplate!.Replace(Placeholder, value, StringComparison.Ordinal);
            headers[server.HeaderName!] = sent;
            secrets.Add(value);
            secrets.Add(sent);
        }

        return (new McpTarget(url, headers, secrets, connection), null);
    }

    private async Task<McpServerCheckAnswer> RecordAsync(
        ConnectionActor actor, McpServerRecord server, string outcome, string sentence, IReadOnlyList<string>? tools,
        IReadOnlyList<string> secrets, CancellationToken ct)
    {
        sentence = ConnectionTester.Redact(sentence, secrets);

        var status = outcome switch
        {
            ConnectionTested.Ok => McpServerRecord.Ok,
            ConnectionTested.Refused => McpServerRecord.NeedsYou,
            _ => null,
        };

        var reason = status == McpServerRecord.NeedsYou
            ? $"{sentence} Fix the credential{(server.ConnectionId is null ? "" : " in its connection")}, then test it again."
            : null;

        await store.RecordCheckAsync(server.Id, status, reason, tools, clock.GetUtcNow(), actor.Row(
            TenantActions.McpServerChecked, server.Id, server.Name,
            new { server = server.Id, name = server.Name, url = server.UrlShown, outcome, sentence, toolCount = tools?.Count }), ct);

        return new McpServerCheckAnswer(outcome, sentence, tools);
    }

    // ---- what a row says ------------------------------------------------------------------------------

    /// <summary>An entry as its added, edited and removed rows describe it: never a value or the URL's query.</summary>
    public static object Detail(McpServerRecord server, string? connectionName, object? extra = null) => new
    {
        server = server.Id,
        name = server.Name,
        kind = server.Kind,
        url = server.UrlShown,
        connection = server.ConnectionId,
        connectionName,
        header = server.HeaderName,
        command = server.Command,
        package = server.Package,
        version = server.Version,
        env = server.Env.Select(e => new { name = e.Name, connection = e.Connection, field = e.Field }),
        extra,
    };

    /// <summary>The settings an edit changed, by their names on the route; never a value.</summary>
    public static IReadOnlyList<string> Changed(McpServerRecord before, McpServerRecord after, bool urlGiven)
    {
        var changed = new List<string>();
        if (before.Name != after.Name) changed.Add("name");
        if (before.Kind != after.Kind) changed.Add("kind");
        if (before.UrlShown != after.UrlShown || urlGiven && before.Kind == McpServerRecord.Web) changed.Add("url");
        if (before.ConnectionId != after.ConnectionId) changed.Add("connection");
        if (before.Field != after.Field) changed.Add("field");
        if (before.HeaderName != after.HeaderName) changed.Add("header");
        if (before.ValueTemplate != after.ValueTemplate) changed.Add("template");
        if (before.Command != after.Command) changed.Add("command");
        if (before.Package != after.Package) changed.Add("package");
        if (before.Version != after.Version) changed.Add("version");
        if (!before.Args.SequenceEqual(after.Args)) changed.Add("args");
        if (!before.Env.SequenceEqual(after.Env)) changed.Add("env");
        return changed;
    }
}
