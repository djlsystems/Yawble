using System.ComponentModel;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>A page of recorded agent steps, oldest first. <see cref="Next"/> is the <c>after</c> of
/// the next page, or null when this page is the last.</summary>
public sealed record AgentStepsPage(IReadOnlyList<AgentStep> Steps, long? Next);

/// <summary>
/// THE MONITOR'S REPLAY OF WHAT AGENTS DID: <c>GET /api/run-steps</c>. What
/// <see cref="AgentStepFollow"/> recorded from every run's transcript, for a period, oldest first,
/// paged by the row's id, as <c>GET /api/file-changes</c> is.
///
/// <para>
/// PEOPLE ONLY, as the push is: a machine principal is refused. At most what the log holds -
/// nothing older than the oldest log row - and only a team that exists now, from its creation, so a
/// team re-created under the same name reads none of its predecessor's rows.
/// </para>
/// </summary>
public static class AgentStepEndpoints
{
    public const int DefaultTake = WorkspaceFileChangeEndpoints.DefaultTake;
    public const int MaxTake = WorkspaceFileChangeEndpoints.MaxTake;

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/run-steps", async (
                [Description("The period's start, an instant. Omit it for the oldest row the log holds.")]
                DateTimeOffset? from,
                [Description("The period's end, an instant, inclusive. Omit it for now.")]
                DateTimeOffset? to,
                [Description("Only rows after this id - the previous page's `next`. Omit it for the first page.")]
                long? after,
                [Description("How many to return. Default 200, clamped to 1..500.")]
                int? take,
                IAgentStepStore store, IMessageLog log, TeamRegistry teams, ITeamStore teamStore,
                TimeProvider clock, CancellationToken ct) =>
            {
                if (from is { } f && to is { } t && f > t)
                {
                    return Results.BadRequest(new { error = "The period's start is after its end." });
                }

                var count = Math.Clamp(take ?? DefaultTake, 1, MaxTake);

                // AT MOST WHAT THE LOG HOLDS: never a step older than the oldest log row.
                if ((await log.ReadRangeAsync(0, 1, ct)) is not [var oldest])
                {
                    return Results.Ok(new AgentStepsPage([], null));
                }

                var start = from is { } asked && asked > oldest.OccurredAt ? asked : oldest.OccurredAt;
                var end = to ?? clock.GetUtcNow();

                // Each team that exists now, from its creation.
                var since = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
                foreach (var team in teams.All())
                {
                    since[team.Id] = await teamStore.CreatedAtAsync(team.Id, ct) ?? DateTimeOffset.MinValue;
                }

                var steps = await store.ReadAsync(start, end, after ?? 0, count, since, ct);

                return Results.Ok(new AgentStepsPage(steps, steps.Count == count ? steps[^1].Id : null));
            })
            .WithTags("Activity")
            .HumansOnly()
            .WithSummary("The steps members' agents took, for the Monitor's replay")
            .WithDescription(
                "What each running agent did, step by step, as its own transcript shows it: one row per "
                + "tool call, tool result, thing it said, user line or note, recorded during every run "
                + "whether or not anybody watched. Answers `{ steps, next }`, oldest first; each step is "
                + "`{ id, team, member, run, workflow, at, kind, label, mcpTool, count }` - the "
                + "`agentSteps` push's own shape. `kind` is `tool`, `result`, `text`, `user` or `note`; "
                + "`label` is the tool's name for a `tool` step (`MCP` for an MCP tool, whose own name is "
                + "`mcpTool`) and empty otherwise; `at` is the transcript's own time, or when it was read "
                + "when it names none; `run` is the run's terminal row once it is written, else the run's "
                + "delivery; `workflow` is null when not known; `count` is above 1 when steps over the "
                + "cap of " + AgentStepCap.PerSecond + " a run a second were counted into this one.\n\n"
                + "Kind, tool name and time only: never a command, path, argument, result, prose, user "
                + "line or note body.\n\n"
                + "The period is `from`..`to` (instants, `to` inclusive), and never older than the oldest "
                + "row the log holds: steps are pruned with the log. Only teams that exist now, from each "
                + "team's creation, so a team re-created under the same name reads none of its "
                + "predecessor's. Paged by `after`: pass `next` for the following page; `next` is null on "
                + "the last. `take` defaults to 200, clamped to 1..500. 400 when `from` is after `to`.\n\n"
                + "**A person's read.**");
    }
}
