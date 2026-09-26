using System.Diagnostics;
using Harness.Contracts;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Harness.Host;

/// <summary>
/// A pull request as GitHub last described it: its URL, number, and one of
/// <see cref="PullRequestStates"/>.
/// </summary>
public sealed record PullRequestInfo(string Url, int Number, string State);

/// <summary>A fork GitHub made (or already had) for an upstream: its URL and the account it is in.</summary>
public sealed record ForkInfo(string Url, string Owner);

/// <summary>
/// What GitHub answered: a value, or a refusal. <see cref="Unreachable"/> separates "GitHub could
/// not be asked" from "GitHub said no", and both are an unknown to anything reading a state.
/// <see cref="Refusal"/> is GitHub's own sentence, then what the token needs where that is the cause.
/// </summary>
public sealed record GitHubAnswer<T>(T? Value, string? Refusal, bool Unreachable = false)
{
    public bool Answered => Refusal is null;

    public static GitHubAnswer<T> Of(T value) => new(value, null);
}

/// <summary>
/// The three words GitHub gives a pull request, as the platform stores them. <c>closed</c> here is
/// closed WITHOUT merging: a merged pull request is <c>merged</c>, never <c>closed</c>.
/// </summary>
public static class PullRequestStates
{
    public const string Open = "open";
    public const string Closed = "closed";
    public const string Merged = "merged";
}

/// <summary>
/// EVERYTHING THE PLATFORM ASKS GITHUB IN CONTRIBUTOR MODE: make a fork, find an open pull
/// request, open one, read one. An interface so every automated test answers with a fake - no test
/// calls GitHub. Only a person's button reaches any of it: no agent tool, no skill, and
/// <c>workflow_complete</c> never opens a pull request.
/// </summary>
public interface IGitHubContributor
{
    /// <summary><c>gh repo fork &lt;upstream&gt; --clone=false</c>, into the token's account, or <paramref name="organisation"/>.</summary>
    Task<GitHubAnswer<ForkInfo>> ForkAsync(string upstreamUrl, string? organisation, CancellationToken ct);

    /// <summary>The open pull request on the upstream whose head is <c>&lt;forkOwner&gt;:&lt;branch&gt;</c>, or null.</summary>
    Task<GitHubAnswer<PullRequestInfo?>> FindOpenAsync(string upstreamUrl, string forkOwner, string branch, CancellationToken ct);

    /// <summary><c>gh pr create --repo &lt;upstream&gt; --head &lt;forkOwner&gt;:&lt;branch&gt; --base &lt;base&gt;</c>.</summary>
    Task<GitHubAnswer<PullRequestInfo>> CreateAsync(
        string upstreamUrl, string forkOwner, string branch, string baseBranch, string title, string body, CancellationToken ct);

    /// <summary>The recorded pull request's state now.</summary>
    Task<GitHubAnswer<PullRequestInfo>> ReadAsync(string upstreamUrl, int number, CancellationToken ct);
}

/// <summary>
/// WHAT A TOKEN NEEDS FOR CONTRIBUTOR MODE, said once so every refusal names the same fix.
/// Verified against an upstream the token's owner does not own: a classic-scoped token
/// (<c>repo</c>) forks it into its own account and opens a pull request on it.
/// </summary>
public static class GitHubTokenNeeds
{
    public const string ContributorMode =
        "Contributor mode needs a classic GitHub token with the public_repo scope (repo for a private "
        + "upstream): it forks into the token's own account and opens pull requests on a repository "
        + "the token's owner does not own. Set it with the operator CLI's `secret set GH_TOKEN`, then "
        + "its `up`; its `doctor` checks the kind. Or fork on GitHub and give both URLs.";

    public const string NoToken =
        "No GitHub token is configured for the instance, so GitHub cannot be asked. " + ContributorMode;
}

/// <summary>An owner and a name, read from a github.com URL.</summary>
public sealed record GitHubRepository(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";

    public static GitHubRepository? From(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Host, "www.github.com", StringComparison.OrdinalIgnoreCase)) return null;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;
        var name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        return name.Length == 0 ? null : new GitHubRepository(parts[0], name);
    }
}

/// <summary>
/// <see cref="IGitHubContributor"/> through the GitHub CLI, <c>gh</c>, with the instance's
/// <c>GH_TOKEN</c>. Started the way <see cref="GitRunner"/> starts git: from a root-owned system
/// directory, through <see cref="AgentLaunchUser.Prefix"/>, with <see cref="SystemCommand.SafePath"/>
/// and no provider key - only the GitHub token, and only for a github.com upstream.
/// </summary>
public sealed partial class GhContributor(AgentLaunchUser? runAs = null, Func<string?>? token = null, int timeoutSeconds = 30)
    : IGitHubContributor
{
    private readonly Func<string?> _token = token ?? (() => AgentEnvironment.GitHubTokenFor(["https://github.com/"]));

    public async Task<GitHubAnswer<ForkInfo>> ForkAsync(string upstreamUrl, string? organisation, CancellationToken ct)
    {
        if (GitHubRepository.From(upstreamUrl) is not { } upstream) return NotGitHub<ForkInfo>(upstreamUrl);

        List<string> args = ["repo", "fork", upstream.FullName, "--clone=false"];
        if (!string.IsNullOrWhiteSpace(organisation)) args.AddRange(["--org", organisation.Trim()]);

        var run = await RunAsync(args, ct);
        if (run.Failure is not null) return Refused<ForkInfo>(run);

        // gh names the fork on stdout when it makes one, and "<owner>/<name> already exists" on
        // stderr when the account already had it. Either way GitHub is then asked what it is.
        var named = ForkUrl().Match(run.Stdout + "\n" + run.Stderr) is { Success: true } url
            ? $"{url.Groups[1].Value}/{url.Groups[2].Value}"
            : AlreadyExists().Match(run.Stderr) is { Success: true } existing
                ? existing.Groups[1].Value
                : null;
        if (named is null)
        {
            return new(null, $"GitHub made the fork, but gh did not say where: {Bound(run.Stdout + run.Stderr)}");
        }

        var read = await RunAsync(["api", $"repos/{named}"], ct);
        if (read.Failure is not null) return Refused<ForkInfo>(read);

        try
        {
            using var json = JsonDocument.Parse(read.Stdout);
            var root = json.RootElement;
            var parent = root.TryGetProperty("parent", out var p) && p.ValueKind == JsonValueKind.Object
                ? p.GetProperty("full_name").GetString()
                : null;
            if (!root.GetProperty("fork").GetBoolean()
                || !string.Equals(parent, upstream.FullName, StringComparison.OrdinalIgnoreCase))
            {
                return new(null,
                    $"{named} already exists and is not a fork of {upstream.FullName}, so it was not used. "
                    + "Fork on GitHub under another name and give both URLs.");
            }

            return GitHubAnswer<ForkInfo>.Of(new ForkInfo(
                root.GetProperty("html_url").GetString()!, root.GetProperty("owner").GetProperty("login").GetString()!));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new(null, $"GitHub's answer about {named} could not be read.");
        }
    }

    public async Task<GitHubAnswer<PullRequestInfo?>> FindOpenAsync(
        string upstreamUrl, string forkOwner, string branch, CancellationToken ct)
    {
        if (GitHubRepository.From(upstreamUrl) is not { } upstream) return NotGitHub<PullRequestInfo?>(upstreamUrl);

        var run = await RunAsync(
            ["api", "-X", "GET", $"repos/{upstream.FullName}/pulls", "-f", "state=open", "-f", $"head={forkOwner}:{branch}"], ct);
        if (run.Failure is not null) return Refused<PullRequestInfo?>(run);

        try
        {
            using var json = JsonDocument.Parse(run.Stdout);
            foreach (var pull in json.RootElement.EnumerateArray())
            {
                return GitHubAnswer<PullRequestInfo?>.Of(Describe(pull));
            }

            return GitHubAnswer<PullRequestInfo?>.Of(null);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new(null, "GitHub's list of open pull requests could not be read.", Unreachable: true);
        }
    }

    public async Task<GitHubAnswer<PullRequestInfo>> CreateAsync(
        string upstreamUrl, string forkOwner, string branch, string baseBranch, string title, string body, CancellationToken ct)
    {
        if (GitHubRepository.From(upstreamUrl) is not { } upstream) return NotGitHub<PullRequestInfo>(upstreamUrl);

        var run = await RunAsync(
            ["pr", "create", "--repo", upstream.FullName, "--head", $"{forkOwner}:{branch}", "--base", baseBranch,
             "--title", title, "--body", body], ct);
        if (run.Failure is not null) return Refused<PullRequestInfo>(run);

        if (PullUrl().Match(run.Stdout) is not { Success: true } url)
        {
            return new(null, $"GitHub opened the pull request, but gh did not say where: {Bound(run.Stdout + run.Stderr)}");
        }

        return GitHubAnswer<PullRequestInfo>.Of(
            new PullRequestInfo(url.Value, int.Parse(url.Groups[1].Value), PullRequestStates.Open));
    }

    public async Task<GitHubAnswer<PullRequestInfo>> ReadAsync(string upstreamUrl, int number, CancellationToken ct)
    {
        if (GitHubRepository.From(upstreamUrl) is not { } upstream) return NotGitHub<PullRequestInfo>(upstreamUrl);

        var run = await RunAsync(["api", $"repos/{upstream.FullName}/pulls/{number}"], ct);
        if (run.Failure is not null) return Refused<PullRequestInfo>(run);

        try
        {
            using var json = JsonDocument.Parse(run.Stdout);
            return GitHubAnswer<PullRequestInfo>.Of(Describe(json.RootElement));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new(null, $"GitHub's answer about pull request #{number} could not be read.", Unreachable: true);
        }
    }

    /// <summary>open, closed (without merging) or merged, from GitHub's REST shape.</summary>
    public static PullRequestInfo Describe(JsonElement pull)
    {
        var merged = (pull.TryGetProperty("merged", out var m) && m.ValueKind == JsonValueKind.True)
            || (pull.TryGetProperty("merged_at", out var at) && at.ValueKind == JsonValueKind.String);
        var state = merged
            ? PullRequestStates.Merged
            : string.Equals(pull.GetProperty("state").GetString(), "open", StringComparison.OrdinalIgnoreCase)
                ? PullRequestStates.Open
                : PullRequestStates.Closed;

        return new PullRequestInfo(pull.GetProperty("html_url").GetString()!, pull.GetProperty("number").GetInt32(), state);
    }

    /// <summary>
    /// GitHub's sentence, and what the token needs when the token is why. Unreachable is kept
    /// apart: a state read that could not reach GitHub is unknown, never a guess.
    /// </summary>
    public static GitHubAnswer<T> Refused<T>(GhRun run)
    {
        var said = Bound(run.Failure!);
        if (run.NotStarted) return new(default, said, Unreachable: true);

        var unreachable = Unreachable().IsMatch(said);
        var tokenIsWhy = TokenRefused().IsMatch(said);
        return new(default,
            tokenIsWhy ? $"GitHub said: {said} {GitHubTokenNeeds.ContributorMode}" : $"GitHub said: {said}",
            unreachable);
    }

    private static GitHubAnswer<T> NotGitHub<T>(string url) =>
        new(default, $"'{url}' is not a github.com repository URL, so GitHub cannot be asked about it.");

    public sealed record GhRun(string Stdout, string Stderr, string? Failure, bool NotStarted = false);

    private async Task<GhRun> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (_token() is not { } token) return new(string.Empty, string.Empty, GitHubTokenNeeds.NoToken, NotStarted: true);
        if (runAs is { Refuses: true }) return new(string.Empty, string.Empty, runAs.Refusal("gh"), NotStarted: true);
        if (SystemCommand.Find("gh") is not { } executable)
        {
            return new(string.Empty, string.Empty,
                "gh is not installed in a root-owned system directory on this machine, so GitHub cannot be asked.",
                NotStarted: true);
        }

        var prefix = runAs?.Prefix ?? [];
        var start = new ProcessStartInfo(prefix.Count > 0 ? prefix[0] : executable)
        {
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var part in prefix.Skip(1)) start.ArgumentList.Add(part);
        if (prefix.Count > 0) start.ArgumentList.Add(executable);
        foreach (var arg in args) start.ArgumentList.Add(arg);

        foreach (var variable in AgentEnvironment.ProviderVariables) start.Environment.Remove(variable);
        start.Environment.Remove("GITHUB_TOKEN");
        start.Environment[AgentEnvironment.GitHubVariable] = token;
        start.Environment["PATH"] = SystemCommand.SafePath;
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        start.Environment["GH_PAGER"] = "cat";
        start.Environment["NO_COLOR"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        Process? started;
        try
        {
            started = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return new(string.Empty, string.Empty, $"gh could not be started: {exception.Message}", NotStarted: true);
        }

        using var process = started ?? throw new InvalidOperationException("The gh process did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            var (output, error) = (await stdout, await stderr);
            return process.ExitCode == 0
                ? new(output, error, null)
                : new(output, error, string.IsNullOrWhiteSpace(error) ? output : error);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new(string.Empty, string.Empty, $"GitHub did not answer within {timeoutSeconds} seconds.", NotStarted: true);
        }
    }

    /// <summary>At most a few lines, and never a token.</summary>
    internal static string Bound(string text)
    {
        var lines = (GitOutputRedaction.Redact(text) ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kept = string.Join(" ", lines.Take(4));
        return kept.Length > 600 ? kept[..600] + "…" : kept;
    }

    [GeneratedRegex(@"https://github\.com/([A-Za-z0-9-]+)/([A-Za-z0-9._-]+?)(?:\.git)?(?=\s|$)")]
    private static partial Regex ForkUrl();

    [GeneratedRegex(@"^([A-Za-z0-9-]+/[A-Za-z0-9._-]+) already exists", RegexOptions.Multiline)]
    private static partial Regex AlreadyExists();

    [GeneratedRegex(@"https://github\.com/[^/\s]+/[^/\s]+/pull/(\d+)")]
    private static partial Regex PullUrl();

    [GeneratedRegex(@"error connecting|dial tcp|could not resolve|no such host|i/o timeout|TLS handshake|connection refused|did not answer|HTTP 5\d\d", RegexOptions.IgnoreCase)]
    private static partial Regex Unreachable();

    [GeneratedRegex(@"HTTP 401|HTTP 403|Bad credentials|Resource not accessible|must have (?:admin|push|write)|not have permission|scope", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRefused();
}

/// <summary>
/// WHAT A RECORDED PULL REQUEST SAYS NOW, asked when the backlog or the Git dialog is read
/// and AT MOST ONCE A MINUTE PER PULL REQUEST. A successful answer is stored on the repository with
/// when it was read; a failed one is remembered for the same minute and reads as unknown - the
/// last stored answer is still shown beside it, and never passed off as the current one.
/// </summary>
public sealed class PullRequestStateReader(
    IGitHubContributor gitHub, TeamRegistry teams, Func<DateTimeOffset>? now = null, TimeSpan? interval = null)
{
    /// <summary>The spec's bound: GitHub is asked about one pull request at most once a minute.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly TimeSpan _interval = interval ?? DefaultInterval;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, string? Failure)> _asked =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The recorded pull request's reading, or null when the repository is owned or none is recorded.
    /// </summary>
    public async Task<PullRequestReading?> ReadAsync(string team, string repo, CancellationToken ct)
    {
        var contributor = teams.ContributorFor(team, repo);
        if (!contributor.ContributorMode || contributor.PullRequest is not { } recorded) return null;

        var gate = _locks.GetOrAdd(recorded.Url, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_asked.TryGetValue(recorded.Url, out var last) && _now() - last.At < _interval)
            {
                return Reading(teams.ContributorFor(team, repo).PullRequest ?? recorded, last.Failure);
            }

            var answer = await gitHub.ReadAsync(contributor.UpstreamUrl!, recorded.Number, ct);
            var at = _now();
            if (!answer.Answered || answer.Value is null)
            {
                _asked[recorded.Url] = (at, answer.Refusal ?? "GitHub gave no answer.");
                return Reading(recorded, answer.Refusal ?? "GitHub gave no answer.");
            }

            var read = recorded with { State = answer.Value.State, ReadAt = at };
            await teams.RecordPullRequestAsync(team, repo, read, ct);
            _asked[recorded.Url] = (at, null);
            return Reading(read, null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A pull request just opened or linked: its state was read now, so the minute starts now.</summary>
    public void Saw(RepoPullRequest pullRequest) => _asked[pullRequest.Url] = (pullRequest.ReadAt, null);

    private static PullRequestReading Reading(RepoPullRequest pullRequest, string? failure) =>
        new(pullRequest, failure is null ? PullRequestLanding.For(pullRequest.State) : BacklogLandedStates.Unknown, failure);
}

/// <summary>
/// One read of a recorded pull request: the last stored answer and when it was read, the landing
/// word it means now, and - when this read could not reach GitHub or the token was refused - why,
/// in which case <see cref="Landing"/> is <c>unknown</c> whatever the last answer was.
/// </summary>
public sealed record PullRequestReading(RepoPullRequest PullRequest, string Landing, string? UnknownReason);

/// <summary>The landing word a pull request's state means.</summary>
public static class PullRequestLanding
{
    public static string For(string state) => state switch
    {
        PullRequestStates.Open => BacklogLandedStates.InReview,
        PullRequestStates.Closed => BacklogLandedStates.Declined,
        PullRequestStates.Merged => BacklogLandedStates.Landed,
        _ => BacklogLandedStates.Unknown,
    };
}
