using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Connections;
using Yawble.Core.Email;
using Yawble.Core.Ipc;

namespace Yawble.Core.Integrations;

/// <summary>An Email integration as the app lists it: its name, the email Connection it signs in with (null when removed), and who it is offered to.</summary>
public sealed record EmailIntegrationView(long Id, string Name, long? ConnectionId, string? Connection, bool EnabledAll, IReadOnlyList<long> TeamIds);

/// <summary>
/// The built-in Email integration (B133, card 590): a mailbox, an email Connection, offered to teams like the other integrations
/// (to every team, or to some), whose scripts read it through the actions in <see cref="Scripts.Actions.EmailScriptActions"/>. The password
/// stays in the credential store under the Connection; nothing here reads or writes it except to sign in. Every script call is logged to
/// integration_calls as "email:&lt;name&gt;" with the script member as the caller, the action and its arguments (no message content).
/// RPCs: integrations.emailList, integrations.emailSave {id?, name, connectionId}, integrations.emailEnable {name, all?, teamId?, on?},
/// integrations.emailRemove {name}.
/// </summary>
public sealed partial class IntegrationService
{
    /// <summary>The call log's integration prefix of an Email integration.</summary>
    public const string EmailIntegrationPrefix = "email:";

    private static bool IsEmailIntegrationMethod(string method) => method.StartsWith("integrations.email", StringComparison.Ordinal);

    private JsonNode? DispatchEmailIntegration(string method, JsonObject? p) => method switch
    {
        "integrations.emailList" => new JsonArray(EmailIntegrations().Select(e => (JsonNode)EmailIntegrationJson(e)).ToArray()),
        "integrations.emailSave" => EmailIntegrationJson(SaveEmailIntegration(OptLong(p, "id"), Str(p, "name"),
            OptLong(p, "connectionId") ?? throw new RpcException("bad_params", "which email account (connectionId)?"))),
        "integrations.emailEnable" => EnableEmailIntegrationJson(Str(p, "name"), p?["all"]?.GetValue<bool>(), OptLong(p, "teamId"), p?["on"]?.GetValue<bool>() ?? true),
        "integrations.emailRemove" => RemoveEmailIntegrationJson(Str(p, "name")),
        _ => throw new RpcException("unknown_method", $"no method named '{method}'"),
    };

    private static JsonObject EmailIntegrationJson(EmailIntegrationView e) => new()
    {
        ["id"] = e.Id, ["name"] = e.Name, ["kind"] = "email", ["connectionId"] = e.ConnectionId, ["connection"] = e.Connection,
        ["enabledAll"] = e.EnabledAll, ["teamIds"] = new JsonArray(e.TeamIds.Select(t => (JsonNode)t).ToArray()),
    };

    private JsonNode EnableEmailIntegrationJson(string name, bool? all, long? teamId, bool on)
    {
        EnableEmailIntegration(name, all, teamId, on);
        return new JsonObject { ["ok"] = true };
    }

    private JsonNode RemoveEmailIntegrationJson(string name)
    {
        RemoveEmailIntegration(name);
        return new JsonObject { ["ok"] = true };
    }

    // ------------------------------------------------------------------ setup

    /// <summary>Every Email integration, by name.</summary>
    public IReadOnlyList<EmailIntegrationView> EmailIntegrations()
    {
        var rows = new List<(long Id, string Name, long? Conn, bool All)>();
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, connection_id, enabled_all FROM email_integrations ORDER BY name COLLATE NOCASE;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetInt64(3) != 0));
        }
        return rows.Select(x => new EmailIntegrationView(x.Id, x.Name, x.Conn, x.Conn is { } cid ? connections?.Get(cid)?.Name : null, x.All, EmailTeamsOf(x.Id))).ToList();
    }

    /// <summary>
    /// Adds (no <paramref name="id"/>) or edits an Email integration: its name and the email Connection it signs in with. The Connection must
    /// be an email account. A new one is offered to no team until <see cref="EnableEmailIntegration"/>.
    /// </summary>
    public EmailIntegrationView SaveEmailIntegration(long? id, string name, long connectionId)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new ArgumentException("an Email integration needs a name");
        if (n.Length > 80 || n.Any(char.IsControl)) throw new ArgumentException("the name is at most 80 characters, without control characters");
        var view = Conns.Get(connectionId) ?? throw new ArgumentException("no such connection");
        if (view.Kind != EmailConnection.Kind) throw new ArgumentException($"'{view.Name}' is not an email account");
        var now = Now();
        try
        {
            if (id is { } eid)
            {
                if (Exec("UPDATE email_integrations SET name = $n, connection_id = $c, updated_at = $now WHERE id = $id;",
                        ("$n", n), ("$c", connectionId), ("$now", now), ("$id", eid)) == 0)
                    throw new ArgumentException("no such Email integration");
            }
            else
                Exec("INSERT INTO email_integrations (name, connection_id, enabled_all, created_at, updated_at) VALUES ($n, $c, 0, $now, $now);",
                    ("$n", n), ("$c", connectionId), ("$now", now));
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            throw new ArgumentException($"there is already an Email integration named '{n}'");
        }
        return EmailIntegrationNamed(n) ?? throw new InvalidOperationException("the Email integration was not saved");
    }

    /// <summary>Offers the Email integration to every team (<paramref name="all"/>) and/or turns it on or off for one team.</summary>
    public void EnableEmailIntegration(string name, bool? all, long? teamId, bool on)
    {
        var e = EmailIntegrationNamed(name) ?? throw new ArgumentException($"no Email integration named '{Clip(name ?? "", 60)}'");
        if (all is { } al) Exec("UPDATE email_integrations SET enabled_all = $a, updated_at = $now WHERE id = $id;", ("$a", al ? 1 : 0), ("$now", Now()), ("$id", e.Id));
        if (teamId is { } t)
        {
            if (on && Scalar("SELECT count(*) FROM teams WHERE id = $t;", ("$t", t)) == 0) throw new ArgumentException($"no team {t}");
            Exec(on ? "INSERT OR IGNORE INTO email_integration_teams (email_id, team_id) VALUES ($e, $t);" : "DELETE FROM email_integration_teams WHERE email_id = $e AND team_id = $t;",
                ("$e", e.Id), ("$t", t));
        }
    }

    /// <summary>Removes the Email integration (its offers go with it); the email Connection stays.</summary>
    public void RemoveEmailIntegration(string name)
    {
        var e = EmailIntegrationNamed(name) ?? throw new ArgumentException($"no Email integration named '{Clip(name ?? "", 60)}'");
        Exec("DELETE FROM email_integrations WHERE id = $id;", ("$id", e.Id));
    }

    /// <summary>The Email integrations turned on for the team (by its name, any case), by name.</summary>
    public IReadOnlyList<string> EmailIntegrationsOfferedTo(string team)
    {
        var (teamId, _) = ScriptTeam(team);
        return EmailIntegrations().Where(e => e.EnabledAll || e.TeamIds.Contains(teamId)).Select(e => e.Name).ToList();
    }

    private EmailIntegrationView? EmailIntegrationNamed(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.StartsWith(EmailIntegrationPrefix, StringComparison.OrdinalIgnoreCase)) n = n[EmailIntegrationPrefix.Length..].Trim();
        return n.Length == 0 ? null : EmailIntegrations().FirstOrDefault(e => string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase));
    }

    private List<long> EmailTeamsOf(long emailId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT team_id FROM email_integration_teams WHERE email_id = $e ORDER BY team_id;";
        cmd.Parameters.AddWithValue("$e", emailId);
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    // ------------------------------------------------------------------ a script's call

    /// <summary>
    /// Runs one Email action for a script: the integration <paramref name="integration"/> ("name" or "email:name") must exist and be turned on
    /// for the caller's team, and its email Connection be there with its password. Logs the call (the action and its arguments, never message
    /// content) with the script member as the caller, then opens a signed-in IMAP session and runs <paramref name="work"/> on its mailbox.
    /// Refusals throw <see cref="ScriptApiRefusedException"/>; a mail failure throws <see cref="MailException"/>; a bad argument
    /// <see cref="ArgumentException"/>. There is no send or delete action.
    /// </summary>
    public async Task<T> UseEmailForScriptAsync<T>(ScriptCaller caller, string integration, string action, JsonObject args,
        Func<EmailMailbox, Task<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(work);
        var (teamId, teamName) = ScriptTeam(caller.Team);
        var email = EmailIntegrationNamed(integration)
            ?? throw new ScriptApiRefusedException($"refused: no Email integration named '{Clip(SecretScrubber.Scrub(integration ?? ""), 60)}'");
        if (!email.EnabledAll && !email.TeamIds.Contains(teamId)) throw new ScriptApiRefusedException($"refused: {email.Name} is not turned on for {teamName}");
        if (email.ConnectionId is not { } cid) throw new ScriptApiRefusedException($"refused: {email.Name} has no email account; choose one in Integrations");

        EmailAccount account;
        try { account = EmailAccountOf(cid); }
        catch (ArgumentException e) { throw new ScriptApiRefusedException("refused: " + e.Message); }

        var member = caller.Member.Contains('/', StringComparison.Ordinal) ? caller.Member : $"{teamName}/{caller.Member}";
        string[] callArgs = [action, SecretScrubber.Scrub((args ?? []).ToJsonString()), $"script run {Clip(caller.ScriptRunId, 100)}"];
        Insert(EmailIntegrationPrefix + email.Name, teamName, member, null, callArgs, Decisions.Allow, null, null, new Provenance.Actor(ScriptVia, null));

        await using var session = await MailClients.OpenImapAsync(account, ct);
        return await work(new EmailMailbox(session));
    }
}
