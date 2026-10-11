using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;

namespace Harness.Tests;

/// <summary>
/// THE MIGRATOR IS HANDED A SUPERSET OF EVERY STEP IT HAS EVER APPLIED.
///
/// Two halves. Every step Program.cs hands the migrator must apply to a database that has never
/// seen any of them - a step that only works on top of some older state is a fresh instance that
/// will not boot. And a database carrying a step the list does not name is refused before anything
/// moves, because that is what a deleted step or a downgrade looks like from here.
/// </summary>
public sealed class SchemaTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"harness-schema-{Guid.NewGuid():N}");

    public SchemaTests() => Directory.CreateDirectory(_directory);

    private string Database => Path.Combine(_directory, "messages.db");

    [Fact]
    public async Task Every_schema_step_applies_to_an_empty_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var migrator = new SchemaMigrator(Database);

        await migrator.ApplyAsync(SchemaModules.All, ct);

        Assert.Equal(
            SchemaModules.All.Select(s => s.Id).Order(StringComparer.Ordinal),
            await migrator.AppliedAsync(ct));
    }

    [Fact]
    public async Task The_migrator_refuses_a_database_holding_a_step_it_was_not_handed()
    {
        var ct = TestContext.Current.CancellationToken;
        var migrator = new SchemaMigrator(Database);
        await migrator.ApplyAsync(SchemaModules.All, ct);

        // One step deleted from the list, and one new step the refusal must stop from running.
        var deleted = SchemaModules.All[^1];
        var added = new MigrationStep("test-999-must-not-run", "CREATE TABLE must_not_run (id INTEGER);");
        IReadOnlyList<MigrationStep> handed = [.. SchemaModules.All.Take(SchemaModules.All.Count - 1), added];

        var refused = await Assert.ThrowsAsync<SchemaFromTheFutureException>(
            () => migrator.ApplyAsync(handed, ct));

        Assert.Equal([deleted.Id], refused.UnknownSteps);
        Assert.DoesNotContain(added.Id, await migrator.AppliedAsync(ct));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
