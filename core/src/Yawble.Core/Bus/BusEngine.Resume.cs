using System.Globalization;
using System.Text.Json.Nodes;

namespace Yawble.Core.Bus;

/// <summary>
/// Automatic resume after a usage limit: a run the model API's limit stopped records when
/// the API said to come back. Once that time has passed, the workflow's Manager is told to pick it up, at most
/// <see cref="MaxAutomaticResumes"/> times per workflow; after that only a person's nudge restarts it. Everything is
/// read again from the log each sweep (markers on the resume rows), so there is no state to lose.
/// </summary>
public sealed partial class BusEngine
{
    public const int MaxAutomaticResumes = 3;
    public const string AutomaticResumeMarker = "automaticResume";

    private void ResumeRateLimited(DateTimeOffset now)
    {
        var held = _store.HeldTeams();
        foreach (var failed in _store.RecentOfType(MessageTypes.Failed, 400))
        {
            if (Payload(failed) is not { } p || p["failureClass"]?.GetValue<string>() != FailureClasses.Rate) continue;
            if (p["run"]?["retryAfter"] is not JsonValue rv || !rv.TryGetValue<string>(out var retryText)
                || !DateTimeOffset.TryParse(retryText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var retryAt)
                || retryAt > now) continue;
            var correlation = failed.CorrelationId;
            if (_store.WorkflowEnded(correlation)) continue;
            if (_store.MemberBySubscriber(failed.Source) is not { } member || held.Contains(member.TeamId)) continue;
            var manager = _store.TeamMembers(member.TeamId).SingleOrDefault(m => m.Role == MemberRole.Manager);
            if (manager is null) continue;
            var thread = _store.Workflow(correlation);
            // The member worked again since (someone already picked it up), or this failure was already answered.
            if (thread.Any(m => m.Seq > failed.Seq && string.Equals(m.Source, failed.Source, StringComparison.OrdinalIgnoreCase))) continue;
            if (thread.Any(m => m.Payload.Contains($"\"resumeOf\":{failed.Seq}", StringComparison.Ordinal))) continue;
            var attempts = thread.Count(m => m.Payload.Contains($"\"{AutomaticResumeMarker}\":true", StringComparison.Ordinal));
            if (attempts >= MaxAutomaticResumes) continue;
            var text = $"The app resumed this workflow by itself. {member.Name}'s last run stopped at the model's usage limit; the " +
                $"provider said to come back at {retryAt:yyyy-MM-dd HH:mm} UTC, and that time has passed. This is automatic resume " +
                $"{attempts + 1} of {MaxAutomaticResumes}; after that nothing restarts it until a person nudges it. Look at the thread " +
                "(workflow_show), then continue the work, declare it complete with workflow_complete, or say why it should stop.";
            Publish(new NewMessage(MessageTypes.Instruction(manager),
                BatchText.InstructionPayload(text, new JsonObject { [AutomaticResumeMarker] = true, ["resumeOf"] = failed.Seq, ["resumeAttempt"] = attempts + 1 }),
                MessageTypes.HostSource, failed.Seq));
            Log($"workflow {correlation}: automatic resume {attempts + 1} after {member.Qualified}'s usage limit");
        }
    }

    private static JsonObject? Payload(Message m)
    {
        try { return JsonNode.Parse(m.Payload) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
