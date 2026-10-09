using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// WHO MAY MERGE A TEAM BRANCH TO THE DEFAULT BRANCH: a person, or the person's own tenant
/// Concierge holding <see cref="Permits.Merge"/> while the tenant setting <c>concierge.mayMerge</c>
/// is on. The setting is read through <paramref name="mayMerge"/> on every merge, never captured, so
/// turning it off refuses the next merge of a Concierge session already running.
///
/// <para>
/// Asked first by Merge to main and by Bring current and merge, before anything is touched. The
/// route's <see cref="HumansOrConciergeMarker"/> has already refused every other machine principal;
/// this repeats that check rather than trusting it, and adds the setting. Past it, the Concierge
/// runs the same handler as a person - default branch known, fast-forward or merge commit, never a
/// force, a conflict refused, contributor mode 409 - and its tenant rows name the person with
/// <c>viaConcierge</c>, as <c>backlog.item-implemented</c> does.
/// </para>
/// </summary>
public sealed class ConciergeMergeGate(Func<bool> mayMerge)
{
    /// <summary>The Concierge's refusal while the setting is off: it names the setting and who turns it on.</summary>
    public const string SettingOff =
        "Merging is a person's step here: the setting " + TenantSettings.ConciergeMayMergeName
        + " is off, and only a person turns it on, in " + TenantSettings.ConciergeTab + ". Nothing was changed.";

    /// <summary>Who is merging, read once per request.</summary>
    /// <param name="Refusal">Non-null when the caller may not merge; answer it and change nothing.</param>
    /// <param name="ActorEmail">The person's email: the claim, or for the Concierge its owner's.</param>
    /// <param name="ViaConcierge">Whether the Concierge is merging for the person.</param>
    public sealed record Caller(IResult? Refusal, string? ActorEmail, bool ViaConcierge);

    public async Task<Caller> CheckAsync(HttpContext context, IUserStore users, CancellationToken ct)
    {
        var email = context.User.FindFirstValue(ClaimTypes.Email);

        // A PERSON: unchanged.
        if (PrincipalClaims.From(context.User) is not { Kind: not PrincipalKind.User } principal)
        {
            return new Caller(null, email, false);
        }

        if (!ConciergeLaunchFactory.IsConcierge(principal) || !principal.May(Permits.Merge))
        {
            return new Caller(
                Results.Json(new { error = PermitGate.HumansOnlyMessage }, statusCode: StatusCodes.Status403Forbidden),
                email, false);
        }

        var person = email ?? (await users.FindByIdAsync(principal.OwnerUserId!, ct))?.Email;
        if (!mayMerge())
        {
            return new Caller(
                Results.Json(new { error = SettingOff, setting = TenantSettings.ConciergeMayMergeName },
                    statusCode: StatusCodes.Status403Forbidden),
                person, true);
        }

        return new Caller(null, person, true);
    }

    /// <summary>
    /// <paramref name="log"/>, with <c>viaConcierge: true</c> added to every row's detail when the
    /// Concierge is merging: each refusal and the landing alike say whose act it was.
    /// </summary>
    public static ITenantLog For(ITenantLog log, Caller caller) =>
        caller.ViaConcierge && log is not ViaConciergeLog ? new ViaConciergeLog(log) : log;

    private sealed class ViaConciergeLog(ITenantLog inner) : ITenantLog
    {
        public Task WriteAsync(
            string? actorId, string? actorEmail, string action, string? subject = null,
            string? subjectName = null, string? detail = null, CancellationToken ct = default) =>
            inner.WriteAsync(actorId, actorEmail, action, subject, subjectName, Mark(detail), ct);

        public Task<TenantEvent?> FindLatestAsync(string action, string subject, CancellationToken ct = default) =>
            inner.FindLatestAsync(action, subject, ct);

        public Task<IReadOnlyList<TenantEvent>> FindLatestBySubjectAsync(
            IReadOnlyCollection<string> actions, CancellationToken ct = default) =>
            inner.FindLatestBySubjectAsync(actions, ct);

        public Task<TenantLogPage> ReadAsync(long? before = null, int take = 50, CancellationToken ct = default) =>
            inner.ReadAsync(before, take, ct);

        private static string Mark(string? detail)
        {
            var node = detail is null ? null : JsonNode.Parse(detail) as JsonObject;
            node ??= new JsonObject();
            node["viaConcierge"] = true;
            return node.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.General));
        }
    }
}
