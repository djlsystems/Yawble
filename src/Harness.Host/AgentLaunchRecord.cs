using System.Text.Json;

namespace Harness.Host;

/// <summary>
/// THE HOST'S LAUNCH DECISION, AS IT MADE IT AT ITS LAST START, in
/// <c>&lt;dataRoot&gt;/agent-launch.json</c>. <c>--doctor</c> runs in a separate process, often as
/// another user and without the Host's capabilities, so resolving the decision again there would
/// answer for the doctor and not for the Host. It reads this instead, the way it reads the CLI
/// versions the last start recorded.
/// </summary>
public sealed record AgentLaunchRecord(DateTimeOffset At, string Mode, bool Refuses, string User, string Reason)
{
    public const string FileName = "agent-launch.json";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static AgentLaunchRecord Of(AgentLaunchUser runAs, DateTimeOffset at) =>
        new(at, runAs.Mode, runAs.Refuses, runAs.Name, runAs.Reason);

    /// <summary>Written at start. Never throws: a Host that cannot write it still starts, and logs why.</summary>
    public static void Write(string dataRoot, AgentLaunchUser runAs, ILogger? log = null)
    {
        var path = Path.Combine(dataRoot, FileName);
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Of(runAs, DateTimeOffset.UtcNow), Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.LogWarning("Agent launch: could not record the decision in {Path}: {Error}", path, exception.Message);
        }
    }

    /// <summary>The last start's record, or null when there is none or it cannot be read.</summary>
    public static AgentLaunchRecord? Read(string dataRoot)
    {
        try
        {
            var path = Path.Combine(dataRoot, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<AgentLaunchRecord>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
