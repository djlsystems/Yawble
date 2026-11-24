using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Text;

namespace Yawble.Core.Email;

/// <summary>A mailbox as a message names it: the display name (null when none) and the address. The built-in schema EmailAddress.</summary>
public sealed record EmailAddress(string? Name, string Address)
{
    public JsonObject ToJson() => new() { ["name"] = Name, ["address"] = Address };
}

/// <summary>
/// One message as a script sees it (the built-in schema EmailMessage, scripts-design.md section 9). <see cref="Id"/> is stable across sessions:
/// Gmail's X-GM-MSGID in decimal, otherwise "UIDVALIDITY:UID" of <see cref="Folder"/>. <see cref="ThreadId"/>: Gmail's X-GM-THRID in decimal,
/// otherwise the thread's first Message-ID (References, else In-Reply-To, else its own), else <see cref="Id"/>. <see cref="ReceivedAt"/> is
/// the server's INTERNALDATE in UTC. <see cref="Snippet"/> is the first 500 characters of the plain text, its white space collapsed.
/// <see cref="Body"/> is set by get only (the whole plain text; an HTML-only message's text converted from its HTML).
/// </summary>
public sealed record EmailMessage(
    string Id, string ThreadId, string? MessageId, string Folder, EmailAddress From, IReadOnlyList<EmailAddress> To, IReadOnlyList<EmailAddress> Cc,
    string Subject, DateTimeOffset ReceivedAt, string Snippet, bool HasAttachments, bool Unread, string? Body = null)
{
    public const int SnippetLength = 500;

    /// <summary>receivedAt as written: ISO-8601 UTC, to the second ("2026-11-23T09:15:00Z").</summary>
    public static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The JSON a script gets. body is present only when it was fetched (get).</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["id"] = Id, ["threadId"] = ThreadId, ["messageId"] = MessageId, ["folder"] = Folder, ["from"] = From.ToJson(),
            ["to"] = new JsonArray(To.Select(a => (JsonNode)a.ToJson()).ToArray()),
            ["cc"] = new JsonArray(Cc.Select(a => (JsonNode)a.ToJson()).ToArray()),
            ["subject"] = Subject, ["receivedAt"] = Instant(ReceivedAt), ["snippet"] = Snippet,
            ["hasAttachments"] = HasAttachments, ["unread"] = Unread,
        };
        if (Body is not null) o["body"] = Body;
        return o;
    }

    /// <summary>The message's plain text: its text/plain body, else its HTML body as text, else "".</summary>
    public static string PlainText(MimeMessage message)
    {
        if (message.TextBody is { } text) return Normalize(text);
        if (message.HtmlBody is { } html) return Normalize(new HtmlToText().Convert(html));
        return "";
    }

    /// <summary>The first <see cref="SnippetLength"/> characters of the text, runs of white space as one space.</summary>
    public static string SnippetOf(string plainText)
    {
        var flat = Regex.Replace(plainText, @"\s+", " ").Trim();
        if (flat.Length <= SnippetLength) return flat;
        var cut = SnippetLength;
        if (char.IsHighSurrogate(flat[cut - 1])) cut--; // never half a character
        return flat[..cut];
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();

    /// <summary>The mailboxes of an address list, groups flattened.</summary>
    public static IReadOnlyList<EmailAddress> Addresses(InternetAddressList list) =>
        list.Mailboxes.Select(m => new EmailAddress(string.IsNullOrWhiteSpace(m.Name) ? null : m.Name, m.Address ?? "")).ToList();

    /// <summary>The thread's root Message-ID for a server without Gmail's thread ids: References' first, else In-Reply-To, else the message's own.</summary>
    public static string? RootMessageId(MimeMessage message) =>
        message.References.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))
        ?? (string.IsNullOrWhiteSpace(message.InReplyTo) ? null : message.InReplyTo.Trim().Trim('<', '>'))
        ?? (string.IsNullOrWhiteSpace(message.MessageId) ? null : message.MessageId);

    /// <summary>Builds the message from what the server said about it and its MIME content.</summary>
    public static EmailMessage Build(string id, string? gmailThreadId, string folder, DateTimeOffset receivedAt, bool unread, MimeMessage message, bool withBody)
    {
        var text = PlainText(message);
        var from = Addresses(message.From).FirstOrDefault() ?? (message.Sender is { } sender ? new EmailAddress(string.IsNullOrWhiteSpace(sender.Name) ? null : sender.Name, sender.Address ?? "") : null)
            ?? new EmailAddress(null, "");
        return new EmailMessage(
            id, gmailThreadId ?? RootMessageId(message) ?? id, string.IsNullOrWhiteSpace(message.MessageId) ? null : message.MessageId, folder,
            from, Addresses(message.To), Addresses(message.Cc), message.Subject ?? "", receivedAt.ToUniversalTime(), SnippetOf(text),
            message.Attachments.Any(), unread, withBody ? text : null);
    }
}

/// <summary>A message id as the Email integration writes it: Gmail's X-GM-MSGID, or a folder's UIDVALIDITY and the UID.</summary>
public readonly record struct EmailMessageId(ulong? GmailId, uint UidValidity, uint Uid)
{
    public static string Gmail(ulong id) => id.ToString(CultureInfo.InvariantCulture);

    public static string Imap(uint uidValidity, uint uid) =>
        uidValidity.ToString(CultureInfo.InvariantCulture) + ":" + uid.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses an id for this kind of server; a malformed one throws <see cref="ArgumentException"/>.</summary>
    public static EmailMessageId Parse(string? id, bool gmail)
    {
        var s = (id ?? "").Trim();
        if (gmail)
        {
            if (ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var g) && g > 0) return new(g, 0, 0);
            throw new ArgumentException($"'{Clip(s)}' is not a message id of this mailbox (a Gmail id is a number)");
        }
        var colon = s.IndexOf(':');
        if (colon > 0
            && uint.TryParse(s[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            && uint.TryParse(s[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var u) && v > 0 && u > 0)
            return new(null, v, u);
        throw new ArgumentException($"'{Clip(s)}' is not a message id of this mailbox (UIDVALIDITY:UID)");
    }

    private static string Clip(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Take(60)) sb.Append(char.IsControl(ch) ? '?' : ch);
        return sb.ToString();
    }
}
