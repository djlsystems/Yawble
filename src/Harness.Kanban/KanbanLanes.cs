namespace Harness.Kanban;

/// <summary>
/// The board's lanes and where each status lands. There is ONE set: every team's board has these
/// lanes. Lanes per team would belong to the business-workflows spec, stored on the team, if that is
/// built.
/// </summary>
public static class KanbanLanes
{
    /// <summary>
    /// WHAT THE BOARD CALLS THE LANE FOR WORK THAT IS WAITING ON A PERSON. `blocked`,
    /// `needs-decision` and `failed` all mean the same thing to a reader - the next move is yours -
    /// so they share one lane, and "Blocked" as its label would undersell the other two. The id
    /// is `blocked`: a `kanban.card.moved` row writes its `laneId` into the APPEND-ONLY log, so
    /// a lane id is a durable reference. Render the name, address with the id.
    /// </summary>
    public const string HumanActionLaneLabel = "Needs You";

    /// <summary>The lane that holds RUNNING work. Its limit is <c>wip.maxRunning</c>.</summary>
    public const string Running = "in-progress";

    /// <summary>The lane finished work lands in, and where a person's "done" move puts a card.</summary>
    public const string Done = "done";

    public static readonly IReadOnlyList<Lane> All =
    [
        new Lane("todo", "To Do"),
        new Lane(Running, "In Progress"),

        // A lane of its own: a blocked card falling through to TO DO would not be a wrong enough place
        // to notice - it would read as work waiting to be picked up rather than work waiting on you.
        new Lane("blocked", HumanActionLaneLabel),
        new Lane(Done, "Done"),
    ];

    /// <summary>The board's own spelling of <paramref name="laneId"/>, or null when it has no such lane.</summary>
    public static string? Find(string laneId) =>
        All.FirstOrDefault(lane => string.Equals(lane.Id, laneId, StringComparison.OrdinalIgnoreCase))?.Id;

    /// <summary>The lane a card with this status belongs in.</summary>
    public static string ForStatus(string status) =>
        status switch
        {
            "running" => Running,
            "blocked" or "needs-decision" or "failed" => "blocked",

            // A HAND-BACK IS A SUCCESS AND ROUTES WITH `completed`, NEVER WITH `blocked`,
            // which is labelled for HUMAN ACTION: a team that finished must not read as a team in
            // trouble. A member hands back and its run exits seconds later, so `handback` and
            // `completed` both land on one card; routed apart, the card would cross the board twice
            // for one ending.
            "completed" or "handback" or "done" => Done,
            _ => "todo",
        };

    /// <summary>
    /// Colour palette: locked.
    /// </summary>
    public static string ColourForStatus(string status) =>
        status switch
        {
            "queued" => "slate",           // slate
            "running" => "blue",           // blue
            "blocked" => "amber",          // amber
            "needs-decision" => "orange",  // orange
            "failed" => "red",             // red
            "completed" => "teal",         // teal

            // TEAL, THE SAME WORD `completed` CARRIES, and shared rather than minted. The palette
            // is locked and the browser has a class per word (`web/src/lib/kanban.ts`), so a new
            // colour here would render as a colourless card until a stylesheet caught up. What
            // tells a hand-back from a completion is the STATUS and its label; what the colour has
            // to say is that neither is trouble.
            "handback" => "teal",          // teal
            "done" => "green",             // green
            _ => "slate",                  // slate (default)
        };
}
