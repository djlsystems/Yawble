using System.Text.Json;

namespace Harness.Host.Solutions;

/// <summary>
/// THE OPERATOR CLI'S <c>solution install</c>, answered by the running Host through the seam
/// <c>plugin install --from-instance</c> uses (<see cref="PluginRescanRequests"/>): the CLI writes
/// <c>{ request, action, folder, team, teamName, agent, settings, connections, documents }</c> to
/// <c>&lt;plugins&gt;/.solution</c>, and this service runs <see cref="SolutionInstaller"/> - the
/// code the routes run - and answers <c>{ request, status, body }</c> in
/// <c>&lt;plugins&gt;/.solution-report.json</c>, <c>body</c> being exactly the matching route's.
///
/// <para>
/// WHO CAN ASK: whoever can write in the plugins directory (<c>harness:agent 0750</c>): the Host and
/// root, never an agent - so the file is the credential, and an agent never installs. The CLI
/// stages a package from the operator's computer under <c>&lt;plugins&gt;/.solutions/</c>, a
/// dot-named folder the plugin catalog passes over.
/// </para>
/// </summary>
public sealed class SolutionRequests(SolutionInstaller installer, PluginCatalog plugins, ILogger<SolutionRequests> logger)
    : BackgroundService
{
    public const string RequestFile = ".solution";

    public const string ReportFile = ".solution-report.json";

    /// <summary>Where the CLI stages packages and documents, inside the plugins directory.</summary>
    public const string StagingFolder = ".solutions";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
                logger.LogWarning(exception, "A solution request could not be answered.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Answers a pending request, if there is one. Returns whether it did.</summary>
    public async Task<bool> AnswerAsync(CancellationToken ct)
    {
        var path = Path.Combine(plugins.Root, RequestFile);
        if (!File.Exists(path)) return false;

        SolutionRequest? request;

        try
        {
            request = JsonSerializer.Deserialize<SolutionRequest>(await File.ReadAllTextAsync(path, ct), Json);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is not { Request: { Length: > 0 } nonce } || nonce == _answered) return false;

        var answers = new SolutionAnswers(request.Settings, request.Connections, request.Documents);

        var outcome = request.Action switch
        {
            "preview" => await installer.PreviewAsync(request.Folder, request.Team ?? request.TeamName, ct),
            "install" => await installer.InstallAsync(
                new SolutionInstallRequest(request.Folder, request.TeamName ?? request.Team, request.Agent, request.LocalRepository, answers),
                SolutionActor.Operator, ct),
            "update" => await installer.UpdateAsync(new SolutionUpdateRequest(request.Folder, request.Team, answers), SolutionActor.Operator, ct),
            _ => new SolutionOutcome(400, new { error = $"'{request.Action}' is not a solution request; ask preview, install or update." }),
        };

        var target = Path.Combine(plugins.Root, ReportFile);
        var temporary = target + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { request = nonce, status = outcome.Status, body = outcome.Body }, Json) + "\n", ct);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, target, overwrite: true);

        _answered = nonce;

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The nonce is remembered, so a request that stays is not answered twice.
        }

        return true;
    }

    private sealed record SolutionRequest(
        string? Request,
        string? Action,
        string? Folder,
        string? Team,
        string? TeamName,
        string? Agent,
        bool? LocalRepository,
        Dictionary<string, Dictionary<string, JsonElement>>? Settings,
        Dictionary<string, Dictionary<string, string>>? Connections,
        Dictionary<string, List<string>>? Documents);
}
