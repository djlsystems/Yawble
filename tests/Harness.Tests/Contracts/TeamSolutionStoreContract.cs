using Harness.Contracts;

namespace Harness.Tests.Contracts;

/// <summary>
/// WHAT EVERY <see cref="ITeamSolutionStore"/> DOES, on SQLite and on SQL Server alike. Taken from
/// <c>FreshVolumeTests</c>' solution-row tests (same names, same values; a raw insert without
/// <c>plugin_minimums</c> stands for a row from before <c>auth-029</c>) and from the interface's
/// remarks: an installed row and an uninstalled one kept apart, a team folding case, every write with
/// its tenant row or not at all. Plus the providers' differences made into rules: a trailing space, and
/// two writers at once.
/// </summary>
public abstract class TeamSolutionStoreContract : IdentityContract
{
    protected async Task<ITeamSolutionStore> CreateAsync() => (await DatabaseAsync()).Solutions();

    private static TeamSolutionRow Row(string team, DateTimeOffset at) =>
        new(
            team, "job-tracker", "Job Tracker", "1.0.0", "/data/documents/Alpha/job-tracker", at, "person@example.test", null,
            "{\"format\":1}", new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, string>());

    [Fact]
    public async Task The_team_solutions_step_applies_to_an_empty_volume_and_round_trips()
    {
        var store = await CreateAsync();
        var at = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
        var row = new TeamSolutionRow(
            "JobTracker", "job-tracker", "Job Tracker", "1.0.0", "/data/documents/Alpha/job-tracker", at, "person@example.test", null,
            "{\"format\":1}",
            new Dictionary<string, string> { ["skill:playbook"] = "abc" },
            new Dictionary<string, string> { ["Coordinator"] = "Manager" },
            new Dictionary<string, string> { ["Scan"] = "t1" },
            new Dictionary<string, string> { ["job-board"] = "0.1.0" });

        await store.SaveAsync(row, new TriggerAudit(null, "person@example.test", TenantActions.SolutionInstalled, "JobTracker", "Job Tracker", null), Ct);
        await store.SaveAsync(row with { Version = "1.1.0", UpdatedAt = at }, new TriggerAudit(null, null, TenantActions.SolutionUpdated, "JobTracker", null, null), Ct);

        var read = (await store.FindAsync("jobtracker", Ct))!;
        Assert.Equal(("job-tracker", "1.1.0", at), (read.PackageId, read.Version, read.UpdatedAt));
        Assert.Equal("Manager", read.Members["Coordinator"]);
        Assert.Equal("0.1.0", read.Plugins["job-board"]);
        Assert.Equal("abc", read.Digests["skill:playbook"]);
        Assert.Equal(
            [TenantActions.SolutionInstalled, TenantActions.SolutionUpdated],
            await (await DatabaseAsync()).ColumnAsync("SELECT action FROM tenant_events WHERE action LIKE 'solution.%' ORDER BY seq"));

        Assert.True(await store.DeleteAsync("JobTracker", null, Ct));
        Assert.Empty(await store.ListAsync(Ct));
    }

    [Fact]
    public async Task An_uninstalled_team_solutions_row_is_kept_apart_from_the_installed_ones_and_goes_with_a_delete()
    {
        var store = await CreateAsync();
        var at = new DateTimeOffset(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);
        var row = Row("JobTracker", at);
        var audit = new TriggerAudit(null, null, TenantActions.SolutionUninstalled, "JobTracker", null, null);

        await store.SaveAsync(row with { UninstalledAt = at, Answers = "{\"settings\":{}}" }, audit, Ct);

        Assert.Null(await store.FindAsync("JobTracker", Ct));
        Assert.Empty(await store.ListAsync(Ct));
        var kept = (await store.FindUninstalledAsync("jobtracker", Ct))!;
        Assert.Equal(("job-tracker", (DateTimeOffset?)at, "{\"settings\":{}}"), (kept.PackageId, kept.UninstalledAt, kept.Answers));
        Assert.Single(await store.ListUninstalledAsync(Ct));

        await store.SaveAsync(row, audit with { Action = TenantActions.SolutionInstalled }, Ct);
        Assert.NotNull(await store.FindAsync("JobTracker", Ct));
        Assert.Null(await store.FindUninstalledAsync("JobTracker", Ct));

        await store.SaveAsync(row with { UninstalledAt = at }, audit, Ct);
        Assert.True(await store.DeleteAsync("JobTracker", null, Ct));
        Assert.Null(await store.FindUninstalledAsync("JobTracker", Ct));
    }

    [Fact]
    public async Task The_plugin_minimums_step_keeps_an_existing_row_on_its_shipped_version_and_round_trips()
    {
        await (await DatabaseAsync()).ExecuteAsync(
            "INSERT INTO team_solutions (team, package_id, name, version, folder, installed_at, manifest, plugins) "
            + "VALUES ('Old', 'old-pack', 'Old', '1.0.0', '/x', '2026-10-01T00:00:00Z', '{}', '{\"fixture-mail\":\"2.3.1\"}');");

        var store = await CreateAsync();
        var old = (await store.FindAsync("Old", Ct))!;
        Assert.Empty(old.PluginMinimums);
        Assert.Equal("2.3.1", old.MinimumFor("fixture-mail"));
        Assert.Null(old.MinimumFor("other"));

        var audit = new TriggerAudit(null, null, TenantActions.SolutionUpdated, "Old", null, null);
        await store.SaveAsync(old with { PluginMinimums = new Dictionary<string, string> { ["fixture-mail"] = "2.3.0" } }, audit, Ct);
        Assert.Equal("2.3.0", (await store.FindAsync("Old", Ct))!.MinimumFor("fixture-mail"));

        await store.SaveAsync(old with { PluginMinimums = new Dictionary<string, string> { ["fixture-mail"] = "2.4.0" } }, audit, Ct);
        Assert.Equal("2.4.0", (await store.FindAsync("Old", Ct))!.MinimumFor("fixture-mail"));
    }

    [Fact]
    public async Task Lists_order_by_team_without_case_and_a_save_in_another_case_is_the_same_team()
    {
        var store = await CreateAsync();
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        foreach (var team in new[] { "beta", "Gamma", "Alpha" })
        {
            await store.SaveAsync(Row(team, at), Audit(TenantActions.SolutionInstalled, team), Ct);
        }

        await store.SaveAsync(Row("ALPHA", at) with { Version = "2.0.0" }, Audit(TenantActions.SolutionUpdated, "ALPHA"), Ct);
        await store.SaveAsync(Row("Delta", at) with { UninstalledAt = at }, Audit(TenantActions.SolutionUninstalled, "Delta"), Ct);
        await store.SaveAsync(Row("charlie", at) with { UninstalledAt = at }, Audit(TenantActions.SolutionUninstalled, "charlie"), Ct);

        var listed = await store.ListAsync(Ct);
        Assert.Equal(["Alpha", "beta", "Gamma"], listed.Select(r => r.Team));
        Assert.Equal("2.0.0", listed[0].Version);
        Assert.Equal(["charlie", "Delta"], (await store.ListUninstalledAsync(Ct)).Select(r => r.Team));
    }

    [Fact]
    public async Task A_write_is_not_saved_when_its_tenant_row_cannot_be()
    {
        var store = await CreateAsync();
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        await store.SaveAsync(Row("Alpha", at), Audit(TenantActions.SolutionInstalled, "Alpha"), Ct);
        await (await DatabaseAsync()).ExecuteAsync("DROP TABLE tenant_events");

        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(Row("Alpha", at) with { Version = "9.9.9" }, Audit(TenantActions.SolutionUpdated, "Alpha"), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(Row("Beta", at), Audit(TenantActions.SolutionInstalled, "Beta"), Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => store.DeleteAsync("Alpha", Audit(TenantActions.SolutionUninstalled, "Alpha"), Ct));

        Assert.Equal("1.0.0", (await store.FindAsync("Alpha", Ct))!.Version);
        Assert.Null(await store.FindAsync("Beta", Ct));
    }

    [Fact]
    public async Task A_delete_writes_its_tenant_row_only_when_there_was_a_row()
    {
        var store = await CreateAsync();
        var database = await DatabaseAsync();
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        await store.SaveAsync(Row("Alpha", at), Audit(TenantActions.SolutionInstalled, "Alpha"), Ct);

        Assert.False(await store.DeleteAsync("Beta", Audit(TenantActions.SolutionUninstalled, "Beta"), Ct));
        Assert.True(await store.DeleteAsync("alpha", Audit(TenantActions.SolutionUninstalled, "Alpha"), Ct));

        Assert.Equal(1, await database.LongAsync($"SELECT COUNT(*) FROM tenant_events WHERE action = '{TenantActions.SolutionUninstalled}'"));
    }

    /// <summary>REVIEW FOCUS 4: the manifest and answers over 200 KB with emoji, accents, CJK and <c>'</c>.</summary>
    [Fact]
    public async Task Long_and_unicode_text_round_trips()
    {
        var store = await CreateAsync();
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var row = Row("Alpha", at) with
        {
            Name = "O'Brien's 🚀 café",
            Manifest = LongUnicode("manifest"),
            Members = new Dictionary<string, string> { ["Coördinator 日本"] = "Manager'1" },
            UninstalledAt = at,
            Answers = LongUnicode("answers"),
        };

        await store.SaveAsync(row, Audit(TenantActions.SolutionUninstalled, "Alpha"), Ct);

        var read = (await store.FindUninstalledAsync("Alpha", Ct))!;
        Assert.Equal((row.Name, row.Manifest, row.Answers, "Manager'1"), (read.Name, read.Manifest, read.Answers, read.Members["Coördinator 日本"]));
    }

    /// <summary>RULING 5: a team with a trailing space is another team, as SQLite's <c>=</c> has it.</summary>
    [Fact]
    public async Task A_team_with_a_trailing_space_is_another_team()
    {
        var store = await CreateAsync();
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        await store.SaveAsync(Row("Alpha", at), Audit(TenantActions.SolutionInstalled, "Alpha"), Ct);
        await store.SaveAsync(Row("Beta", at) with { UninstalledAt = at }, Audit(TenantActions.SolutionUninstalled, "Beta"), Ct);

        Assert.Null(await store.FindAsync("Alpha ", Ct));
        Assert.Null(await store.FindUninstalledAsync("Beta ", Ct));
        Assert.False(await store.DeleteAsync("Alpha ", null, Ct));
        Assert.NotNull(await store.FindAsync("Alpha", Ct));
    }

    /// <summary>REVIEW FOCUS 2: eight installs onto one team at once. Every save succeeds and one row stands.</summary>
    [Fact]
    public async Task Concurrent_saves_of_one_teams_first_row_make_one_row()
    {
        var store = await CreateAsync();
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        for (var round = 0; round < 12; round++)
        {
            var team = $"Raced{round}";
            await RaceAsync(8, async i =>
            {
                await store.SaveAsync(Row(i % 2 == 0 ? team : team.ToLowerInvariant(), at) with { Version = $"1.0.{i}" }, Audit(TenantActions.SolutionInstalled, team), Ct);
                return i;
            });

            Assert.Single(await store.ListAsync(Ct), r => string.Equals(r.Team, team, StringComparison.OrdinalIgnoreCase));
        }
    }
}

public sealed class SqliteTeamSolutionStoreTests : TeamSolutionStoreContract
{
    protected override Task<IdentityDatabase> OpenAsync() => Task.FromResult<IdentityDatabase>(new SqliteIdentityDatabase());
}

public sealed class SqlServerTeamSolutionStoreTests : TeamSolutionStoreContract
{
    protected override async Task<IdentityDatabase> OpenAsync() => await SqlServerIdentityDatabase.CreateAsync();
}
