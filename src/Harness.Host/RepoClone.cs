using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>What became of one repository the platform was asked to make ready.</summary>
public enum RepoCloneResult
{
    /// <summary>The platform cloned it just now.</summary>
    Cloned,

    /// <summary>A directory was already at the target path, so nothing was touched.</summary>
    AlreadyThere,

    /// <summary>git was run and refused. <see cref="RepoCloneOutcome.Error"/> says what it said.</summary>
    Failed,

    /// <summary>Cloning is switched off for this instance - see <see cref="RepoClone"/>.</summary>
    NotAttempted,
}

/// <summary>One repository, its target path, and what happened to it. <paramref name="DefaultBranch"/>
/// is what origin's HEAD named right after a clone, null when not known or not cloned.</summary>
public sealed record RepoCloneOutcome(
    string Url, string Path, RepoCloneResult Result, string? Error = null, string? DefaultBranch = null);

/// <summary>Completes repository setup before its outcomes can be handed to a Manager.</summary>
public interface IRepoClone
{
    Task<IReadOnlyList<RepoCloneOutcome>> EnsureAllAsync(
        IReadOnlyList<(string Url, string Path)> repos, CancellationToken ct);
}

/// <summary>
/// THE PLATFORM MAKES A TEAM'S MAIN CLONE. THE MANAGER DOES NOT, AND NO MEMBER EVER DID.
///
/// On team creation the Host wakes the Manager with *"set up the team's repositories using the
/// worktrees skill"*, and that skill has **no arm for a clone that is not there yet**: its manager
/// section is *"bring the main clone current, and nothing else"*, written entirely in
/// `git -C "&lt;path&gt;/main" …` commands that all presuppose the directory, and its member section
/// says a member does not clone. Left to the agents, a clone happens only when one improvises the
/// command; one that reads the manager-only sentence as binding on itself blocks, and the Manager
/// blocks behind it. A gap that usually works is worse than one that never does, because nothing
/// reports it.
///
/// **SO IT IS THE HOST'S, and that is a placement rather than a convenience.** A clone is
/// deterministic: one URL, one path, no judgement. The Host already runs `fetch`, `merge --ff-only`,
/// `push` and `worktree remove` itself through <see cref="GitRunner"/>, every one of them a harder
/// call than this. The same line the repo ladder draws - git-level facts are the
/// platform's - and an agent asked to do it spends tokens deciding something that has one answer.
/// </summary>
/// <param name="git">The typed git operations. Nothing else here runs a process.</param>
/// <param name="enabled">
/// Whether to actually clone. **ABSENT MEANS THE PRODUCT'S BEHAVIOUR, and only a test that
/// constructs this itself asks for anything else** - there is no setting for it in the shipped
/// binary, the rule the stores' durability and hash-iteration parameters follow too. A suite that
/// creates teams carrying unreachable example URLs by the dozen gains nothing from a real clone
/// attempt for each, and pays a network timeout apiece. What it must NOT do
/// is fake a success: a disabled instance reports <see cref="RepoCloneResult.NotAttempted"/> and the
/// Manager is told the paths WITHOUT being told they hold anything.
/// </param>
/// <param name="localRepos">Where a <c>local:&lt;name&gt;</c> repository is. Without one, such a
/// repository cannot be cloned and says so.</param>
public sealed class RepoClone(GitRunner git, bool enabled = true, LocalRepos? localRepos = null) : IRepoClone
{
    /// <summary>Every repository in the list, in order. Never throws: a clone that fails is an
    /// OUTCOME, because a team whose creation 500s over an unreachable remote is worse than a team
    /// that exists and says its repository is not there.</summary>
    public async Task<IReadOnlyList<RepoCloneOutcome>> EnsureAllAsync(
        IReadOnlyList<(string Url, string Path)> repos, CancellationToken ct)
    {
        var outcomes = new List<RepoCloneOutcome>(repos.Count);

        foreach (var (url, path) in repos)
        {
            outcomes.Add(await EnsureAsync(url, path, ct));
        }

        return outcomes;
    }

    /// <summary>
    /// One repository.
    ///
    /// **AN EXISTING DIRECTORY IS LEFT ALONE, whatever is in it.** This is the never-adopt rule from
    /// the other side: a path that is there may be a healthy clone, a half-finished one, or somebody's
    /// unrelated work under a team root a person typed. Re-cloning over it destroys unpushed commits,
    /// which is the one unrecoverable thing in this whole file.
    /// </summary>
    public async Task<RepoCloneOutcome> EnsureAsync(string url, string path, CancellationToken ct)
    {
        if (!enabled) return new RepoCloneOutcome(url, path, RepoCloneResult.NotAttempted);

        if (Directory.Exists(path)) return new RepoCloneOutcome(url, path, RepoCloneResult.AlreadyThere);

        // The PARENT, because `git clone` makes the leaf and needs somewhere to make it in - and
        // because `GitRunner` runs every operation with its path as the WORKING DIRECTORY, and
        // `Process.Start` throws on one that does not exist.
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf))
        {
            return new RepoCloneOutcome(
                url, path, RepoCloneResult.Failed, $"'{path}' is not a path a clone can be made at.");
        }

        try
        {
            Directory.CreateDirectory(parent);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new RepoCloneOutcome(url, path, RepoCloneResult.Failed, exception.Message);
        }

        // A LOCAL REPOSITORY IS CLONED FROM ITS FOLDER, found by name and never by a path the
        // reference carries; the clone's origin is that folder.
        var source = url;
        if (LocalRepos.IsLocal(url))
        {
            if (localRepos?.PathForReference(url) is not { } bare)
            {
                return new RepoCloneOutcome(
                    url, path, RepoCloneResult.Failed, $"'{url}' names no local repository on this instance.");
            }

            source = bare;
        }

        var run = await git.CloneAsync(parent, source, leaf, ct, noLocal: !ReferenceEquals(source, url));

        if (run.ExitCode != 0)
        {
            return new RepoCloneOutcome(url, path, RepoCloneResult.Failed, FirstLine(run.Stderr, run.Stdout));
        }

        // On clone, read the branch origin's HEAD names. Null is not known.
        return new RepoCloneOutcome(
            url, path, RepoCloneResult.Cloned, DefaultBranch: await git.ReadOriginHeadBranchAsync(path, ct));
    }

    /// <summary>git says what went wrong on its FIRST line and then explains at length. This goes
    /// into an instruction an agent is billed for reading, so it carries the sentence and not the
    /// essay. Falls through to stdout because a shell-not-found is reported there by some shells.</summary>
    private static string FirstLine(params string[] streams)
    {
        foreach (var stream in streams)
        {
            var line = stream
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(line)) return line;
        }

        return "git failed and said nothing.";
    }
}

/// <summary>
/// WHAT THE MANAGER IS TOLD ABOUT ITS REPOSITORIES - a pure function over outcomes, which is what
/// lets every shape of it be tested without a repository, a network or a process.
///
/// THESE MESSAGES STATE A FACT: the clone is there, or it is not and a person is needed. They never
/// ask the Manager to set up its repositories, which is work the worktrees skill forbids everybody
/// from doing. An instruction that orders an agent to do something no rule allows sends it round in
/// an expensive deadlock.
/// </summary>
public static class RepoSetupMessage
{
    /// <summary>The instruction for a team whose repository list was just cleared.</summary>
    public const string Cleared =
        "The team's repository list was cleared. Do not clone any repositories.";

    public static string For(IReadOnlyList<RepoCloneOutcome> outcomes)
    {
        if (outcomes.Count == 0) return Cleared;

        var failed = outcomes.Where(o => o.Result is RepoCloneResult.Failed).ToList();
        var lines = string.Join("\n", outcomes.Select(Line));

        // FAILURE WINS THE WHOLE MESSAGE, and it says a person is needed in its first sentence.
        // A manager that reads "two ready, one failed" retries the one that failed - with the same
        // credentials, against the same unreachable remote, at the same cost, forever.
        if (failed.Count > 0)
        {
            return
                "The platform could not make one of this team's repositories ready, and nothing you "
                + "can run will fix it - it needs a person.\n\n"
                + lines
                + "\n\nSay so with the `blocked` tool, naming the repository and the reason above, "
                + "and stop: once the cause is fixed, a person makes the clone with Fetch in the "
                + "team's Git dialog and nudges the workflow. DO NOT clone it yourself: the main "
                + "clone is the platform's, and a hand-made one in its place is the state this "
                + "message exists to prevent.";
        }

        // NOT ATTEMPTED IS NOT READY. The paths are named because they are still true; nothing
        // claims anything is at them.
        if (outcomes.All(o => o.Result is RepoCloneResult.NotAttempted))
        {
            return "This team's repositories:\n\n" + lines;
        }

        return
            "Your team's repositories are ready - the platform cloned them, and you do not clone "
            + "anything yourself.\n\n"
            + lines
            + "\n\nHire a member when there is work. A member cuts its own worktree from that clone; "
            + "see the `worktrees` skill. What a manager does to the clone itself is bring it "
            + "current, and nothing else.";
    }

    /// <summary>The heading the Manager's repository section sits under.</summary>
    public const string PromptHeading = "## Your team's repositories";

    /// <summary>
    /// WHAT THE READY NOTICE CARRIED, IN THE MANAGER'S PROMPT INSTEAD. A team whose repositories all
    /// cloned is not told so by an instruction: that notice woke the Manager into a paid run with
    /// nothing to do. The clone paths and the rule that a member cuts its own worktree reach it here,
    /// on its first real run and every one after. A clone that FAILED still sends <see cref="For"/>'s
    /// failure notice, because that one needs a person. Null for a team with no repositories.
    /// </summary>
    public static string? PromptSection(IReadOnlyList<(string Url, string Path)> clones)
    {
        if (clones.Count == 0) return null;

        return
            PromptHeading + "\n\n"
            + "The platform makes the main clone of each repository, and you do not clone anything "
            + "yourself. If a clone could not be made, you are told so in an instruction.\n\n"
            + string.Join("\n", clones.Select(c => $"  {GitOutputRedaction.Redact(c.Url)} -> {c.Path}"))
            + "\n\nHire a member when there is work. A member cuts its own worktree from that clone; "
            + "see the `worktrees` skill. What a manager does to the clone itself is bring it "
            + "current, and nothing else.";
    }

    /// <summary>
    /// THE ONE PLACE A REPOSITORY URL IS COMPOSED INTO A ROW, AND THEREFORE THE ONE PLACE THE
    /// CREDENTIAL IN IT IS TAKEN OUT.
    ///
    /// <para>
    /// <b>THIS LEAKED, AT CREATION, FOR EVERY TEAM WITH AN AUTHENTICATED REMOTE.</b>
    /// <c>RepoUrls.Validate</c> accepts userinfo - <c>https://x-access-token:&lt;token&gt;@host/repo.git</c>
    /// is a legal remote and is exactly how the platform's own clone authenticates - and this method
    /// interpolated <c>outcome.Url</c> verbatim. <see cref="RepoSetupMessage.For"/>'s result is
    /// appended to the append-only message log by <c>Teams.WakeManagerForReposAsync</c> and by
    /// <c>BacklogEndpoints</c>' create-and-dispatch, so the token went onto a row nothing can delete,
    /// before any push, independent of any push. <c>DiagnosticRedaction</c> guards the DIAGNOSTICS
    /// store; it never saw this one.
    /// </para>
    ///
    /// <para>
    /// <b>BOTH FIELDS, NOT JUST THE URL.</b> <c>Error</c> is git's own first line, and git quotes the
    /// remote it could not reach - userinfo and all - into <c>fatal: unable to access '…'</c>.
    /// </para>
    ///
    /// <para>
    /// <b>AT THE COMPOSITION, NOT AT THE OUTCOME.</b> <see cref="RepoCloneOutcome"/> keeps the real
    /// URL because <c>EnsureAsync</c> has to hand it to git; what must not carry it is the ROW. Same
    /// placement as <c>TeamPublisher.RowFor</c> and the same rule AGENTS.md records.
    /// </para>
    /// </summary>
    private static string Line(RepoCloneOutcome outcome)
    {
        var url = GitOutputRedaction.Redact(outcome.Url);
        var error = GitOutputRedaction.Redact(outcome.Error);

        return outcome.Result switch
        {
            RepoCloneResult.Cloned => $"  {url} -> {outcome.Path}  (cloned)",
            RepoCloneResult.AlreadyThere => $"  {url} -> {outcome.Path}  (already there)",
            RepoCloneResult.NotAttempted => $"  {url} -> {outcome.Path}",
            _ => $"  {url} -> {outcome.Path}  COULD NOT CLONE: {error}",
        };
    }
}

/// <summary>
/// ANSWERS THE FAILURE NOTICE WHEN THE CLONE IS MADE LATER. <see cref="RepoSetupMessage"/>'s
/// failure roots a workflow addressed to the Manager, and the Manager reports the block there and
/// stops, as it is told to. When a person then makes the clone from the Git dialog (Fetch or Bring
/// current), nothing else reaches that workflow, and the team would read BLOCKED long after its
/// clone existed and its real work had finished. This finds every still-open
/// workflow rooted by a notice naming that clone path (<see cref="PayloadFields.RepoNotReady"/>)
/// and tells the Manager inside it, so the block clears the way it was raised.
/// </summary>
public static class RepoReadyNotice
{
    private const int Page = 500;

    public static async Task<int> SendAsync(
        IMessageLog log, string team, string repo, string clonePath, CancellationToken ct)
    {
        var manager = MessageTypes.InstructionFor(new ContainerId(team, TeamRegistry.DefaultManagerName));

        // Rare path (a clone made by hand after a failure), so reading the Manager's addressed
        // instructions from the start is affordable; they are one type, filtered in the query.
        var notices = new List<long>();
        long after = 0;
        while (true)
        {
            var page = await log.ReadAfterAsync(after, [manager], Page, ct);
            notices.AddRange(page.Where(m => m.Seq == m.CorrelationId && Names(m, clonePath)).Select(m => m.Seq));
            if (page.Count < Page) break;
            after = page[^1].Seq;
        }

        if (notices.Count == 0) return 0;

        var open = await log.OpenWorkflowsAmongAsync(notices, ct);
        var instruction =
            $"{repo} is ready now: a person made its clone at {clonePath} from the Git dialog. The "
            + "block this workflow reported about it is resolved. Check the repository with the `repo` "
            + "tool; if nothing else under this workflow is outstanding, declare it complete with "
            + "`workflow_complete`, otherwise carry on with what it was for.";

        foreach (var correlation in open.Order())
        {
            await log.AppendAsync(
                new NewMessage(manager, JsonSerializer.Serialize(new { instruction }), "host", correlation), ct);
        }

        return open.Count;
    }

    private static bool Names(Message notice, string clonePath)
    {
        try
        {
            using var payload = JsonDocument.Parse(notice.Payload);
            return payload.RootElement.TryGetProperty(PayloadFields.RepoNotReady, out var paths)
                && paths.ValueKind == JsonValueKind.Array
                && paths.EnumerateArray().Any(p => string.Equals(p.GetString(), clonePath, StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
