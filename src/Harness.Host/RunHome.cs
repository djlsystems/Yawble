using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// A HOME OF ONE RUN'S OWN, for a run whose preset signs in with an issued credential:
/// <c>&lt;TMPDIR&gt;/home-&lt;random&gt;</c>, owner-only, made as the agent and removed when the run
/// ends. The CLI finds no login in it - only the credential in its environment - so an issued run
/// never reads the shared agent home's sign-in files, and anything it writes for itself goes with it.
///
/// <para>
/// THE WORKER MAKES AND REMOVES IT (<see cref="RunHomes"/>), for the runs it launches, the launch
/// check and a tool listing. This is control's way to the same homes - the sweep at Host start -
/// removed through <see cref="FolderRemoval"/>, because the agent writes inside it: the Host's pass,
/// then the agent's, links removed and never followed.
/// </para>
/// </summary>
public static class RunHome
{
    /// <summary>The start of every run home's name in its parent.</summary>
    public const string Prefix = RunHomes.Prefix;

    /// <summary>Where a run home's CLIs keep their caches: beside the homes, kept across runs and listings.</summary>
    public const string CacheFolder = RunHomes.CacheFolder;

    /// <summary>Where a run home's transcripts are kept once the run ends.</summary>
    public const string TranscriptsFolder = RunHomes.TranscriptsFolder;

    /// <summary>How long a kept transcript is kept.</summary>
    public static readonly TimeSpan TranscriptsKept = RunHomes.TranscriptsKept;

    /// <summary>How old a home in a parent several launches share must be before a sweep removes it.</summary>
    public static readonly TimeSpan SharedLeftoverAge = RunHomes.SharedLeftoverAge;

    /// <summary>The homes of launches as <paramref name="runAs"/>, removed through <see cref="FolderRemoval"/>.</summary>
    public static RunHomes Homes(AgentLaunchUser? runAs) =>
        new(runAs, (parent, home) => new FolderRemoval(runAs).RemoveInsideAsync(parent, home, CancellationToken.None));

    /// <summary>The cache folder beside <paramref name="home"/>, for its XDG_CACHE_HOME.</summary>
    public static string CacheBeside(string home) => RunHomes.CacheBeside(home);

    /// <summary>The grok config file inside a home, as grok looks for it.</summary>
    public static string GrokConfigIn(string home) => RunHomes.GrokConfigIn(home);

    /// <summary>Removes <paramref name="home"/> and everything the run wrote in it. Never throws.</summary>
    public static Task RemoveAsync(string home, AgentLaunchUser? runAs) => Homes(runAs).RemoveAsync(home);

    /// <summary>Removes the old homes no live launch owns in a shared <paramref name="parent"/>; see <see cref="RunHomes.SweepSharedAsync"/>.</summary>
    public static Task SweepSharedAsync(string parent, AgentLaunchUser? runAs, CancellationToken ct, DateTime? now = null) =>
        Homes(runAs).SweepSharedAsync(parent, ct, now);
}
