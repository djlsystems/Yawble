using System.Collections.Concurrent;
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
/// A BOARD READ ASKS FOR ITS CARDS' OUTCOMES ONCE, however many cards it has: the Host's own
/// <see cref="IOutcomeStore"/> wrapped in one that records every call, and a board of several cards
/// read through both board routes. N cards asking N times fails here.
/// </summary>
public sealed class BoardOutcomeQueryTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-board-outcome-query-{Guid.NewGuid():N}");
    private readonly ConcurrentQueue<(string Method, object?[] Args)> _calls = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentRunner>(new FakeAgent());

                // The Host registers its store as an instance; the board reads this one around it.
                var real = (IOutcomeStore)services.Last(d => d.ServiceType == typeof(IOutcomeStore)).ImplementationInstance!;
                services.AddSingleton(CountingOutcomeStore.Around(real, _calls));
            }));

        _team = (await _factory.Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Counted", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await _factory.Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
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
    public async Task A_board_of_several_cards_asks_for_their_outcomes_in_one_query_on_either_route()
    {
        var first = await CreateAsync("Counted first");
        var second = await CreateAsync("Counted second");
        var workflows = new[]
        {
            await WorkflowAsync(first), await WorkflowAsync(first), await WorkflowAsync(second), await WorkflowAsync(null),
        };

        foreach (var route in new[] { $"/api/teams/{_team}/kanban/board", $"/api/kanban/board?team={_team}" })
        {
            _calls.Clear();
            var board = JsonDocument.Parse(await _person.GetStringAsync(route, Ct)).RootElement;

            var cards = board.GetProperty("cards").EnumerateArray()
                .Where(c => workflows.Contains(c.GetProperty("workflowSeq").GetInt64()))
                .ToDictionary(c => c.GetProperty("workflowSeq").GetInt64(), c => c.GetProperty("outcome"));
            Assert.Equal(workflows.Length, cards.Count);
            Assert.Equal(
                new string?[] { first, first, second, null },
                workflows.Select(w => cards[w].ValueKind == JsonValueKind.Null ? null : cards[w].GetProperty("id").GetString()).ToArray());

            // ONE query, naming every card's workflow; never one per card, nor the whole link table.
            var asked = _calls
                .Where(c => c.Method == nameof(IOutcomeStore.CurrentOutcomesAsync))
                .Select(c => ((IReadOnlyCollection<long>)c.Args[0]!).ToHashSet())
                .Where(set => set.Overlaps(workflows))
                .ToList();
            Assert.Single(asked);
            Assert.Superset(workflows.ToHashSet(), asked[0]);
            Assert.DoesNotContain(_calls, c => c.Method == nameof(IOutcomeStore.ReadLinksAsync));
            Assert.DoesNotContain(_calls, c => c.Method == nameof(IOutcomeStore.CurrentLinkAsync) && workflows.Contains((long)c.Args[0]!));
        }
    }

    private async Task<string> CreateAsync(string name)
    {
        var created = await _person.PostAsJsonAsync("/api/outcomes", new { name }, Ct);
        created.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("id").GetString()!;
    }

    private async Task<long> WorkflowAsync(string? outcome)
    {
        var told = await _person.PostAsJsonAsync(
            $"/api/teams/{_team}/containers/{TeamRegistry.DefaultManagerName}/tell",
            new { instruction = "look at the openings", outcome }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));
        return JsonDocument.Parse(await told.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("correlationId").GetInt64();
    }

    /// <summary>The real store, with every call recorded by name and arguments before it runs.</summary>
    public class CountingOutcomeStore : DispatchProxy
    {
        private IOutcomeStore _real = null!;
        private ConcurrentQueue<(string, object?[])> _calls = null!;

        public static IOutcomeStore Around(IOutcomeStore real, ConcurrentQueue<(string, object?[])> calls)
        {
            var proxy = Create<IOutcomeStore, CountingOutcomeStore>();
            var counting = (CountingOutcomeStore)(object)proxy;
            counting._real = real;
            counting._calls = calls;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            _calls.Enqueue((method!.Name, args ?? []));
            try { return method.Invoke(_real, args); }
            catch (TargetInvocationException thrown) when (thrown.InnerException is not null)
            {
                throw thrown.InnerException;
            }
        }
    }
}
