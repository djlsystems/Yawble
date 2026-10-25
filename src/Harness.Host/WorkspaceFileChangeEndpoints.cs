using System.ComponentModel;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>A page of recorded file changes, oldest first. <see cref="Next"/> is the <c>after</c>
/// of the next page, or null when this page is the last.</summary>
public sealed record WorkspaceFileChangesPage(IReadOnlyList<RecordedWorkspaceChange> Changes, long? Next);

/// <summary>
/// THE MONITOR'S REPLAY OF MEMBERS' FILE CHANGES: <c>GET /api/file-changes</c>. What
/// <see cref="WorkspaceFileWatch"/> recorded during every run, for a period, oldest first, paged by
/// the row's id.
///
/// <para>
/// PEOPLE ONLY, as the watch itself is: a machine principal is refused. At most what the log holds -
/// nothing older than the oldest log row - and only a team that exists now, from its creation, so a
/// team re-created under the same name reads none of its predecessor's rows.
/// </para>
/// </summary>
public static class WorkspaceFileChangeEndpoints
{
    public const int DefaultTake = 200;
    public const int MaxTake = 500;

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/file-changes", async (
                [Description("The period's start, an instant. Omit it for the oldest row the log holds.")]
                DateTimeOffset? from,
                [Description("The period's end, an instant, inclusive. Omit it for now.")]
                DateTimeOffset? to,
                [Description("Only rows after this id - the previous page's `next`. Omit it for the first page.")]
                long? after,
                [Description("How many to return. Default 200, clamped to 1..500.")]
                int? take,
                IWorkspaceFileChangeStore store, IMessageLog log, TeamRegistry teams, ITeamStore teamStore,
                TimeProvider clock, CancellationToken ct) =>
            {
                if (from is { } f && to is { } t && f > t)
                {
                    return Results.BadRequest(new { error = "The period's start is after its end." });
                }

                var count = Math.Clamp(take ?? DefaultTake, 1, MaxTake);

                // AT MOST WHAT THE LOG HOLDS: never a file change older than the oldest log row.
                if ((await log.ReadRangeAsync(0, 1, ct)) is not [var oldest])
                {
                    return Results.Ok(new WorkspaceFileChangesPage([], null));
                }

                var start = from is { } asked && asked > oldest.OccurredAt ? asked : oldest.OccurredAt;
                var end = to ?? clock.GetUtcNow();

                // Each team that exists now, from its creation.
                var since = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
                foreach (var team in teams.All())
                {
                    since[team.Id] = await teamStore.CreatedAtAsync(team.Id, ct) ?? DateTimeOffset.MinValue;
                }

                var changes = await store.ReadAsync(start, end, after ?? 0, count, since, ct);

                return Results.Ok(new WorkspaceFileChangesPage(changes, changes.Count == count ? changes[^1].Id : null));
            })
            .WithTags("Activity")
            .HumansOnly()
            .WithSummary("The file changes members' runs made, for the Monitor's replay")
            .WithDescription(
                "What a running member did to the files of its team's repositories, as the Monitor's live "
                + "view draws it: one row per read of one member's worktree that found something, recorded "
                + "during every run whether or not anybody watched. Answers `{ changes, next }`, oldest "
                + "first; each change is `{ id, team, member, repo, at, files, commits, more, correlation }` "
                + "- the `workspaceChanged` push's own shape (`files` with `path`, `project` and `action`; "
                + "`commits` with `sha`, `subject` and `files`; `more` counts what the read's caps left out) "
                + "with the row's `id` and the run's workflow (`correlation`, null when not known).\n\n"
                + "Paths, actions, shas and subjects only: never a file's contents or a diff.\n\n"
                + "The period is `from`..`to` (instants, `to` inclusive), and never older than the oldest "
                + "row the log holds: file changes are pruned with the log. Only teams that exist now, "
                + "from each team's creation, so a team re-created under the same name reads none of its "
                + "predecessor's. Paged by `after`: pass `next` for the following page; `next` is null on "
                + "the last. `take` defaults to 200, clamped to 1..500. 400 when `from` is after `to`.\n\n"
                + "**A person's read.**");
    }
}
