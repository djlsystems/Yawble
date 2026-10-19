using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A WORKFLOW IS NAMED BY ITS WORK, on the real Host: a dispatch workflow's timing row carries its
/// item's id and the backlog's CURRENT title on <c>GET /api/teams/{team}/workflow</c>, <c>/workflows</c>
/// and the rollup (<see cref="WorkflowItems"/>); a told workflow carries <c>item: null</c> and its
/// subject; an item since deleted keeps the dispatch row's title; and each request asks the backlog
/// for titles ONCE, through <see cref="IBacklogStore.TitlesAsync"/>, however many workflows or teams
/// it carries.
///
/// <para>
/// The Host's <see cref="IBacklogStore"/> is wrapped in one that records every call into the recorder
/// of the async flow that makes it, as <see cref="BoardOutcomeQueryTests"/> does for outcomes.
/// </para>
/// </summary>
public sealed class WorkflowItemTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-workflow-item-{Guid.NewGuid():N}");
    private readonly AsyncLocal<ConcurrentQueue<(string Method, object?[] Args)>?> _recording = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IBacklogStore Backlog => _factory.Services.GetRequiredService<IBacklogStore>();

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());

                var real = (IBacklogStore)services.Last(d => d.ServiceType == typeof(IBacklogStore)).ImplementationInstance!;
                services.AddSingleton(CountingBacklogStore.Around(real, _recording));
            }));

        _team = await TeamAsync("Named");

        await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _factory.Server.PreserveExecutionContext = true;
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task A_dispatch_workflow_carries_its_items_id_and_current_title_after_a_rename_too()
    {
        var (item, correlation) = await DispatchAsync(_team, "Name the workflows");

        foreach (var row in await RowsAsync(_team, correlation))
        {
            Assert.Equal(item, row.GetProperty("item").GetProperty("id").GetInt64());
            Assert.Equal("Name the workflows", row.GetProperty("item").GetProperty("title").GetString());
        }

        var renamed = await _person.PatchAsJsonAsync($"/api/backlog/{item}", new { title = "Name every workflow" }, Ct);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        foreach (var row in await RowsAsync(_team, correlation))
        {
            Assert.Equal(item, row.GetProperty("item").GetProperty("id").GetInt64());
            Assert.Equal("Name every workflow", row.GetProperty("item").GetProperty("title").GetString());
        }

        // A READER OUTSIDE THE OVERLAY (an agent tool's) serves the dispatch row's own title.
        var projected = (await _factory.Services.GetRequiredService<IMessageLog>().WorkflowsForTeamAsync(_team, 0, Ct))
            .Workflows.Single(w => w.Correlation == correlation);
        Assert.Equal(new WorkflowItem(item, "Name the workflows"), projected.Item);
    }

    [Fact]
    public async Task A_told_workflow_carries_a_null_item_and_keeps_its_subject()
    {
        var told = await _person.PostAsJsonAsync(
            $"/api/teams/{_team}/containers/{TeamRegistry.DefaultManagerName}/tell",
            new { instruction = "look at the openings", subject = "Openings review" }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));
        var correlation = JsonDocument.Parse(await told.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("correlationId").GetInt64();
        await RanAsync(_team, correlation);

        foreach (var row in await RowsAsync(_team, correlation))
        {
            // PRESENT AND NULL, not absent: the web reads `item: { id, title } | null`.
            Assert.Equal(JsonValueKind.Null, row.GetProperty("item").ValueKind);
            Assert.Equal("Openings review", row.GetProperty("subject").GetString());
        }
    }

    [Fact]
    public async Task A_dispatched_item_since_deleted_keeps_the_dispatch_rows_title()
    {
        var (item, correlation) = await DispatchAsync(_team, "Short-lived item");

        Assert.True((await _person.PostAsync($"/api/backlog/{item}/archive", null, Ct)).IsSuccessStatusCode);
        Assert.True((await _person.DeleteAsync($"/api/backlog/{item}", Ct)).IsSuccessStatusCode);
        Assert.Null(await Backlog.GetAsync(item, Ct));

        foreach (var row in await RowsAsync(_team, correlation))
        {
            Assert.Equal(item, row.GetProperty("item").GetProperty("id").GetInt64());
            Assert.Equal("Short-lived item", row.GetProperty("item").GetProperty("title").GetString());
        }
    }

    [Fact]
    public async Task The_rollups_projection_carries_the_same_item_per_workflow_as_the_team_route()
    {
        var other = await TeamAsync("Named other");
        var (first, _) = await DispatchAsync(_team, "First item");
        var (second, _) = await DispatchAsync(other, "Second item");
        await _person.PatchAsJsonAsync($"/api/backlog/{second}", new { title = "Second item, renamed" }, Ct);

        var rollup = JsonDocument.Parse(await _person.GetStringAsync("/api/teams/rollup", Ct)).RootElement;

        var carried = new List<long>();
        foreach (var team in new[] { _team, other })
        {
            var row = rollup.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("team").GetString() == team);
            var routed = JsonDocument.Parse(await _person.GetStringAsync($"/api/teams/{team}/workflows", Ct)).RootElement
                .GetProperty("workflows").EnumerateArray()
                .ToDictionary(w => w.GetProperty("correlation").GetInt64(), w => w.GetProperty("item").GetRawText());

            foreach (var workflow in row.GetProperty("openWorkflows").GetProperty("workflows").EnumerateArray())
            {
                var correlation = workflow.GetProperty("correlation").GetInt64();
                Assert.Equal(routed[correlation], workflow.GetProperty("item").GetRawText());
                if (workflow.GetProperty("item").ValueKind is JsonValueKind.Object)
                {
                    carried.Add(workflow.GetProperty("item").GetProperty("id").GetInt64());
                }
            }
        }

        Assert.Equal(new[] { first, second }, carried.Order().ToArray());
        var renamed = rollup.GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("team").GetString() == other)
            .GetProperty("workflow").GetProperty("item");
        Assert.Equal("Second item, renamed", renamed.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Each_route_asks_the_backlog_for_titles_once_however_many_workflows_and_teams()
    {
        var other = await TeamAsync("Named counted");
        var items = new List<long>();
        for (var i = 0; i < 3; i++) items.Add((await DispatchAsync(_team, $"Counted {i}")).Item);
        items.Add((await DispatchAsync(other, "Counted elsewhere")).Item);

        foreach (var route in new[] { $"/api/teams/{_team}/workflow", $"/api/teams/{_team}/workflows", "/api/teams/rollup" })
        {
            var calls = new ConcurrentQueue<(string Method, object?[] Args)>();
            _recording.Value = calls;
            (await _person.GetAsync(route, Ct)).EnsureSuccessStatusCode();
            _recording.Value = null;

            // ONE READ, naming the response's items; never one per row, and never the item by itself.
            var asked = Assert.Single(calls, c => c.Method == nameof(IBacklogStore.TitlesAsync));
            Assert.DoesNotContain(calls, c => c.Method == nameof(IBacklogStore.GetAsync));
            Assert.DoesNotContain(calls, c => c.Method == nameof(IBacklogStore.ListAsync));

            var ids = ((IReadOnlyCollection<long>)asked.Args[0]!).ToHashSet();
            var expected = route.EndsWith("/workflow", StringComparison.Ordinal) ? ids.Where(items.Take(3).Contains)
                : route.EndsWith("/workflows", StringComparison.Ordinal) ? items.Take(3)
                : items;
            Assert.Equal(expected.ToHashSet(), ids);
            Assert.NotEmpty(ids);
        }

        // THE STORE'S READ IS ONE STATEMENT FOR ANY NUMBER OF IDS: well past SQLite's old 999-variable
        // limit, unknown ids absent, no ids no answer.
        var many = Enumerable.Range(1, 5000).Select(n => (long)n + 1_000_000).Concat(items).ToList();
        var titles = await Backlog.TitlesAsync(many, Ct);
        Assert.Equal(items.ToHashSet(), titles.Keys.ToHashSet());
        Assert.Equal("Counted elsewhere", titles[items[^1]]);
        Assert.Empty(await Backlog.TitlesAsync([], Ct));
    }

    private async Task<string> TeamAsync(string name) =>
        (await _factory.Services.GetRequiredService<TeamRegistry>()
            .CreateAsync(name, "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

    /// <summary>A ready item, dispatched to <paramref name="team"/>: its id and the dispatch workflow.</summary>
    private async Task<(long Item, long Correlation)> DispatchAsync(string team, string title)
    {
        var created = await _person.PostAsJsonAsync("/api/backlog", new { title, body = "spec" }, Ct);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Ct));
        var item = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetInt64();
        (await _person.PatchAsJsonAsync($"/api/backlog/{item}", new { state = "ready" }, Ct)).EnsureSuccessStatusCode();

        var dispatched = await _person.PostAsync($"/api/teams/{team}/backlog/{item}/dispatch", null, Ct);
        Assert.True(dispatched.IsSuccessStatusCode, await dispatched.Content.ReadAsStringAsync(Ct));
        var correlation = JsonDocument.Parse(await dispatched.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("correlation").GetInt64();
        await RanAsync(team, correlation);

        return (item, correlation);
    }

    private async Task RanAsync(string team, long correlation)
    {
        // THE TEAM'S PROJECTION LISTS A WORKFLOW ONCE THE TEAM'S OWN ROWS ARE IN IT: wait for the
        // Manager's run on it to finish.
        var log = _factory.Services.GetRequiredService<IMessageLog>();
        for (var tries = 0; ; tries++)
        {
            var listed = (await log.WorkflowsForTeamAsync(team, 0, Ct)).Workflows.FirstOrDefault(w => w.Correlation == correlation);
            if (listed is { Members.Count: > 0, RunsCounted: > 0 }) break;
            Assert.True(tries < 200, $"The Manager never ran workflow {correlation}.");
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>The timing row of <paramref name="correlation"/> as each of the three routes serves it:
    /// <c>/workflow</c> (it is the team's newest), <c>/workflows</c>, and the rollup's list for the team.</summary>
    private async Task<IReadOnlyList<JsonElement>> RowsAsync(string team, long correlation)
    {
        var single = JsonDocument.Parse(await _person.GetStringAsync($"/api/teams/{team}/workflow", Ct)).RootElement.Clone();
        Assert.Equal(correlation, single.GetProperty("correlation").GetInt64());

        var listed = JsonDocument.Parse(await _person.GetStringAsync($"/api/teams/{team}/workflows", Ct)).RootElement
            .GetProperty("workflows").EnumerateArray().Single(w => w.GetProperty("correlation").GetInt64() == correlation).Clone();

        var rolled = JsonDocument.Parse(await _person.GetStringAsync("/api/teams/rollup", Ct)).RootElement
            .GetProperty("teams").EnumerateArray().Single(t => t.GetProperty("team").GetString() == team)
            .GetProperty("openWorkflows").GetProperty("workflows").EnumerateArray()
            .Single(w => w.GetProperty("correlation").GetInt64() == correlation).Clone();

        return [single, listed, rolled];
    }

    /// <summary>The real store, with every call recorded by name and arguments before it runs, into the
    /// recorder of the async flow that makes it, if that flow has one.</summary>
    public class CountingBacklogStore : DispatchProxy
    {
        private IBacklogStore _real = null!;
        private AsyncLocal<ConcurrentQueue<(string, object?[])>?> _recording = null!;

        public static IBacklogStore Around(IBacklogStore real, AsyncLocal<ConcurrentQueue<(string, object?[])>?> recording)
        {
            var proxy = Create<IBacklogStore, CountingBacklogStore>();
            var counting = (CountingBacklogStore)(object)proxy;
            counting._real = real;
            counting._recording = recording;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(method);
            _recording.Value?.Enqueue((method.Name, args ?? []));
            try { return method.Invoke(_real, args); }
            catch (TargetInvocationException thrown) when (thrown.InnerException is not null)
            {
                throw thrown.InnerException;
            }
        }
    }
}
