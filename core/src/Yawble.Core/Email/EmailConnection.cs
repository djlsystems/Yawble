using System.Globalization;
using System.Net;
using Yawble.Core.Connections;

namespace Yawble.Core.Email;

/// <summary>
/// Where a mailbox is and who signs in to it: the non-secret half of an email Connection. <see cref="User"/> is the sign-in name
/// (the address unless the person gave another). The password is never part of this record.
/// </summary>
public sealed record EmailSettings(string Address, string ImapHost, int ImapPort, string SmtpHost, int SmtpPort, string User);

/// <summary>A provider's server addresses, filled in by the form's preset button and by <c>preset</c> on a save. <see cref="Note"/> is shown beside the password.</summary>
public sealed record EmailPreset(string Id, string Label, string ImapHost, int ImapPort, string SmtpHost, int SmtpPort, string Note);

/// <summary>
/// The Connection kind "email": a mailbox the app signs in to over IMAP (reading) and SMTP (checked when the Connection is tested; nothing sends mail). Its fields are
/// <see cref="AddressKey"/>, <see cref="ImapHostKey"/>, <see cref="ImapPortKey"/>, <see cref="SmtpHostKey"/>, <see cref="SmtpPortKey"/> and
/// <see cref="UserKey"/> (kept in the row) and <see cref="PasswordKey"/> (secret: only in the credential store, like every Connection secret).
/// </summary>
public static class EmailConnection
{
    public const string Kind = "email";

    public const string AddressKey = "address";
    public const string ImapHostKey = "imapHost";
    public const string ImapPortKey = "imapPort";
    public const string SmtpHostKey = "smtpHost";
    public const string SmtpPortKey = "smtpPort";
    public const string UserKey = "user";
    public const string PasswordKey = "password";

    /// <summary>Gmail: IMAP on 993 and SMTP on 465, both TLS from the first byte. Gmail signs in a script only with an app password.</summary>
    public static readonly EmailPreset Gmail = new("gmail", "Gmail", "imap.gmail.com", 993, "smtp.gmail.com", 465,
        "Use a Google app password; it needs 2-Step Verification");

    public static IReadOnlyList<EmailPreset> Presets { get; } = [Gmail];

    public static EmailPreset? Preset(string? id) =>
        id is null ? null : Presets.FirstOrDefault(p => string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Checks and tidies what the person typed: an address with one @, host names (no scheme, path, space or user info), ports 1 to 65535.
    /// A blank user is the address. Throws <see cref="ArgumentException"/> with a sentence the form shows.
    /// </summary>
    public static EmailSettings Settings(string? address, string? imapHost, int? imapPort, string? smtpHost, int? smtpPort, string? user)
    {
        var a = (address ?? "").Trim();
        var at = a.IndexOf('@');
        if (at <= 0 || at != a.LastIndexOf('@') || at == a.Length - 1 || a.Any(char.IsWhiteSpace))
            throw new ArgumentException("give the email address, e.g. name@example.com");
        var u = string.IsNullOrWhiteSpace(user) ? a : user.Trim();
        return new EmailSettings(a, Host(imapHost, "the IMAP server"), Port(imapPort, "the IMAP port"), Host(smtpHost, "the SMTP server"),
            Port(smtpPort, "the SMTP port"), u);
    }

    private static string Host(string? text, string what)
    {
        var h = (text ?? "").Trim().TrimEnd('.');
        if (h.Length == 0) throw new ArgumentException($"give {what}, e.g. imap.example.com");
        if (IPAddress.TryParse(h, out _)) return h;
        if (Uri.CheckHostName(h) != UriHostNameType.Dns) throw new ArgumentException($"{what} must be a host name such as mail.example.com, without https:// or a path");
        return h.ToLowerInvariant();
    }

    private static int Port(int? port, string what) =>
        port is >= 1 and <= 65535 ? port.Value : throw new ArgumentException($"{what} must be a number from 1 to 65535");

    /// <summary>A port as sent by a form: a number, or digits in a string. Null when absent or blank.</summary>
    public static int? ParsePort(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null
        : int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n
        : throw new ArgumentException("a port must be a number from 1 to 65535");

    /// <summary>The Connection's fields for these settings, in the form's order; the password field carries no value (it goes to the store).</summary>
    public static IReadOnlyList<ConnectionField> Fields(EmailSettings s) =>
    [
        new(AddressKey, "Email address", false, s.Address),
        new(ImapHostKey, "IMAP server", false, s.ImapHost),
        new(ImapPortKey, "IMAP port", false, s.ImapPort.ToString(CultureInfo.InvariantCulture)),
        new(SmtpHostKey, "SMTP server", false, s.SmtpHost),
        new(SmtpPortKey, "SMTP port", false, s.SmtpPort.ToString(CultureInfo.InvariantCulture)),
        new(UserKey, "User name", false, s.User),
        new(PasswordKey, "Password", true, null),
    ];

    /// <summary>The settings a saved email Connection holds. Throws <see cref="ArgumentException"/> when it is not one, or its fields no longer check.</summary>
    public static EmailSettings Settings(ConnectionView view)
    {
        if (view.Kind != Kind) throw new ArgumentException($"'{view.Name}' is not an email account");
        string? F(string key) => view.Fields.FirstOrDefault(f => f.Key == key && !f.Secret)?.Shown;
        int? P(string key) { try { return ParsePort(F(key)); } catch (ArgumentException) { return null; } }
        return Settings(F(AddressKey), F(ImapHostKey), P(ImapPortKey), F(SmtpHostKey), P(SmtpPortKey), F(UserKey));
    }
}
