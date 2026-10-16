using System.Globalization;

namespace Harness.Host;

/// <summary>
/// Which workflow the person at the board is looking at, so the Concierge can join it.
/// </summary>
/// <remarks>
/// A process environment cannot be revised after launch, so the live channel is
/// <c>STEERING.md</c> in the Concierge workspace. This file is the value the next launch
/// also copies into <c>HARNESS_CAUSATION</c>. The number is a message seq. A workflow's root
/// seq is its correlation id, which is what the board highlights.
/// </remarks>
public static class SteeringFile
{
    public static string PathFor(string dataRoot, string userId) =>
        Path.Combine(dataRoot, "steering", userId + ".txt");

    public static void Write(string dataRoot, string userId, long? correlationId)
    {
        var path = PathFor(dataRoot, userId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (correlationId is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        File.WriteAllText(path, correlationId.Value.ToString(CultureInfo.InvariantCulture));
    }

    public static string? Read(string dataRoot, string userId)
    {
        var path = PathFor(dataRoot, userId);
        if (!File.Exists(path)) return null;

        var text = File.ReadAllText(path).Trim();
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? text
            : null;
    }

    /// <summary>
    /// The note for an open workflow names its team and its state in the log's words, and its newest
    /// row; <c>HARNESS_CAUSATION</c> stays the bare number.
    /// </summary>
    public static string NoteFor(SteeredWorkflow? open)
    {
        if (open is null) return Note((string?)null);

        var workflow = open.Workflow.ToString(CultureInfo.InvariantCulture);
        var latest = open.LatestRow.ToString(CultureInfo.InvariantCulture);
        return $"""
                The person is looking at workflow {workflow} (team {open.TeamLabel}, {open.State}), latest row {latest}.
                When you tell a member about that work, pass causation "{workflow}"
                so the instruction joins the workflow instead of starting a new one.
                """;
    }

    public static string Note(string? causation) =>
        causation is null
            ? """
              No workflow is selected.
              Omit causation when you tell a member about new work. When the request names a
              workflow, or a card or a member's blocked work that belongs to an open workflow,
              pass that workflow's latest row as causation so the instruction joins it.
              """
            : $"""
              The person is looking at workflow {causation}.
              When you tell a member about that work, pass causation "{causation}"
              so the instruction joins the workflow instead of starting a new one.
              """;
}
