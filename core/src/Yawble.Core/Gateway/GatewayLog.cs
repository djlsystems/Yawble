using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Gateway;

/// <summary>Kinds of gateway event (stored in gateway_events.kind).</summary>
public static class GatewayEventKinds
{
    public const string Forward = "forward";
    public const string PathDenied = "path-denied";
    public const string HostDenied = "host-denied";
    public const string ConnectAllowed = "connect-allowed";
    public const string ConnectDenied = "connect-denied";
    public const string Unauthorized = "unauthorized";
    public const string UpstreamError = "upstream-error";
    public const string CredentialProblem = "credential-problem";
    public const string BadRequest = "bad-request";
    public const string Bus = "bus";

    /// <summary>The model API said "too many requests" (429): detail is when it said to come back (ISO 8601, UTC).</summary>
    public const string RateLimited = "rate-limited";
}

/// <summary>One decision of a run's gateway. Never carries a credential: tokens are logged as "Bearer &lt;redacted&gt;".</summary>
public sealed record GatewayEvent(
    long? RunId,
    string? Member,
    DateTimeOffset At,
    string Kind,
    bool Guardrail,
    string? Vhost = null,
    string? Method = null,
    string? Target = null,
    int? Status = null,
    string? Detail = null);

/// <summary>
/// The gateway log (a refusal is a 403 and is logged to the agent's card): every denial, every
/// unauthorised call, every CONNECT and every upstream credential failure, in gateway_events. Forwards and bus calls
/// are written too, so a person can see what a run reached. Observers (the card, tests) get each event as written.
/// </summary>
public sealed class GatewayLog(string connectionString, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _lock = new();

    public event Action<GatewayEvent>? Written;

    public DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>How many identical denials (run, kind, target) are written per window before the rest are only counted.</summary>
    public const int RepeatLimit = 5;

    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(1);

    private static readonly HashSet<string> Repeating = [GatewayEventKinds.ConnectDenied, GatewayEventKinds.HostDenied, GatewayEventKinds.PathDenied];
    private readonly Dictionary<(long?, string, string?), (DateTimeOffset Start, int Count)> _repeats = [];

    public void Write(GatewayEvent e)
    {
        // A tool that retries a refused connection in a loop (npm against a host not allowed) would write a row per try:
        // half a million in one run. The first few of each are written; the rest are counted, and the count is written
        // as one row when the window ends (at the next such denial).
        GatewayEvent? summary = null;
        if (Repeating.Contains(e.Kind))
        {
            lock (_lock)
            {
                var key = (e.RunId, e.Kind, e.Target);
                if (_repeats.TryGetValue(key, out var seen) && e.At - seen.Start < RepeatWindow)
                {
                    _repeats[key] = (seen.Start, seen.Count + 1);
                    if (seen.Count >= RepeatLimit) return;
                }
                else
                {
                    if (seen.Count > RepeatLimit)
                        summary = e with { Detail = $"{e.Detail} (and {seen.Count - RepeatLimit} more like it in the minute from {seen.Start:HH:mm:ss}, not logged one by one)" };
                    _repeats[key] = (e.At, 1);
                    if (_repeats.Count > 10_000) _repeats.Clear();
                }
            }
        }
        if (summary is not null) Insert(summary);
        Insert(e);
    }

    private void Insert(GatewayEvent e)
    {
        lock (_lock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO gateway_events (run_id, member, at, kind, guardrail, vhost, method, target, status, detail)
                VALUES ($run, $member, $at, $kind, $guardrail, $vhost, $method, $target, $status, $detail);
                """;
            cmd.Parameters.AddWithValue("$run", (object?)e.RunId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$member", (object?)e.Member ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", e.At.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$kind", e.Kind);
            cmd.Parameters.AddWithValue("$guardrail", e.Guardrail ? 1 : 0);
            cmd.Parameters.AddWithValue("$vhost", (object?)e.Vhost ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$method", (object?)e.Method ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$target", (object?)Truncate(e.Target, 2048) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", (object?)e.Status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$detail", (object?)Truncate(e.Detail, 4096) ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        Written?.Invoke(e);
    }

    /// <summary>One forwarded model request's usage, as the provider reported it (0015_provenance.sql usage_events).</summary>
    public void WriteUsage(string? runKey, long? runNumber, string? member, string vhost, string path, int status, Provenance.UsageReading u)
    {
        lock (_lock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO usage_events (at, run_key, run_number, member, vhost, path, status, model, input_tokens, output_tokens,
                                          cache_read_tokens, cache_write_tokens, usage)
                VALUES ($at, $key, $run, $member, $vhost, $path, $status, $model, $in, $out, $cr, $cw, $usage);
                """;
            cmd.Parameters.AddWithValue("$at", _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$key", (object?)runKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$run", (object?)runNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$member", (object?)member ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$vhost", vhost);
            cmd.Parameters.AddWithValue("$path", Truncate(path, 512)!);
            cmd.Parameters.AddWithValue("$status", status);
            cmd.Parameters.AddWithValue("$model", (object?)u.Model ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$in", (object?)u.InputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$out", (object?)u.OutputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cr", (object?)u.CacheReadTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cw", (object?)u.CacheWriteTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$usage", (object?)Truncate(u.Usage, 8192) ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The events of one run (or all, when null), oldest first.</summary>
    public IReadOnlyList<GatewayEvent> Read(long? runId = null)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT run_id, member, at, kind, guardrail, vhost, method, target, status, detail FROM gateway_events"
            + (runId is null ? "" : " WHERE run_id = $run") + " ORDER BY id;";
        if (runId is not null) cmd.Parameters.AddWithValue("$run", runId);
        using var reader = cmd.ExecuteReader();
        var list = new List<GatewayEvent>();
        while (reader.Read())
        {
            list.Add(new GatewayEvent(
                reader.IsDBNull(0) ? null : reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                reader.GetString(3),
                reader.GetInt64(4) != 0,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return list;
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max] + "…";
}
