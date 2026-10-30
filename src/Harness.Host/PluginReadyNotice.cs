using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WHAT A PLUGIN MEMBER STILL WAITS FOR A PERSON TO SET, and the word to its Manager once it no
/// longer does.
///
/// A Manager (or a Concierge) may hire a plugin it cannot finish setting up: a key a person has
/// not bound on the team, a connection only a person may choose. Such a hire makes the member and
/// answers <see cref="Missing"/>; each run of the member fails <c>needs-person</c>
/// (<see cref="FailureClasses.NeedsPerson"/>) naming the same, which wakes the Manager once and
/// shows on the member's card. When a person's save (the member's settings, or a key's value)
/// leaves nothing missing, <see cref="SendAsync"/> tells the Manager inside every open workflow
/// whose run of the member failed that way, so the work carries on without being explained again.
/// Only a person's write calls it: an agent can never be the one that makes a member ready.
/// </summary>
public static class PluginReadyNotice
{
    private const int Page = 500;

    /// <summary>The payload field naming the member a notice says is ready: how a second save finds
    /// it already said.</summary>
    public const string ReadyField = "pluginReady";

    /// <summary>
    /// Each thing a person must still set before the member's runs can start, in words, or none:
    /// a required secret with no value of the member's own and no key bound, a bound key with no value
    /// on the Host, a required
    /// connection slot with nothing bound. A connection that needs reconnecting or lacks a scope is
    /// said by the run itself, which asks the provider.
    /// </summary>
    public static IReadOnlyList<string> Missing(
        PluginManifest manifest, PluginMemberSettings settings, ISecretStore? secrets, IReadOnlySet<string>? valued = null)
    {
        var missing = new List<string>();

        // A FIELD MOVED TO A SLOT (`movedTo`, CON 7) asks for no own value: its slot's binding is what is missing.
        foreach (var (name, secret) in manifest.Secrets.Where(s => s.Value.Required && s.Value.MovedTo is null))
        {
            // The member's own value satisfies the field.
            if (valued?.Contains(name) == true) continue;

            if (!settings.Secrets.TryGetValue(name, out var key))
            {
                missing.Add($"set the secret `{name}` in the member's settings");
            }
            else if (secrets?.TryGet(key) is null)
            {
                missing.Add($"set the value of the key `{key}` (bound for `{name}`), in the member's settings");
            }
        }

        foreach (var (slot, declared) in manifest.Connections.Where(c => c.Value.Required))
        {
            if (!settings.Connections.ContainsKey(slot))
            {
                missing.Add($"bind a connection for the slot `{slot}`, in the member's settings");
            }
        }

        return missing;
    }

    /// <summary>
    /// Tells the team's Manager, in every open workflow where a run of <paramref name="member"/>
    /// last failed <c>needs-person</c> and no notice has said so since, that the member is ready
    /// and its instruction is to be sent again. Answers how many workflows were told.
    /// </summary>
    public static async Task<int> SendAsync(IMessageLog log, ContainerId member, string memberName, CancellationToken ct)
    {
        var source = member.ToString();

        // Rare path (a person finishing a plugin's setup), so reading the failed rows from the start
        // is affordable; they are one type, filtered in the query.
        var waiting = new Dictionary<long, Message>();
        long after = 0;
        while (true)
        {
            var page = await log.ReadAfterAsync(after, [MessageTypes.Failed, MessageTypes.Completed], Page, ct);
            foreach (var row in page.Where(m => string.Equals(m.Source, source, StringComparison.Ordinal)))
            {
                if (row.Type == MessageTypes.Failed && ClassOf(row) == FailureClasses.NeedsPerson)
                {
                    waiting[row.CorrelationId] = row;
                }
                else
                {
                    // The member ran (or failed otherwise) after it waited: that workflow is not
                    // waiting on this setup any more.
                    waiting.Remove(row.CorrelationId);
                }
            }

            if (page.Count < Page) break;
            after = page[^1].Seq;
        }

        if (waiting.Count == 0) return 0;

        var open = await log.OpenWorkflowsAmongAsync([.. waiting.Keys], ct);
        var manager = MessageTypes.InstructionFor(new ContainerId(member.Team, TeamRegistry.DefaultManagerName));
        var told = 0;

        foreach (var correlation in open.Order())
        {
            var failure = waiting[correlation];
            if (await AlreadyToldAsync(log, manager, failure, source, ct)) continue;

            var instruction =
                $"{memberName} is ready now: a person set what it was waiting for. Its run in this "
                + "workflow did not start, so nothing it was asked has been done yet: send it the same "
                + "instruction again, with the same `card`.";

            await log.AppendAsync(
                new NewMessage(
                    manager,
                    JsonSerializer.Serialize(new Dictionary<string, string> { ["instruction"] = instruction, [ReadyField] = source }),
                    "host",
                    failure.Seq),
                ct);
            told++;
        }

        return told;
    }

    /// <summary>Whether a notice for this member already went to the Manager in the failure's workflow, after it.</summary>
    private static async Task<bool> AlreadyToldAsync(IMessageLog log, string manager, Message failure, string source, CancellationToken ct)
    {
        long after = failure.Seq;
        while (true)
        {
            var page = await log.ReadAfterAsync(after, [manager], Page, ct);
            if (page.Any(m => m.CorrelationId == failure.CorrelationId && Names(m, source))) return true;
            if (page.Count < Page) return false;
            after = page[^1].Seq;
        }
    }

    private static string? ClassOf(Message row)
    {
        try
        {
            using var payload = JsonDocument.Parse(row.Payload);
            return payload.RootElement.TryGetProperty(PayloadFields.FailureClass, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Names(Message notice, string source)
    {
        try
        {
            using var payload = JsonDocument.Parse(notice.Payload);
            return payload.RootElement.TryGetProperty(ReadyField, out var named)
                && string.Equals(named.GetString(), source, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
