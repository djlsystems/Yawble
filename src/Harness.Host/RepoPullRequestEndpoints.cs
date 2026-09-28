using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// CONTRIBUTOR MODE'S GITHUB BUTTONS: Open pull request (which replaces Merge to main), its
/// draft, and Fork it for me. Every one is a PERSON's button - <c>.HumansOnly()</c> - and none is
/// reachable from an agent: no tool, no skill text, and <c>workflow_complete</c> never opens a pull
/// request. GitHub is asked through <see cref="IGitHubContributor"/>, which every test fakes.
/// </summary>
public static partial class RepoEndpoints
{
    private static void MapPullRequest(WebApplication app)
    {
        app.MapGet("/api/teams/{team}/repos/{repo}/pull-request-draft", PullRequestDraftAsync)
            .WithTags("Repos")
            .HumansOnly()
            .WithSummary("The title and body Open pull request starts with")
            .WithDescription(
                "Title: the title of the backlog item this team was last given, else the subject of "
                + "its latest completed workflow. Body: that workflow's `workflow_complete` delivery text, then "
                + "the item's citation. Both are a starting point: the person edits them before sending. Reads "
                + "nothing from GitHub. 404 for an unknown team or repository.");

        app.MapPost("/api/teams/{team}/repos/{repo}/pull-request", OpenPullRequestAsync)
            .WithTags("Repos")
            .HumansOnly()
            .WithSummary("Open a pull request upstream from team/{id} on the fork")
            .WithDescription(
                "Contributor mode only (409 for an owned repository, whose button is Merge to main). "
                + "Fetches both remotes, then runs `gh pr create --repo <upstream> --head <fork-owner>:team/{id} "
                + "--base <default>` with the title and body given. Refused with 409, changing nothing: while "
                + "the default branch is not known (never assumed `main`), while team/{id} is not on the fork, "
                + "and - with Sign off commits (DCO) on - while any commit on the fork's team/{id} since "
                + "upstream/<default> lacks Signed-off-by, naming those commits. When one is already open for "
                + "that branch it is linked and recorded, and no second is opened. On success the pull request's "
                + "URL, number and state are stored and `repo.pullRequestOpened` is appended - after GitHub "
                + "opened it, not before. GitHub's refusal is answered with its own sentence and what the token "
                + "needs (409), or 502 when GitHub could not be reached.");

        app.MapPost("/api/github/fork", ForkUpstreamAsync)
            .WithTags("Repos")
            .HumansOnly()
            .WithSummary("Fork it for me: fork an upstream into the GH_TOKEN account, or an organisation")
            .WithDescription(
                "Body `{ \"upstreamUrl\": \"https://github.com/project/Widget\", \"organisation\": null }`. "
                + "Runs `gh repo fork <upstream> --clone=false` into the account the instance's GH_TOKEN belongs "
                + "to, or into `organisation` with `--org` when one is given. An existing fork of it is used as "
                + "it is. Answers `{ forkUrl, forkOwner, upstreamUrl }`: the fork becomes the repository's URL "
                + "(origin) and the upstream its upstream, at team creation or in Team settings. A token that "
                + "cannot fork is answered with GitHub's sentence and what the token needs (409); the person "
                + "can fork on GitHub and give both URLs instead. 502 when GitHub could not be reached.");
    }

    /// <summary>What a person sends from the Open pull request dialog.</summary>
    internal sealed record OpenPullRequest(
        [property: Description("The pull request's title, as the person edited it.")] string? Title,
        [property: Description("The pull request's body, as the person edited it.")] string? Body);

    /// <summary>What Fork it for me sends.</summary>
    internal sealed record ForkUpstream(
        [property: Description("The upstream repository's github.com URL.")] string? UpstreamUrl,
        [property: Description("An organisation to fork into (`--org`). Null or blank: the GH_TOKEN account.")] string? Organisation);

    /// <summary>The longest title a person may send. GitHub's own limit is 256.</summary>
    public const int PullRequestTitleLimit = 256;

    private static async Task<IResult> PullRequestDraftAsync(
        [Description(Describe.Team)] string team,
        [Description("The repository name (derived from the URL in the team's repos list)")] string repo,
        TeamRegistry teams,
        IBacklogStore backlog,
        IMessageLog messages,
        CancellationToken ct)
    {
        if (teams.ExistingName(team) is not { } stored)
        {
            return Results.NotFound(new { error = $"No team '{team}'." });
        }

        var draftUrl = teams.ReposFor(stored).FirstOrDefault(u => RepoUrls.DeriveName(u).Equals(repo, StringComparison.OrdinalIgnoreCase));
        if (draftUrl is null)
        {
            return Results.NotFound(new { error = $"No repo '{repo}' on team '{team}'." });
        }

        if (LocalRepos.IsLocal(draftUrl))
        {
            return Results.Conflict(new { error = LocalRepoSentences.NoPullRequest(RepoUrls.DeriveName(draftUrl)) });
        }

        return Results.Ok(await PullRequestDraft.ComposeAsync(teams, backlog, messages, stored, ct));
    }

    private static async Task<IResult> OpenPullRequestAsync(
        [Description(Describe.Team)] string team,
        [Description("The repository name (derived from the URL in the team's repos list)")] string repo,
        OpenPullRequest request,
        HttpContext httpContext,
        TeamRegistry teams,
        GitRunner gitRunner,
        TeamPaths paths,
        ITenantLog log,
        IMessageLog messages,
        IGitHubContributor gitHub,
        PullRequestStateReader reader,
        BacklogLandedCache landedCache,
        CancellationToken ct)
    {
        if (teams.ExistingName(team) is not { } stored)
        {
            return Results.NotFound(new { error = $"No team '{team}'." });
        }

        var email = httpContext.User.FindFirstValue(ClaimTypes.Email);
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        var repoUrl = teams.ReposFor(stored).FirstOrDefault(u => RepoUrls.DeriveName(u).Equals(repo, StringComparison.OrdinalIgnoreCase));
        if (repoUrl is null)
        {
            return Results.NotFound(new { error = $"No repo '{repo}' on team '{team}'." });
        }

        var name = RepoUrls.DeriveName(repoUrl);
        var clonePath = Path.Combine(paths.ReposFor(stored), name, "main");
        var teamBranch = $"team/{stored}";

        async Task<IResult> RefuseAsync(string error, int status = StatusCodes.Status409Conflict, object? extra = null)
        {
            await log.WriteAsync(userId, email, TenantActions.RepoPullRequestOpen, $"{stored}/{name}", null,
                JsonSerializer.Serialize(new { refused = true, reason = error, extra }), ct);
            return Results.Json(new { error, detail = extra }, statusCode: status);
        }

        if (LocalRepos.IsLocal(repoUrl))
        {
            return await RefuseAsync(LocalRepoSentences.NoPullRequest(name));
        }

        var contributor = teams.ContributorFor(stored, name);
        if (!contributor.ContributorMode)
        {
            return await RefuseAsync(
                $"{name} is owned, not contributed to: it has no upstream, so its button is Merge to main. Nothing was sent.");
        }

        if (!Directory.Exists(clonePath))
        {
            return Results.NotFound(new { error = "Repository not cloned yet." });
        }

        // Never guess `main` for --base.
        if (teams.DefaultBranchFor(stored, name).Branch is not { } branch)
        {
            return await BranchNotKnownAsync(log, userId, email, TenantActions.RepoPullRequestOpen, stored, name, ct);
        }

        if (contributor.ForkOwner is not { } forkOwner)
        {
            return await RefuseAsync(
                $"The fork's owner is not known for {name}, so the pull request's head cannot be named. "
                + "Set it in Team settings, then open the pull request again.");
        }

        var title = request.Title?.Trim() ?? string.Empty;
        var body = request.Body?.Trim() ?? string.Empty;
        if (title.Length == 0)
        {
            return Results.BadRequest(new { error = "A pull request needs a title." });
        }

        if (title.Length > PullRequestTitleLimit)
        {
            return Results.BadRequest(new { error = $"The title is longer than {PullRequestTitleLimit} characters." });
        }

        var (fetch, fetchedRemote) = await FetchRemotesAsync(gitRunner, contributor, clonePath, ct);
        if (fetch.ExitCode != 0)
        {
            return await RefuseAsync(
                $"{fetchedRemote} could not be fetched, so what the pull request would carry is not known. Nothing was sent.",
                StatusCodes.Status502BadGateway, BoundLines(fetch.Stderr));
        }

        var forkBranch = $"refs/remotes/{ContributorRemotes.Origin}/{teamBranch}";
        var upstreamBranch = $"refs/remotes/{ContributorRemotes.Upstream}/{branch}";
        if ((await gitRunner.RunGitAsync(clonePath, ["rev-parse", "--verify", "--quiet", forkBranch], ct)).ExitCode != 0)
        {
            return await RefuseAsync($"{teamBranch} is not on the fork yet, so there is nothing to open a pull request from. Push it first.");
        }

        if ((await gitRunner.RunGitAsync(clonePath, ["rev-parse", "--verify", "--quiet", upstreamBranch], ct)).ExitCode != 0)
        {
            return await RefuseAsync($"upstream/{branch} does not exist, so the pull request has no base. Nothing was sent.");
        }

        // ONE OPEN PULL REQUEST PER BRANCH. GitHub is asked rather than the record trusted: one opened
        // on GitHub by hand is linked the same way.
        var open = await gitHub.FindOpenAsync(contributor.UpstreamUrl!, forkOwner, teamBranch, ct);
        if (!open.Answered)
        {
            return await RefuseAsync(
                $"GitHub could not be asked whether a pull request is already open for {teamBranch}, so none was opened. {open.Refusal}",
                open.Unreachable ? StatusCodes.Status502BadGateway : StatusCodes.Status409Conflict);
        }

        if (open.Value is { } existing)
        {
            var linked = await RecordAsync(teams, reader, landedCache, stored, name, existing, ct);
            await log.WriteAsync(userId, email, TenantActions.RepoPullRequestOpen, $"{stored}/{name}", null,
                JsonSerializer.Serialize(new { success = true, linked = true, url = linked.Url, number = linked.Number }), ct);
            return await ActionResultAsync(
                httpContext, gitRunner, name, clonePath, stored,
                $"A pull request is already open for {teamBranch}: #{existing.Number}, {existing.Url}. No second one was opened.",
                success: true, originReachable: true, originUnreachableReason: null, ct);
        }

        // DCO. Every commit the pull request would carry - the fork's team/{id} since
        // upstream/<default> - must carry Signed-off-by. The hook adds it to new commits; this
        // catches any made before the setting was on, or without the hook.
        if (contributor.DcoSignOff)
        {
            var unsigned = await UnsignedCommitsAsync(gitRunner, clonePath, upstreamBranch, forkBranch, ct);
            if (unsigned is null)
            {
                return await RefuseAsync(
                    $"The commits on {teamBranch} could not be read to check their sign-off, so nothing was sent.");
            }

            if (unsigned.Count > 0)
            {
                return await RefuseAsync(
                    $"Sign off commits (DCO) is on, and {unsigned.Count} commit{(unsigned.Count == 1 ? "" : "s")} on "
                    + $"{teamBranch} since upstream/{branch} {(unsigned.Count == 1 ? "has" : "have")} no Signed-off-by: "
                    + string.Join("; ", unsigned) + ". Nothing was sent.",
                    extra: unsigned);
            }
        }

        var created = await gitHub.CreateAsync(contributor.UpstreamUrl!, forkOwner, teamBranch, branch, title, body, ct);
        if (!created.Answered || created.Value is null)
        {
            return await RefuseAsync(
                $"GitHub did not open the pull request. {created.Refusal}",
                created.Unreachable ? StatusCodes.Status502BadGateway : StatusCodes.Status409Conflict);
        }

        var pull = await RecordAsync(teams, reader, landedCache, stored, name, created.Value, ct);

        // AFTER GitHub opened it, never before: the row is a receipt for something that happened.
        await messages.AppendAsync(
            new NewMessage(
                MessageTypes.RepoPullRequestOpened,
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    [PayloadFields.Team] = stored,
                    [PayloadFields.Repo] = name,
                    [PayloadFields.Branch] = teamBranch,
                    [PayloadFields.Url] = pull.Url,
                    [PayloadFields.Number] = pull.Number,
                }),
                PrincipalClaims.From(httpContext.User)?.Id ?? "console",
                null),
            ct);

        await log.WriteAsync(userId, email, TenantActions.RepoPullRequestOpen, $"{stored}/{name}", null,
            JsonSerializer.Serialize(new { success = true, url = pull.Url, number = pull.Number }), ct);

        return await ActionResultAsync(
            httpContext, gitRunner, name, clonePath, stored,
            $"Opened pull request #{pull.Number} upstream from {teamBranch}: {pull.Url}.",
            success: true, originReachable: true, originUnreachableReason: null, ct);
    }

    /// <summary>Stores what GitHub just said about a pull request, read now.</summary>
    private static async Task<RepoPullRequest> RecordAsync(
        TeamRegistry teams, PullRequestStateReader reader, BacklogLandedCache landedCache,
        string stored, string name, PullRequestInfo info, CancellationToken ct)
    {
        var pull = new RepoPullRequest(info.Url, info.Number, info.State, DateTimeOffset.UtcNow);
        await teams.RecordPullRequestAsync(stored, name, pull, ct);
        reader.Saw(pull);
        landedCache.Clear();
        return pull;
    }

    /// <summary>
    /// "shortsha subject" for every commit in <c>base..head</c> without a Signed-off-by trailer, or
    /// null when git could not list them.
    /// </summary>
    internal static async Task<IReadOnlyList<string>?> UnsignedCommitsAsync(
        GitRunner gitRunner, string clonePath, string baseRef, string headRef, CancellationToken ct)
    {
        var listed = await gitRunner.RunGitAsync(
            clonePath,
            ["log", "--format=%h%x1f%s%x1f%(trailers:key=Signed-off-by,valueonly,separator=%x2C)%x1e", $"{baseRef}..{headRef}"],
            ct);
        if (listed.ExitCode != 0) return null;

        var unsigned = new List<string>();
        foreach (var record in listed.Stdout.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Trim('\n', '\r').Split('\x1f');
            if (fields.Length < 2) continue;
            var signed = fields.Length > 2 && fields[2].Trim().Length > 0;
            if (!signed) unsigned.Add($"{fields[0]} {fields[1]}");
        }

        return unsigned;
    }

    private static async Task<IResult> ForkUpstreamAsync(
        ForkUpstream request, HttpContext httpContext, IGitHubContributor gitHub, ITenantLog log, CancellationToken ct)
    {
        var email = httpContext.User.FindFirstValue(ClaimTypes.Email);
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        var upstream = request.UpstreamUrl?.Trim() ?? string.Empty;
        if (GitHubRepository.From(upstream) is null)
        {
            return Results.BadRequest(new
            {
                error = $"'{upstream}' is not a github.com repository URL (https://github.com/<owner>/<repository>), so it cannot be forked here.",
            });
        }

        var organisation = string.IsNullOrWhiteSpace(request.Organisation) ? null : request.Organisation.Trim();
        if (organisation is not null && ContributorSettings.OwnerOf($"https://github.com/{organisation}/x") != organisation)
        {
            return Results.BadRequest(new { error = $"'{organisation}' is not a GitHub organisation name." });
        }

        var fork = await gitHub.ForkAsync(upstream, organisation, ct);
        if (!fork.Answered || fork.Value is null)
        {
            var error = $"The fork was not made. {fork.Refusal}";
            await log.WriteAsync(userId, email, TenantActions.RepoFork, GitOutputRedaction.RedactUserInfo(upstream), null,
                JsonSerializer.Serialize(new { success = false, organisation, reason = error }), ct);
            return Results.Json(new { error },
                statusCode: fork.Unreachable ? StatusCodes.Status502BadGateway : StatusCodes.Status409Conflict);
        }

        await log.WriteAsync(userId, email, TenantActions.RepoFork, GitOutputRedaction.RedactUserInfo(upstream), null,
            JsonSerializer.Serialize(new { success = true, organisation, fork = fork.Value.Url, owner = fork.Value.Owner }), ct);

        return Results.Ok(new { forkUrl = fork.Value.Url, forkOwner = fork.Value.Owner, upstreamUrl = upstream });
    }
}

/// <summary>
/// WHAT OPEN PULL REQUEST STARTS WITH. Title: the backlog item this team was last given,
/// else its latest completed workflow's subject. Body: that workflow's <c>workflow_complete</c>
/// delivery text, then the item's citation. A starting point only - the person edits both.
/// </summary>
public static class PullRequestDraft
{
    public sealed record Draft(string Title, string Body, string? Citation, long? Workflow);

    public static async Task<Draft> ComposeAsync(
        TeamRegistry teams, IBacklogStore backlog, IMessageLog messages, string stored, CancellationToken ct)
    {
        var floor = teams.FloorFor(stored);
        var dispatch = (await backlog.LatestDispatchesAsync(ct))
            .Where(d => string.Equals(d.TeamId, stored, StringComparison.OrdinalIgnoreCase) && d.Correlation > floor)
            .MaxBy(d => d.Correlation);

        BacklogItem? item = dispatch is null ? null : await backlog.GetAsync(dispatch.Item, ct);
        long? workflow = dispatch?.Correlation;
        string? subject = null;

        if (workflow is null)
        {
            // No item: the newest workflow the Manager declared complete.
            var workflows = await messages.WorkflowsForTeamAsync(stored, floor, ct);
            foreach (var candidate in workflows.Workflows
                .Where(w => w.Correlation is not null)
                .OrderByDescending(w => w.Correlation))
            {
                if (await DeliveredAsync(messages, candidate.Correlation!.Value, ct) is not null)
                {
                    workflow = candidate.Correlation;
                    subject = candidate.Subject;
                    break;
                }
            }
        }

        var delivered = workflow is null ? null : await DeliveredAsync(messages, workflow.Value, ct);
        subject ??= workflow is null ? null : await SubjectAsync(messages, workflow.Value, ct);

        var citation = item is null ? null : PlatformBacklogId.Format(item.Id);
        var title = item?.Title ?? subject ?? string.Empty;
        var body = string.Join("\n\n", new[] { delivered?.Trim(), citation is null ? null : $"Backlog {citation}" }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        return new Draft(title.Trim(), body, citation, workflow);
    }

    /// <summary>The Manager's latest <c>workflow_complete</c> delivery text in a workflow, or null.</summary>
    private static async Task<string?> DeliveredAsync(IMessageLog messages, long correlation, CancellationToken ct) =>
        (await messages.ReadCorrelationAsync(correlation, ct))
            .Where(m => m.Type == MessageTypes.WorkflowCompleted)
            .OrderByDescending(m => m.Seq)
            .Select(m => Field(m.Payload, PayloadFields.Delivered))
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

    /// <summary>The subject of the instruction that began a workflow, or null.</summary>
    private static async Task<string?> SubjectAsync(IMessageLog messages, long correlation, CancellationToken ct) =>
        (await messages.ReadCorrelationAsync(correlation, ct))
            .Where(m => m.Seq == correlation)
            .Select(m => Field(m.Payload, "subject"))
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

    private static string? Field(string payload, string name)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
