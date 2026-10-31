using System.Data.Common;
using System.Text.Json;
using Harness.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harness.Host;

/// <summary>
/// WEB CREDENTIALS ENTRIES BECOME WEBSITE CONNECTIONS (CON 6), at the first start with this step: every
/// entry of every member running the Web Credentials plugin, on every team, becomes a website connection
/// named after its team and its entry (<c>Alpha supplier-admin</c>), assigned to that team, with its
/// fields and its password, token, TOTP secret and saved session - the platform's own session format,
/// so nothing is signed in to again. One <see cref="TenantActions.ConnectionsCarriedOver"/> row names how
/// many, never a value, and marks the step done: a later start does nothing.
/// </summary>
/// <remarks>
/// <para>
/// THE PLUGIN'S OWN MEMBERS STAY as they are, entries, schedule and events included - CON 9 retires them.
/// Its hourly verify schedule and its <c>status.changed</c> event are not moved.
/// </para>
/// <para>
/// Each connection is written with its own <c>connections.connected</c> and <c>team.connection-assigned</c>
/// rows, as every connection is. A start stopped half way writes no summary row, so the next start goes on:
/// a connection already called by an entry's name is that entry's, and is not made twice. An entry with no
/// http(s) Site URL cannot be a website connection; it is counted as left out.
/// </para>
/// </remarks>
public sealed class WebCredentialsCarryOver(
    ITeamStore teams, IPluginMemberSettingsStore settings, IPluginMemberSecrets secrets, IConnectionStore store, ITenantLog tenantLog,
    TeamRegistry? registry = null, TimeProvider? clock = null, ILogger<WebCredentialsCarryOver>? log = null) : IHostedService
{
    /// <summary>The Web Credentials plugin's id and its member's agent.</summary>
    public const string PluginId = "web-credentials";

    /// <summary>The plugin's entries table.</summary>
    public const string Table = "entries";

    /// <summary>The summary row's subject: what the step is done for.</summary>
    public const string Subject = "web-credentials";

    /// <summary>The entry columns carried as they are: the Web Credentials table's, by the same names.</summary>
    private static readonly string[] Carried =
    [
        WebsiteFields.SiteUrl, WebsiteFields.Hosts, WebsiteFields.Method, WebsiteFields.SignInUrl, WebsiteFields.Username,
        WebsiteFields.TokenHeader, WebsiteFields.CheckUrl, WebsiteFields.CheckText, WebsiteFields.UsernameSelector,
        WebsiteFields.PasswordSelector, WebsiteFields.SubmitSelector, WebsiteFields.TotpSelector,
    ];

    /// <summary>The entry's masked cells carried: its password, TOTP secret and token (the session apart).</summary>
    private static readonly string[] Masked = [WebsiteFields.Password, WebsiteFields.TotpSecret, WebsiteFields.Token];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CarryOverOnceAsync(cancellationToken);
        }
        catch (DbException exception)
        {
            log?.LogWarning(exception, "Web Credentials entries were not carried over as website connections; the next start tries again.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The step: how many connections it made, or null when it had already run.</summary>
    public async Task<int?> CarryOverOnceAsync(CancellationToken ct = default)
    {
        if (await tenantLog.FindLatestAsync(TenantActions.ConnectionsCarriedOver, Subject, ct) is not null) return null;

        var preset = CredentialPresets.For(ConnectionKinds.Website, null)!;
        var names = (await teams.TeamsAsync(ct)).ToDictionary(t => t.Id, t => string.IsNullOrWhiteSpace(t.Name) ? t.Id : t.Name!, StringComparer.OrdinalIgnoreCase);
        var made = 0;
        var already = 0;
        var left = 0;
        var forTeams = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var member in await teams.MembersAsync(ct))
        {
            if (!MemberRef.IsPlugin(member.Agent, out var plugin) || plugin != PluginId || !names.TryGetValue(member.Team, out var team)) continue;

            var id = new ContainerId(member.Team, member.Name);
            var stored = await settings.ForAsync(id, ct);
            if (!stored.Config.TryGetValue(Table, out var rows) || rows.ValueKind != JsonValueKind.Array) continue;
            var own = await secrets.ValuesAsync(id, ct);

            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || Text(row, "name") is not { Length: > 0 } entry) continue;

                var name = Name(team, entry);
                if (await store.NameTakenAsync(name, null, ct))
                {
                    already++;
                    continue;
                }

                if (Website(preset, stored, row, entry, own) is not { } website)
                {
                    left++;
                    continue;
                }

                var record = new ConnectionRecord(
                    Guid.NewGuid().ToString("N"), name, ConnectionKinds.Website, CredentialForm.AccountOf(preset, website.Settings), [],
                    (clock ?? TimeProvider.System).GetUtcNow(), null, ConnectionRecord.Ok, null, Credential: website.Settings);

                try
                {
                    await store.InsertCredentialAsync(record, website.Secrets, null, new TriggerAudit(
                        null, null, TenantActions.ConnectionConnected, record.Id, record.Name, JsonSerializer.Serialize(new
                        {
                            connection = record.Id, provider = ConnectionKinds.Website, account = record.Account,
                            fields = website.Settings.Fields.Where(f => f.Set).Select(f => f.Name), from = $"{PluginId} {id}",
                        })), ct);
                }
                catch (ConnectionNameTakenException)
                {
                    already++;
                    continue;
                }

                await store.AssignAsync(member.Team, record.Id, new TriggerAudit(
                    null, null, TenantActions.TeamConnectionAssigned, member.Team, team,
                    JsonSerializer.Serialize(new { team = member.Team, connection = record.Id, name = record.Name, kind = ConnectionKinds.Website, via = "Web Credentials" })), ct);
                made++;
                forTeams.Add(member.Team);
            }
        }

        await store.RecordAsync(new TriggerAudit(
            null, null, TenantActions.ConnectionsCarriedOver, Subject, "Web Credentials",
            JsonSerializer.Serialize(new { count = made, already, leftOut = left, teams = forTeams })), ct);

        if (made > 0 && registry is not null) await registry.RefreshConnectionPresenceAsync(ct);
        log?.LogInformation("{Count} Web Credentials entries became website connections ({Already} already were, {Left} left out).", made, already, left);
        return made;
    }

    /// <summary>The connection's name: its team's and its entry's, at most a connection's length.</summary>
    public static string Name(string team, string entry)
    {
        var name = $"{team.Trim()} {entry.Trim()}";
        return name.Length <= Connections.MaximumNameLength ? name : name[..Connections.MaximumNameLength].TrimEnd();
    }

    /// <summary>
    /// An entry as a website's fields and masked values: each column through the website's own form
    /// (a value the form refuses is left out, so one odd cell does not lose the entry), the plugin's
    /// languages and time zone under Advanced, and the masked cells beside them. Null without a Site URL
    /// the form takes, or a method.
    /// </summary>
    private static (CredentialSettings Settings, Dictionary<string, string> Secrets)? Website(
        CredentialPreset preset, PluginMemberSettings stored, JsonElement row, string entry, IReadOnlyDictionary<string, string> own)
    {
        var given = new List<CredentialFieldValue>();
        foreach (var column in Carried)
        {
            if (Text(row, column) is { Length: > 0 } value) given.Add(new CredentialFieldValue(column, value));
        }

        if (stored.Config.TryGetValue("languages", out var languages))
        {
            var tags = languages.ValueKind == JsonValueKind.Array
                ? string.Join(", ", languages.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()))
                : languages.ValueKind == JsonValueKind.String ? languages.GetString() : null;
            if (!string.IsNullOrWhiteSpace(tags)) given.Add(new CredentialFieldValue(WebsiteFields.Languages, tags));
        }

        if (stored.Config.TryGetValue("timeZone", out var zone) && zone.ValueKind == JsonValueKind.String && zone.GetString() is { Length: > 0 } timeZone)
        {
            given.Add(new CredentialFieldValue(WebsiteFields.TimeZone, timeZone));
        }

        foreach (var column in Masked)
        {
            if (own.TryGetValue(PluginMemberSecretStore.CellField(Table, entry, column), out var value) && value.Length > 0) given.Add(new CredentialFieldValue(column, value));
        }

        // ONE ODD CELL DOES NOT LOSE THE ENTRY: what the form refuses is left out, field by field.
        for (var tries = 0; tries <= given.Count; tries++)
        {
            var (settings, values, _, error) = CredentialForm.Read(preset, given);
            if (error is null)
            {
                var fields = settings!.Fields.ToList();
                if (own.TryGetValue(PluginMemberSecretStore.CellField(Table, entry, WebsiteFields.Session), out var session)
                    && session.Length is > 0 and <= WebsiteFields.MaxSessionLength)
                {
                    var at = fields.FindIndex(f => f.Name == WebsiteFields.Session);
                    fields[at] = fields[at] with { Set = true };
                    values![WebsiteFields.Session] = session;
                }

                return (settings with { Fields = fields }, values!);
            }

            var refused = given.FirstOrDefault(f => error.Contains($"`{f.Name}`", StringComparison.Ordinal));
            if (refused is null || refused.Name is WebsiteFields.SiteUrl or WebsiteFields.Method) return null;
            given.Remove(refused);
        }

        return null;
    }

    private static string? Text(JsonElement row, string column) =>
        row.TryGetProperty(column, out var cell) && cell.ValueKind == JsonValueKind.String ? cell.GetString()?.Trim() : null;
}
