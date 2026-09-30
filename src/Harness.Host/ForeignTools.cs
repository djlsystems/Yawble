using System.Globalization;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE ONE SEAM TO THE PRESET ISOLATION MODEL: the CLI's own tools a preset allows beyond
/// <c>harness</c>, by name, or null when the preset declares none and so is NOT VERIFIED. The per-run
/// check asks nothing else of a preset. Program.cs passes the function that answers it.
/// </summary>
public sealed class PresetAllowedTools(Func<string, IReadOnlyCollection<string>?> allowed)
{
    /// <summary>The allowed local tools of <paramref name="agent"/>, or null when it is not verified.</summary>
    public IReadOnlyCollection<string>? For(string agent) => allowed(agent);
}

/// <summary>What one run's check found. A clean run is <see cref="Clean"/>; the other two write a row.</summary>
public enum ForeignToolsStatus
{
    Clean,
    Foreign,
    NotMeasured,
}

/// <summary>
/// One run's finding: the foreign tools it called, the foreign tools and servers it was offered and
/// did not call, and, when it is not measured, why.
/// </summary>
public sealed record ForeignToolsFinding(
    ForeignToolsStatus Status,
    IReadOnlyList<TranscriptTool> Called,
    IReadOnlyList<TranscriptTool> Offered,
    string? Why = null)
{
    /// <summary>The payload's status word: <c>foreign</c> or <c>notMeasured</c>.</summary>
    public string Word => Status == ForeignToolsStatus.Foreign ? "foreign" : "notMeasured";
}

/// <summary>
/// WHAT IS FOREIGN. The platform gives a member the <c>harness</c> MCP server and the CLI's own
/// tools its preset allows, and nothing else. So a tool from any other MCP server - an account
/// connector, a home MCP server, a plugin's - is foreign whatever the preset says, and a CLI's own
/// tool is foreign when the preset declares an allowed list without it.
///
/// UNKNOWN STAYS UNKNOWN. Clean needs both halves known: the transcript wrote down the whole offer,
/// and the preset declares its allowed list. Anything less, with nothing foreign seen, is
/// <see cref="ForeignToolsStatus.NotMeasured"/> - never clean. A foreign tool that IS seen is reported
/// whatever else is unknown: evidence is not an estimate.
/// </summary>
public static class ForeignToolsJudge
{
    /// <summary>The MCP server the platform gives every member.</summary>
    public const string Harness = "harness";

    public static ForeignToolsFinding Judge(TranscriptToolUse use, IReadOnlyCollection<string>? allowed)
    {
        bool Foreign(TranscriptTool tool) => tool.Server is { } server
            ? !string.Equals(server, Harness, StringComparison.OrdinalIgnoreCase)
            : allowed is not null && !allowed.Contains(tool.Name, StringComparer.Ordinal);

        var called = use.Called.Where(Foreign).OrderBy(t => t.ToString(), StringComparer.Ordinal).ToList();
        var offered = use.Offered.Where(Foreign)
            .Where(t => !called.Contains(t))
            .OrderBy(t => t.ToString(), StringComparer.Ordinal)
            .ToList();

        if (called.Count > 0 || offered.Count > 0) return new(ForeignToolsStatus.Foreign, called, offered);

        if (!use.OfferedComplete) return new(ForeignToolsStatus.NotMeasured, [], [], $"its transcript records {use.Measured}");

        return allowed is null
            ? new(ForeignToolsStatus.NotMeasured, [], [], "its preset declares no allowed tools, so it is not verified")
            : new(ForeignToolsStatus.Clean, [], []);
    }

    /// <summary>The finding as a person reads it, naming the member.</summary>
    public static string Sentence(string member, ForeignToolsFinding finding)
    {
        static string Names(IEnumerable<TranscriptTool> tools) => string.Join(", ", tools.Select(t => t.ToString()));

        return finding.Status switch
        {
            ForeignToolsStatus.Foreign when finding.Called.Count > 0 =>
                $"{member} CALLED tools the platform did not give it: {Names(finding.Called)}."
                + (finding.Offered.Count > 0 ? $" It was also offered: {Names(finding.Offered)}." : ""),
            ForeignToolsStatus.Foreign =>
                $"{member} was offered tools the platform did not give it, and called none: {Names(finding.Offered)}.",
            ForeignToolsStatus.NotMeasured =>
                $"{member}'s run was not measured for foreign tools: {finding.Why}.",
            _ => $"{member}'s run offered and called only the tools the platform gives it.",
        };
    }
}

/// <summary>
/// THE PER-RUN CHECK. For a member run's terminal row: read the transcript the agent wrote, as the
/// agent, judge its tools, and write what is not clean - an <c>agent.foreignTools</c> row on the
/// team's log inside the run's workflow, so it lands on the member's card, and for a foreign tool a
/// <c>tenant_events</c> row too. A row that is not a team member's run is not checked: the Concierge
/// runs no team log rows and is never flagged. Never throws except on the token: a check that cannot
/// read is not measured, and costs nothing else.
/// </summary>
public sealed class ForeignToolsCheck(
    TeamRegistry teams,
    AgentCatalog catalog,
    PresetAllowedTools allowed,
    AgentLaunchUser runAs,
    IMessageLog log,
    ITenantLog tenantLog)
{
    public async Task<ForeignToolsFinding?> CheckAsync(Message terminal, CancellationToken ct)
    {
        if (terminal.Type is not (MessageTypes.Completed or MessageTypes.Failed)) return null;
        if (!ContainerId.TryParse(terminal.Source, out var id)) return null;

        using var payload = JsonDocument.Parse(terminal.Payload);
        var root = payload.RootElement;

        // One run can close several deliveries; only its first terminal row carries the run.
        if (root.TryGetProperty(PayloadFields.UsageCountedOn, out var counted) && counted.ValueKind != JsonValueKind.Null) return null;

        PersistedMember member;
        try
        {
            member = await teams.MemberAsync(id.Team, id.Name, ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (catalog.Definition(member.Agent) is not { Launch.LanguageModel: true } definition) return null;

        var path = Text(root, PayloadFields.AgentTranscript);
        var format = Text(root, PayloadFields.AgentTranscriptFormat) ?? LiveView.ClaudeJsonl;
        var permitted = allowed.For(definition.Name);

        ForeignToolsFinding finding;
        string measured;
        if (path is null)
        {
            measured = "nothing: the run recorded no transcript";
            finding = new(ForeignToolsStatus.NotMeasured, [], [], "the run recorded no agent transcript");
        }
        else
        {
            AgentFiles.Read read;
            try
            {
                read = await AgentFiles.ReadAllAsync(path, runAs, ct);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                read = new AgentFiles.Read(null, exception.Message);
            }

            if (read.Text is not { } text)
            {
                measured = "nothing: the transcript could not be read";
                finding = new(ForeignToolsStatus.NotMeasured, [], [],
                    read.Unreadable is { } why ? $"its transcript could not be read ({why})" : "its transcript is gone");
            }
            else
            {
                var use = TranscriptTools.Extract(format, text);
                measured = use.Measured;
                finding = ForeignToolsJudge.Judge(use, permitted);
            }
        }

        if (finding.Status == ForeignToolsStatus.Clean) return finding;

        var sentence = ForeignToolsJudge.Sentence(id.Name, finding);
        var called = finding.Called.Select(t => t.ToString()).ToList();
        var offered = finding.Offered.Select(t => t.ToString()).ToList();

        await log.AppendAsync(new NewMessage(
            MessageTypes.AgentForeignTools,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                [PayloadFields.ForeignToolsStatus] = finding.Word,
                [PayloadFields.ForeignCalled] = called,
                [PayloadFields.ForeignOffered] = offered,
                [PayloadFields.Agent] = definition.Name,
                [PayloadFields.AgentTranscriptFormat] = format,
                [PayloadFields.Measured] = measured,
                [PayloadFields.Run] = terminal.Seq,
                [PayloadFields.Text] = sentence,
            }),
            terminal.Source,
            terminal.Seq), ct);

        if (finding.Status == ForeignToolsStatus.Foreign)
        {
            await tenantLog.WriteAsync(
                actorId: null,
                actorEmail: null,
                action: TenantActions.AgentForeignTools,
                subject: id.ToString(),
                subjectName: member.Label ?? id.Name,
                detail: JsonSerializer.Serialize(new
                {
                    team = id.Team,
                    member = id.Name,
                    agent = definition.Name,
                    format,
                    run = terminal.Seq.ToString(CultureInfo.InvariantCulture),
                    called,
                    offered,
                }),
                ct);
        }

        return finding;
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// Runs <see cref="ForeignToolsCheck"/> on every terminal row appended while the Host runs: a tail
/// of the log, as <c>KanbanChangePush</c> is, because runs end in more than one place and the log
/// sees them all.
/// </summary>
internal sealed class ForeignToolsWatch(
    IMessageLog log, ForeignToolsCheck check, ILogger<ForeignToolsWatch> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var last = await log.HighestSeqAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);

                var rows = await log.ReadRangeAsync(last, 500, stoppingToken);
                if (rows.Count == 0) continue;

                last = rows[^1].Seq;

                foreach (var row in rows)
                {
                    if (row.Type is not (MessageTypes.Completed or MessageTypes.Failed)) continue;

                    try
                    {
                        await check.CheckAsync(row, stoppingToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(exception, "The foreign tools check of run {Seq} failed.", row.Seq);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "The foreign tools watch failed a pass; trying again on the next one.");
            }
        }
    }
}
