using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// One entry of the instance's MCP servers (MCP 1), as every route may show it: never a value, and never
/// the URL's query - <see cref="UrlShown"/> is the address without it. <see cref="Kind"/> is
/// <see cref="Web"/> (a web address, optionally sent a connection's value in a header) or
/// <see cref="Runs"/> (Yawble runs it: a command, a package at a pinned version, fixed arguments, and
/// environment variables each filled from a field of a connection).
/// </summary>
public sealed record McpServerRecord(
    string Id,
    string Name,
    string Kind,
    string? UrlShown,
    string? ConnectionId,
    string? Field,
    string? HeaderName,
    string? ValueTemplate,
    string? Command,
    string? Package,
    string? Version,
    IReadOnlyList<string> Args,
    IReadOnlyList<McpServerEnv> Env,
    string Status,
    string? StatusReason,
    IReadOnlyList<string>? Tools,
    DateTimeOffset? CheckedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public const string Web = "url";

    public const string Runs = "runs";

    /// <summary>Never checked, or a "Yawble runs it" entry, which MCP 3 checks.</summary>
    public const string Unchecked = "unchecked";

    public const string Ok = "ok";

    /// <summary>The server refused the credential: a person fixes the connection, then tests again.</summary>
    public const string NeedsYou = "needs-you";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string ListJson<T>(IReadOnlyList<T> list) => JsonSerializer.Serialize(list, Json);

    public static IReadOnlyList<T> ReadList<T>(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<T>>(json, Json) ?? [];
}

/// <summary>An environment variable of a "Yawble runs it" entry: filled from <see cref="Field"/> of
/// connection <see cref="Connection"/> (its id) when MCP 3 starts it. Never a value.</summary>
public sealed record McpServerEnv(string Name, string Connection, string Field);

/// <summary>One MCP server given to one team.</summary>
public sealed record TeamMcpServer(string Team, string ServerId, DateTimeOffset AssignedAt);

/// <summary>What an add or an edit came to.</summary>
public enum McpServerWrite
{
    Done,
    NameTaken,
    Missing,
}

/// <summary>
/// <c>mcp_servers</c> and <c>team_mcp_servers</c> (schema step auth-033). The whole URL is Data Protection
/// ciphertext; only the address without its query is plain. Every write lands with its tenant rows in the
/// same transaction, or not at all.
/// </summary>
public interface IMcpServerStore
{
    /// <summary>Every entry, by name.</summary>
    Task<IReadOnlyList<McpServerRecord>> ListAsync(CancellationToken ct = default);

    Task<McpServerRecord?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>A web address entry's whole URL, query and all, for its check alone. Null for none.</summary>
    Task<string?> UrlAsync(string id, CancellationToken ct = default);

    /// <summary>Adds <paramref name="server"/> with its whole <paramref name="url"/> sealed; <see cref="McpServerWrite.NameTaken"/>
    /// writes nothing.</summary>
    Task<McpServerWrite> AddAsync(McpServerRecord server, string? url, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>Replaces an entry's settings, its health set to the record's with no reason (a web address's
    /// check follows), and its tools kept only for a web address. <paramref name="url"/> null keeps the stored URL.</summary>
    Task<McpServerWrite> UpdateAsync(McpServerRecord server, string? url, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>
    /// A check's result with its row: <paramref name="status"/> null keeps the entry's health (a server not
    /// reached marks nothing), and <paramref name="tools"/> null keeps what it last listed.
    /// </summary>
    Task RecordCheckAsync(
        string id, string? status, string? reason, IReadOnlyList<string>? tools, DateTimeOffset at, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>
    /// Removes an entry and every team's assignment of it, in one transaction with the rows
    /// <paramref name="rows"/> makes of the teams that had it: one for the entry, one per team. Null, with
    /// nothing written, when there is no such entry; otherwise the teams it was taken from.
    /// </summary>
    Task<IReadOnlyList<string>?> RemoveAsync(
        string id, Func<IReadOnlyList<string>, IReadOnlyList<TriggerAudit>> rows, CancellationToken ct = default);

    /// <summary>The MCP servers given to <paramref name="team"/>, oldest first.</summary>
    Task<IReadOnlyList<TeamMcpServer>> AssignedAsync(string team, CancellationToken ct = default);

    /// <summary>Every assignment on the instance: server id to the teams it is given to.</summary>
    Task<ILookup<string, string>> AllAssignmentsAsync(CancellationToken ct = default);

    /// <summary>Gives <paramref name="id"/> to <paramref name="team"/> with its row. <see cref="AssignOutcome.Already"/>
    /// and <see cref="AssignOutcome.NoConnection"/> (no such entry) write nothing.</summary>
    Task<AssignOutcome> AssignAsync(string team, string id, TriggerAudit audit, CancellationToken ct = default);

    /// <summary>Takes <paramref name="id"/> off <paramref name="team"/> with its row. False, writing nothing, when it was not given.</summary>
    Task<bool> UnassignAsync(string team, string id, TriggerAudit audit, CancellationToken ct = default);
}
