using System.Text.Json;

namespace Harness.Host;

/// <summary>
/// THE HOST'S ADMISSION AND RUN MEMORY FIGURES, as <c>GET /api/wip</c> states them in <c>limit</c> and
/// <c>runMemory</c>, and as the Host last recorded them in <c>&lt;dataRoot&gt;/wip.json</c> for
/// <c>--doctor</c>. Both are built by <see cref="Of"/> from <see cref="TenantSettings.RunLimit"/> and
/// <see cref="RunMemoryLimits.Report"/>: the doctor is another process, without the Host's cgroup or its
/// settings, so working the figures out again there would answer for the doctor and not for the Host.
/// </summary>
public sealed record WipRecord(DateTimeOffset At, WipRunLimit Limit, RunMemoryReport RunMemory)
{
    public const string FileName = "wip.json";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The figures now, from the Host's own decision code.</summary>
    public static WipRecord Of(TenantSettings settings, RunMemoryLimits memory) =>
        new(DateTimeOffset.UtcNow, settings.RunLimit(), memory.Report());

    /// <summary>
    /// Recorded now and again after every setting a person changes, since both figures read settings on
    /// use. Never throws: a Host that cannot record it still starts, and logs why.
    /// </summary>
    public static void Keep(string dataRoot, TenantSettings settings, RunMemoryLimits memory, ILogger? log = null)
    {
        Of(settings, memory).Write(dataRoot, log);
        settings.Changed += _ => Of(settings, memory).Write(dataRoot, log);
    }

    public void Write(string dataRoot, ILogger? log = null)
    {
        var path = Path.Combine(dataRoot, FileName);
        try
        {
            var temp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.LogWarning("Wip: could not record the run limit and run memory in {Path}: {Error}", path, exception.Message);
        }
    }

    /// <summary>The last record, or null when there is none or it cannot be read.</summary>
    public static WipRecord? Read(string dataRoot)
    {
        try
        {
            var path = Path.Combine(dataRoot, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<WipRecord>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
