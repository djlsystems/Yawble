using System.Text.Json;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Storage;
using Xunit;

namespace Yawble.Core.Tests.Bus;

/// <summary>
/// A migrated database file in its own temp folder, deleted afterwards. The file is a copy of
/// <see cref="TemplateDb"/>, so making one runs no SQL. A test that needs an empty or older database makes its own
/// file and runs <see cref="SchemaMigrator"/> on it.
/// </summary>
internal sealed class TempDb : IDisposable
{
    public TempDb()
    {
        Dir = Directory.CreateTempSubdirectory("yawble-bus-").FullName;
        Path = System.IO.Path.Combine(Dir, "yawble.db");
        TemplateDb.CopyTo(Path);
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

/// <summary>
/// The fully migrated database every <see cref="TempDb"/> starts from. The migrations run once per test process, on
/// first use, into a file of their own; the file is checkpointed and closed (so it is one whole file in WAL mode,
/// with no -wal or -shm beside it), read into memory and deleted. Each copy is those bytes written to a new file.
/// </summary>
internal static class TemplateDb
{
    private static readonly Lazy<byte[]> Bytes = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Writes a fresh, fully migrated database to <paramref name="path"/>.</summary>
    public static void CopyTo(string path) => File.WriteAllBytes(path, Bytes.Value);

    private static byte[] Build()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-template-").FullName;
        try
        {
            var path = Path.Combine(dir, "yawble.db");
            var connectionString = $"Data Source={path};Pooling=False";
            SchemaMigrator.Migrate(connectionString);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var checkpoint = connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpoint.ExecuteNonQuery();
            }
            // Pooling is off, so the file is closed here. A log file still beside it would hold pages the copy lacks.
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(path + suffix) && new FileInfo(path + suffix).Length > 0)
                    throw new InvalidOperationException($"the template database still has a {suffix} file after its checkpoint");
            return File.ReadAllBytes(path);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>A fake runner whose behaviour per member is a script; everything else completes at once.</summary>
internal sealed class ScriptedRunner : IAgentRunner
{
    private readonly Dictionary<string, Func<RunContext, CancellationToken, Task<RunResult>>> _workflows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private int _running;

    public Func<RunContext, CancellationToken, Task<RunResult>> Default { get; set; } = (_, _) => Task.FromResult(RunResult.Ok);
    public List<RunContext> Admitted { get; } = [];
    public int MaxConcurrent { get; private set; }

    public ScriptedRunner On(string qualified, Func<RunContext, CancellationToken, Task<RunResult>> script)
    {
        _workflows[qualified] = script;
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
            var script = _workflows.GetValueOrDefault(run.Member.Qualified) ?? Default;
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
    /// <summary>
    /// How long a wait lasts when the caller gives no timeout. A passing wait ends as soon as its condition holds,
    /// so the length costs only a failing test; it is long enough for a busy machine.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static async Task UntilAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
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
