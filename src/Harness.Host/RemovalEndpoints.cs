using System.ComponentModel;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>What a person sends to retry an unfinished removal. No path retries every one.</summary>
public sealed record RemovalRetryRequest(
    [property: Description(
        "The folder to retry, exactly as `remaining` or `GET /api/removals` named its root. Omit it "
        + "to retry every unfinished removal.")]
    string? Path = null);

/// <summary>
/// The unfinished removals, and a person's retry of one. The delete dialog reports a removal that
/// did not finish and offers the retry right there; the Host also retries every one at start.
/// </summary>
public static class RemovalEndpoints
{
    /// <summary>One retried removal as the tenant log and the wire carry it, camel-cased by hand
    /// because nothing camel-cases a tenant log detail on the way out.</summary>
    public static object Detail(RemovalRetried retried) => new
    {
        path = retried.Path,
        kind = retried.Kind,
        team = retried.Team,
        member = retried.Member,
        finished = retried.Finished,
        remaining = retried.Remaining,
        note = retried.Note,
    };

    public static void Map(WebApplication app)
    {
        // PEOPLE ONLY, like deleting a team: this removes files, and no agent credential is consent
        // to that.
        app.MapGet("/api/removals", async (UnfinishedRemovalRetry retry, CancellationToken ct) =>
            Results.Ok((await retry.ListAsync(ct)).Select(row => new
            {
                path = row.Path,
                kind = row.Kind,
                team = row.Team,
                member = row.Member,
                remaining = row.Remaining,
                recordedAt = row.RecordedAt,
                attempts = row.Attempts,
            })))
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Folders whose removal did not finish")
            .WithDescription(
                "Every team root, member workspace, reset folder or deleted local repository's "
                + "`.deleting-<guid>` folder a deletion or reset could not finish removing, with each path still on disk. A team root keeps its "
                + "`.harness-team` marker until it is gone. The Host retries every one at start.\n\n"
                + "**A person's action.**");

        app.MapPost("/api/removals/retry", async (
            RemovalRetryRequest? request,
            UnfinishedRemovalRetry retry, TenantLogging audit, HttpContext context,
            CancellationToken ct) =>
        {
            var retried = await retry.RetryAsync(request?.Path, ct);

            if (request?.Path is { } path && retried.Count == 0)
            {
                return Results.NotFound(new { error = $"No unfinished removal is recorded for '{path}'." });
            }

            var details = retried.Select(Detail).ToArray();

            await audit.WriteAsync(
                context, TenantActions.RemovalRetried, request?.Path, request?.Path,
                new { atStart = false, retried = details }, ct);

            return Results.Ok(new { retried = details });
        })
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Retry an unfinished removal")
            .WithDescription(
                "Retries the removal a team deletion, member deletion or reset could not finish - one "
                + "folder by `path`, or every one when `path` is omitted - through the same removal "
                + "the first attempt used: content the Host cannot remove is removed as the agent "
                + "when the Host launches agents as a separate user, symbolic links are never "
                + "followed, and a team root's marker is removed last. A root without its marker is "
                + "never deleted.\n\n"
                + "Answers 200 with each folder retried, whether it `finished`, and every path still "
                + "`remaining`; 404 when `path` names nothing recorded.\n\n"
                + "**A person's action.**");
    }
}
