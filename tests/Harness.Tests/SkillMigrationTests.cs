using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;
using Harness.Skills;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// Skill migration: the first start on a volume holding skill files moves every one to the
/// backup - never deleting one - replaces a built-in's name with the built-in, keeps any other skill
/// as a Custom one, and leaves `tenant-manager` behind.
/// </summary>
public sealed class SkillMigrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"harness-skill-migration-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Skills => Path.Combine(_root, "skills");

    private string Backups => Path.Combine(_root, "backups", "skills-before-builtins");

    private SqliteSkillStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var database = Path.Combine(_root, "messages.db");
        await new SchemaMigrator(database).ApplyAsync(SchemaModules.All, ct: Ct);
        _store = new SqliteSkillStore(database);
        await _store.ReplaceBuiltInsAsync(BuiltInSkills.Drafts(), DateTimeOffset.UtcNow, Ct);
    }

    [Fact]
    public async Task Skill_files_on_a_volume_are_moved_to_the_backup_and_only_the_custom_ones_imported()
    {
        Write("manager", "Use when managing team work.", "An edited old copy.");
        Write("tenant-manager", "Use when managing the tenant.", "Stale.");
        Write("release-notes", "Use when writing release notes.", "Say what changed.");
        Write("concierge-greeting", "Use when the Concierge greets a person.", "Say hello.");

        var report = await SkillMigration.RunAsync([("skills", Skills)], Backups, _store, Ct);

        Assert.Equal(["manager"], report.ReplacedByBuiltIn);
        Assert.Equal(["tenant-manager"], report.NotMigrated);
        Assert.Equal(["concierge-greeting", "release-notes"], report.Imported.Order(StringComparer.Ordinal));

        // Nothing is deleted: every folder is in the backup, and the old folder is gone.
        Assert.False(Directory.Exists(Skills));
        foreach (var name in new[] { "manager", "tenant-manager", "release-notes", "concierge-greeting" })
        {
            Assert.True(File.Exists(Path.Combine(Backups, "skills", name, "SKILL.md")), name);
        }

        // The built-in wins its name; the old copy's words do not reach the index.
        var manager = await _store.GetAsync("manager", Ct);
        Assert.Equal(SkillKind.BuiltIn, manager!.Kind);
        Assert.DoesNotContain("An edited old copy.", manager.Body, StringComparison.Ordinal);

        Assert.Null(await _store.GetAsync("tenant-manager", Ct));

        var notes = await _store.GetAsync("release-notes", Ct);
        Assert.Equal(SkillKind.Custom, notes!.Kind);
        Assert.Equal([SkillRoles.Member], notes.Roles);
        Assert.Equal("Say what changed.", notes.Body.Trim());

        // A description that clearly names a role is offered to that role instead.
        Assert.Equal([SkillRoles.Concierge], (await _store.GetAsync("concierge-greeting", Ct))!.Roles);

        // A second start finds nothing left to do.
        var again = await SkillMigration.RunAsync([("skills", Skills)], Backups, _store, Ct);
        Assert.False(again.Moved);
    }

    [Fact]
    public async Task Rebuilding_the_built_ins_takes_the_builds_text_and_keeps_every_custom_skill()
    {
        await _store.CreateCustomAsync(
            new SkillDraft("house-style", "Use when writing.", [SkillRoles.Member], "Short."), "person", Ct);

        var changed = BuiltInSkills.Drafts()
            .Select(d => d.Name == "member" ? d with { Body = "A newer member body from the build." } : d)
            .ToList();
        await _store.ReplaceBuiltInsAsync(changed, DateTimeOffset.UtcNow, Ct);

        Assert.Equal("A newer member body from the build.", (await _store.GetAsync("member", Ct))!.Body);
        Assert.NotNull(await _store.GetAsync("house-style", Ct));
    }

    [Fact]
    public async Task A_custom_skill_holding_a_name_the_build_now_ships_is_moved_out_of_the_way()
    {
        await _store.CreateCustomAsync(
            new SkillDraft("shipped-later", "Use when.", [SkillRoles.Member], "Mine."), "person", Ct);

        var moved = await _store.ReplaceBuiltInsAsync(
            [.. BuiltInSkills.Drafts(), new SkillDraft("shipped-later", "Built in now.", [SkillRoles.Any], "Ours.")],
            DateTimeOffset.UtcNow, Ct);

        Assert.Equal(["shipped-later -> shipped-later-custom"], moved);
        Assert.Equal(SkillKind.BuiltIn, (await _store.GetAsync("shipped-later", Ct))!.Kind);
        Assert.Equal("Mine.", (await _store.GetAsync("shipped-later-custom", Ct))!.Body);
    }

    [Fact]
    public async Task Search_and_listing_honour_the_role()
    {
        var member = await _store.ListAsync(SkillKindFilter.All, null, SkillRoles.Member, null, 200, Ct);
        Assert.Contains(member, s => s.Name == "worktrees");
        Assert.DoesNotContain(member, s => s.Name is "manager" or "wrap-up");

        // `any` is offered to every role.
        Assert.Contains(member, s => s.Name == "test-credentials");

        var found = await _store.ListAsync(SkillKindFilter.All, "worktree", SkillRoles.Manager, null, 200, Ct);
        Assert.DoesNotContain(found, s => s.Name == "worktrees");
    }

    private void Write(string name, string description, string body)
    {
        var directory = Path.Combine(Skills, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\nmetadata:\n  gated: false\n  version: \"1.0\"\n---\n\n{body}\n");
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}
