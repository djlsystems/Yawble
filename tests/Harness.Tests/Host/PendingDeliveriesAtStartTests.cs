using System.Collections.Concurrent;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harness.Tests.Host;

/// <summary>
/// Queued deliveries a deleted team left behind, through the real Host's start: removed and
/// logged in one line naming them; a live team's are never touched, one for a member a live team
/// no longer has included - that one is logged and left.
/// </summary>
public sealed class PendingDeliveriesAtStartTests : IAsyncDisposable
{
    private readonly string _dataRoot = Directory.CreateTempSubdirectory("harness-pending-sweep-").FullName;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly LineCapture _capture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pending_delivery_for_a_deleted_team_is_removed_at_start_and_logged_and_a_live_teams_is_not()
    {
        var first = Start();
        var registry = first.Services.GetRequiredService<TeamRegistry>();
        var agent = first.Services.GetRequiredService<AgentCatalog>().Definitions.First(d => d.Mode == AgentMode.Headless).Name;
        var live = (await registry.CreateAsync("Alpha", agent, memberAgent: agent, ct: Ct)).Id;
        var gone = (await registry.CreateAsync("Gone", agent, memberAgent: agent, ct: Ct)).Id;
        Assert.NotNull(await first.Services.GetRequiredService<TeamDeletion>().DeleteAsync(gone, ct: Ct));

        // As a deletion from before the queue was cleared left them: nothing receives these.
        var pending = first.Services.GetRequiredService<IPendingDeliveries>();
        await pending.AddAsync(new ContainerId(gone, "DeveloperTobias"), 983, Ct);
        await pending.AddAsync(new ContainerId(gone, "Manager"), 984, Ct);

        // A live team's, under its member's floor so the resume that follows the sweep skips it
        // rather than running it: what is left afterwards is what the sweep left.
        await first.Services.GetRequiredService<ITeamStore>().SetFloorAsync(live, "Manager", 1000, Ct);
        await pending.AddAsync(new ContainerId(live, "Manager"), 1, Ct);
        await pending.AddAsync(new ContainerId(live, "Departed"), 2, Ct);
        await first.DisposeAsync();

        var second = Start();
        pending = second.Services.GetRequiredService<IPendingDeliveries>();

        Assert.Empty(await pending.ForTeamAsync(gone, Ct));
        Assert.Equal(
            [$"{live}/Departed", $"{live}/Manager"],
            (await pending.ForTeamAsync(live, Ct)).Select(r => r.Subscriber).Order(StringComparer.Ordinal));

        var removed = Assert.Single(_capture.Lines, l => l.StartsWith("Removed ", StringComparison.Ordinal));
        Assert.Contains("Removed 2 queued deliveries for teams that no longer exist", removed);
        Assert.Contains($"{gone}/DeveloperTobias (1)", removed);
        Assert.Contains($"{gone}/Manager (1)", removed);
        Assert.DoesNotContain(live, removed);

        var left = Assert.Single(_capture.Lines, l => l.StartsWith("Left ", StringComparison.Ordinal));
        Assert.Contains($"{live}/Departed (1)", left);
        Assert.DoesNotContain($"{live}/Manager", left);
    }

    private WebApplicationFactory<Program> Start()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureServices(services => services.AddSingleton<ILoggerProvider>(_capture))
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));
        _factories.Add(factory);
        _ = factory.Server;
        return factory;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories) await factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Every line the sweep writes, formatted.</summary>
    private sealed class LineCapture : ILoggerProvider
    {
        public ConcurrentQueue<string> Queue { get; } = new();

        public IEnumerable<string> Lines => Queue;

        public ILogger CreateLogger(string categoryName) =>
            categoryName == typeof(PendingDeliveriesAtStart).FullName ? new Logger(this) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose() { }

        private sealed class Logger(LineCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Queue.Enqueue(formatter(state, exception));
        }
    }
}
