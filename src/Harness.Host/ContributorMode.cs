using System.Text.RegularExpressions;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// The two remote names a contributor-mode clone carries. <c>origin</c> is always the URL
/// in the team's repository list - the fork, in contributor mode - and <c>upstream</c> the original
/// project. An owned repository has no <c>upstream</c> remote.
/// </summary>
public static class ContributorRemotes
{
    public const string Origin = "origin";
    public const string Upstream = "upstream";

    /// <summary>The remote a repository is brought current against: upstream in contributor mode, else origin.</summary>
    public static string BaseFor(RepoContributor contributor) =>
        contributor.ContributorMode ? Upstream : Origin;
}

/// <summary>A git author identity: what a DCO sign-off names.</summary>
public sealed record GitIdentity(string Name, string Email)
{
    /// <summary>The trailer the commit-msg hook adds.</summary>
    public string SignedOffBy => $"Signed-off-by: {Name} <{Email}>";
}

/// <summary>
/// THE INSTANCE'S GIT IDENTITY: <c>GIT_AUTHOR_NAME</c> and <c>GIT_AUTHOR_EMAIL</c>, set on
/// the container with the operator CLI's <c>secret set</c>. It is the PERSON's identity, because a DCO sign-off
/// certifies what a person sends upstream. Read per call, so a test can hand one in as a setting.
/// Null when either is missing, blank, or not something a trailer can carry.
/// </summary>
public sealed class InstanceGitIdentity(IConfiguration configuration)
{
    public const string NameVariable = "GIT_AUTHOR_NAME";
    public const string EmailVariable = "GIT_AUTHOR_EMAIL";

    /// <summary>What turning DCO on without an identity answers: the cause and the fix.</summary>
    public const string Missing =
        "Sign off commits (DCO) needs the instance's git identity, and none is configured. Set it "
        + "with the operator CLI where the instance is run - `secret set GIT_AUTHOR_NAME` and "
        + "`secret set GIT_AUTHOR_EMAIL`, then its `up` - and turn the setting on again.";

    public GitIdentity? Current
    {
        get
        {
            var name = configuration[NameVariable]?.Trim();
            var email = configuration[EmailVariable]?.Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(email)) return null;
            if (!Carriable(name) || !Carriable(email)) return null;

            return new GitIdentity(name, email);
        }
    }

    // A trailer is one line, and `<`/`>` delimit the address in it.
    private static bool Carriable(string value) =>
        !value.Any(c => char.IsControl(c) || c is '<' or '>');
}

/// <summary>What a person may store as a repository's contributor settings.</summary>
public static partial class ContributorSettings
{
    /// <summary>Longer than any note a person writes; shorter than a pasted document.</summary>
    public const int ClaNoteLimit = 500;

    /// <summary>
    /// The settings to store, or <see cref="ArgumentException"/> naming what is wrong. A blank
    /// upstream is an owned repository, and it clears the fork owner with it. A blank fork owner
    /// is read from the origin URL's first path segment (<c>github.com/&lt;owner&gt;/&lt;repo&gt;</c>).
    /// </summary>
    public static RepoContributor Validate(
        string team, string repo, string originUrl,
        string? upstreamUrl, string? forkOwner, bool dcoSignOff, string? claSignedNote)
    {
        var upstream = string.IsNullOrWhiteSpace(upstreamUrl) ? null : upstreamUrl.Trim();
        string? owner = null;

        if (upstream is not null && LocalRepos.IsLocal(originUrl))
        {
            throw new ArgumentException(LocalRepoSentences.NoContributorMode(repo));
        }

        if (upstream is not null)
        {
            if (!Uri.TryCreate(upstream, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException($"'{upstream}' is not an absolute http or https repository URL.");
            }

            if (SameRepository(upstream, originUrl))
            {
                throw new ArgumentException(
                    "The upstream is the repository's own URL. In contributor mode the repository's URL is "
                    + "the fork, and the upstream is the project it was forked from.");
            }

            owner = string.IsNullOrWhiteSpace(forkOwner) ? OwnerOf(originUrl) : forkOwner.Trim();
            if (owner is not null && !AccountName().IsMatch(owner))
            {
                throw new ArgumentException($"'{owner}' is not a GitHub account name.");
            }
        }

        var note = string.IsNullOrWhiteSpace(claSignedNote) ? null : claSignedNote.Trim();
        if (note is { Length: > ClaNoteLimit })
        {
            throw new ArgumentException($"The CLA note is longer than {ClaNoteLimit} characters.");
        }

        return new RepoContributor(team, repo, upstream, owner, dcoSignOff, note);
    }

    /// <summary>The first path segment of an http(s) URL, when it is an account name.</summary>
    public static string? OwnerOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var first = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && AccountName().IsMatch(first) ? first : null;
    }

    private static bool SameRepository(string a, string b) =>
        string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalise(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        return trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    private static partial Regex AccountName();
}

/// <summary>
/// THE DCO commit-msg HOOK. Installed in the main clone's <c>.git/hooks</c>, which every
/// worktree cut from it shares. It adds <c>Signed-off-by: &lt;name&gt; &lt;email&gt;</c> with
/// <c>git interpret-trailers --if-exists doNothing</c>, so a message that already carries a
/// sign-off (<c>git commit -s</c>, an amend) keeps one, never two.
///
/// <para>
/// THE PLATFORM NEVER RUNS IT. Every git the Host starts passes <c>core.hooksPath=/dev/null</c>
/// (<see cref="GitRunner.Hardening"/>), so this runs only for commits a member makes in its own
/// tree, as the member's own user.
/// </para>
///
/// <para>
/// A HOOK THE PLATFORM DID NOT WRITE IS NEVER OVERWRITTEN OR REMOVED. Ours carries
/// <see cref="Marker"/>; anything else at that path is somebody's, and turning DCO on refuses
/// and names it rather than replacing it.
/// </para>
/// </summary>
public static class DcoHook
{
    public const string Marker = "# harness: DCO sign-off";

    public static string PathFor(string clonePath) => Path.Combine(clonePath, ".git", "hooks", "commit-msg");

    public static string Script(GitIdentity identity) =>
        "#!/bin/sh\n"
        + Marker + "\n"
        + "# Written by the platform while \"Sign off commits (DCO)\" is on for this repository, and\n"
        + "# removed when it is turned off. Adds the sign-off once: an existing one is kept.\n"
        + "exec git interpret-trailers --in-place --if-exists doNothing --trailer "
        + ShellQuote(identity.SignedOffBy) + " \"$1\"\n";

    /// <summary>Whether the hook at this clone is the platform's.</summary>
    public static bool IsOurs(string clonePath)
    {
        var path = PathFor(clonePath);
        return File.Exists(path) && File.ReadAllText(path).Contains(Marker, StringComparison.Ordinal);
    }

    /// <summary>Writes (or rewrites) the hook. Answers a refusal when a hook that is not ours is there.</summary>
    public static string? Install(string clonePath, GitIdentity identity)
    {
        var path = PathFor(clonePath);
        if (File.Exists(path) && !IsOurs(clonePath))
        {
            return $"A commit-msg hook the platform did not write is already at {path}, so the sign-off "
                + "hook was not installed. Move it away, then turn the setting on again.";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var script = Script(identity);
        if (!File.Exists(path) || File.ReadAllText(path) != script)
        {
            File.WriteAllText(path, script);
        }

        // Executable by the agent's group: the clone is the agent's, and the Host writes as its own user.
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
        return null;
    }

    /// <summary>Removes the hook when it is ours; leaves anything else alone.</summary>
    public static void Remove(string clonePath)
    {
        if (IsOurs(clonePath)) File.Delete(PathFor(clonePath));
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

/// <summary>
/// BRINGS ONE CLONE IN LINE WITH ITS CONTRIBUTOR SETTINGS: the <c>upstream</c> remote, and
/// the DCO hook. Idempotent, so it runs after a setting changes, after a clone is made, and before
/// every Fetch. A clone that is not there yet has nothing to change; it is brought in line when it
/// is made.
/// </summary>
public sealed class ContributorClone(GitRunner git, InstanceGitIdentity identity)
{
    /// <summary>Null when the clone matches its settings, else what could not be done.</summary>
    public async Task<string?> ApplyAsync(string clonePath, RepoContributor settings, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(clonePath, ".git"))) return null;

        var remote = await git.SetUpstreamRemoteAsync(clonePath, settings.UpstreamUrl, ct);
        if (remote is not null)
        {
            return "The upstream remote could not be set: "
                + GitOutputRedaction.Redact(RepoEndpoints.BoundLines((remote.Stderr + remote.Stdout).Trim()));
        }

        if (!settings.DcoSignOff)
        {
            DcoHook.Remove(clonePath);
            return null;
        }

        // An identity removed after DCO was turned on leaves the hook it wrote; turning it ON is
        // what refuses without one.
        return identity.Current is { } person ? DcoHook.Install(clonePath, person) : null;
    }
}
