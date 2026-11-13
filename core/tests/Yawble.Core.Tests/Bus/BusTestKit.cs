using System.Text.Json;
using Yawble.Core.Bus;
using Yawble.Core.Storage;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>A migrated database file in its own temp folder, deleted afterwards.</summary>
internal sealed class TempDb : IDisposable
{
    public TempDb()
    {
        Dir = Directory.CreateTempSubdirectory("yawble-bus-").FullName;
        Path = System.IO.Path.Combine(Dir, "yawble.db");
        SchemaMigrator.Migrate(ConnectionString);
    }

    public string Dir { get; }
    public string Path { get; }
    public string ConnectionString => $"Data Source={Path};Pooling=False";

    public BusStore OpenStore() => new(ConnectionString);

    public void Dispose()
    {
        // TestFiles.DeleteTree: the team repo tests leave git clones here, whose read-only object files a plain
        // recursive delete cannot remove on Windows (the temp folder would leak on every run).
        try { TestFiles.DeleteTree(Dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A fake runner whose behaviour per member is a script; everything else completes at once.</summary>
internal sealed class ScriptedRunner : IAgentRunner
{
    private readonly Dictionary<string, Func<RunContext, CancellationToken, Task<RunResult>>> _scripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private int _running;

    public Func<RunContext, CancellationToken, Task<RunResult>> Default { get; set; } = (_, _) => Task.FromResult(RunResult.Ok);
    public List<RunContext> Admitted { get; } = [];
    public int MaxConcurrent { get; private set; }

    public ScriptedRunner On(string qualified, Func<RunContext, CancellationToken, Task<RunResult>> script)
    {
        _scripts[qualified] = script;
        return this;
    }

    public async Task<RunResult> RunAsync(RunContext run, CancellationToken ct)
    {
        lock (_lock)
        {
            Admitted.Add(run);
            MaxConcurrent = Math.Max(MaxConcurrent, ++_running);
        }
        try
        {
            var script = _scripts.GetValueOrDefault(run.Member.Qualified) ?? Default;
            return await script(run, ct);
        }
        finally
        {
            lock (_lock) _running--;
        }
    }

    /// <summary>
    /// Who was admitted, in the engine's admission order. Run ids are assigned in that order under the engine's gate;
    /// the order in which concurrently admitted runs reach <see cref="RunAsync"/> is the thread pool's, not the
    /// scheduler's (seen reversed on the 3-core macOS runner).
    /// </summary>
    public IReadOnlyList<string> AdmittedNames()
    {
        lock (_lock) return Admitted.OrderBy(r => r.RunId).Select(r => r.Member.Qualified).ToArray();
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"timed out waiting for: {what}");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    public static Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null) =>
        UntilAsync(() => Task.FromResult(condition()), what, timeout);
}

internal static class Payload
{
    public static string? Field(Message m, string name)
    {
        using var doc = JsonDocument.Parse(m.Payload);
        return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
