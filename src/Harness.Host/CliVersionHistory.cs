using System.Text.Json;

namespace Harness.Host;

/// <summary>One container start and the version each agent CLI reported at it. A null version is
/// a CLI that was not installed at that start. <paramref name="By"/> is null for a start's line and
/// `update` for the line the Host writes after a person's update of one CLI; <paramref name="Person"/>
/// is that person's email when the Host knew it, and null on a start's line.</summary>
public sealed record CliVersionsAtStart(
    DateTimeOffset At, IReadOnlyDictionary<string, string?> Versions, string? By = null, string? Person = null);

/// <summary>
/// Which CLI versions this VOLUME has started with, newest first.
///
/// <para>
/// <b>WRITTEN BY <c>scripts/ensure-agent-clis.sh</c>, READ HERE.</b> The script is what installs the
/// CLIs at each container start, so it is the one that knows what the start had; it appends one
/// JSON line to <c>&lt;dataRoot&gt;/cli-versions.jsonl</c> and keeps the newest 200. The file lives
/// on the volume, so the history follows the volume across image rebuilds. A host started outside
/// the container has no such file, and that reads as an empty history, not a failure.
/// </para>
///
/// <para>
/// A line that does not parse is skipped rather than failing the read: a start interrupted mid-write
/// must not hide every start before it.
/// </para>
/// </summary>
public sealed class CliVersionHistory(string path)
{
    public const string FileName = "cli-versions.jsonl";

    public const int MaxTake = 200;

    public string Path { get; } = path;

    public static CliVersionHistory In(string dataRoot) => new(System.IO.Path.Combine(dataRoot, FileName));

    public async Task<IReadOnlyList<CliVersionsAtStart>> ReadAsync(int take, CancellationToken ct = default)
    {
        if (!File.Exists(Path)) return [];

        var starts = new List<CliVersionsAtStart>();

        foreach (var line in await File.ReadAllLinesAsync(Path, ct))
        {
            if (Parse(line) is { } start) starts.Add(start);
        }

        return [.. starts.OrderByDescending(start => start.At).Take(Math.Clamp(take, 1, MaxTake))];
    }

    /// <summary>
    /// The version of <paramref name="cli"/> the newest line records, and when it last CHANGED: the
    /// oldest line of the newest run of lines that agree on it. A start and a person's update are
    /// both lines, so either one that brought a new version is when it was updated.
    /// </summary>
    public static CliVersionNow Now(string cli, IReadOnlyList<CliVersionsAtStart> newestFirst)
    {
        if (newestFirst.Count == 0) return new CliVersionNow(cli, null, null, null);

        var version = newestFirst[0].Versions.GetValueOrDefault(cli);
        var since = newestFirst[^1].At;

        // A version the newest line does not have is NOT KNOWN, and a not-known version has no
        // update time and no one who brought it: the line where it went missing is when reading
        // failed, not an update, and the last line that had a version speaks of a different one.
        if (version is null) return new CliVersionNow(cli, null, null, since);

        for (var i = 1; i < newestFirst.Count; i++)
        {
            if (!string.Equals(newestFirst[i].Versions.GetValueOrDefault(cli), version, StringComparison.Ordinal))
            {
                var arrived = newestFirst[i - 1];
                return new CliVersionNow(
                    cli, version, arrived.At, since,
                    arrived.By == "update" ? "person" : "start", arrived.Person);
            }
        }

        return new CliVersionNow(cli, version, null, since);
    }

    /// <summary>
    /// Appends a line: the newest line's versions with <paramref name="changed"/> laid over them,
    /// marked <paramref name="by"/>, keeping the newest <see cref="MaxTake"/>. Rewritten IN PLACE,
    /// never moved over, for the start script's reason: the Host may write the file and may not
    /// replace an entry in the data root. False when it could not be written; never throws.
    /// <paramref name="person"/> is the email of the person who asked, written as `person` when known.
    /// </summary>
    public async Task<bool> AppendAsync(
        IReadOnlyDictionary<string, string?> changed, string by, CancellationToken ct = default, string? person = null)
    {
        try
        {
            var newest = (await ReadAsync(1, ct)).FirstOrDefault();
            var versions = new SortedDictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (cli, version) in newest?.Versions ?? new Dictionary<string, string?>()) versions[cli] = version;
            foreach (var (cli, version) in changed) versions[cli] = version;

            var fields = new Dictionary<string, object?>
            {
                ["at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                ["versions"] = versions,
                ["by"] = by,
            };
            if (person is not null) fields["person"] = person;
            var line = JsonSerializer.Serialize(fields);

            var lines = File.Exists(Path) ? [.. await File.ReadAllLinesAsync(Path, ct)] : new List<string>();
            lines.Add(line);
            var kept = lines.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(MaxTake);

            await using var file = new FileStream(Path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            file.SetLength(0);
            await using var writer = new StreamWriter(file);
            foreach (var l in kept) await writer.WriteLineAsync(l.AsMemory(), ct);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static CliVersionsAtStart? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (!root.TryGetProperty("at", out var at) || !at.TryGetDateTimeOffset(out var when)
                || !root.TryGetProperty("versions", out var versions)
                || versions.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var map = new SortedDictionary<string, string?>(StringComparer.Ordinal);

            foreach (var cli in versions.EnumerateObject())
            {
                map[cli.Name] = cli.Value.ValueKind == JsonValueKind.String ? cli.Value.GetString() : null;
            }

            var by = root.TryGetProperty("by", out var byValue) && byValue.ValueKind == JsonValueKind.String
                ? byValue.GetString()
                : null;

            var person = root.TryGetProperty("person", out var personValue) && personValue.ValueKind == JsonValueKind.String
                ? personValue.GetString()
                : null;

            return new CliVersionsAtStart(when, map, by, person);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
