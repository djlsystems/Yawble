using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>One published release as the feed lists it. <paramref name="Notes"/> is the release's
/// own text, kept as text: the web shows it as characters, never as markup.</summary>
public sealed record PublishedRelease(
    string Version, bool Prerelease, DateTimeOffset? PublishedAt, string Url, string Notes);

/// <summary>
/// Where releases are read from. The Host is told the repository (<see cref="ReleaseCheck.RepositorySetting"/>)
/// by whoever starts it, so no repository name is written in the code; tests replace this interface
/// and never reach the network.
/// </summary>
public interface IReleaseFeed
{
    /// <summary>The repository it reads, or null when none was configured (a development build).</summary>
    string? Repository { get; }

    /// <summary>The published releases, drafts left out, in no particular order.</summary>
    Task<IReadOnlyList<PublishedRelease>> ListAsync(CancellationToken ct);
}

/// <summary>GitHub's public releases API, read anonymously: the repository is public and one read
/// an hour is far inside the unauthenticated limit (see <see cref="ReleaseCheck.Interval"/>).</summary>
public sealed class GitHubReleaseFeed(HttpClient http, string? repository) : IReleaseFeed
{
    public string? Repository { get; } = string.IsNullOrWhiteSpace(repository) ? null : repository.Trim();

    public async Task<IReadOnlyList<PublishedRelease>> ListAsync(CancellationToken ct)
    {
        if (Repository is null) return [];

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page=30");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("release-check", BuildVersion.Current.Version.Split('+')[0]));
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var listed = await response.Content.ReadFromJsonAsync<List<GitHubRelease>>(cancellationToken: ct) ?? [];
        return listed
            .Where(r => !r.Draft && !string.IsNullOrWhiteSpace(r.TagName))
            .Select(r => new PublishedRelease(r.TagName!.TrimStart('v'), r.Prerelease, r.PublishedAt, r.HtmlUrl ?? "", r.Body ?? ""))
            .ToList();
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("body")] string? Body);
}

/// <summary>
/// WHETHER A NEWER RELEASE IS OUT, read about every hour and kept, so the version chip can say so
/// and the person can read what changed and how to update. It never updates anything: the update
/// itself is the operator CLI's, run on the machine, because the Host never starts the container
/// engine (see AGENTS.md, Containment).
///
/// - Nothing is guessed: until a read has answered, and whenever the last one failed, the answer is
///   "not checked" with the reason, never "up to date".
/// - The channel follows the running build: a build whose own release is a pre-release, or that is
///   not in the list (a development build), is offered pre-releases too; a regular release is
///   offered regular releases only.
/// - A development build (<c>2026.10.06.1+3.abc</c>) is ahead of its base release, so only a
///   release after that base is newer.
/// - Off through the tenant setting <c>updates.check</c>, read through a delegate at every read.
/// </summary>
public sealed class ReleaseCheck(IReleaseFeed feed, Func<bool> enabled, Func<DateTimeOffset> now, ILogger<ReleaseCheck> log)
{
    /// <summary>The configuration key (or <c>HARNESS_RELEASE_REPOSITORY</c>) naming the repository,
    /// <c>owner/name</c>. The operator CLI sets it on the control container.</summary>
    public const string RepositorySetting = "Updates:Repository";
    public const string RepositoryVariable = "HARNESS_RELEASE_REPOSITORY";

    /// <summary>How often the background loop reads the feed: hourly, so a release published while a
    /// Host runs is noticed within about an hour (pre-releases come out several times a day). One read
    /// an hour stays far inside GitHub's unauthenticated rate limit of 60 requests an hour per IP
    /// address, leaving the rest for Check now.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>The most newer releases one answer lists, newest first.</summary>
    public const int MaxListed = 10;

    /// <summary>The longest release text kept per release, in characters.</summary>
    public const int MaxNotes = 8000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Checked? _last;

    private sealed record Checked(DateTimeOffset At, IReadOnlyList<PublishedRelease>? Releases, string? Failure);

    /// <summary>What the version chip reads: the last answer, never a fresh read.</summary>
    public UpdateStatus Status() => StatusFor(BuildVersion.Current.Version);

    /// <summary>Reads the feed now and keeps the answer. A failure is kept as the reason, never thrown.</summary>
    public async Task<UpdateStatus> CheckAsync(CancellationToken ct)
    {
        if (!enabled() || feed.Repository is null) return Status();

        await _gate.WaitAsync(ct);
        try
        {
            try
            {
                var releases = await feed.ListAsync(ct);
                _last = new Checked(now(), releases, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogInformation("Release check could not read {Repository}: {Reason}", feed.Repository, ex.Message);
                _last = new Checked(now(), null, ex is TaskCanceledException ? "the release list did not answer in time" : ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }

        return Status();
    }

    /// <summary>The answer for a build of version <paramref name="running"/>; <see cref="Status"/> is
    /// this for the running Host.</summary>
    public UpdateStatus StatusFor(string running)
    {
        var current = running.Split('+')[0];
        if (!enabled())
            return new UpdateStatus(current, false, false, null, $"Checking for updates is turned off in {TenantSettings.SystemTab}.", null, false, null, []);
        if (feed.Repository is null)
            return new UpdateStatus(current, true, false, null,
                "Not checked: this build was not told where its releases are published.", null, false, null, []);

        var last = _last;
        if (last is null)
            return new UpdateStatus(current, true, false, null, "Not checked yet.", null, false, null, []);
        if (last.Releases is null)
            return new UpdateStatus(current, true, false, last.At, $"Not checked: {last.Failure}", null, false, null, []);

        var mine = last.Releases.FirstOrDefault(r => r.Version == current);
        var prereleaseChannel = mine is null || mine.Prerelease;
        var newer = last.Releases
            .Where(r => (prereleaseChannel || !r.Prerelease) && Compare(r.Version, current) > 0)
            .OrderByDescending(r => r.Version, Comparer<string>.Create(Compare))
            .Take(MaxListed)
            .Select(r => r with { Notes = r.Notes.Length > MaxNotes ? r.Notes[..MaxNotes] + "…" : r.Notes })
            .ToList();

        return new UpdateStatus(
            current, true, true, last.At, null,
            newer.FirstOrDefault()?.Version, newer.Count > 0, newer.FirstOrDefault()?.Prerelease, newer);
    }

    /// <summary>Orders two dated versions (<c>2026.10.06.1</c>) part by part, numerically; a part
    /// that is not a number orders before any number.</summary>
    public static int Compare(string a, string b)
    {
        var left = a.Split('+')[0].Split('.');
        var right = b.Split('+')[0].Split('.');
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var x = i < left.Length && long.TryParse(left[i], out var l) ? l : -1;
            var y = i < right.Length && long.TryParse(right[i], out var r) ? r : -1;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }
}

/// <summary>What <c>GET /api/version/updates</c> answers.</summary>
/// <param name="Current">The running release, without the development suffix.</param>
/// <param name="Enabled">Whether checking is on (<c>updates.check</c>).</param>
/// <param name="Checked">Whether the last read answered. False is "not known", never "up to date".</param>
/// <param name="CheckedAt">When the feed was last read, answered or not.</param>
/// <param name="Detail">Why nothing is known, in a sentence; null when <paramref name="Checked"/>.</param>
/// <param name="Latest">The newest release this build is offered, or null.</param>
/// <param name="UpdateAvailable">True only when a read answered and listed a newer release.</param>
/// <param name="LatestIsPrerelease">Whether <paramref name="Latest"/> is a pre-release, so the
/// update command needs its pre-release flag.</param>
/// <param name="Newer">Every newer release offered, newest first, with its notes.</param>
public sealed record UpdateStatus(
    string Current, bool Enabled, bool Checked, DateTimeOffset? CheckedAt, string? Detail,
    string? Latest, bool UpdateAvailable, bool? LatestIsPrerelease, IReadOnlyList<PublishedRelease> Newer);

/// <summary>Reads the feed shortly after start, then every <see cref="ReleaseCheck.Interval"/>, on
/// <paramref name="clock"/> (the system's unless a test moves one by hand).</summary>
public sealed class ReleaseCheckLoop(ReleaseCheck check, TimeProvider? clock = null) : BackgroundService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstDelay, _clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await check.CheckAsync(stoppingToken);
                await Task.Delay(ReleaseCheck.Interval, _clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

public static class ReleaseCheckEndpoints
{
    public const string Route = "/api/version/updates";

    public static void Map(WebApplication app)
    {
        // A PERSON'S, unlike /api/version: the anonymous login card says which build this is and
        // nothing else, and an agent has no use for an update it cannot run.
        app.MapGet(Route, (ReleaseCheck check) => Results.Ok(check.Status()))
            .HumansOnly()
            .WithTags("Diagnostics")
            .WithSummary("Whether a newer release is out, what changed, and whether it was checked")
            .WithDescription(
                "The last answer of the release check, read about every hour: `current`, `enabled` "
                + "(the tenant setting updates.check), `checked` (false means not known, never up to "
                + "date, and `detail` says why), `checkedAt`, `latest`, `updateAvailable`, "
                + "`latestIsPrerelease` and `newer` (each newer release offered, newest first, with "
                + "`version`, `prerelease`, `publishedAt`, `url` and `notes` as text). A build whose own "
                + "release is a pre-release is offered pre-releases too. Nothing is updated: the update "
                + "is run on the machine with the operator CLI.");

        app.MapPost(Route + "/check", async (ReleaseCheck check, CancellationToken ct) => Results.Ok(await check.CheckAsync(ct)))
            .HumansOnly()
            .WithTags("Diagnostics")
            .WithSummary("Check for a newer release now")
            .WithDescription("Reads the release list now and answers as GET does. A failed read is kept as "
                + "the reason, never thrown; with checking off it reads nothing.");
    }
}
