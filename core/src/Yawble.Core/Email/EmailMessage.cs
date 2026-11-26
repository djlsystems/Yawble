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
/// One message as a workflow sees it (the built-in schema EmailMessage). <see cref="Id"/> is opaque to workflows and is
/// list's polling cursor (<see cref="EmailMessageId"/>): it carries <see cref="Folder"/>'s UIDVALIDITY and the message's UID there, "UIDVALIDITY:UID",
/// on Gmail "gm:X-GM-MSGID:UIDVALIDITY:UID"; it is stable while the message stays in that folder or label. <see cref="ThreadId"/>: Gmail's
/// X-GM-THRID in decimal, otherwise the thread's first Message-ID (References, else In-Reply-To, else its own), else <see cref="Id"/>.
/// <see cref="ReceivedAt"/> is the server's INTERNALDATE in UTC, whole seconds. <see cref="Snippet"/> is the first 500 characters of the plain
/// text, its white space collapsed. <see cref="Body"/> is set by get only (the whole plain text; an HTML-only message's text converted from its HTML).
/// </summary>
public sealed record EmailMessage(
    string Id, string ThreadId, string? MessageId, string Folder, EmailAddress From, IReadOnlyList<EmailAddress> To, IReadOnlyList<EmailAddress> Cc,
    string Subject, DateTimeOffset ReceivedAt, string Snippet, bool HasAttachments, bool Unread, string? Body = null)
{
    public const int SnippetLength = 500;

    /// <summary>receivedAt as written: ISO-8601 UTC, whole seconds ("2026-11-23T09:15:00Z").</summary>
    public static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The JSON a workflow gets. body is present only when it was fetched (get).</summary>
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


/// <summary>The result of list (the built-in schema EmailList): the messages, and whether the after it was given no longer applied.</summary>
public sealed record EmailList(IReadOnlyList<EmailMessage> Messages, bool CursorReset)
{
    public JsonObject ToJson() => new()
    {
        ["messages"] = new JsonArray(Messages.Select(m => (JsonNode)m.ToJson()).ToArray()),
        ["cursorReset"] = CursorReset,
    };
}

/// <summary>
/// A message id as the Email integration writes it, opaque to workflows: the folder's UIDVALIDITY and the message's UID there, "UIDVALIDITY:UID";
/// on Gmail with its X-GM-MSGID first, "gm:X-GM-MSGID:UIDVALIDITY:UID" (get, markRead, move and thread find a Gmail message by the X-GM-MSGID).
/// The UIDVALIDITY and UID are what make it list's after.
/// </summary>
public readonly record struct EmailMessageId(ulong? GmailId, uint UidValidity, uint Uid)
{
    public const string GmailPrefix = "gm:";

    public static string Gmail(ulong id, uint uidValidity, uint uid) =>
        GmailPrefix + id.ToString(CultureInfo.InvariantCulture) + ":" + Imap(uidValidity, uid);

    public static string Imap(uint uidValidity, uint uid) =>
        uidValidity.ToString(CultureInfo.InvariantCulture) + ":" + uid.ToString(CultureInfo.InvariantCulture);

    public override string ToString() => GmailId is { } g ? Gmail(g, UidValidity, Uid) : Imap(UidValidity, Uid);

    /// <summary>Parses an id for this kind of server; a malformed one throws <see cref="ArgumentException"/>.</summary>
    public static EmailMessageId Parse(string? id, bool gmail) =>
        TryParse(id, gmail, out var parsed) ? parsed
        : throw new ArgumentException($"'{Clip((id ?? "").Trim())}' is not a message id of this mailbox ({(gmail ? "gm:MSGID:UIDVALIDITY:UID" : "UIDVALIDITY:UID")})");

    /// <summary>Parses list's after (an id from an earlier list); a malformed one throws <see cref="ArgumentException"/>.</summary>
    public static EmailMessageId ParseAfter(string? after, bool gmail) =>
        TryParse(after, gmail, out var parsed) ? parsed
        : throw new ArgumentException($"after '{Clip((after ?? "").Trim())}' is not a message id (pass back the id of a message from list, unchanged)");

    public static bool TryParse(string? id, bool gmail, out EmailMessageId parsed)
    {
        parsed = default;
        var s = (id ?? "").Trim();
        ulong? g = null;
        if (gmail)
        {
            if (!s.StartsWith(GmailPrefix, StringComparison.Ordinal)) return false;
            s = s[GmailPrefix.Length..];
            var c = s.IndexOf(':');
            if (c <= 0 || !ulong.TryParse(s[..c], NumberStyles.None, CultureInfo.InvariantCulture, out var msgid) || msgid == 0) return false;
            g = msgid;
            s = s[(c + 1)..];
        }
        var colon = s.IndexOf(':');
        if (colon > 0
            && uint.TryParse(s[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            && uint.TryParse(s[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var u) && v > 0 && u > 0)
        {
            parsed = new(g, v, u);
            return true;
        }
        return false;
    }

    internal static string Clip(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Take(60)) sb.Append(char.IsControl(ch) ? '?' : ch);
        return sb.ToString();
    }
}