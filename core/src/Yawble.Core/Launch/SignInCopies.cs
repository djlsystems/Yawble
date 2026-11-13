using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Yawble.Core.Agents;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;

namespace Yawble.Core.Launch;

/// <summary>
/// The sign-in files a CLI gets as a COPY (<see cref="SignInAccess.CopyIn"/>: Codex's auth.json). Before the run the
/// host's file is copied into the member's own folder; after it, a copy the CLI refreshed (a later "last_refresh") is
/// written back over the host's, so the person stays signed in when the vendor rotates refresh tokens.
/// <para>
/// Two runs must never refresh the same token: the second would present a refresh token the first already used, and
/// the vendor may then revoke the whole sign-in. Codex refreshes when its copy is older than its refresh interval
/// (8 days), so a run that starts from a copy older than <see cref="DueAfter"/> holds a lease for that file until it
/// ends; other runs of the same CLI wait for it, then start from the refreshed copy. Fresh copies run in parallel.
/// </para>
/// </summary>
public sealed class SignInCopy : IAsyncDisposable
{
    /// <summary>A copy older than this may be refreshed by the CLI during the run (Codex refreshes at 8 days).</summary>
    public static readonly TimeSpan DueAfter = TimeSpan.FromDays(7);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_leases = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<(string Host, string Copy)> _files;
    private readonly SemaphoreSlim? _lease;
    private readonly Action<string>? _log;

    private SignInCopy(List<(string, string)> files, SemaphoreSlim? lease, Action<string>? log)
    {
        _files = files;
        _lease = lease;
        _log = log;
    }

    /// <summary>Where each CLI's copy goes in the member's folders.</summary>
    public static string CopyPath(RunLayout l, CliSignInFile file) => l.Cli switch
    {
        AgentCli.Codex => Path.Combine(AgentPresets.CodexHome(l), Path.GetFileName(file.Path)),
        _ => throw new ArgumentException($"{l.Cli.Id()} has no copied sign-in"),
    };

    /// <summary>Copies the host's sign-in files in (waiting for a lease when one is due for a refresh).</summary>
    public static async Task<SignInCopy> PrepareAsync(RunLayout l, TimeProvider clock, Action<string>? log, CancellationToken ct)
    {
        var files = l.CliInstall.SignInCopies.Select(f => (f.Path, CopyPath(l, f))).ToList();
        SemaphoreSlim? lease = null;
        if (files.Count > 0 && files.Any(f => IsDue(f.Path, clock)))
        {
            lease = s_leases.GetOrAdd(files[0].Path, _ => new SemaphoreSlim(1, 1));
            if (!lease.Wait(0))
            {
                log?.Invoke($"{l.Cli.Id()}: another run is refreshing the sign-in; waiting for it");
                await lease.WaitAsync(ct);
            }
        }
        try
        {
            foreach (var (host, copy) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(host, copy, overwrite: true);
            }
        }
        catch
        {
            lease?.Release();
            throw;
        }
        return new SignInCopy(files, lease, log);
    }

    /// <summary>After the run: a copy the CLI refreshed replaces the host's (atomically), then the lease is released.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var (host, copy) in _files)
            {
                try
                {
                    if (!File.Exists(copy)) continue;
                    var mine = LastRefresh(copy);
                    var theirs = LastRefresh(host);
                    if (mine is null || (theirs is not null && mine <= theirs)) continue;
                    var temp = host + ".yawble-" + Guid.NewGuid().ToString("N")[..8];
                    await File.WriteAllBytesAsync(temp, await File.ReadAllBytesAsync(copy));
                    File.Move(temp, host, overwrite: true);
                    _log?.Invoke($"the run refreshed the sign-in; copied it back to {host}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
                {
                    _log?.Invoke($"the refreshed sign-in could not be copied back to {host}: {e.Message}");
                }
            }
        }
        finally
        {
            _lease?.Release();
        }
    }

    private static bool IsDue(string file, TimeProvider clock) =>
        LastRefresh(file) is not { } at || clock.GetUtcNow() - at > DueAfter;

    /// <summary>Codex's auth.json "last_refresh" (ISO 8601); null when absent or unreadable.</summary>
    public static DateTimeOffset? LastRefresh(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            return doc.RootElement.TryGetProperty("last_refresh", out var v) && v.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
