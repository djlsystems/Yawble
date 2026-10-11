using System.Globalization;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE ONE SEAM TO THE PRESET ISOLATION MODEL: what a preset may be offered, as
/// <see cref="AgentCatalog.Allowance"/> answers it, or null for a preset the tenant does not have.
/// The per-run check asks nothing else of a preset. Program.cs passes the catalog's answer; a test
/// may pass its own.
/// </summary>
public sealed class PresetAllowedTools(Func<string, ToolAllowance?> allowance)
{
    /// <summary>The allowance of <paramref name="agent"/>, or null when there is no such preset.</summary>
    public ToolAllowance? For(string agent) => allowance(agent);
}

/// <summary>What one run's check found. A clean run is <see cref="Clean"/>; the other two write a row.</summary>
public enum ForeignToolsStatus
{
    Clean,
    Foreign,
    NotMeasured,

    /// <summary>The whole offer was read and nothing foreign was in it, but the preset declares no
    /// allowed tools, so its own tools were not judged: not verified, never clean.</summary>
    NotVerified,
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
    /// <summary>The payload's status word: <c>foreign</c>, <c>notMeasured</c> or <c>notVerified</c>.</summary>
    public string Word => Status switch
    {
        ForeignToolsStatus.Foreign => "foreign",
        ForeignToolsStatus.NotVerified => "notVerified",
        _ => "notMeasured",
    };
}

/// <summary>
/// WHAT IS FOREIGN is the preset's <see cref="ToolAllowance"/>: the platform gives a member the
/// <c>harness</c> MCP server, the servers its preset allows and the CLI's own tools its preset
/// allows, and nothing else. A tool from any other MCP server - an account connector, a home MCP
/// server, a plugin's - is foreign whatever the preset says; a CLI's own tool is foreign when the
/// preset is isolated and does not list it. A NOT VERIFIED preset lists no tools, so only its
/// servers are judged (<see cref="ToolAllowance.Allows"/>).
///
/// UNKNOWN STAYS UNKNOWN. Clean needs both halves known: the transcript wrote down the whole offer,
/// and the preset is isolated. With nothing foreign seen, an offer not written down is
/// <see cref="ForeignToolsStatus.NotMeasured"/> and a preset that is not isolated is
/// <see cref="ForeignToolsStatus.NotVerified"/> - never clean. A foreign tool that IS seen is reported
/// whatever else is unknown: evidence is not an estimate.
/// </summary>
public static class ForeignToolsJudge
{
    public static ForeignToolsFinding Judge(TranscriptToolUse use, ToolAllowance allowance)
    {
        bool Foreign(TranscriptTool tool) => !allowance.Allows(tool.Server, tool.Name);

        var called = use.Called.Where(Foreign).OrderBy(t => t.ToString(), StringComparer.Ordinal).ToList();
        var offered = use.Offered.Where(Foreign)
            .Where(t => !called.Contains(t))
            .OrderBy(t => t.ToString(), StringComparer.Ordinal)
            .ToList();

        if (called.Count > 0 || offered.Count > 0) return new(ForeignToolsStatus.Foreign, called, offered);

        if (!use.OfferedComplete) return new(ForeignToolsStatus.NotMeasured, [], [], $"its transcript records {use.Measured}");

        return allowance.State == IsolationState.Isolated
            ? new(ForeignToolsStatus.Clean, [], [])
            : new(ForeignToolsStatus.NotVerified, [], [],
                "its preset declares no allowed tools, so only its MCP servers were judged");
    }

    /// <summary>The finding as a person reads it, naming the member.</summary>
    public static string Sentence(string member, ForeignToolsFinding finding, ToolAllowance allowance)
    {
        static string Names(IEnumerable<TranscriptTool> tools) => string.Join(", ", tools.Select(t => t.ToString()));

        var unverified = allowance.State == IsolationState.Isolated
            ? ""
            : $" Its preset {allowance.Preset} is not verified: it declares no allowed tools, so only MCP servers were judged.";

        return finding.Status switch
        {
            ForeignToolsStatus.Foreign when finding.Called.Count > 0 =>
                $"{member} CALLED tools the platform did not give it: {Names(finding.Called)}."
                + (finding.Offered.Count > 0 ? $" It was also offered: {Names(finding.Offered)}." : "")
                + unverified,
            ForeignToolsStatus.Foreign =>
                $"{member} was offered tools the platform did not give it, and called none: {Names(finding.Offered)}."
                + unverified,
            ForeignToolsStatus.NotMeasured =>
                $"{member}'s run was not measured for foreign tools: {finding.Why}." + unverified,
            ForeignToolsStatus.NotVerified =>
                $"{member}'s run was offered no foreign MCP server, but its preset {allowance.Preset} is not "
                + "verified: it declares no allowed tools, so the CLI's own tools were not judged.",
            _ => $"{member}'s run offered and called only the tools the platform gives it.",
        };
    }
}

/// <summary>
/// THE PER-RUN CHECK, called by the member itself with its run's first terminal row, inside the run
/// (<c>MemberRuntime</c>'s <c>onTerminal</c>). For that row: read the transcript the agent wrote, as the
/// agent, judge its tools, and write what is not clean - an <c>agent.foreignTools</c> row on the
/// team's log inside the run's workflow, so it lands on the member's card, and for a foreign tool a
/// <c>tenant_events</c> row too. Only a preset its <see cref="ToolAllowance"/> says is
/// <see cref="ToolAllowance.Checked"/> is judged: a Concierge preset and one that runs no language
/// model are never flagged, and a row that is not a team member's run is not checked at all. Never throws except on the token: a check that cannot
/// read is not measured, and costs nothing else.
/// </summary>
public sealed class ForeignToolsCheck(
    TeamRegistry teams,
    AgentCatalog catalog,
    PresetAllowedTools allowed,
    WorkerReads reads,
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

        if (allowed.For(member.Agent) is not { Checked: true } allowance
            || catalog.Definition(member.Agent) is not { } definition)
        {
            return null;
        }

        var path = Text(root, PayloadFields.AgentTranscript);

        // A preset that names a transcript and a run that left none: the run did not get far
        // enough to write one (a launch that failed, a run stopped at once), and its own row says
        // so. A preset that names NO transcript can never be measured, and each of its runs says so.
        if (path is null && definition.LiveView is not null) return null;
        var format = Text(root, PayloadFields.AgentTranscriptFormat) ?? LiveView.ClaudeJsonl;

        ForeignToolsFinding finding;
        string measured;
        if (path is null)
        {
            measured = "nothing: the run recorded no transcript";
            finding = new(ForeignToolsStatus.NotMeasured, [], [], "the run recorded no agent transcript");
        }
        else
        {
            // Read as the agent on a worker, unredacted: the text stays in control's memory, and only
            // the tools it names are written.
            var read = await reads.ReadAsync(path, [], null, ct);

            if (read is not { Kind: FileReadKind.Ok, Text: { } text })
            {
                measured = "nothing: the transcript could not be read";
                finding = new(ForeignToolsStatus.NotMeasured, [], [], read.Kind switch
                {
                    FileReadKind.Gone => "its transcript is gone",
                    FileReadKind.NoWorker => "no worker is connected to read its transcript",
                    _ => $"its transcript could not be read ({read.Why})",
                });
            }
            else
            {
                var use = TranscriptTools.Extract(format, text, await BesideAsync(path, format, ct));
                measured = use.Measured;
                finding = ForeignToolsJudge.Judge(use, allowance);
            }
        }

        if (finding.Status == ForeignToolsStatus.Clean) return finding;

        var sentence = ForeignToolsJudge.Sentence(id.Name, finding, allowance);
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
                [PayloadFields.PresetVerified] = allowance.State == IsolationState.Isolated,
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
                    verified = allowance.State == IsolationState.Isolated,
                    format,
                    run = terminal.Seq.ToString(CultureInfo.InvariantCulture),
                    called,
                    offered,
                }),
                ct);
        }

        return finding;
    }

    /// <summary>
    /// The files the format keeps beside its transcript that the extractor reads too (Grok's tool
    /// definitions and MCP events), by name, read as the agent. One that is missing or unreadable is
    /// left out, and the extractor then does not call the offer complete.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> BesideAsync(string path, string format, CancellationToken ct)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Path.GetDirectoryName(path) is not { Length: > 0 } folder) return found;

        foreach (var name in TranscriptTools.Beside(format))
        {
            if ((await reads.ReadAsync(Path.Combine(folder, name), [], null, ct)) is { Kind: FileReadKind.Ok, Text: { } text }) found[name] = text;
        }

        return found;
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
