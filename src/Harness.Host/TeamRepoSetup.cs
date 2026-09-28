using System.Text.RegularExpressions;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// What a caller may choose for a repository URL that <c>git ls-remote</c> could not read. The codes
/// are the contract with the web app (<c>repoChoices</c> on create and attach).
/// </summary>
public static class RepoChoices
{
    /// <summary>A person's only: <c>gh repo create &lt;owner&gt;/&lt;name&gt; --private</c>, then continue.</summary>
    public const string CreateOnGitHub = "create-on-github";

    /// <summary>Anyone: drop the URL and attach the team's local repository instead.</summary>
    public const string UseLocal = "use-local";

    /// <summary>A person's only, and only for a network failure: attach the URL unchecked.</summary>
    public const string AttachAnyway = "attach-anyway";

    public static readonly IReadOnlyList<string> All = [CreateOnGitHub, UseLocal, AttachAnyway];
}

/// <summary>Why a URL failed its check: gone or unreadable, or the network did not answer.</summary>
public static class RepoCheckFailures
{
    public const string NotFound = "not-found";
    public const string Unreachable = "unreachable";
}

/// <summary>One URL <c>git ls-remote</c> could not read, and git's own reason.</summary>
public sealed record RepoCheckFailure(string Url, string Failure, string Reason);

/// <summary>
/// ASKS WHETHER A REPOSITORY URL CAN BE READ, before a team is created with it or it is attached:
/// <c>git ls-remote --exit-code &lt;url&gt;</c> through <see cref="GitRunner"/>, so a github.com URL is
/// handed <c>GH_TOKEN</c> by the same rule as Fetch (<c>LsRemoteTokenTests</c>). Exit 2 is an empty
/// repository - it exists. Anything else is refused as not found (or not readable), unless git's
/// words say the network failed.
/// </summary>
public interface IRemoteRepoCheck
{
    /// <summary>Null when <paramref name="url"/> can be read; otherwise why not.</summary>
    Task<RepoCheckFailure?> CheckAsync(string url, CancellationToken ct);
}

/// <inheritdoc cref="IRemoteRepoCheck"/>
public sealed partial class RemoteRepoCheck(GitRunner git) : IRemoteRepoCheck
{
    public async Task<RepoCheckFailure?> CheckAsync(string url, CancellationToken ct)
    {
        GitRunner.GitInvocation result;
        try
        {
            result = await git.LsRemoteAsync(Path.GetTempPath(), url, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(url, RepoCheckFailures.Unreachable, "git ls-remote did not answer in time.");
        }

        if (result.ExitCode is 0 or 2) return null;

        var reason = Reason(result.Stderr, result.Stdout, result.ExitCode);
        return new(url, Network().IsMatch(reason) ? RepoCheckFailures.Unreachable : RepoCheckFailures.NotFound, reason);
    }

    /// <summary>git's words, a few lines at most and never a token.</summary>
    private static string Reason(string stderr, string stdout, int exitCode)
    {
        var text = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        var bounded = GhContributor.Bound(text);
        return bounded.Length > 0 ? bounded : $"git ls-remote exited {exitCode}.";
    }

    [GeneratedRegex(
        @"could not resolve|failed to connect|connection refused|connection timed out|timed out|network is unreachable|"
        + @"no route to host|temporary failure in name resolution|connection reset|SSL|TLS|gnutls|"
        + @"returned error: 5\d\d|did not answer",
        RegexOptions.IgnoreCase)]
    private static partial Regex Network();
}

/// <summary>A team's local repository: its name, <c>local:&lt;name&gt;</c>, and whether it was made now.</summary>
public sealed record TeamLocalRepository(string Name, string Reference, bool Created);

/// <summary>A refusal the route answers as it is: its status and its JSON body.</summary>
public sealed class RepoSetupRefusedException(int status, object body, string message) : Exception(message)
{
    public int Status { get; } = status;

    public object Body { get; } = body;
}

/// <summary>
/// A new team's checked repositories (<see cref="TeamRepoSetup.PlanNewTeamAsync"/>), and what
/// creating it made of them. <see cref="AddRepoAsync"/> runs inside the create, after every check on
/// the team and before any write, so a repository that cannot be made refuses the create; a create
/// that fails after it calls <see cref="ForgetUnlessCreatedAsync"/> so nothing is left behind.
/// </summary>
public sealed class NewTeamRepos(TeamRepoSetup setup, RepoPlan plan, bool wantsLocal)
{
    /// <summary>The URLs the team is created with.</summary>
    public IReadOnlyList<string> Repos => plan.Kept;

    /// <summary>The team's local repository, once made or reused; null when it has none.</summary>
    public TeamLocalRepository? LocalRepository { get; private set; }

    /// <summary>The github.com repositories created for it.</summary>
    public IReadOnlyList<string> CreatedOnGitHub { get; private set; } = [];

    public async Task<string?> AddRepoAsync(string team, CancellationToken ct)
    {
        CreatedOnGitHub = await setup.CreateOnGitHubAsync(plan, ct);
        if (!wantsLocal) return null;

        LocalRepository = await setup.EnsureLocalAsync(team, ct);
        return LocalRepository.Reference;
    }

    /// <summary>The <c>local-repo.created</c> tenant row, when the create made one.</summary>
    public async Task LogAsync(TenantLogging audit, HttpContext context, string team, CancellationToken ct)
    {
        if (LocalRepository is not { Created: true } made) return;
        await audit.WriteAsync(
            context, TenantActions.LocalRepoCreated, made.Name, made.Name,
            new { reference = made.Reference, defaultBranch = LocalRepos.InitialBranch, team }, ct);
    }

    /// <summary>A local repository made for a create that then failed is not left behind.</summary>
    public Task ForgetUnlessCreatedAsync(bool teamCreated) =>
        teamCreated ? Task.CompletedTask : setup.ForgetAsync(LocalRepository, CancellationToken.None);
}

/// <summary>What a checked list of URLs becomes: the URLs kept, whether the local repository is
/// wanted in place of some, and the github.com repositories to create first.</summary>
public sealed record RepoPlan(
    IReadOnlyList<string> Kept, bool UseLocal, IReadOnlyList<(string Url, GitHubRepository Repository)> CreateOnGitHub);

/// <summary>
/// FORGIVING TEAM REPOSITORIES (B001F). A team with no repository gets a local one named after it; a
/// URL that cannot be read is caught before anything is created, with the choices the caller may
/// take. Shared by create (<c>POST /api/teams</c>, and so <c>team_create</c>), attach
/// (<c>PUT /api/teams/{team}/repos</c>) and "Create a local repository"
/// (<c>POST /api/teams/{team}/local-repo</c>).
///
/// <para>
/// <b>AN AGENT NEVER CREATES A GITHUB REPOSITORY OR ATTACHES ANYWAY.</b> Those are a person's
/// choices: an agent's refusal names them and offers it only <see cref="RepoChoices.UseLocal"/>,
/// and an agent that sends either is refused before anything is asked.
/// </para>
/// </summary>
public sealed class TeamRepoSetup(
    LocalRepos localRepos, Func<TeamRegistry> teams, IRemoteRepoCheck check, IGitHubContributor gitHub)
{
    /// <summary>How many suffixes are tried before the name is given up on.</summary>
    private const int MaximumSuffix = 100;

    /// <summary>
    /// The team's local repository: <paramref name="team"/> itself when no repository has that name
    /// (in any case) or no team uses the one that has it; otherwise <c>-2</c>, <c>-3</c>, ... by the
    /// same rule. Made when it does not exist. Throws <see cref="ArgumentException"/> naming the
    /// reason when it cannot be made.
    /// </summary>
    public async Task<TeamLocalRepository> EnsureLocalAsync(string team, CancellationToken ct)
    {
        for (var n = 1; n <= MaximumSuffix; n++)
        {
            var candidate = n == 1 ? team : $"{team}-{n}";
            if (!LocalRepos.IsLegalName(candidate))
            {
                throw new ArgumentException(
                    $"No local repository could be made for '{team}': {LocalRepos.IllegalName(candidate)}");
            }

            var existing = localRepos.Names()
                .FirstOrDefault(name => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (teams().TeamsUsingLocalRepo(existing).Count == 0)
                {
                    return new(existing, LocalRepos.ReferenceFor(existing), Created: false);
                }

                continue;
            }

            string? refusal;
            try
            {
                refusal = await localRepos.CreateAsync(candidate, ct);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                refusal = $"The local repository '{candidate}' could not be created: {exception.Message}";
            }

            if (refusal is not null) throw new ArgumentException(refusal);

            return new(candidate, LocalRepos.ReferenceFor(candidate), Created: true);
        }

        throw new ArgumentException(
            $"No local repository could be made for '{team}': '{team}' to '{team}-{MaximumSuffix}' are all used by teams.");
    }

    /// <summary>Removes a local repository <see cref="EnsureLocalAsync"/> made for a create or attach
    /// that then failed, when no team has come to use it since.</summary>
    public async Task ForgetAsync(TeamLocalRepository? made, CancellationToken ct)
    {
        if (made is not { Created: true } || teams().TeamsUsingLocalRepo(made.Name).Count > 0) return;
        await localRepos.DeleteAsync(made.Name, CancellationToken.None);
    }

    /// <summary>
    /// Checks every URL in <paramref name="urls"/> that is not <c>local:</c> and not in
    /// <paramref name="alreadyAttached"/> with <c>git ls-remote</c>, and applies
    /// <paramref name="choices"/>. Throws <see cref="RepoSetupRefusedException"/> with the refusal
    /// to answer - 400 for a choice that does not apply, 403 for a person's choice sent by an agent,
    /// 422 for a URL that could not be read and has no choice. Nothing is created here.
    /// </summary>
    public async Task<RepoPlan> PlanAsync(
        IReadOnlyList<string> urls, IReadOnlyDictionary<string, string>? choices, bool person,
        IReadOnlyCollection<string> alreadyAttached, CancellationToken ct)
    {
        var trimmed = urls.Select(u => u?.Trim() ?? "").ToList();
        var chosen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (url, choice) in choices ?? new Dictionary<string, string>())
        {
            var key = url.Trim();
            var code = choice?.Trim() ?? "";
            if (!trimmed.Contains(key, StringComparer.Ordinal))
            {
                throw BadRequest($"'{url}' has a repository choice but is not one of the repositories.");
            }

            if (!RepoChoices.All.Contains(code, StringComparer.Ordinal))
            {
                throw BadRequest(
                    $"'{choice}' is not a repository choice. Use {string.Join(", ", RepoChoices.All)}.");
            }

            if (!person && code is RepoChoices.CreateOnGitHub or RepoChoices.AttachAnyway)
            {
                throw new RepoSetupRefusedException(
                    StatusCodes.Status403Forbidden,
                    new { error = AgentRefusal(key, code) },
                    AgentRefusal(key, code));
            }

            if (code == RepoChoices.CreateOnGitHub && GitHubRepository.From(key) is null)
            {
                throw BadRequest(
                    $"'{key}' is not a github.com repository URL (https://github.com/<owner>/<name>), so it "
                    + "cannot be created on GitHub.");
            }

            chosen[key] = code;
        }

        var toCheck = trimmed
            .Where(url => !LocalRepos.IsLocal(url)
                && !alreadyAttached.Contains(url, StringComparer.Ordinal)
                && chosen.GetValueOrDefault(url) != RepoChoices.UseLocal)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var results = await Task.WhenAll(toCheck.Select(url => check.CheckAsync(url, ct)));
        var failed = results.OfType<RepoCheckFailure>().ToDictionary(f => f.Url, StringComparer.Ordinal);

        var kept = new List<string>();
        var create = new List<(string, GitHubRepository)>();
        var unresolved = new List<RepoCheckFailure>();
        var useLocal = false;
        foreach (var url in trimmed)
        {
            var choice = chosen.GetValueOrDefault(url);
            if (choice == RepoChoices.UseLocal)
            {
                useLocal = true;
                continue;
            }

            if (failed.GetValueOrDefault(url) is not { } failure)
            {
                kept.Add(url);
                continue;
            }

            switch (choice)
            {
                case RepoChoices.CreateOnGitHub:
                    create.Add((url, GitHubRepository.From(url)!));
                    kept.Add(url);
                    break;
                case RepoChoices.AttachAnyway when failure.Failure == RepoCheckFailures.Unreachable:
                    kept.Add(url);
                    break;
                case RepoChoices.AttachAnyway:
                    throw BadRequest(
                        $"{url} could not be read ({failure.Reason}), which is not a network failure, so it "
                        + "cannot be attached anyway. Create it, or use a local repository instead.");
                default:
                    unresolved.Add(failure);
                    break;
            }
        }

        if (unresolved.Count > 0) throw await RefusalAsync(unresolved, person, ct);

        return new RepoPlan(kept, useLocal, create);
    }

    /// <summary>
    /// The repositories of a team about to be created, checked: <paramref name="urls"/> and
    /// <paramref name="upstreams"/> validated (400 as <see cref="ArgumentException"/>), then read and
    /// answered by <see cref="PlanAsync"/>. ONE PATH FOR EVERY ROUTE THAT CREATES A TEAM -
    /// <c>POST /api/teams</c> and backlog dispatch-to-new - so neither can skip the check or the
    /// default. Nothing is created here; hand <see cref="NewTeamRepos.AddRepoAsync"/> to
    /// <see cref="TeamRegistry.CreateAsync"/> as its <c>addRepo</c>.
    /// </summary>
    public async Task<NewTeamRepos> PlanNewTeamAsync(
        IReadOnlyList<string>? urls, IReadOnlyDictionary<string, string>? upstreams,
        IReadOnlyDictionary<string, string>? choices, bool person, bool? localRepository, CancellationToken ct)
    {
        teams().ValidateRepos(urls, upstreams);
        var plan = await PlanAsync(urls ?? [], choices, person, alreadyAttached: [], ct);

        // A team that ends up with no repository gets a local one named after it, unless the caller
        // said not to; `use-local` asks for the same one in place of a URL.
        return new NewTeamRepos(this, plan, plan.UseLocal || (plan.Kept.Count == 0 && localRepository != false));
    }

    /// <summary>
    /// Creates each github.com repository the plan names, private. Throws
    /// <see cref="RepoSetupRefusedException"/> (422, the refusal shape) with GitHub's sentence when
    /// GitHub refuses; nothing after it is created.
    /// </summary>
    public async Task<IReadOnlyList<string>> CreateOnGitHubAsync(RepoPlan plan, CancellationToken ct)
    {
        var created = new List<string>();
        foreach (var (url, repository) in plan.CreateOnGitHub)
        {
            var answer = await gitHub.CreatePrivateRepositoryAsync(repository.Owner, repository.Name, ct);
            if (!answer.Answered)
            {
                var failure = new RepoCheckFailure(url, RepoCheckFailures.NotFound, answer.Refusal!);
                var sentence = $"{url} could not be created on GitHub: {answer.Refusal} Nothing was created. "
                    + "Create it yourself, or use a local repository instead.";
                throw new RepoSetupRefusedException(
                    StatusCodes.Status422UnprocessableEntity,
                    Body(sentence, [Entry(failure, [RepoChoices.UseLocal])]),
                    sentence);
            }

            created.Add(url);
        }

        return created;
    }

    private async Task<RepoSetupRefusedException> RefusalAsync(
        IReadOnlyList<RepoCheckFailure> failures, bool person, CancellationToken ct)
    {
        var entries = new List<object>();
        var sentences = new List<string>();
        foreach (var failure in failures)
        {
            var choices = new List<string>();
            var gitHubRepository = GitHubRepository.From(failure.Url);
            if (person && gitHubRepository is not null && await CanCreateAsync(gitHubRepository.Owner, ct))
            {
                choices.Add(RepoChoices.CreateOnGitHub);
            }

            choices.Add(RepoChoices.UseLocal);
            var unreachable = failure.Failure == RepoCheckFailures.Unreachable;
            if (person && unreachable) choices.Add(RepoChoices.AttachAnyway);

            entries.Add(Entry(failure, choices));
            sentences.Add(person
                ? $"{failure.Url} could not be read: {failure.Reason} Choose: {PersonChoices(choices)}."
                : $"{failure.Url} could not be read: {failure.Reason} {AgentChoices(gitHubRepository is not null, unreachable)}");
        }

        var sentence = string.Join(" ", sentences) + " Nothing was created.";
        return new RepoSetupRefusedException(StatusCodes.Status422UnprocessableEntity, Body(sentence, entries), sentence);
    }

    private async Task<bool> CanCreateAsync(string owner, CancellationToken ct)
    {
        try
        {
            var answer = await gitHub.CanCreateRepositoryAsync(owner, ct);
            return answer.Answered && answer.Value;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static string PersonChoices(IReadOnlyList<string> choices)
    {
        var words = choices.Select(c => c switch
        {
            RepoChoices.CreateOnGitHub => "create it on GitHub (private)",
            RepoChoices.UseLocal => "use a local repository instead",
            _ => "attach it anyway",
        }).ToList();
        return words.Count == 1 ? words[0] : string.Join(", ", words[..^1]) + ", or " + words[^1];
    }

    private static string AgentChoices(bool gitHubUrl, bool unreachable)
    {
        var personOnly = (gitHubUrl, unreachable) switch
        {
            (true, true) => "A person can create it on GitHub (private) or attach it anyway; you may not. ",
            (true, false) => "A person can create it on GitHub (private); you may not. ",
            (false, true) => "A person can attach it anyway; you may not. ",
            _ => "",
        };
        return personOnly
            + "Offer the person a local repository instead (send it again without this URL: a team with no "
            + "repository gets a local one), or ask them to create the remote and try again.";
    }

    private static string AgentRefusal(string url, string choice) =>
        (choice == RepoChoices.CreateOnGitHub
            ? $"Creating {url} on GitHub is a person's choice"
            : $"Attaching {url} without a successful check is a person's choice")
        + ", and an agent may not make it. Nothing was created. Offer the person a local repository "
        + "instead, or ask them to create the remote.";

    private static object Entry(RepoCheckFailure failure, IReadOnlyList<string> choices) =>
        new { url = failure.Url, failure = failure.Failure, reason = failure.Reason, choices };

    private static object Body(string sentence, IReadOnlyList<object> repos) =>
        new { error = sentence, code = "repo-check-failed", repos };

    private static RepoSetupRefusedException BadRequest(string sentence) =>
        new(StatusCodes.Status400BadRequest, new { error = sentence }, sentence);
}
