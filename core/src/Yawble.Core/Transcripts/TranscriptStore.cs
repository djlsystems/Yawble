using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Yawble.Core.Transcripts;

public sealed record TranscriptInfo(long RunId, string Member, string Path, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, long Bytes);

/// <summary>
/// One transcript per run: the raw bytes the agent's CLI wrote (headless stream-json or the PTY stream),
/// appended to an owner-only file &lt;data&gt;/transcripts/&lt;team&gt;/&lt;member&gt;/run-&lt;id&gt;.log and indexed in the
/// transcripts table. The run token in a transcript is useless after the run; no model credential is ever in one,
/// because none ever enters the sandbox.
/// </summary>
public sealed class TranscriptStore(string dataDir, string connectionString, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string Root => Path.Combine(dataDir, "transcripts");

    public TranscriptWriter Open(long runId, string team, string member)
    {
        var dir = Path.Combine(Root, Safe(team), Safe(member));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(dir, $"run-{runId}.log");
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(path, options);
        Exec("""
            INSERT INTO transcripts (run_id, member, path, started_at, bytes) VALUES ($r, $m, $p, $s, $b)
            ON CONFLICT(run_id) DO UPDATE SET ended_at = NULL;
            """, ("$r", runId), ("$m", $"{team}/{member}"), ("$p", path), ("$s", Now()), ("$b", stream.Length));
        return new TranscriptWriter(this, runId, stream);
    }

    public TranscriptInfo? Find(long runId)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT run_id, member, path, started_at, ended_at, bytes FROM transcripts WHERE run_id = $r;";
        cmd.Parameters.AddWithValue("$r", runId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new TranscriptInfo(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
            reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            reader.GetInt64(5));
    }

    /// <summary>The whole transcript (for the replay buffer and the run history), or null if there is none.</summary>
    public byte[]? Read(long runId) => Find(runId) is { } info && File.Exists(info.Path) ? File.ReadAllBytes(info.Path) : null;

    internal void Closed(long runId, long bytes) =>
        Exec("UPDATE transcripts SET ended_at = $e, bytes = $b WHERE run_id = $r;", ("$e", Now()), ("$b", bytes), ("$r", runId));

    private string Now() => _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || c == '.' ? '_' : c).ToArray());
        return cleaned.Length == 0 ? "_" : cleaned;
    }

    private void Exec(string sql, params (string Name, object Value)[] args)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>Appends a run's output as it arrives; flushed per write so a crash loses at most the last write.</summary>
public sealed class TranscriptWriter : IAsyncDisposable
{
    private readonly TranscriptStore _store;
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _lock = new(1, 1);

    internal TranscriptWriter(TranscriptStore store, long runId, FileStream stream)
    {
        _store = store;
        RunId = runId;
        _stream = stream;
    }

    public long RunId { get; }

    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(bytes, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task WriteAsync(string text, CancellationToken ct = default) => WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), ct);

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var length = _stream.Length;
            await _stream.DisposeAsync();
            _store.Closed(RunId, length);
        }
        finally
        {
            _lock.Release();
        }
    }
}
