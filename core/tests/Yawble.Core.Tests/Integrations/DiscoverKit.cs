using Microsoft.Data.Sqlite;
using Yawble.Core.Integrations;
using Yawble.Core.Tests.Bus;

namespace Yawble.Core.Tests.Integrations;

/// <summary>Help served from the recorded fixtures (Fixtures/cli-help/cli/version/path_words.txt) or from overrides, and a log of every call.</summary>
internal sealed class FixtureCli(string? dir = null, Dictionary<string, string>? overrides = null)
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Integrations", "Fixtures", "cli-help");

    public static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar))).ReplaceLineEndings("\n"); // a Windows checkout has CRLF

    public List<string[]> Calls { get; } = [];
    public Dictionary<string, string> Overrides { get; } = overrides ?? [];

    public HelpRunner Runner => (args, ct) =>
    {
        lock (Calls) Calls.Add(args.ToArray());
        var words = args.Take(args.Count - 1).ToArray();
        var key = words.Length == 0 ? "_root" : string.Join('_', words);
        string? text = null;
        if (Overrides.TryGetValue(key, out var o)) text = o;
        else if (dir is not null && File.Exists(Path.Combine(Root, dir, key + ".txt"))) text = File.ReadAllText(Path.Combine(Root, dir, key + ".txt"));
        return Task.FromResult(text is null
            ? new CliOutput(1, "", $"unknown command {string.Join(' ', words)}", 10, TimeSpan.Zero, false, false)
            : new CliOutput(0, text, "", text.Length, TimeSpan.Zero, false, false));
    };
}

internal sealed class FakeModel(Func<string, string> answer) : ICommandModel
{
    public List<string> Prompts { get; } = [];

    public Task<string> CompleteAsync(string prompt, CancellationToken ct)
    {
        Prompts.Add(prompt);
        return Task.FromResult(answer(prompt));
    }
}

/// <summary>A database with the schema, a Discover service on it, and helpers to read what it wrote.</summary>
internal sealed class DiscoverRig : IDisposable
{
    public DiscoverRig(Func<ICommandModel?>? model = null)
    {
        Db = new TempDb();
        Svc = new DiscoverService(Db.ConnectionString, model);
    }

    public TempDb Db { get; }
    public DiscoverService Svc { get; }

    public long AddTeam(string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO teams (name, created_at) VALUES ($n, '2026-01-01') RETURNING id;";
        cmd.Parameters.AddWithValue("$n", name);
        return (long)cmd.ExecuteScalar()!;
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(Db.ConnectionString);
        c.Open();
        using var fk = c.CreateCommand();
        fk.CommandText = "PRAGMA foreign_keys=ON;";
        fk.ExecuteNonQuery();
        return c;
    }

    /// <summary>The rules of a CLI: (team, pattern, decision).</summary>
    public List<(long? Team, string Pattern, string Decision)> Rules(string cli)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT team_id, pattern, decision FROM integration_rules r JOIN integrations i ON i.id = r.integration_id WHERE i.name = $n ORDER BY r.id;";
        cmd.Parameters.AddWithValue("$n", cli);
        using var r = cmd.ExecuteReader();
        var list = new List<(long?, string, string)>();
        while (r.Read()) list.Add((r.IsDBNull(0) ? null : r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    public long Scalar(string sql)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public (IReadOnlyList<CliRule> Team, IReadOnlyList<CliRule> Global) RulesFor(string cli, long? teamId) => (
        Rules(cli).Where(r => r.Team == teamId && teamId is not null).Select(r => new CliRule(r.Pattern, r.Decision, "team")).ToList(),
        Rules(cli).Where(r => r.Team is null).Select(r => new CliRule(r.Pattern, r.Decision, "global")).ToList());

    public void Dispose() => Db.Dispose();
}
