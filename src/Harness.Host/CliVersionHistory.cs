using System.Text.Json;

namespace Harness.Host;

/// <summary>One container start and the version each agent CLI reported at it. A null version is
/// a CLI that was not installed at that start.</summary>
public sealed record CliVersionsAtStart(DateTimeOffset At, IReadOnlyDictionary<string, string?> Versions);

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

            return new CliVersionsAtStart(when, map);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
