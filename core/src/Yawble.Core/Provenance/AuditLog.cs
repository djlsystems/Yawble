using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Provenance;

/// <summary>
/// Every call that changes something, from the window or a cloud viewer (0015_provenance.sql audit_log): who, how,
/// which call, what it was about, and whether it worked. Calls that only read are left out (the screens poll several
/// every second). Only identifying parameters are kept: never a key, a token, a message or keystrokes.
/// </summary>
public sealed class AuditLog(string connectionString, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The parameters that say what a call was about; every other parameter is left out.</summary>
    private static readonly string[] TargetKeys =
        ["team", "teamId", "member", "memberId", "name", "id", "card", "item", "lane", "workflow", "conversationId", "trigger", "cli", "mode"];

    private static readonly HashSet<string> ReadVerbs = new(StringComparer.Ordinal)
    {
        "list", "get", "status", "card", "messages", "decisions", "overview", "agents", "detect", "read", "preview", "attach",
        "banner", "input", "resize", "previewLoosen", "getInfo", "getAvailableBackends", "getPlatformSupport", "getState",
        "getGlobal", "getMember", "version",
    };

    /// <summary>A call that only reads: not audited.</summary>
    public static bool IsReadOnly(string method)
    {
        var dot = method.LastIndexOf('.');
        var verb = dot < 0 ? method : method[(dot + 1)..];
        return ReadVerbs.Contains(verb) || method.StartsWith("git.status", StringComparison.Ordinal)
            // Opening and closing the read-only Code view change nothing (a download is recorded).
            || method is "code.open" or "code.close";
    }

    /// <summary>What a call was about, from its identifying parameters only ("team=web member=DeveloperPriya").</summary>
    public static string? Target(JsonObject? parameters)
    {
        if (parameters is null) return null;
        var parts = new List<string>();
        foreach (var key in TargetKeys)
        {
            if (parameters[key] is JsonValue v && v.ToString() is { Length: > 0 } s)
                parts.Add($"{key}={(s.Length > 80 ? s[..80] + "\u2026" : s)}");
        }
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    public void Record(Actor actor, string method, JsonObject? parameters, string outcome)
    {
        if (IsReadOnly(method)) return;
        try
        {
            using var c = new SqliteConnection(connectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO audit_log (at, via, user_name, client, method, target, outcome) VALUES ($at, $via, $user, $client, $method, $target, $outcome);";
            cmd.Parameters.AddWithValue("$at", _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$via", actor.Via);
            cmd.Parameters.AddWithValue("$user", (object?)actor.User ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$client", (object?)actor.Client ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$method", method);
            cmd.Parameters.AddWithValue("$target", (object?)Target(parameters) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$outcome", outcome.Length > 300 ? outcome[..300] : outcome);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // The audit is a record, not a gate: a write that fails never fails the call it describes.
        }
    }

    public sealed record Row(string At, string Via, string? User, string Method, string? Target, string Outcome, string? Client = null);

    public IReadOnlyList<Row> Recent(int limit = 100)
    {
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT at, via, user_name, method, target, outcome, client FROM audit_log ORDER BY id DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var rows = new List<Row>();
        while (r.Read())
            rows.Add(new Row(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return rows;
    }
}
