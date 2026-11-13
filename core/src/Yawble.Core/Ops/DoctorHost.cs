using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Guardrail;

namespace Yawble.Core.Ops;

/// <summary>The doctor's view of this computer: the real database, disk, launcher and Cloud Link.</summary>
public sealed class DoctorHostSystem(
    string dataDir, Func<IReadOnlyList<string>> applied, string? launcherProblem, GuardrailService? guardrail,
    Func<JsonObject> linkStatus, IEnumerable<string> logFolders,
    Func<CancellationToken, Task<string?>>? gatewayProbe = null) : IDoctorSystem
{
    public string QuickCheck()
    {
        using var c = new SqliteConnection($"Data Source={Path.Combine(dataDir, "yawble.db")}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check;";
        return cmd.ExecuteScalar()?.ToString() ?? "no answer";
    }

    public IReadOnlyList<string> AppliedMigrations() => applied();

    public IReadOnlyList<string> ShippedMigrations() => Storage.SchemaMigrator.Available;

    public long FreeBytesOnDataDrive() => new DriveInfo(Path.GetFullPath(dataDir)).AvailableFreeSpace;

    public IReadOnlyDictionary<string, long> TeamFolderSizes()
    {
        if (BusHost.AppRootFor(dataDir) is not { } root) return new Dictionary<string, long>();
        var teams = Path.Combine(root, "teams");
        if (!Directory.Exists(teams)) return new Dictionary<string, long>();
        return Directory.GetDirectories(teams).ToDictionary(d => Path.GetFileName(d),
            d => new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length));
    }

    public long LogBytes() => logFolders.Where(Directory.Exists)
        .Sum(f => new DirectoryInfo(f).EnumerateFiles("*.log", SearchOption.AllDirectories).Sum(x => x.Length));

    public SandboxState Sandbox()
    {
        var problem = launcherProblem ?? guardrail?.Problem;
        var g = guardrail?.Get(null);
        var state = g?["state"]?.GetValue<string>() ?? "none";
        var detail = g?["error"]?.GetValue<string>();
        // Gateways are per run, so none is up when idle: start a throwaway one and send it a request (GatewaySelfTest).
        string? gatewayProblem = null;
        if (launcherProblem is null)
        {
            if (gatewayProbe is null) gatewayProblem = "this core has no gateway to test";
            else
            {
                try { gatewayProblem = gatewayProbe(CancellationToken.None).GetAwaiter().GetResult(); }
                catch (Exception e) when (e is not OperationCanceledException) { gatewayProblem = $"the test failed to run ({e.GetType().Name})"; }
            }
        }
        return new SandboxState(launcherProblem, state, detail ?? (state == "done" ? null : problem is null ? null : problem), gatewayProblem);
    }

    public LinkState Link()
    {
        var s = linkStatus();
        return new LinkState(s["state"]?.GetValue<string>() ?? "off", s["error"]?.GetValue<string>());
    }
}

/// <summary>The doctor's view of the teams, over the bus and its database.</summary>
public sealed class DoctorHostTeams(BusEngine bus, string connectionString, TimeProvider? clock = null, TimeSpan? watchdog = null) : IDoctorTeams
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<IReadOnlyList<DoctorTeam>> ListAsync(CancellationToken ct)
    {
        var stuck = Stuck();
        var result = new List<DoctorTeam>();
        foreach (var t in await bus.TeamsAsync(ct).ConfigureAwait(false))
        {
            var last = (await bus.RecentTeamMessagesAsync(t.Name, 1, ct).ConfigureAwait(false)).FirstOrDefault()?.OccurredAt;
            var created = Created(t.Id);
            var workflows = await bus.WorkflowsAsync(t.Name, 200, ct).ConfigureAwait(false);
            Teams.TeamBehind? behind = null;
            if (!t.Archived && bus.Repos is { } repos)
            {
                try { behind = await repos.BehindBaseAsync(t.Id, t.Name, _clock.GetUtcNow(), ct).ConfigureAwait(false); }
                catch (Exception e) when (e is not OperationCanceledException) { } // a repository git cannot read is not this check's to report
            }
            result.Add(new DoctorTeam(t.Name, t.Paused, t.Archived, last ?? created,
                workflows.Count(w => w.Undeclared), stuck.TryGetValue(t.Id, out var s) ? s : [],
                behind?.Behind ?? 0, behind?.Age, behind?.TeamBranch ?? "", behind?.BaseBranch ?? ""));
        }
        return result;
    }

    public Task ArchiveAsync(string team, CancellationToken ct) => bus.ArchiveTeamAsync(team, ct);

    public async Task<(bool Done, string Message)> SyncAsync(string team, CancellationToken ct)
    {
        if (bus.Repos is not { } repos) return (false, "this core has no team repositories");
        var t = (await bus.TeamsAsync(ct).ConfigureAwait(false)).FirstOrDefault(x => x.Name.Equals(team, StringComparison.OrdinalIgnoreCase))
            ?? throw new Ipc.RpcException("bad_params", $"no team '{team}'");
        var r = await repos.SyncNowAsync(t.Id, t.Name, ct).ConfigureAwait(false);
        return r.Outcome switch
        {
            "conflict" => (false, r.Note ?? "could not be synced automatically"),
            "synced" => (true, $"brought {string.Join(", ", r.Moved)} up to {r.BaseBranch}"),
            "in-step" => (true, $"{t.Name} is already up to {r.BaseBranch}"),
            _ => (false, $"nothing synced ({r.Outcome})"),
        };
    }

    private DateTimeOffset Created(long teamId)
    {
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT created_at FROM teams WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", teamId);
        return cmd.ExecuteScalar() is string s ? DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : _clock.GetUtcNow();
    }

    /// <summary>Runs still "running" after longer than the watchdog (the global wall-clock timeout, default 2 h, plus slack).</summary>
    private Dictionary<long, IReadOnlyList<StuckMember>> Stuck()
    {
        var limit = watchdog ?? TimeSpan.FromHours(3);
        var now = _clock.GetUtcNow();
        var rows = new Dictionary<long, List<StuckMember>>();
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT m.team_id, m.name, r.started_at FROM runs r JOIN members m ON m.id = r.member_id WHERE r.state = 'running';";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var running = now - DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture);
            if (running <= limit) continue;
            if (!rows.TryGetValue(reader.GetInt64(0), out var list)) rows[reader.GetInt64(0)] = list = [];
            list.Add(new StuckMember(reader.GetString(1), running));
        }
        return rows.ToDictionary(r => r.Key, r => (IReadOnlyList<StuckMember>)r.Value);
    }
}

