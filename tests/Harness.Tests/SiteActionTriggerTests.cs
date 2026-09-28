using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// A <c>site.action</c> row through the ordinary event-trigger arm of the pump: a trigger narrowed to
/// one site fires on that site's actions only, and one narrowed to an action name as well fires on
/// that action only. The row is <see cref="SiteService.ActionMessage"/>, the one the action route
/// appends.
/// </summary>
public sealed class SiteActionTriggerTests : IAsyncLifetime
{
    private const string Team = "Alpha";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"harness-site-action-{Guid.NewGuid():N}");
    private readonly ContainerId _onDone = new(Team, "OnDone");
    private readonly ContainerId _onTriage = new(Team, "OnTriage");
    private readonly ContainerId _onOther = new(Team, "OnOther");
    private readonly DateTimeOffset _t0 = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private ContainerTestBed _bed = null!;
    private SqliteTriggerStore _triggers = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);

        var database = Path.Combine(_directory, "harness.db");
        await new SchemaMigrator(database).ApplyAsync(AuthSchema.Steps, ct: Ct);

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO teams (id, created_utc) VALUES ('Alpha', '2026-09-28T00:00:00Z')";
            await insert.ExecuteNonQueryAsync(Ct);
        }

        _triggers = new SqliteTriggerStore(database);
        _bed = new ContainerTestBed(triggers: _triggers);

        await AddAsync(_onDone, "siteAction eq triage/done");
        await AddAsync(_onTriage, "site eq triage");
        await AddAsync(_onOther, "site eq other");
    }

    public async ValueTask DisposeAsync()
    {
        await _bed.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private async Task AddAsync(ContainerId member, string filter)
    {
        Assert.Null(TriggerFilter.RefusalFor(filter, MessageTypes.SiteAction));

        var container = await _bed.AddAsync(member);

        await _triggers.SaveAsync(
            new TriggerRow(
                Guid.NewGuid().ToString("N"), Team, member.Name, $"On {filter}",
                "Handle {event.siteAction} from {event.by}: {event.payload}",
                "event", null, null, null, null,
                IdleOnly: false, Enabled: true, NextDueAt: null, null, null, null, 0, _t0, "person-1",
                EventType: MessageTypes.SiteAction, Filter: filter),
            Ct);

        // What EffectiveSubscriptions writes for a member holding an enabled event trigger.
        await _bed.Store.SetAsync(member, [.. container.Snapshot().Subscribes, MessageTypes.SiteAction], Ct);
    }

    [Fact]
    public async Task A_trigger_narrowed_to_a_site_and_optionally_an_action_fires_on_those_only()
    {
        var done = await _bed.Store.AppendAsync(
            SiteService.ActionMessage(Team, "triage", "done", JsonNode.Parse("""{"id":"j1"}"""), "person@example.test", _t0), Ct);
        await _bed.Store.AppendAsync(
            SiteService.ActionMessage(Team, "triage", "assign", JsonNode.Parse("""{"id":"j2"}"""), "person@example.test", _t0), Ct);
        await _bed.Store.AppendAsync(
            SiteService.ActionMessage(Team, "other", "done", null, "person@example.test", _t0), Ct);

        Assert.True(await _bed.PumpUntilAsync(() =>
            _bed.Agent.RunsFor(_onDone) == 1 && _bed.Agent.RunsFor(_onTriage) == 2 && _bed.Agent.RunsFor(_onOther) == 1));
        await _bed.SettleAsync();

        Assert.Equal(1, _bed.Agent.RunsFor(_onDone));
        Assert.Equal(2, _bed.Agent.RunsFor(_onTriage));
        Assert.Equal(1, _bed.Agent.RunsFor(_onOther));

        // The fire is caused by the action, so it runs in the workflow the action rooted.
        var fired = Assert.Single(await _bed.OfTypeAsync(MessageTypes.InstructionFor(_onDone)));
        Assert.Equal(done.Seq, fired.CausationSeq);
        Assert.Equal(done.Seq, fired.CorrelationId);
        Assert.Equal(
            """Handle triage/done from person@example.test: {"id":"j1"}""",
            JsonDocument.Parse(fired.Payload).RootElement.GetProperty("instruction").GetString());
    }
}
