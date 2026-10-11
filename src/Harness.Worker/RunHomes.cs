using System.Diagnostics;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// A HOME OF ONE RUN'S OWN, for a run whose preset signs in with an issued credential:
/// <c>&lt;TMPDIR&gt;/home-&lt;random&gt;</c>, owner-only, made as the agent and removed when the run
/// ends. The CLI finds no login in it - only the credential in its environment - so an issued run
/// never reads the shared agent home's sign-in files, and anything it writes for itself goes with it.
///
/// <para>
/// THE WORKER'S: it makes, keeps and removes the home of a run it launches, and of a launch check.
/// Control's tool listing reaches it through <c>RunHome</c>. The removal itself is the one this is
/// composed with (<paramref name="removeInside"/>: control's <c>FolderRemoval</c>, whose agent pass
/// is on the run boundary's allow-list until it moves to a worker).
/// </para>
///
/// <para>
/// MADE AS THE AGENT, as the member's TMPDIR is (<see cref="MemberTemp"/>): through the same
/// <see cref="AgentLaunchUser.Prefix"/> every agent child gets when the Host switches users, so the
/// agent owns it; by the Host otherwise, whose user is then the agent's. The one file the launch
/// puts in it - grok's <c>harness</c> MCP entry, which grok reads only from its home - is written by
/// the Host to its own launch directory and COPIED in as the agent.
/// </para>
///
/// <para>
/// REMOVED THROUGH <c>FolderRemoval</c>, because the agent writes inside it: the Host's pass,
/// then the agent's, links removed and never followed, however the run or the making of its home
/// ends. A home a crash left behind is removed before the member's next issued run makes its own; one
/// a launch check or a listing left in the folder they share, at Host start and before the next one.
/// </para>
/// </summary>
/// <param name="removeInside">Removes a folder (the second argument) strictly inside its parent (the
/// first), the Host's pass and then the agent's, links never followed.</param>
public sealed class RunHomes(AgentLaunchUser? runAs, Func<string, string, Task> removeInside)
{
    /// <summary>The start of every run home's name in its parent.</summary>
    public const string Prefix = "home-";

    /// <summary>Where a run home's CLIs keep their caches: beside the homes, kept across runs and
    /// listings. Copilot unpacks about 186 MB into a fresh HOME's cache otherwise, every time.</summary>
    public const string CacheFolder = ".cache";

    /// <summary>Where a run home's transcripts are kept once the run ends: beside the homes, so the
    /// finished run's transcript is still there to read after its home is removed.</summary>
    public const string TranscriptsFolder = "transcripts";

    /// <summary>How long a kept transcript is kept. Older ones are removed whenever the member
    /// keeps another.</summary>
    public static readonly TimeSpan TranscriptsKept = TimeSpan.FromDays(14);

    /// <summary>How old a home in a parent several launches share must be before a sweep removes it,
    /// when no live launch of this Host owns it. Longer than any listing or launch check takes.</summary>
    public static readonly TimeSpan SharedLeftoverAge = TimeSpan.FromMinutes(10);

    /// <summary>The homes this Host has made and not yet removed: a sweep never touches one.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Live = new(StringComparer.Ordinal);

    /// <summary>The cache folder beside <paramref name="home"/>, for its XDG_CACHE_HOME.</summary>
    public static string CacheBeside(string home) => Path.Combine(Path.GetDirectoryName(home)!, CacheFolder);

    /// <summary>The grok config file inside a home, as grok looks for it.</summary>
    public static string GrokConfigIn(string home) => Path.Combine(home, ".grok", "config.toml");

    /// <summary>
    /// A new home in <paramref name="parent"/>, with <paramref name="grokConfig"/> (a file the Host
    /// wrote and shared with the agent's group) copied to <c>.grok/config.toml</c> when given, and
    /// the <see cref="CacheFolder"/> beside it. When <paramref name="memberFolder"/> - the parent is
    /// one member's TMPDIR - every home an earlier run left there is removed first; in a parent
    /// several launches share (the launch check's and the listings'), only those older than
    /// <see cref="SharedLeftoverAge"/> that no live launch owns. Null when it could not be made, and
    /// then nothing is left: a home that was begun is removed however its making ends, cancelled
    /// or failed.
    /// </summary>
    public async Task<string?> CreateAsync(string parent, string? grokConfig, CancellationToken ct, bool memberFolder = true)
    {
        if (memberFolder) await SweepAsync(parent, ct);
        else await SweepSharedAsync(parent, ct);

        var home = Path.Combine(parent, Prefix + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..12]);
        Live[home] = 0;

        var made = false;
        try
        {
            made = await MakeAsync(parent, home, grokConfig, ct);
            return made ? home : null;
        }
        finally
        {
            if (!made) await RemoveAsync(home);
        }
    }

    private async Task<bool> MakeAsync(string parent, string home, string? grokConfig, CancellationToken ct)
    {
        if (runAs is { Switches: true } agent)
        {
            if (SystemCommand.Find("mkdir") is not { } mkdir || SystemCommand.Find("cp") is not { } cp) return false;
            if (!await RunAsync([.. agent.Prefix, mkdir, "-m", "700", "--", home], ct)) return false;
            if (!await RunAsync([.. agent.Prefix, mkdir, "-p", "-m", "700", "--", Path.Combine(parent, CacheFolder)], ct)) return false;

            if (grokConfig is not null)
            {
                var grok = Path.GetDirectoryName(GrokConfigIn(home))!;
                if (!await RunAsync([.. agent.Prefix, mkdir, "-m", "700", "--", grok], ct)) return false;
                if (!await RunAsync([.. agent.Prefix, cp, "--", grokConfig, GrokConfigIn(home)], ct)) return false;
            }

            return true;
        }

        try
        {
            const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(home);
                Directory.CreateDirectory(Path.Combine(parent, CacheFolder));
            }
            else
            {
                Directory.CreateDirectory(home, OwnerOnly);
                Directory.CreateDirectory(Path.Combine(parent, CacheFolder), OwnerOnly);
            }

            ct.ThrowIfCancellationRequested();

            if (grokConfig is not null)
            {
                var grok = Path.GetDirectoryName(GrokConfigIn(home))!;
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(grok);
                else Directory.CreateDirectory(grok, OwnerOnly);
                File.Copy(grokConfig, GrokConfigIn(home));
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// <paramref name="transcript"/> MOVED OUT of <paramref name="home"/> before the home is removed,
    /// to <c>&lt;memberTemp&gt;/transcripts/&lt;home's name&gt;-&lt;file name&gt;</c>, and the moved one
    /// returned, so the run's terminal row names a file that outlives the home. As the agent, as the
    /// home was made: a rename, so a link is moved as a link and never followed. A transcript outside
    /// the home, or one that cannot be moved, is returned as it was. Never throws.
    /// </summary>
    public async Task<AgentTranscript?> KeepTranscriptAsync(AgentTranscript? transcript, string home, string memberTemp)
    {
        if (transcript is null) return null;

        string source;
        try
        {
            source = Path.GetFullPath(transcript.Path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return transcript;
        }

        if (!source.StartsWith(Path.GetFullPath(home) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return transcript;

        var folder = Path.Combine(memberTemp, TranscriptsFolder);
        var kept = Path.Combine(folder, Path.GetFileName(home) + "-" + Path.GetFileName(source));

        if (runAs is { Switches: true } agent)
        {
            if (SystemCommand.Find("mkdir") is not { } mkdir || SystemCommand.Find("mv") is not { } mv) return transcript;
            if (!await RunAsync([.. agent.Prefix, mkdir, "-p", "-m", "700", "--", folder], CancellationToken.None)) return transcript;
            if (!await RunAsync([.. agent.Prefix, mv, "-n", "-T", "--", source, kept], CancellationToken.None)) return transcript;

            // Files only, as the agent, older than the bound: never a folder, never through a link.
            if (SystemCommand.Find("find") is { } find)
            {
                await RunAsync(
                    [.. agent.Prefix, find, folder, "-mindepth", "1", "-maxdepth", "1", "-type", "f",
                        "-mmin", "+" + (long)TranscriptsKept.TotalMinutes, "-delete"],
                    CancellationToken.None);
            }

            return transcript with { Path = kept };
        }

        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
            else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            File.Move(source, kept);
            PruneKept(folder, DateTime.UtcNow - TranscriptsKept);
            return transcript with { Path = kept };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return transcript;
        }
    }

    /// <summary>Removes the transcripts in <paramref name="folder"/> last written before
    /// <paramref name="before"/>: files only, a link removed as a link.</summary>
    private static void PruneKept(string folder, DateTime before)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (File.GetLastWriteTimeUtc(file) < before) File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next kept transcript prunes again.
        }
    }

    /// <summary>Removes <paramref name="home"/> and everything the run wrote in it. Never throws.</summary>
    public async Task RemoveAsync(string home)
    {
        try
        {
            await removeInside(Path.GetDirectoryName(home)!, home);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Swept later: the member's next issued run, or the next shared sweep.
        }
        finally
        {
            Live.TryRemove(home, out _);
        }
    }

    /// <summary>
    /// Removes the homes in <paramref name="parent"/>, a folder several launches share, that no live
    /// launch of this Host owns and that nothing has written in for <see cref="SharedLeftoverAge"/>:
    /// those a stopped Host or a lost removal left. Run at Host start and before each new one is made.
    /// </summary>
    public async Task SweepSharedAsync(string parent, CancellationToken ct, DateTime? now = null)
    {
        var before = (now ?? DateTime.UtcNow) - SharedLeftoverAge;

        IEnumerable<string> stale;
        try
        {
            stale = Directory.EnumerateDirectories(parent, Prefix + "*")
                .Where(home => !Live.ContainsKey(home) && Directory.GetLastWriteTimeUtc(home) < before)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var home in stale)
        {
            ct.ThrowIfCancellationRequested();
            await RemoveAsync(home);
        }
    }

    /// <summary>Removes every run home in <paramref name="parent"/>: a member runs one run at a
    /// time, so any home there is one a stopped Host left.</summary>
    private async Task SweepAsync(string parent, CancellationToken ct)
    {
        IEnumerable<string> stale;
        try
        {
            stale = Directory.EnumerateDirectories(parent, Prefix + "*").Where(home => !Live.ContainsKey(home)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var home in stale)
        {
            ct.ThrowIfCancellationRequested();
            await RemoveAsync(home);
        }
    }

    private static async Task<bool> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
    {
        var start = new ProcessStartInfo(command[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);
            if (process is null) return false;

            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(output, error);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
