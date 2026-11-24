using System.Text.Json.Nodes;
using Yawble.Core.Email;

namespace Yawble.Core.Integrations;

/// <summary>
/// Email accounts as Connections (kind "email", B133): saving one from the form, Test (signs in to IMAP and lists the folders, then
/// authenticates to SMTP; nothing is sent), and the host-side way in for the Email integration: <see cref="OpenEmailImapAsync"/> and
/// <see cref="OpenEmailSmtpAsync"/> open a signed-in session through the <see cref="IMailClientFactory"/> seam.
/// </summary>
public sealed partial class IntegrationService
{
    private IMailClientFactory? _defaultMail;

    /// <summary>The mail-client seam: the one given to the constructor (tests: a factory pointed at the fake server), else MailKit's.</summary>
    internal IMailClientFactory MailClients => mail ?? (_defaultMail ??= new MailKitClientFactory());

    /// <summary>
    /// integrations.connectionSave for an email account: { kind: "email", id?, name?, preset?, address, imapHost, imapPort, smtpHost, smtpPort,
    /// user?, password? }. preset "gmail" fills the servers and ports not given. On an edit, what is not sent stays; a user left blank is the
    /// address. A new account needs its password; an edit without one keeps the stored password. Changing where or as whom it signs in
    /// without a new password makes it untested again.
    /// </summary>
    private JsonNode EmailConnectionSave(JsonObject? p)
    {
        var id = OptLong(p, "id");
        var before = id is { } eid ? Conns.Get(eid) ?? throw new ArgumentException("no such connection") : null;
        if (before is not null && before.Kind != EmailConnection.Kind) throw new ArgumentException("that connection is not an email account");
        EmailSettings? old = null;
        if (before is not null)
        {
            try { old = EmailConnection.Settings(before); }
            catch (ArgumentException) { /* its saved fields no longer check: the edit must give them again */ }
        }
        var preset = OptStr(p, "preset") is { } pid ? EmailConnection.Preset(pid) ?? throw new ArgumentException($"no email preset named '{pid}'") : null;

        var address = EmailText(p, "address") ?? old?.Address;
        // the user follows the address when it was the address (the default) and the edit does not name one
        var user = p is not null && p.ContainsKey("user") ? EmailText(p, "user")
            : old is not null && old.User != old.Address ? old.User : null;
        var settings = EmailConnection.Settings(address,
            EmailText(p, "imapHost") ?? preset?.ImapHost ?? old?.ImapHost, PortOf(p, "imapPort") ?? preset?.ImapPort ?? old?.ImapPort,
            EmailText(p, "smtpHost") ?? preset?.SmtpHost ?? old?.SmtpHost, PortOf(p, "smtpPort") ?? preset?.SmtpPort ?? old?.SmtpPort, user);

        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        var password = p?["password"] is JsonValue pv && pv.TryGetValue<string>(out var pw) ? pw
            : p?["secrets"]?[EmailConnection.PasswordKey] is JsonValue sv && sv.TryGetValue<string>(out var spw) ? spw : null;
        if (password?.Trim() is { Length: > 0 } given) secrets[EmailConnection.PasswordKey] = given;

        var name = EmailText(p, "name") ?? before?.Name;
        if (name is null)
        {
            var taken = Conns.List().Where(c => c.Id != id).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            name = settings.Address;
            for (var i = 2; taken.Contains(name); i++) name = $"{settings.Address} {i}";
        }
        var saved = Conns.Save(id, name, EmailConnection.Fields(settings), secrets, EmailConnection.Kind);
        if (before is not null && secrets.Count == 0 && old != settings && saved.Status != "untested")
        {
            Conns.MarkStatus(saved.Id, "untested", null);
            saved = Conns.Get(saved.Id)!;
        }
        return ConnectionJson(saved);
    }

    private static string? EmailText(JsonObject? p, string key) => p?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;

    private static int? PortOf(JsonObject? p, string key) => p?[key] switch
    {
        JsonValue v when Ui.UiRpc.WholeNumber(v, out var n) => n is >= int.MinValue and <= int.MaxValue ? (int)n : -1,
        JsonValue v when v.TryGetValue<string>(out var s) => EmailConnection.ParsePort(s),
        null => null,
        _ => throw new ArgumentException($"{key} must be a number from 1 to 65535"),
    };

    /// <summary>
    /// Test for an email account: signs in to IMAP and lists the folders, then authenticates to SMTP and quits (nothing is sent). Both
    /// succeed: works. Either fails (the password refused, a server not reached, no secure connection): needs reconnect, with what failed.
    /// Not logged as a call; the reason never holds the password.
    /// </summary>
    private async Task<JsonNode> EmailConnectionTestAsync(Connections.ConnectionView view, CancellationToken ct)
    {
        string status, reason;
        try
        {
            var account = EmailAccountOf(view.Id);
            var s = account.Settings;
            int folders;
            await using (var imap = await MailClients.OpenImapAsync(account, ct))
                folders = (await imap.ListFoldersAsync(ct)).Count;
            await using (await MailClients.OpenSmtpAsync(account, ct)) { }
            status = "works";
            reason = $"signed in to {s.ImapHost}:{s.ImapPort} ({folders} {(folders == 1 ? "folder" : "folders")}) and {s.SmtpHost}:{s.SmtpPort} at {Stamp()}";
        }
        catch (MailException e)
        {
            status = "needs_reconnect";
            reason = $"{Clip(e.Message, 240)} at {Stamp()}";
        }
        catch (ArgumentException e)
        {
            status = "needs_reconnect";
            reason = Clip(e.Message, 240);
        }
        Conns.MarkStatus(view.Id, status, reason);
        return new JsonObject { ["status"] = status, ["reason"] = reason, ["changed"] = true };
    }

    /// <summary>
    /// An email Connection's sign-in, read from its row and the credential store. Host only: the account holds the password. Throws
    /// <see cref="ArgumentException"/> when the Connection is missing, not an email account, or has no password on this computer.
    /// </summary>
    internal EmailAccount EmailAccountOf(long connectionId)
    {
        var view = Conns.Get(connectionId) ?? throw new ArgumentException("no such connection");
        var settings = EmailConnection.Settings(view);
        if (!Conns.Secrets(connectionId).TryGetValue(EmailConnection.PasswordKey, out var password))
            throw new ArgumentException($"'{view.Name}' has no password on this computer; edit it and enter the password again");
        return new EmailAccount(settings, password);
    }

    /// <summary>A signed-in IMAP session for an email Connection (the Email integration's read actions). Throws <see cref="MailException"/> or <see cref="ArgumentException"/>.</summary>
    internal Task<IImapSession> OpenEmailImapAsync(long connectionId, CancellationToken ct) => MailClients.OpenImapAsync(EmailAccountOf(connectionId), ct);

    /// <summary>An authenticated SMTP session for an email Connection (it sends nothing). Throws <see cref="MailException"/> or <see cref="ArgumentException"/>.</summary>
    internal Task<ISmtpSession> OpenEmailSmtpAsync(long connectionId, CancellationToken ct) => MailClients.OpenSmtpAsync(EmailAccountOf(connectionId), ct);
}
