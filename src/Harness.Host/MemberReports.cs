using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// WHAT A MEMBER'S REPORT DOES, written once. The `progress`, `blocked`, `handback` and
/// `needs-decision` routes call this after checking who is asking; a plugin member's stdout records
/// call it through <c>PluginMemberRunner</c> with no credential at all. The row, the mark, the
/// snapshot push and the idle-clock reset are the same either way, because there is only this.
///
/// Checks that belong to HTTP - the team exists, the caller IS the member, the words are not
/// empty - stay on the routes. What is refused here is refused for every caller: a member that is
/// not hosted, a batch item that is not in the run.
/// </summary>
public sealed class MemberReports(
    ContainerHost host, IMessageLog log, RunHeartbeat heartbeat, ILoggerFactory? loggers = null) : IMemberReports
{
    public async Task<MemberReportOutcome> ProgressAsync(ContainerId member, string status, CancellationToken ct = default)
    {
        if (host.Find(member) is not { } container) return NoSuchMember(member);

        // The seq of the message being worked on: a progress report is CAUSED by it, and correlation
        // is inherited from causation at append time, so this one value puts the line in the right
        // workflow.
        var causation = container.CurrentCausation;
        status = status.Trim();

        // A REPORT FROM A MEMBER THAT IS NOT RUNNING is a process that outlived its run - still
        // recorded, because it is a fact, and flagged on the row and in the log, because nothing
        // will report what that process produces.
        if (causation is null)
        {
            loggers?.CreateLogger("Harness.Host.Progress").LogWarning(
                "{Container} reported progress while idle: \"{Status}\". Its run has ended, so this "
                + "came from a process that outlived it - and nothing will report what that process "
                + "produces.",
                container.Id,
                status);
        }

        await log.AppendAsync(
            new NewMessage(
                MessageTypes.Progress,
                causation is null
                    ? JsonSerializer.Serialize(new { status, whileIdle = true })
                    : JsonSerializer.Serialize(new { status }),
                container.Id.ToString(),
                causation),
            ct);

        // The card re-renders from the log; this only tells it to look.
        container.Republish();

        // A DELIBERATE ACT resets the idle clock - see RunHeartbeat.
        heartbeat.Touch(container.Id);

        return MemberReportOutcome.Ok;
    }

    public async Task<MemberReportOutcome> BlockedAsync(
        ContainerId member, string reason, int? item = null, CancellationToken ct = default)
    {
        if (host.Find(member) is not { } container) return NoSuchMember(member);

        reason = reason.Trim();

        // ONE ITEM OF A BATCH: that delivery is closed by this row instead of by the run's terminal
        // row, and the member is not marked - the rest of its batch may still complete.
        if (item is { } index)
        {
            if (!container.TryBlockItem(index, out var cause, out var error))
            {
                return MemberReportOutcome.Refused(error!, 400);
            }

            await log.AppendAsync(
                new NewMessage(
                    MessageTypes.Blocked,
                    JsonSerializer.Serialize(new { reason, item = index }),
                    container.Id.ToString(),
                    cause),
                ct);
        }
        else
        {
            await log.AppendAsync(
                new NewMessage(
                    MessageTypes.Blocked,
                    JsonSerializer.Serialize(new { reason }),
                    container.Id.ToString(),
                    container.CurrentCausation),
                ct);

            container.MarkBlocked(reason);
        }

        return MemberReportOutcome.Ok;
    }

    public async Task<MemberReportOutcome> HandbackAsync(ContainerId member, string delivered, CancellationToken ct = default)
    {
        if (host.Find(member) is not { } container) return NoSuchMember(member);

        delivered = delivered.Trim();

        await log.AppendAsync(
            new NewMessage(
                MessageTypes.Handback,
                JsonSerializer.Serialize(new { delivered }),
                container.Id.ToString(),
                container.CurrentCausation),
            ct);

        container.MarkHandedBack(delivered);

        return MemberReportOutcome.Ok;
    }

    public async Task<MemberReportOutcome> NeedsDecisionAsync(ContainerId member, string question, CancellationToken ct = default)
    {
        if (host.Find(member) is not { } container) return NoSuchMember(member);

        question = question.Trim();

        await log.AppendAsync(
            new NewMessage(
                MessageTypes.NeedsDecision,
                JsonSerializer.Serialize(new { question }),
                container.Id.ToString(),
                container.CurrentCausation),
            ct);

        container.MarkNeedsDecision(question);

        return MemberReportOutcome.Ok;
    }

    public async Task<MemberReportOutcome> PublishAsync(ContainerId member, string type, string payload, CancellationToken ct = default)
    {
        if (host.Find(member) is not { } container) return NoSuchMember(member);

        // NEVER A PLATFORM TYPE, whoever asks: only a type an installed plugin declares.
        if (EventCatalog.For(type) is not { Publisher: EventPublisher.Plugin })
        {
            return MemberReportOutcome.Refused($"`{type}` is not an event an installed plugin declares, so it was not published.", 400);
        }

        // An event belongs to the run that published it: a process that outlived its run has no
        // workflow to join, and an event rooting a workflow of its own would be the member deciding
        // that for itself.
        if (container.CurrentCausation is not { } causation)
        {
            return MemberReportOutcome.Refused($"'{member.Name}' is not running, so `{type}` was not published.");
        }

        await log.AppendAsync(new NewMessage(type, payload, container.Id.ToString(), causation), ct);

        // A DELIBERATE ACT resets the idle clock - see RunHeartbeat.
        heartbeat.Touch(container.Id);

        return MemberReportOutcome.Ok;
    }

    private static MemberReportOutcome NoSuchMember(ContainerId member) =>
        MemberReportOutcome.Refused($"No member '{member.Name}'.", 404);
}
