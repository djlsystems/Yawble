using System.Text.Json;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// THE OPERATOR'S RESCAN: a plugin installed with the operator CLI is registered with no restart and
/// no person's API key.
///
/// <para>
/// The CLI writes a nonce to <c>&lt;plugins&gt;/.rescan</c> through the container engine. Once a
/// second this service looks for that file; when it holds a nonce not yet answered, it calls
/// <see cref="PluginCatalog.Rescan"/> - the same call <c>POST /api/plugins/rescan</c> makes - and
/// writes what the Host now holds to <c>&lt;plugins&gt;/.rescan-report.json</c>, carrying that nonce,
/// so the CLI can print the Host's own verdict and knows it is not an older one.
/// </para>
///
/// <para>
/// WHO CAN ASK: whoever can write in the plugins directory, which is <c>harness:agent 0750</c>
/// (<c>scripts/prepare-volume.sh</c>). The Host and root can; an agent cannot. So the file is the
/// credential, and nothing an agent does can make the Host rescan.
/// </para>
///
/// <para>
/// POLLING, NOT A FileSystemWatcher, for <see cref="FolderWatch"/>'s reason: the volume may be a
/// host folder, and inotify does not see every write made from outside the container. One stat a
/// second is the whole cost.
/// </para>
/// </summary>
public sealed class PluginRescanRequests(
    PluginCatalog plugins, TeamRegistry teams, TenantLogging audit, ILogger<PluginRescanRequests> logger)
    : BackgroundService
{
    public const string RequestFile = ".rescan";

    public const string ReportFile = ".rescan-report.json";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private string? _answered;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await AnswerAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "A plugin rescan request could not be answered.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Answers a pending request, if there is one. Returns whether it did.</summary>
    public async Task<bool> AnswerAsync(CancellationToken ct)
    {
        var request = Path.Combine(plugins.Root, RequestFile);

        if (!File.Exists(request)) return false;

        var nonce = (await File.ReadAllTextAsync(request, ct)).Trim();

        if (nonce.Length == 0 || nonce == _answered) return false;

        var scan = plugins.Rescan();
        PluginEndpoints.Report(scan, Console.Out);

        await audit.WriteAsAsync(
            null, null, TenantActions.PluginsRescanned, null, null,
            new
            {
                by = "operator",
                installed = scan.Plugins.Select(p => $"{p.Manifest.Id}@{p.Manifest.Version}").ToArray(),
                refused = scan.Refused.Select(r => r.Id).ToArray(),
            },
            ct);

        var report = new
        {
            request = nonce,
            at = DateTimeOffset.UtcNow,
            plugins = scan.Plugins.Select(p => new { id = p.Manifest.Id, version = p.Manifest.Version, name = p.Manifest.Name }),
            refused = scan.Refused.Select(r => new { id = r.Id, reason = r.Reason }),
            // WHO IS HIRED ON EACH PLUGIN, so `plugin remove` can refuse while one is.
            members = teams.All()
                .SelectMany(team => team.Containers.Select(member => (team, member)))
                .Where(pair => MemberRef.IsPlugin(pair.member.Agent))
                .Select(pair => new
                {
                    plugin = MemberRef.IsPlugin(pair.member.Agent, out var id) ? id : null,
                    team = pair.team.Name,
                    member = pair.member.Name,
                }),
        };

        // A .tmp then a rename, so the CLI never reads half a report. The Host's alone (0600): it
        // names members of every team, and agent can read the plugins directory. The CLI reads it
        // as root.
        var target = Path.Combine(plugins.Root, ReportFile);
        var temporary = target + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, Json) + "\n", ct);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, target, overwrite: true);

        _answered = nonce;

        try
        {
            File.Delete(request);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The nonce is remembered, so a request that stays is not answered twice.
        }

        return true;
    }
}
