using System.ComponentModel;
using System.Text.Json;
using System.Text;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// <c>GET /api/teams/{team}/members/{member}/live</c>: what a running member's agent is
/// doing, one readable line per transcript event, as it happens. <c>.../runs</c> lists the
/// member's finished runs that recorded a transcript, and <c>.../runs/{seq}/transcript</c> renders
/// one whole. None of them writes to the database or the message log.
/// </summary>
public static class LiveViewEndpoints
{
    public static void Map(WebApplication app)
    {
        // HUMANS ONLY. Raw agent output can hold whatever the agent printed or read, file contents
        // included, and no agent needs to watch another.
        app.MapGet("/api/teams/{team}/members/{member}/live", WatchAsync)
            .WithTags("Members")
            .HumansOnly()
            .WithSummary("Watch a running member's agent")
            .WithDescription(
                "Streams the current run's transcript as `text/plain; charset=utf-8`, one display "
                + "line per event ending in a newline: from the start of the run, then each new "
                + "line as the agent writes it. The response ends when the run ends.\n\n"
                + "Each line is `<when>` TAB `<line>`: `<when>` is the event's time in UTC "
                + "(`2026-09-26T13:34:41.454Z`), empty when the event names none.\n\n"
                + "Lines are at most 240 characters and end with `…` when clipped: `Read <path>`, "
                + "`Bash: <command>`, `MCP: <tool>`, `<Tool>: <short input>`, the assistant's "
                + "text, a tool result's first line and size (`<first line> (<n> bytes)`), "
                + "`User: <first line>` and `Attachment: <name or first line>`. A line that does "
                + "not parse is shown raw.\n\n"
                + "404 with a sentence when the member is not running. 200 with "
                + "`{ \"live\": false, \"reason\": \"...\" }` as JSON when its agent has no live "
                + "view, or its transcript was not found within 30 seconds of launch. For an agent "
                + "whose transcript is found after launch, the response starts once it is found; "
                + "until then the dialog says it is waiting for the agent to start its session.\n\n"
                + "Writes nothing: not the database, not the message log.\n\n"
                + "**A person's action; no machine principal.**");

        app.MapGet("/api/teams/{team}/members/{member}/runs", RunsAsync)
            .WithTags("Members")
            .HumansOnly()
            .WithSummary("A member's earlier runs")
            .WithDescription(
                "This member's finished runs that recorded the agent's own transcript, newest first, "
                + $"{RunsPage} at a time: `{{ \"runs\": [ {{ \"seq\", \"workflow\", \"startedAt\", "
                + "\"endedAt\", \"durationMs\", \"outcome\", \"output\", \"reason\", \"quiet\" } ], \"nextBefore\" }`. `seq` is the run's "
                + "terminal row, `workflow` its correlation, `outcome` one of `completed`, "
                + "`handedBack`, `blocked` and `failed`. `startedAt` and `durationMs` are null when the "
                + "run's start is not in the log. Pass `nextBefore` as `before` for the next page; it "
                + "is null on the last.\n\n"
                + "A PLUGIN member (`kind: plugin`) records no transcript, so every one of its finished "
                + "runs is listed, one per run, and `output` is what the run reported: its result "
                + "output, or for a failure the launch error or output. `output` is null for an agent "
                + "member's runs; read the transcript instead.\n\n"
                + "A plugin run that blocked every item it was given wrote no completed or failed row; "
                + "it is listed with `outcome` `blocked`, `seq` its last `blocked` row, `output` null "
                + "and `reason` that row's reason. `reason` is null on every other run. While the member "
                + "is running, an item its current run has blocked is not listed: that run is not "
                + "over, and it is listed once it is.\n\n"
                + "`quiet` is true for a run that finished quiet: its `completed` row is marked "
                + "`quiet` and woke nobody. It is false for every other run, a failure always.\n\n"
                + "`workflowDeclared` is true for a run whose workflow was declared complete by the "
                + "time it ended - by the platform, because its owner cannot declare (a person told a "
                + "plugin member directly), or by the member itself, as that workflow's owner: the "
                + "Manager was not woken by it. False for every other run.\n\n"
                + "`items` lists, for a run that carried more than one item, what became of each: "
                + "`{ \"item\", \"seq\", \"outcome\", \"reason\" }` in prompt order, `outcome` one of "
                + "`answered`, `failed`, `blocked` and `deferred`, `reason` set on a deferral only. A "
                + "deferred item is delivered again as its own run, whose `deferredFromRun` is the "
                + "`started` seq of the run it was deferred from. `items` is null on a run of one "
                + "item and `deferredFromRun` null on every run that is not a deferred item's.\n\n"
                + "Runs from before transcripts were recorded are not listed.\n\n"
                + "Writes nothing.\n\n"
                + "**A person's action; no machine principal.**");

        app.MapGet("/api/teams/{team}/members/{member}/runs/{seq:long}/transcript", RunTranscriptAsync)
            .WithTags("Members")
            .HumansOnly()
            .WithSummary("One earlier run's transcript")
            .WithDescription(
                "The whole transcript of one of this member's finished runs, as "
                + "`text/plain; charset=utf-8`, in the live route's form: one `<when>` TAB `<line>` "
                + "per step, from the start of the file to its end. It is read once, not followed.\n\n"
                + $"410 with `{Gone}` when the file is no longer on disk. 500 with a sentence saying "
                + "why when it is there but cannot be read (e.g. permission denied). 404 when `seq` is not one "
                + "of this member's runs.\n\n"
                + "Writes nothing.\n\n"
                + "**A person's action; no machine principal.**");
    }

    /// <summary>How many runs one page of <c>runs</c> holds.</summary>
    public const int RunsPage = 20;

    /// <summary>What <c>runs/{seq}/transcript</c> answers when the agent's file is gone.</summary>
    public const string Gone = "This run's transcript is no longer on disk.";

    /// <summary>What <c>runs/{seq}/transcript</c> answers when the file is there but cannot be read, e.g. permission denied.</summary>
    public static string Unreadable(string why) => $"This run's transcript is on disk but could not be read: {why}";

    private static async Task<IResult> WatchAsync(
        [Description(Describe.Team)] string team,
        [Description("The member to watch, as addressed in its route.")] string member,
        HttpContext context, TeamRegistry teams, ContainerHost host, LiveRuns live, AgentLaunchUser runAs,
        CancellationToken ct)
    {
        if (teams.ExistingName(team) is not { } stored)
        {
            return Results.NotFound(new { error = $"No team '{team}'." });
        }

        // The FOUND container, never the caller's spelling.
        if (!ContainerId.IsLegalName(member) || host.Find(new ContainerId(stored, member)) is not { } container)
        {
            return Results.NotFound(new { error = $"No member '{member}'." });
        }

        if (live.Find(container.Id) is not { } run)
        {
            return Results.NotFound(new { error = $"{container.Id.Name} is not running." });
        }

        // A transcript the agent names itself is looked for after launch; the response
        // starts once it is found.
        var transcript = run.Transcript ?? await run.Located.WaitAsync(ct);

        if (transcript is null)
        {
            return Results.Ok(new { live = false, reason = run.Reason });
        }

        var format = run.Format ?? LiveView.ClaudeJsonl;

        if (LiveTranscriptReader.Refusal(runAs) is { } refusal)
        {
            return Results.Ok(new { live = false, reason = refusal });
        }

        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/plain; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no";

        await response.StartAsync(ct);

        try
        {
            await foreach (var raw in LiveTranscriptReader.LinesAsync(transcript, runAs, run.Ended, ct))
            {
                // Every line of one event carries that event's time, or an empty one.
                var text = TranscriptLines.Wire(format, raw);
                if (text.Length == 0) continue;

                await response.WriteAsync(text, Encoding.UTF8, ct);
                await response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The watcher left. Nothing to say to nobody.
        }

        return Results.Empty;
    }

    private static async Task<IResult> RunsAsync(
        [Description(Describe.Team)] string team,
        [Description("The member, as addressed in its route.")] string member,
        [Description("Only runs whose seq is below this; the previous page's `nextBefore`.")] long? before,
        TeamRegistry teams, IMessageLog log, ContainerHost host, CancellationToken ct)
    {
        if (await FindMemberAsync(teams, team, member, ct) is not { } found) return NoMember(team, member);

        // A plugin's run has no transcript to open, so its runs are read whole and carry their output.
        var plugin = MemberRef.IsPlugin(found.Agent);
        var id = new ContainerId(found.Team, found.Name);

        // E5-c: an item the RUNNING run has blocked is not a finished run, however its row reads.
        // Only the newest row can be one, so one more is read to keep the page full.
        var running = host.Find(id)?.State == ContainerState.Running;

        var page = (await log.ReadRunsAsync(
                id, found.FloorSeq, before ?? long.MaxValue, RunsPage + 2,
                plugin ? RunsWith.AnyRun : RunsWith.Transcript, ct))
            .Where(run => !(running && run.InLatestRun))
            .Take(RunsPage + 1)
            .ToList();

        var runs = page.Take(RunsPage).Select(run => new
        {
            seq = run.Terminal.Seq,
            workflow = run.Terminal.CorrelationId,
            startedAt = run.StartedAt,
            endedAt = run.Terminal.OccurredAt,
            durationMs = run.StartedAt is { } started
                ? (long?)(run.Terminal.OccurredAt - started).TotalMilliseconds
                : null,
            outcome = Outcome(run),
            output = plugin && run.Terminal.Type != MessageTypes.Blocked ? RunOutput(run.Terminal.Payload) : null,
            reason = run.Terminal.Type == MessageTypes.Blocked ? Field(run.Terminal.Payload, PayloadFields.Reason) : null,

            // A quiet run woke nobody; its `completed` row says so. A failure is never quiet.
            quiet = run.Terminal.Type == MessageTypes.Completed && Bool(run.Terminal.Payload, PayloadFields.Quiet),

            // The platform declared the run's workflow as it ended, so the Manager was not woken.
            workflowDeclared = run.Terminal.Type == MessageTypes.Completed
                && Bool(run.Terminal.Payload, PayloadFields.WorkflowDeclared),

            // WHAT BECAME OF EACH ITEM of a run that carried several, a deferral included. Null on
            // a run of one item, and on a blocked terminal, which closes one item only.
            items = Items(run.Terminal.Payload),

            // The run a deferred item was deferred from, on the run it was delivered again in.
            deferredFromRun = Long(run.Terminal.Payload, PayloadFields.DeferredFromRun),
        }).ToList();

        return Results.Ok(new
        {
            runs,
            nextBefore = page.Count > RunsPage ? runs[^1].seq : (long?)null,
        });
    }

    private static async Task<IResult> RunTranscriptAsync(
        [Description(Describe.Team)] string team,
        [Description("The member, as addressed in its route.")] string member,
        [Description("The run's seq, as `runs` lists it.")] long seq,
        TeamRegistry teams, IMessageLog log, AgentLaunchUser runAs, CancellationToken ct)
    {
        if (await FindMemberAsync(teams, team, member, ct) is not { } found) return NoMember(team, member);

        var id = new ContainerId(found.Team, found.Name);
        var runs = seq == long.MaxValue ? [] : await log.ReadRunsAsync(id, found.FloorSeq, seq + 1, 1, ct);

        if (runs is not [{ } run] || run.Terminal.Seq != seq
            || Field(run.Terminal.Payload, PayloadFields.AgentTranscript) is not { } path)
        {
            return Results.NotFound(new { error = $"{seq} is not one of {id.Name}'s runs." });
        }

        var format = Field(run.Terminal.Payload, PayloadFields.AgentTranscriptFormat) ?? LiveView.ClaudeJsonl;

        if (LiveTranscriptReader.Refusal(runAs) is { } refusal)
        {
            return Results.Problem(refusal, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var read = await AgentFiles.ReadAllAsync(path, runAs, ct);
        if (read.Unreadable is { } why)
        {
            return Results.Text(Unreadable(why), "text/plain; charset=utf-8", Encoding.UTF8, StatusCodes.Status500InternalServerError);
        }

        if (read.Text is not { } text)
        {
            return Results.Text(Gone, "text/plain; charset=utf-8", Encoding.UTF8, StatusCodes.Status410Gone);
        }

        var body = new StringBuilder();
        foreach (var raw in text.Split('\n')) body.Append(TranscriptLines.Wire(format, raw));

        return Results.Text(body.ToString(), "text/plain; charset=utf-8", Encoding.UTF8);
    }

    /// <summary>How a run ended, in the words the dialog shows: a failure, then a block, then a hand-back.</summary>
    private static string Outcome(RunRow run)
    {
        if (run.Terminal.Type == MessageTypes.Failed) return "failed";
        if (run.Blocked) return "blocked";

        return Bool(run.Terminal.Payload, PayloadFields.HandedBack) ? "handedBack" : "completed";
    }

    private static async Task<PersistedMember?> FindMemberAsync(TeamRegistry teams, string team, string member, CancellationToken ct)
    {
        if (!ContainerId.IsLegalName(member)) return null;

        try
        {
            return await teams.MemberAsync(team, member, ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static IResult NoMember(string team, string member) =>
        Results.NotFound(new { error = $"No member '{member}' in team '{team}'." });

    /// <summary>What a run reported: a launch error first, as the failure row's own text reads it, then its output.</summary>
    private static string? RunOutput(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return FailurePayloadText.FirstNonEmpty(document.RootElement, PayloadFields.LaunchError, PayloadFields.Output);
    }

    private static string? Field(string payload, string name)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static long? Long(string payload, string name)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;
    }

    /// <summary>A terminal row's <see cref="PayloadFields.Items"/>, as the route sends them: every
    /// key always present, `reason` null on all but a deferral.</summary>
    public static List<object>? Items(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty(PayloadFields.Items, out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return items.EnumerateArray()
            .Where(entry => entry.ValueKind == JsonValueKind.Object)
            .Select(entry => (object)new
            {
                item = entry.TryGetProperty("item", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0,
                seq = entry.TryGetProperty("seq", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetInt64() : 0,
                outcome = entry.TryGetProperty("outcome", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null,
                reason = entry.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
            })
            .ToList();
    }

    private static bool Bool(string payload, string name)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }
}
