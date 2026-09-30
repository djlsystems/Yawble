using System.Text.RegularExpressions;

namespace Harness.Host;

/// <summary>One local repository as the Admin view and the pickers list it.</summary>
public sealed record LocalRepoInfo(
    string Name,
    string Reference,
    long SizeBytes,
    string? DefaultBranch,
    LocalRepoCommit? LastCommit,
    IReadOnlyList<string> Teams)
{
    /// <summary>Its branches, by short name (what deleting it would lose).</summary>
    public IReadOnlyList<string> Branches { get; init; } = [];

    /// <summary>How many commits its branches hold together; null when git could not count them.</summary>
    public int? CommitCount { get; init; }
}

/// <summary>The newest commit on a local repository's default branch.</summary>
public sealed record LocalRepoCommit(string Sha, string Subject, DateTimeOffset? CommittedAt);

/// <summary>
/// THE INSTANCE'S OWN GIT REPOSITORIES: bare, one per name, at <c>&lt;dataRoot&gt;/repos/&lt;name&gt;.git</c>.
/// A team names one as <c>local:&lt;name&gt;</c> wherever it would name a URL, and its clone uses the
/// bare repository as <c>origin</c>.
///
/// <para>
/// <b><c>local:</c> IS A REFERENCE, NOT A PATH.</b> A person or a request never names a folder: the
/// name is checked against <see cref="IsLegalName"/> (letters, digits, <c>.</c>, <c>_</c>, <c>-</c>)
/// before anything is joined to the data root, and the joined path is checked to stay directly in
/// <see cref="Root"/>. A name that fails either is refused naming it.
/// </para>
///
/// <para>
/// <b>THE HOST'S, NEVER THE AGENT'S.</b> Everything under <see cref="Root"/> is written by the Host
/// as itself (<see cref="GitRunner"/>'s host-side operations) with <c>core.sharedRepository=0640</c>,
/// so the agent's group can read - clone and fetch - and never write. Agents never push: the
/// Host publishes into these as it does to a remote (<see cref="GitRunner.PushRefspecAsync"/>), and
/// <c>scripts/prepare-volume.sh</c> puts the modes back at every start.
/// </para>
/// </summary>
public sealed partial class LocalRepos(string dataRoot, GitRunner git)
{
    public const string Scheme = "local:";

    /// <summary>The branch a new local repository starts on.</summary>
    public const string InitialBranch = "main";

    private const string Suffix = ".git";
    private const string PendingPrefix = ".creating-";
    private const string DeletingPrefix = ".deleting-";

    private readonly SemaphoreSlim _writes = new(1, 1);

    /// <summary><c>&lt;dataRoot&gt;/repos</c>.</summary>
    public string Root { get; } = Path.Combine(Path.GetFullPath(dataRoot), "repos");

    public static bool IsLocal(string? reference) =>
        reference is not null && reference.Trim().StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>The name in <c>local:&lt;name&gt;</c>, unchecked. Callers check it with <see cref="IsLegalName"/>.</summary>
    public static string NameOf(string reference) => reference.Trim()[Scheme.Length..];

    public static string ReferenceFor(string name) => Scheme + name;

    /// <summary>
    /// A name a local repository may have: it becomes a folder name and a clone's folder name, so it
    /// is held to less than a folder allows - 1 to 100 of letters, digits, <c>.</c>, <c>_</c> and
    /// <c>-</c>, starting with a letter or digit, not ending in <c>.git</c> or <c>.lock</c>, no <c>..</c>.
    /// </summary>
    public static bool IsLegalName(string? name) =>
        name is not null
        && NamePattern().IsMatch(name)
        && !name.Contains("..", StringComparison.Ordinal)
        && !name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)
        && !name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);

    /// <summary>The sentence an illegal name is refused with.</summary>
    public static string IllegalName(string? name) =>
        $"'{name}' is not a local repository name: use 1 to 100 letters, digits, '.', '_' or '-', starting "
        + "with a letter or digit, not ending in '.git' or '.lock', with no '..'.";

    /// <summary>
    /// The bare repository's folder for <paramref name="name"/>, or null when the name is not legal.
    /// Whether it exists is <see cref="Exists"/>'s question.
    /// </summary>
    public string? PathFor(string name)
    {
        if (!IsLegalName(name)) return null;

        var path = Path.GetFullPath(Path.Combine(Root, name + Suffix));
        return string.Equals(Path.GetDirectoryName(path), Root, StringComparison.Ordinal) ? path : null;
    }

    /// <summary>Whether a local repository of exactly this name is on the volume. Never a symbolic
    /// link: one planted in <see cref="Root"/> is not a repository the Host made.</summary>
    public bool Exists(string name) =>
        PathFor(name) is { } path
        && Directory.Exists(path)
        && new DirectoryInfo(path).LinkTarget is null;

    /// <summary>The bare repository <paramref name="reference"/> names, when it is a <c>local:</c>
    /// reference to one that exists; null for anything else.</summary>
    public string? PathForReference(string reference) =>
        IsLocal(reference) && NameOf(reference) is var name && Exists(name) ? PathFor(name) : null;

    /// <summary>Every local repository, by name, in ordinal order.</summary>
    public IReadOnlyList<string> Names()
    {
        if (!Directory.Exists(Root)) return [];

        return [.. Directory.EnumerateDirectories(Root, "*" + Suffix)
            .Select(Path.GetFileName)
            .Select(leaf => leaf![..^Suffix.Length])
            .Where(Exists)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>What the Admin view shows of one: size, default branch, last commit, and the teams
    /// <paramref name="teamsUsing"/> says use it.</summary>
    public async Task<LocalRepoInfo?> DescribeAsync(
        string name, IReadOnlyList<string> teamsUsing, CancellationToken ct)
    {
        if (!Exists(name)) return null;
        var path = PathFor(name)!;

        long size = 0;
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            size += file.Length;
        }

        var branch = await git.ReadLocalRepositoryHeadAsync(path, ct);
        var last = branch is null ? null : await git.ReadLocalRepositoryTipAsync(path, branch, ct);

        var (branches, commits) = await git.ReadLocalRepositoryContentsAsync(path, ct);

        return new LocalRepoInfo(name, ReferenceFor(name), size, branch, last, teamsUsing)
        {
            Branches = branches,
            CommitCount = commits,
        };
    }

    /// <summary>
    /// Creates <c>&lt;name&gt;.git</c> with default branch <see cref="InitialBranch"/> and one empty
    /// commit on it, so a team can clone and branch from it at once. Made in a hidden folder and
    /// moved into place, so a failure leaves no half-made repository under the name. Answers null on
    /// success, or the sentence it was refused with.
    /// </summary>
    public async Task<string?> CreateAsync(string name, CancellationToken ct)
    {
        if (PathFor(name) is not { } path) return IllegalName(name);

        await _writes.WaitAsync(ct);
        try
        {
            if (Names().FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) is { } taken)
            {
                return $"A local repository '{taken}' already exists.";
            }

            Directory.CreateDirectory(Root);
            var pending = Path.Combine(Root, PendingPrefix + Guid.NewGuid().ToString("N") + Suffix);
            var made = await git.CreateLocalRepositoryAsync(Root, pending, InitialBranch, ct);
            if (made is not null)
            {
                RemoveQuietly(pending);
                return $"The local repository '{name}' could not be created: {made}";
            }

            Directory.Move(pending, path);
            return null;
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>
    /// Removes the bare repository. Moved aside first, so a removal that fails part-way leaves no
    /// half-repository under the name a team could clone. The caller refuses while a team uses it.
    /// </summary>
    public async Task<bool> DeleteAsync(string name, CancellationToken ct)
    {
        await _writes.WaitAsync(ct);
        try
        {
            if (!Exists(name)) return false;

            var aside = Path.Combine(Root, DeletingPrefix + Guid.NewGuid().ToString("N"));
            Directory.Move(PathFor(name)!, aside);
            RemoveQuietly(aside);
            return true;
        }
        finally
        {
            _writes.Release();
        }
    }

    private static void RemoveQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}

/// <summary>
/// What a local repository answers where a hosting service's feature is asked of it: a sentence,
/// never an error. There is no upstream to contribute to and nobody to open a pull request with.
/// </summary>
public static class LocalRepoSentences
{
    public static string NoPullRequest(string repo) =>
        $"{repo} is a local repository on this instance, so pull requests do not apply to it: there is no "
        + "hosting service to open one on. Its button is Merge to main. Nothing was sent.";

    public static string NoContributorMode(string repo) =>
        $"{repo} is a local repository on this instance, so contributor mode does not apply to it: there is "
        + "no upstream to contribute to and no fork. Leave the upstream empty.";
}
