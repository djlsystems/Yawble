using Harness.Host.Auth;
using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// THE WORKER KEY FROM A FILE. In an image the entrypoint moves the key out of the environment into a
/// file only the container's Host user can read and names it in HARNESS_WORKER_KEY_FILE; control and
/// a worker read it from there, and neither hands the file's name or the key to a run.
/// </summary>
[Collection("worker processes")]
public sealed class WorkerKeyFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("harness-worker-key-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void The_key_itself_is_taken_before_a_file_and_a_file_gives_its_first_line_trimmed()
    {
        var file = Path.Combine(_root, "worker-key");
        File.WriteAllText(file, "  from-the-file \nsecond line\n");

        Assert.Equal(("given", (string?)null), WorkerKeyFile.Read("given", file));
        Assert.Equal(("from-the-file", (string?)null), WorkerKeyFile.Read(null, file));
        Assert.Equal(("from-the-file", (string?)null), WorkerKeyFile.Read("  ", file));
        Assert.Equal(((string?)null, (string?)null), WorkerKeyFile.Read(null, null));
    }

    [Fact]
    public void A_key_file_that_cannot_be_read_or_holds_no_key_is_refused_with_the_sentence()
    {
        var missing = Path.Combine(_root, "missing");
        var (key, refusal) = WorkerKeyFile.Read(null, missing);
        Assert.Null(key);
        Assert.StartsWith($"The worker key file {missing} could not be read: ", refusal);

        var empty = Path.Combine(_root, "empty");
        File.WriteAllText(empty, "\n");
        Assert.Equal(((string?)null, $"The worker key file {empty} could not be read: it holds no key."), WorkerKeyFile.Read(null, empty));
    }

    [Fact]
    public async Task Control_and_a_worker_given_the_key_by_file_connect()
    {
        await using var bed = new ProcessBed();
        var file = Path.Combine(bed.Work, "worker-key");
        await File.WriteAllTextAsync(file, bed.Key + "\n", TestContext.Current.CancellationToken);
        await bed.StartControlAsync(keyFile: file);
        bed.StartWorker("w1", key: "", more: new Dictionary<string, string> { [WorkerKeyFile.FileVariable] = file });
        await bed.UntilAsync("w1 is connected", async () => (await bed.WorkersAsync()).Any(w => w.GetProperty("id").GetString() == "w1"));

        var team = await bed.TeamAsync("Keyed", "Dev");
        (await bed.TellAsync(team, "Dev", "Do the work.")).EnsureSuccessStatusCode();
        await bed.UntilAsync("the run ended", () => Task.FromResult(bed.FakeRuns().Any(r => r.Member == "Dev" && r.Ended)));

        // Neither the key nor where it is reaches the run.
        var run = bed.FakeRuns().Single(r => r.Member == "Dev");
        Assert.DoesNotContain(bed.Key, run.Environment);
        Assert.DoesNotContain(WorkerKeyFile.FileVariable, run.Environment);
    }

    [Fact]
    public async Task A_worker_whose_key_file_cannot_be_read_exits_2_with_the_sentence()
    {
        await using var bed = new ProcessBed();
        await bed.StartControlAsync();
        var missing = Path.Combine(bed.Work, "no-such-key");
        var worker = bed.StartWorker("w1", key: "", more: new Dictionary<string, string> { [WorkerKeyFile.FileVariable] = missing });

        await worker.WaitAsync(ProcessBed.Bound);
        Assert.True(worker.Exited, worker.Text());
        Assert.Equal(WorkerProcess.Misconfigured, worker.ExitCode);
        Assert.Contains($"The worker key file {missing} could not be read: ", worker.Text());
    }

    /// <summary>
    /// The way back to the release before control and workers were split: that release's principal
    /// store reads the worker key's row with a kind it does not know. The line that reads it is the
    /// same in this build, so a kind this build cannot parse stands in for it: refused, not thrown.
    /// </summary>
    [Fact]
    public async Task A_principal_of_a_kind_this_build_cannot_read_is_refused_not_thrown()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = Path.Combine(_root, "messages.db");
        await new SchemaMigrator(database).ApplyAsync(SchemaModules.All, ct);
        var principals = new SqlitePrincipalStore(database);
        const string Key = "a-worker-key-of-a-later-release";
        await WorkerKeyGate.SetAsync(principals, Key, ct);

        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE principals SET kind = 'KindOfALaterRelease' WHERE id = $id";
            command.Parameters.AddWithValue("$id", WorkerKeyGate.PrincipalId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
        }

        Assert.Null(await principals.ResolveAsync(Key, ct));
    }
}
