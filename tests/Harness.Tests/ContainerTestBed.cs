using System.Collections.Concurrent;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Messaging;

namespace Harness.Tests;

/// <summary>
/// An agent that records every invocation and answers with whatever <see cref="Behaviour"/> says.
/// The behaviour is asynchronous so a test can make the "agent" publish to the log mid-run, the way
/// a real member calls a tool.
/// </summary>
public sealed class FakeAgent : IAgentRunner
{
    public ConcurrentQueue<AgentInvocation> Invocations { get; } = new();

    public Func<AgentInvocation, Task<AgentResult>> Behaviour { get; set; } =
        _ => Task.FromResult(new AgentResult(0, "done"));

    public int RunsFor(ContainerId id) => Invocations.Count(i => i.Container == id);

    public async Task<AgentResult> RunAsync(AgentInvocation invocation, CancellationToken ct = default)
    {
        Invocations.Enqueue(invocation);
        return await Behaviour(invocation);
    }
}

/// <summary>
/// A real <see cref="ContainerHost"/> over a real SQLite log in a temp folder, driven by hand
/// through <see cref="ContainerHost.PumpOnceAsync"/>. The subscriptions store is written from the
/// container's own declared set on register, because there is no TeamRegistry here to do it.
/// </summary>
public sealed class ContainerTestBed : IAsyncDisposable
{
    private readonly string _directory;

    public ContainerTestBed(
        WipLedger? wip = null,
        Func<ContainerId, string, IReadOnlyList<RepoWorktree>>? worktrees = null,
        long? workflowSpendLimit = null,
        ITriggerStore? triggers = null,
        bool pending = false,
        Func<Message, CancellationToken, Task>? onTerminal = null,
        Func<ContainerId, long?, bool, CancellationToken, Task<bool>>? onRunEnding = null)
    {
        _directory = Path.Combine(Path.GetTempPath(), $"harness-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);

        var database = DatabasePath = Path.Combine(_directory, "messages.db");
        new SchemaMigrator(database).ApplyAsync(SchemaModules.All).GetAwaiter().GetResult();

        Store = new SqliteMessageStore(database);
        Pending = pending ? new SqlitePendingDeliveries(database) : null;

        Context = new LedgerContextBuilder(new SqliteLedger(database));
        Runner = new AgentMemberRunner(Agent, Context);

        ContainerHost host = null!;
        host = new ContainerHost(
            Store, Store, Store,
            new InMemoryTranscriptStore(),
            pending: Pending,
            triggers: triggers,
            onRegistered: (id, ct) => Store.SetAsync(id, host.Find(id)!.Snapshot().Subscribes, ct),
            workflowSpendLimit: workflowSpendLimit,
            wip: wip,
            worktrees: worktrees,
            onTerminal: onTerminal,
            onRunEnding: onRunEnding);
        Host = host;
    }

    public SqliteMessageStore Store { get; }

    /// <summary>The message log's database file, for a test that reads every table of it.</summary>
    public string DatabasePath { get; }

    /// <summary>The pending-delivery rows, when the bed was asked for them; null otherwise.</summary>
    public SqlitePendingDeliveries? Pending { get; }

    public ContainerHost Host { get; }

    public FakeAgent Agent { get; } = new();

    /// <summary>The ledger history an agent member is handed, as Program.cs wires it.</summary>
    public IContextBuilder Context { get; }

    /// <summary><see cref="Agent"/> as an agent MEMBER: the adapter that renders the prompt and
    /// history from the batch, exactly as the Host composes it.</summary>
    public AgentMemberRunner Runner { get; }

    /// <summary>Any agent runner as a member, with this bed's ledger history.</summary>
    public AgentMemberRunner AsMember(IAgentRunner agent) => new(agent, Context);

    public static ContainerDefinition Definition(ContainerId id, string[]? subscribes = null) =>
        new(id, "fake", SystemPrompt: "", WorkingDirectory: ".",
            Subscribes: subscribes ?? [], Environment: new Dictionary<string, string>());

    public Task<MemberRuntime> AddAsync(ContainerId id, string[]? subscribes = null) =>
        Host.AddAsync(Definition(id, subscribes), Runner);

    public async Task<bool> PumpUntilAsync(Func<bool> until, int attempts = 100)
    {
        for (var i = 0; i < attempts; i++)
        {
            await Host.PumpOnceAsync();
            if (until()) return true;
            await Task.Delay(20);
        }

        return until();
    }

    public async Task<bool> PumpUntilAsync(Func<Task<bool>> until, int attempts = 100)
    {
        for (var i = 0; i < attempts; i++)
        {
            await Host.PumpOnceAsync();
            if (await until()) return true;
            await Task.Delay(20);
        }

        return await until();
    }

    /// <summary>Pumps a few more times so anything the last run published has had its chance to
    /// wake somebody. Used to assert that nothing further happens.</summary>
    public async Task SettleAsync()
    {
        for (var i = 0; i < 10; i++)
        {
            await Host.PumpOnceAsync();
            await Task.Delay(20);
        }
    }

    public async Task<IReadOnlyList<Message>> OfTypeAsync(string type) =>
        await Store.ReadAfterAsync(0, [type], int.MaxValue);

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class InMemoryTranscriptStore : ITranscriptStore
{
    private readonly ConcurrentDictionary<string, string> _written = new(StringComparer.Ordinal);

    public Task<TranscriptRef> WriteAsync(
        ContainerId container, long causeSeq, string content, CancellationToken ct = default)
    {
        var reference = new TranscriptRef(container, causeSeq);
        _written[reference.ToString()] = content;
        return Task.FromResult(reference);
    }

    public Task<string?> ReadAsync(TranscriptRef reference, CancellationToken ct = default) =>
        Task.FromResult(_written.GetValueOrDefault(reference.ToString()));
}
