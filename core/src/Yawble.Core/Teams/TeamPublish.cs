using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Yawble.Core.Teams;

/// <summary>What became of one publish.</summary>
public enum TeamPublishResult
{
    /// <summary>The team repository has no origin (the app's own managed repository, or a local-only one).</summary>
    NoRepository,

    /// <summary>Every branch is already on origin.</summary>
    NothingToPublish,

    /// <summary>Branches reached origin (<see cref="TeamPublishReport.Branches"/>).</summary>
    Published,

    /// <summary>git did not publish: an unreachable origin, a refused credential, a timeout. Retryable; the next run
    /// end tries again. <see cref="TeamPublishReport.Branches"/> names what did NOT reach origin.</summary>
    PushFailed,

    /// <summary>Origin refused branches that do not extend its copy (amended or rebased after a push). Not retryable;
    /// the others were still pushed (<see cref="TeamPublishReport.Published"/>).</summary>
    Rejected,
}

/// <param name="Reason">git's own words, credentials redacted; set for PushFailed and Rejected.</param>
public sealed record TeamPublishReport(TeamPublishResult Result, IReadOnlyList<string> Branches, string? Reason = null, IReadOnlyList<string>? Published = null)
{
    public static readonly TeamPublishReport None = new(TeamPublishResult.NoRepository, []);
    public static readonly TeamPublishReport Nothing = new(TeamPublishResult.NothingToPublish, []);

    /// <summary>
    /// One report for the team's repositories: the primary's, then an extra's (its branches named "&lt;repo&gt;: branch").
    /// The worse result wins (a failed push over a refusal over a publish); every branch and reason is kept.
    /// </summary>
    public static TeamPublishReport Combine(TeamPublishReport primary, string extraName, TeamPublishReport extra)
    {
        if (extra.Result is TeamPublishResult.NoRepository) return primary;
        static int Rank(TeamPublishResult r) => r switch
        {
            TeamPublishResult.PushFailed => 4,
            TeamPublishResult.Rejected => 3,
            TeamPublishResult.Published => 2,
            TeamPublishResult.NothingToPublish => 1,
            _ => 0,
        };
        IEnumerable<string> Named(IEnumerable<string>? bs) => (bs ?? []).Select(b => $"{extraName}: {b}");
        var result = Rank(extra.Result) > Rank(primary.Result) ? extra.Result : primary.Result;
        var reason = string.Join("; ", new[] { primary.Reason, extra.Reason is null ? null : $"{extraName}: {extra.Reason}" }.Where(r => r is not null));
        var published = (primary.Published ?? []).Concat(Named(extra.Published)).ToList();
        return new TeamPublishReport(result, [.. primary.Branches, .. Named(extra.Branches)], reason.Length == 0 ? null : reason,
            published.Count == 0 && primary.Published is null && extra.Published is null ? null : published);
    }
}

/// <summary>
/// The one git command that uses the person's own git setup: <c>git push origin refs/heads/B:refs/heads/B</c> in the
/// TEAM repository (the person's or the app's, never a clone an agent used), so the person's remote and credential
/// helper apply, as for any push they make themselves.
/// No prompt ever opens (GIT_TERMINAL_PROMPT=0, GCM_INTERACTIVE=never): a missing credential is a PushFailed with
/// git's reason. Never forced (no '+', no --force). Every other host git command stays on <see cref="HostGit"/>.
/// </summary>
/// <param name="globalConfig">Tests only: when set, no system config is read and this file is the global one, so the
/// person's own git setup (hooks, credential helpers) never applies to a test's temp repositories.</param>
public sealed class PersonGitPush(TimeSpan? timeout = null, string? globalConfig = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(2);

    /// <summary>
    /// Pushes one branch (or, with <paramref name="source"/>, a commit) to origin's refs/heads/<paramref name="branch"/>.
    /// Never forced: the refspec has no '+', and there is no --force anywhere.
    /// </summary>
    public Task<GitResult> PushAsync(string teamRepo, string branch, CancellationToken ct, string? source = null) =>
        RunAsync(teamRepo, ["push", "--porcelain", "--no-verify", "origin", $"{source ?? "refs/heads/" + branch}:refs/heads/{branch}"], ct);

    /// <summary>
    /// Any other git command that must reach origin as the person does (the Git dialog's Fetch). The person's config
    /// applies; prompts are off; askpass is off. Callers pass only network commands against origin.
    /// </summary>
    public async Task<GitResult> RunAsync(string teamRepo, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = teamRepo,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // The person's config applies, but nothing from this process's environment steers git.
        foreach (var key in psi.Environment.Keys.ToArray())
            if (key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)) psi.Environment.Remove(key);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["LC_ALL"] = "C";
        if (globalConfig is not null)
        {
            psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            psi.Environment["GIT_CONFIG_GLOBAL"] = globalConfig;
        }
        // --no-verify: a pre-push hook would run on the host, outside every sandbox, often over the members' code
        // (tests, build scripts); the app never runs agent-written code unsandboxed.
        foreach (var a in new[] { "-c", "core.askPass=" }.Concat(args)) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new GitException("git could not be started: is git installed?");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new GitResult(-1, "", $"git {args[0]} ran out of time after {_timeout.TotalSeconds:0}s and was stopped; the origin may be unreachable");
        }
        return new GitResult(process.ExitCode, (await stdout).ReplaceLineEndings("\n").Trim(), (await stderr).ReplaceLineEndings("\n").Trim());
    }

    /// <summary>Removes a credential git may quote from a remote URL (https://user:token@host).</summary>
    public static string Redact(string text) => Regex.Replace(text, @"(?i)\b([a-z][a-z0-9+.-]*://)[^/\s@]+@", "$1***@");

    /// <summary>Whether origin refused the ref itself (not a fast-forward), as opposed to the transport failing.</summary>
    public static bool IsRefused(string output) =>
        output.Contains("[rejected]", StringComparison.Ordinal) || output.Contains("[remote rejected]", StringComparison.Ordinal)
        || output.Contains("non-fast-forward", StringComparison.Ordinal) || output.Contains("fetch first", StringComparison.Ordinal);
}
