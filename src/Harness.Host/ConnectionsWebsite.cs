using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// What a website command through Team Connections did (CON 6). <see cref="Sentence"/> is the outcome in
/// one line and <see cref="Report"/> the page it opened, each with every value of the connection taken
/// out; <see cref="Screenshot"/> that page; <see cref="Handoff"/> the session a <c>session</c> command hands
/// over, which only the asking member's workspace receives. <see cref="Held"/> is the CON 4 kind the
/// connection was marked with (<see cref="RunConnectionFailures"/>), or <see cref="ConnectionRecord.NeedsReconnect"/>
/// when it already needed a person and was not used; either way the run is held by it. <see cref="Failed"/> is
/// whether the run that asked fails: a refused sign-in, an expired session or a CAPTCHA hold the connection, a site that
/// did not answer fails the run alone, and a refused command (May act on pages off) is only answered.
/// </summary>
public sealed record WebsiteAnswer(
    string Sentence, string? Report = null, byte[]? Screenshot = null, ConnectionTestSecret? Handoff = null, string? Held = null,
    bool Failed = false, string? ConnectionId = null);

public sealed partial class Connections
{
    /// <summary>How long a website's test or command may take on its worker: a sign-in typed at a person's pace, then a page.</summary>
    public static readonly TimeSpan WebsiteBound = TimeSpan.FromMinutes(3.5);

    /// <summary>The sentence an act command is refused with while May act on pages is off: it names the switch.</summary>
    public static string MayActOffText(string name) =>
        $"{name}: acting on pages is off for this connection. A person turns on {WebsiteFields.MayActLabel} in the connection's settings ({Where}) to allow click, fill and submit.";

    /// <summary>
    /// The probe of a website's run: its unmasked fields, its masked values (the saved session among
    /// them) and its browser profile's key. Null when a masked value cannot be read on this Host.
    /// </summary>
    private async Task<WebsiteProbe?> WebsiteProbeAsync(
        ConnectionRecord connection, CredentialSettings settings, string command, CancellationToken ct,
        string? path = null, string? expect = null, int? minutes = null, IReadOnlyList<WebsiteStep>? steps = null)
    {
        var stored = await store.CredentialSecretsAsync(connection.Id, ct) ?? new Dictionary<string, string>();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in settings.Fields.Where(f => f.Set))
        {
            if (!field.Masked) fields[field.Name] = field.Value!;
            else if (stored.TryGetValue(field.Name, out var value)) secrets[field.Name] = value;
            else return null;
        }

        return new WebsiteProbe(connection.Name, SignInProfileKey.Connection(connection.Id), command, fields, secrets, path, expect, minutes, steps);
    }

    /// <summary>Every value a website's run holds, no sentence may carry: its masked values, its username and each part of its saved session.</summary>
    private static IReadOnlyList<string> WebsiteSecrets(WebsiteProbe probe)
    {
        var values = new List<string>(probe.Secrets.Values);
        if (probe.Fields.TryGetValue(WebsiteFields.Username, out var username) && username.Length >= ValueRedactor.MinimumLength) values.Add(username);
        if (probe.Secrets.TryGetValue(WebsiteFields.Session, out var session)) values.AddRange(SignInSessionValue.SecretParts(session).Anywhere);
        return values;
    }

    /// <summary>
    /// TEAM CONNECTIONS' WEBSITE COMMAND (CON 6): <paramref name="command"/> - <c>check</c>, <c>session</c>
    /// or <c>act</c> - on the website connection <paramref name="id"/>, in the platform's browser on a
    /// worker, held to the site's hosts. Refused before anything is sent when the connection needs a
    /// person (the member never signs in again over a refusal), and an act while May act on pages is off.
    /// A refused sign-in or an expired session marks it Needs you, a CAPTCHA or device approval marks it
    /// Needs you saying the person signs in with Sign in (CON 4); a site that does not answer marks
    /// nothing. One <c>connections.website-used</c> row, never a value.
    /// </summary>
    public async Task<WebsiteAnswer> WebsiteAsync(
        ContainerId member, string id, string command, WorkerAsks asks, CancellationToken ct = default,
        string? path = null, string? expect = null, int? minutes = null, IReadOnlyList<WebsiteStep>? steps = null)
    {
        if (await store.GetAsync(id, ct) is not { } connection || connection.Credential is not { } settings || connection.Kind != ConnectionKinds.Website)
        {
            return new WebsiteAnswer($"There is no website connection '{id}'.");
        }

        if (connection.Status == ConnectionRecord.NeedsReconnect)
        {
            return new WebsiteAnswer(
                $"{connection.Name} needs a person, so it was not used: {connection.StatusReason ?? "it needs reconnecting."} "
                + $"A person fixes it in {Where} (Sign in, or edit it and Test connection).", Held: ConnectionRecord.NeedsReconnect, Failed: true, ConnectionId: id);
        }

        if (command == WebsiteProbe.Act && !WebsiteFields.MayActOn(settings)) return new WebsiteAnswer(MayActOffText(connection.Name));

        ConnectionTested? ran;
        IReadOnlyList<string> secrets;
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            if (await WebsiteProbeAsync(connection, settings, command, ct, path, expect, minutes, steps) is not { } probe)
            {
                return new WebsiteAnswer($"A value of connection {connection.Named} could not be read on this Host. A person edits it in {Where}.", Failed: true);
            }

            secrets = WebsiteSecrets(probe);
            var asked = await asks.AskAsync<ConnectionTested>(new TestConnection(WorkerAsks.NewRequest(), probe.Seal()), WebsiteBound, ct);
            if (asked.Answer is not { } answer)
            {
                return new WebsiteAnswer($"{connection.Name}: the browser did not run: {asked.Why} Nothing was changed; try again.", Failed: true);
            }

            ran = answer;
        }
        finally
        {
            gate.Release();
        }

        var sentence = ConnectionTester.Redact(ran.Sentence, secrets);
        var report = ran.Report is { } words ? ConnectionTester.Redact(words, secrets) : null;
        var held = ran.Outcome == ConnectionTested.Refused
            ? ran.Failure == WebsiteFailures.Captcha ? RunConnectionFailures.SignIn : RunConnectionFailures.Refused
            : null;

        await store.RecordAsync(ConnectionActor.ForMember(member).Row(
            TenantActions.ConnectionWebsiteUsed, id, connection.Name,
            new { connection = id, member = member.ToString(), command, path, outcome = ran.Outcome, failure = ran.Failure, sentence }), ct);

        if (held is not null)
        {
            // CON 4: every member using it reads Needs you and waits, with the browser's own sentence.
            await MarkFromRunAsync(id, RunConnectionFailures.Sentence(held, sentence), held, member, TeamConnectionsMember.Label, ct);
        }

        return new WebsiteAnswer(sentence, report, ran.Screenshot, ran.Handoff, held, ran.Outcome != ConnectionTested.Ok, id);
    }

    /// <summary>
    /// THE SIGN-IN PAGE'S ENTRY FOR A WEBSITE CONNECTION: its Site URL, its other hosts and the masked
    /// field its session is kept in, or why it cannot be signed in to - a sentence for the person.
    /// </summary>
    public async Task<(SignInEntry? Entry, string? Refusal)> WebsiteSignInAsync(string id, CancellationToken ct = default)
    {
        if (await store.GetAsync(id, ct) is not { } connection) return (null, $"There is no connection '{id}': it may have been removed. Open Sign in again from {Where}.");
        if (connection.Credential is not { } settings || connection.Kind != ConnectionKinds.Website)
        {
            return (null, $"Connection {connection.Named} is not a website, so there is nothing to sign in to here.");
        }

        if (!Uri.TryCreate(WebsiteFields.Value(settings, WebsiteFields.SiteUrl), UriKind.Absolute, out var site) || site.Scheme is not ("http" or "https"))
        {
            return (null, $"Connection {connection.Named} has no http(s) Site URL to open. Edit it in {Where} first.");
        }

        var hosts = WebsiteFields.HostsOf(site.ToString(), WebsiteFields.Value(settings, WebsiteFields.Hosts));
        return (new SignInEntry(site.ToString(), [.. hosts.Skip(1)], WebsiteFields.Session), null);
    }

    /// <summary>
    /// KEEPS THE SESSION A PERSON SIGNED IN WITH as the website's masked saved session, replacing what was
    /// there, with a <c>connections.signed-in</c> row naming how many hosts it holds and the providers among
    /// them - never the session. A connection that needed a person is ok again, and every member it held
    /// is told (CON 4). Throws with a sentence when it cannot.
    /// </summary>
    public async Task SaveWebsiteSessionAsync(
        ConnectionActor actor, string id, SignInStorageState state, IReadOnlyList<string> hosts, IReadOnlyList<string> providers, CancellationToken ct = default)
    {
        if (state.Json.Length > WebsiteFields.MaxSessionLength) throw new InvalidOperationException("the session is larger than a connection keeps.");

        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            if (await store.GetAsync(id, ct) is not { } connection || connection.Credential is not { } settings || connection.Kind != ConnectionKinds.Website)
            {
                throw new InvalidOperationException("the connection is no longer a website.");
            }

            var secrets = new Dictionary<string, string>(await store.CredentialSecretsAsync(id, ct) ?? new Dictionary<string, string>(), StringComparer.Ordinal)
            {
                [WebsiteFields.Session] = state.Json,
            };

            var spec = WebsiteFields.All.Single(f => f.Name == WebsiteFields.Session);
            var fields = settings.Fields.Where(f => f.Name != WebsiteFields.Session).ToList();
            fields.Add(new CredentialField(spec.Name, spec.Label, true, null, true));
            var updated = await store.UpdateCredentialAsync(id, connection.Account, settings with { Fields = fields }, secrets, actor.Row(
                TenantActions.ConnectionSignedIn, id, connection.Name,
                new
                {
                    connection = id, name = connection.Name, hosts = hosts.Count, providers,
                    wasNeedingReconnect = connection.Status == ConnectionRecord.NeedsReconnect,
                }), ct);
            if (!updated) throw new InvalidOperationException("the connection was removed.");

            ForgetApis(id);
            await HealthChangedAsync(id, ct);
        }
        finally
        {
            gate.Release();
        }
    }
}
