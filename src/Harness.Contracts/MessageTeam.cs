using System.Text.Json;

namespace Harness.Contracts;

/// <summary>
/// Which team a message belongs to. THE one place this rule lives.
///
/// Two rules, because the two message shapes carry it differently and one of them is forgeable.
/// An addressed instruction's SOURCE is `request.From ?? "console"` - caller-supplied and never
/// validated - so it can say anything, including another team's container. Its TYPE is built by
/// the server from the container it resolved, and is therefore the only half that may decide
/// access. Container events publish their own qualified id as the source, which is authoritative.
/// </summary>
public static class MessageTeam
{
    public static string? Of(Message message)
    {
        // A CARD EVENT'S TEAM IS A WHOLE TEAM, NOT THE FRONT HALF OF A CONTAINER ID, which is why
        // this returns before the split below rather than joining the chain.
        //
        // A `kanban.card.*` row, a `workflow.closed` row and a `backlog.item.dispatched` row are the
        // only rows on the log published by a PERSON. The actor is a bare user id with no `/` in it,
        // so falling through to `message.Source` recovered nothing - for a card event the delivery pump failed closed and
        // a human editing a card woke nobody at all (a 200, a row, and silence); for a close it
        // would make the row readable by a person only, invisible to the very team that closed
        // it. That is what this arm fixes for both.
        //
        // IT IS THE SAME PAYLOAD-READING TRADE THE SCHEDULE ARM BELOW MAKES, and it is safe for the
        // same reason: nothing outside can write either type. `kanban.card.*` is published by the
        // three routes in `KanbanEndpoints`, `workflow.closed` by the close route alone, and a
        // container cannot choose a message type at all.
        //
        // The VALUE is the server's own twice over for a card event: each route writes `team` from
        // the CARD it just resolved - out of a projection scoped to the `{team}` route value
        // `TeamGate` checked - and that card's team is itself this function's answer for the
        // originating instruction, which reads its TYPE. A close route writes `team` from the
        // `{team}` route value directly, the same value `TeamGate` already checked. So both traces
        // back to something the server built, never to anything a caller supplied.
        //
        // `backlog.item.dispatched` NEEDS ITS ARM OR BOTH DISPATCH PATHS LOSE THEIR WORKFLOW
        // CONTROLS. It is the ROOT of every workflow a backlog dispatch starts, its source is the actor's bare
        // email, and with no arm here `Of` would answer NULL for it - so `WorkflowTeamOwnership`
        // could attribute the root to no team and close, nudge and stop would each answer "No
        // workflow with correlation N" for a workflow the SAME team's `/workflows` route had just
        // listed. Two definitions of "this team's workflow" that disagree, which is the exact
        // failure that class exists to prevent.
        //
        // Its `team` is `stored` - the value `ExistingName` returned, or the id of the team the
        // dispatch itself just created - so it is the same server-resolved spelling the other two
        // arms rely on and never anything the caller typed.
        //
        // The schedule arm's warning applies unchanged: if a route ever lets a caller name this
        // team, this arm must read something the server built instead.
        if (message.Type.StartsWith(MessageTypes.KanbanCardPrefix, StringComparison.Ordinal)
            || string.Equals(message.Type, MessageTypes.WorkflowClosed, StringComparison.Ordinal)

            // `workflow.resumed` IS PUBLISHED BY A PERSON and its source is a bare
            // principal id with no `/` in it at all, so without this arm `Of` answers null and
            // the row is dropped by the pump and hidden from the very team that resumed the
            // workflow - the identical defect the `workflow.closed` arm above it exists to prevent,
            // and the one the comment at the head of this method spells out.
            //
            // `workflow.paused` IS HERE TOO EVEN THOUGH IT DOES NOT NEED TO BE. The pump
            // publishes it with a qualified container id, so the source rule below would recover
            // its team perfectly well. Both types write a server-resolved `team` field and both
            // are read from it, so there is ONE rule for the pair rather than two that happen to
            // agree - and a later change of publisher on the pause side cannot silently lose its
            // team.
            || string.Equals(message.Type, MessageTypes.WorkflowPaused, StringComparison.Ordinal)
            || string.Equals(message.Type, MessageTypes.WorkflowResumed, StringComparison.Ordinal)
            || string.Equals(message.Type, MessageTypes.BacklogItemDispatched, StringComparison.Ordinal)

            // Published by the trigger runner (source `trigger:<id>`) and by the Documents
            // routes (source the person), neither of which carries a team. Both write `team` from
            // the row or the route value TeamGate checked; nothing outside can write this type.
            || string.Equals(message.Type, MessageTypes.FileChanged, StringComparison.Ordinal)
            || string.Equals(message.Type, MessageTypes.RepoForkSynced, StringComparison.Ordinal)
            || string.Equals(message.Type, MessageTypes.RepoPullRequestOpened, StringComparison.Ordinal)

            // Published by the site action route only, as `site:<team>/<site>` - a source whose
            // front half is not a team - with `team` from the site the capability names.
            || string.Equals(message.Type, MessageTypes.SiteAction, StringComparison.Ordinal))
        {
            // FAILS CLOSED on anything unusable - malformed JSON, a missing field, a non-string, a
            // blank. `Field` swallows the parse failure; the blank check is here because an empty
            // team would otherwise compare equal to nothing and be returned as if it were an
            // answer. Null means "wakes nobody"/"people-only", the recoverable direction.
            var team = Field(message.Payload, "team");

            return string.IsNullOrWhiteSpace(team) ? null : team;
        }

        // THE THIRD ARM READS A PAYLOAD, WHICH THE OTHER TWO EXIST TO AVOID. It is safe today for
        // one reason only, and that reason is worth stating because it is not visible from here.
        //
        // A scheduled wake has no author: `TriggerSweep` publishes as `schedule:<id>`, which is
        // deliberately neither a person nor a container. That source carries no `/`, so the rule
        // above recovers no team from it and the row would be dropped by the pump and hidden from
        // every read route - which for a skip notice means the one thing nobody would see is the
        // platform declining to do work.
        //
        // WHAT MAKES IT SAFE IS THAT NOTHING OUTSIDE CAN WRITE THIS TYPE. `container.schedule-skipped`
        // is published by TriggerSweep and nowhere else; a container cannot choose a type at all,
        // because the routes it can reach (`progress`, `blocked`) name theirs server-side. So the
        // payload here is the server's own, not a caller's.
        //
        // WHAT WOULD MAKE IT UNSAFE is any route that lets a container publish this type - "let a
        // member report its own skip" is the obvious one. A member could then name another team's
        // container and be attributed to that team, and this function decides both which containers
        // the pump WAKES and which messages a caller may SEE. If that route is ever added, this arm
        // must read something the server built, exactly as the instruction arm reads the TYPE.
        var qualified = message.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)
            ? message.Type[MessageTypes.InstructionPrefix.Length..]
            : string.Equals(message.Type, MessageTypes.ScheduleSkipped, StringComparison.Ordinal)
                // `member` is the CURRENT field - see EventCatalog's ScheduleSkipped entry and
                // TriggerSweep.cs. The `"container"` fallback is a PERMANENT ALIAS: the log is
                // append-only and rows are never rewritten, so a row that carries that key would
                // otherwise lose its team - the same "every spelling is a permanent alias" rule
                // this codebase applies to message TYPEs.
                ? Field(message.Payload, PayloadFields.Member) ?? Field(message.Payload, "container") ?? message.Source
                : message.Source;

        var separator = qualified.LastIndexOf('/');

        // No separator means no team can be recovered: a caller-supplied `from`, or any other row
        // whose source is not a qualified container id. Null, and the caller decides - the read routes
        // show these to a person only.
        return separator <= 0 ? null : qualified[..separator];
    }

    private static string? Field(string payload, string name)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            return json.RootElement.ValueKind is JsonValueKind.Object
                && json.RootElement.TryGetProperty(name, out var field)
                && field.ValueKind is JsonValueKind.String
                    ? field.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
