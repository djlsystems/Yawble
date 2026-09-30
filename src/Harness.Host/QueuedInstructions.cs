using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>One delivery a member has accepted and not started: what `status` lists under the member.</summary>
/// <param name="Member">The member's identifier within its team.</param>
/// <param name="Seq">The queued message's seq - the number a Manager names it by.</param>
/// <param name="Line">The first line of the instruction, or the message type when it is not one.</param>
/// <param name="Source">Who sent it, as the log's Source.</param>
/// <param name="Correlation">The workflow it belongs to.</param>
/// <param name="State">`queued`, or `deferred` for an item a batched run put back.</param>
/// <param name="DeferredFromRun">The run it was deferred from; null unless deferred.</param>
public sealed record QueuedInstruction(
    string Member, long Seq, string Line, string Source, long Correlation, string State, long? DeferredFromRun);

/// <summary>
/// WHAT IS WAITING FOR EACH MEMBER, read from the durable queue rather than the in-memory channel.
///
/// A Manager that cannot see a member's queue re-sends what is already in it when the member's
/// last hand-back is accepted - a duplicate that costs a full member run and a Manager wake to
/// repeat itself. `status` shows this list and `tell` names a queued match, so the Manager has
/// what it needs not to do that.
///
/// QUEUED MEANS ACCEPTED AND NOT STARTED: <see cref="PendingDelivery.Started"/> false. Every item
/// of a running batch is started before the run launches, so nothing the member is working on right
/// now is listed as waiting. A deferred item is back on the queue unstarted and carries
/// <see cref="PendingDelivery.DeferredFromRun"/>, which is the only thing that makes it read as
/// deferred.
/// </summary>
public static class QueuedInstructions
{
    /// <summary>How much of an instruction's first line is shown. A line, not a paragraph.</summary>
    public const int MaxLine = 120;

    public const string Queued = "queued";

    public const string Deferred = "deferred";

    /// <summary>This team's queued instructions, oldest first within each member; one member's when named.</summary>
    public static async Task<IReadOnlyList<QueuedInstruction>> ForTeamAsync(
        IPendingDeliveries pending, IMessageLog log, string team, string? member, CancellationToken ct)
    {
        var rows = await pending.ForTeamAsync(team, ct);
        var queued = new List<QueuedInstruction>();

        foreach (var row in rows)
        {
            if (row.Started) continue;
            if (!ContainerId.TryParse(row.Subscriber, out var subscriber)) continue;
            if (member is not null && !string.Equals(subscriber.Name, member, StringComparison.OrdinalIgnoreCase)) continue;

            // A row whose message is gone is not something a Manager can act on by seq; skipped
            // rather than listed with no line.
            if (await log.FindAsync(row.Seq, ct) is not { } message) continue;

            queued.Add(From(subscriber.Name, message, row.DeferredFromRun));
        }

        return queued;
    }

    public static QueuedInstruction From(string member, Message message, long? deferredFromRun) =>
        new(
            member,
            message.Seq,
            LineOf(message),
            message.Source,
            message.CorrelationId,
            deferredFromRun is null ? Queued : Deferred,
            deferredFromRun);

    /// <summary>
    /// The queued instruction to <paramref name="member"/> in <paramref name="correlation"/> whose
    /// text is <paramref name="instruction"/>, or null. Whitespace and case are not what makes two
    /// instructions different, so neither is compared.
    /// </summary>
    public static async Task<Message?> DuplicateOfAsync(
        IPendingDeliveries pending, IMessageLog log, ContainerId member, long correlation, string instruction,
        CancellationToken ct)
    {
        var wanted = Normalise(instruction);

        foreach (var row in await pending.ForAsync(member, ct))
        {
            if (row.Started) continue;
            if (await log.FindAsync(row.Seq, ct) is not { } message) continue;
            if (message.CorrelationId != correlation) continue;
            if (TextOf(message) is not { } text) continue;

            if (string.Equals(Normalise(text), wanted, StringComparison.OrdinalIgnoreCase)) return message;
        }

        return null;
    }

    /// <summary>The sentence the tell reply carries. The instruction was sent anyway: tell is never held.</summary>
    public static string DuplicateNotice(string member, long queuedSeq) =>
        $"{member} already has this instruction queued in this workflow as #{queuedSeq}. It is delivered "
        + "in turn, so it did not need sending again; this copy was sent anyway and will run as well.";

    private static string LineOf(Message message)
    {
        if (TextOf(message) is not { } text) return message.Type;

        var line = text.Trim().Split('\n', 2)[0].Trim();
        return line.Length <= MaxLine ? line : line[..MaxLine];
    }

    private static string? TextOf(Message message)
    {
        if (!message.Type.StartsWith(MessageTypes.InstructionPrefix, StringComparison.Ordinal)) return null;

        try
        {
            using var document = JsonDocument.Parse(message.Payload);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(PayloadFields.Instruction, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Normalise(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
