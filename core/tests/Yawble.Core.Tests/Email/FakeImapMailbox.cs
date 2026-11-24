using System.Globalization;
using System.Text;
using MimeKit;

namespace Yawble.Core.Tests.Email;

/// <summary>
/// One message of the fake mailbox. Its flags, Gmail ids and labels are shared by every folder it is in (as on Gmail, where a folder is a
/// label); a non-Gmail message is in one folder.
/// </summary>
public sealed class FakeMessage(string raw, DateTimeOffset internalDate)
{
    public string Raw { get; } = raw.Replace("\r\n", "\n").Replace("\n", "\r\n");
    public DateTimeOffset InternalDate { get; } = internalDate;
    public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ulong GmailMessageId { get; set; }
    public ulong GmailThreadId { get; set; }

    private MimeMessage? _mime;
    public MimeMessage Mime => _mime ??= MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(Raw)));

    public bool Seen => Flags.Contains(@"\Seen");
}

/// <summary>A message's place in a folder: its UID there.</summary>
public sealed record FakeEntry(uint Uid, FakeMessage Message);

/// <summary>
/// The read-actions half of the fake IMAP server (card 590): SELECT, EXAMINE, CLOSE, UNSELECT, UID SEARCH, UID FETCH, UID STORE and UID MOVE
/// over <see cref="FakeFolder.Messages"/> (each a <see cref="FakeEntry"/>), with Gmail's X-GM-MSGID, X-GM-THRID, X-GM-RAW and X-GM-LABELS when
/// the server offers X-GM-EXT-1. On Gmail a label is a folder: adding the label puts the message in that folder, taking it off removes it.
/// SEARCH knows ALL, SEEN, UNSEEN, SINCE, TEXT, BODY, SUBJECT, FROM, HEADER, UID, OR, NOT, parentheses and the X-GM-* keys.
/// </summary>
public static class FakeImapMailbox
{
    public static readonly string[] SystemFlags = [@"\Seen", @"\Answered", @"\Flagged", @"\Deleted", @"\Draft"];

    /// <summary>Installs the commands. <paramref name="move"/>: offer MOVE and UIDPLUS (a server that offers neither can only COPY and EXPUNGE).</summary>
    public static FakeImapServer WithMailbox(this FakeImapServer s, bool move = true)
    {
        if (move)
        {
            if (!s.Capabilities.Contains("MOVE")) s.Capabilities.Add("MOVE");
            if (!s.Capabilities.Contains("UIDPLUS")) s.Capabilities.Add("UIDPLUS");
        }
        s.Commands["SELECT"] = (srv, c, cmd, ct) => SelectAsync(srv, c, cmd, readOnly: false, ct);
        s.Commands["EXAMINE"] = (srv, c, cmd, ct) => SelectAsync(srv, c, cmd, readOnly: true, ct);
        s.Commands["CLOSE"] = async (srv, c, cmd, ct) => { c.State.Remove("folder"); await c.WriteLineAsync($"{cmd.Tag} OK CLOSE completed", ct); };
        s.Commands["UNSELECT"] = async (srv, c, cmd, ct) => { c.State.Remove("folder"); await c.WriteLineAsync($"{cmd.Tag} OK UNSELECT completed", ct); };
        s.Commands["UID"] = UidAsync;
        return s;
    }

    /// <summary>Adds a message to the folder with the next UID; the same <see cref="FakeMessage"/> may go in several (Gmail labels).</summary>
    public static FakeEntry Put(this FakeFolder f, FakeMessage m)
    {
        var uid = f.Messages.OfType<FakeEntry>().Select(e => e.Uid).DefaultIfEmpty(0u).Max() + 1;
        var entry = new FakeEntry(uid, m);
        f.Messages.Add(entry);
        return entry;
    }

    public static IEnumerable<FakeEntry> Entries(this FakeFolder f) => f.Messages.OfType<FakeEntry>();

    public static FakeFolder Folder(this FakeImapServer s, string name) =>
        s.Folders.FirstOrDefault(f => f.Name.Equals(name, name.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        ?? throw new InvalidOperationException("no fake folder " + name);

    private static bool Gmail(FakeImapServer s) => s.Capabilities.Contains("X-GM-EXT-1");

    private static async Task SelectAsync(FakeImapServer s, FakeMailConnection c, FakeImapCommand cmd, bool readOnly, CancellationToken ct)
    {
        if (!await s.RequireSignInAsync(c, cmd, ct)) return;
        var name = cmd.Args.FirstOrDefault() ?? "";
        var f = s.Folders.FirstOrDefault(x => x.Name.Equals(name, StringComparison.Ordinal) || (name.Equals("INBOX", StringComparison.OrdinalIgnoreCase) && x.Name == "INBOX"));
        if (f is null) { await c.WriteLineAsync($"{cmd.Tag} NO no such folder", ct); return; }
        c.State["folder"] = f;
        var entries = f.Entries().ToList();
        await c.WriteLineAsync($"* FLAGS ({string.Join(' ', SystemFlags)})", ct);
        if (!readOnly) await c.WriteLineAsync(@"* OK [PERMANENTFLAGS (\Seen \Answered \Flagged \Deleted \Draft)] flags", ct);
        await c.WriteLineAsync($"* {entries.Count} EXISTS", ct);
        await c.WriteLineAsync("* 0 RECENT", ct);
        await c.WriteLineAsync($"* OK [UIDVALIDITY {f.UidValidity}] UIDs valid", ct);
        await c.WriteLineAsync($"* OK [UIDNEXT {entries.Select(e => e.Uid).DefaultIfEmpty(0u).Max() + 1}] next", ct);
        await c.WriteLineAsync($"{cmd.Tag} OK [{(readOnly ? "READ-ONLY" : "READ-WRITE")}] {cmd.Name} completed", ct);
    }

    private static async Task UidAsync(FakeImapServer s, FakeMailConnection c, FakeImapCommand cmd, CancellationToken ct)
    {
        if (!await s.RequireSignInAsync(c, cmd, ct)) return;
        if (c.State.GetValueOrDefault("folder") is not FakeFolder f) { await c.WriteLineAsync($"{cmd.Tag} NO select a folder first", ct); return; }
        var sub = cmd.Args.FirstOrDefault()?.ToUpperInvariant();
        var rest = cmd.Args.Skip(1).ToList();
        switch (sub)
        {
            case "SEARCH":
            {
                var hits = f.Entries().Where(e => { var pos = 0; return All(s, f, e, rest, ref pos); }).Select(e => e.Uid).ToList();
                await c.WriteLineAsync("* SEARCH" + string.Concat(hits.Select(u => " " + u)), ct);
                await c.WriteLineAsync($"{cmd.Tag} OK SEARCH completed", ct);
                return;
            }
            case "FETCH":
            {
                var set = Set(f, rest[0]);
                var items = Inner(rest[1]).Select(i => i.ToUpperInvariant()).ToList();
                var entries = f.Entries().ToList();
                foreach (var e in entries.Where(e => set.Contains(e.Uid)))
                    await c.WriteLineAsync($"* {entries.IndexOf(e) + 1} FETCH ({Fetched(s, e, items)})", ct);
                await c.WriteLineAsync($"{cmd.Tag} OK FETCH completed", ct);
                return;
            }
            case "STORE":
            {
                var set = Set(f, rest[0]);
                var op = rest[1].ToUpperInvariant();
                var values = Inner(rest[2]);
                var silent = op.EndsWith(".SILENT", StringComparison.Ordinal);
                var entries = f.Entries().Where(e => set.Contains(e.Uid)).ToList();
                foreach (var e in entries)
                {
                    if (op.Contains("X-GM-LABELS", StringComparison.Ordinal)) Label(s, e.Message, values, add: op.StartsWith('+'));
                    else if (op.StartsWith('+')) e.Message.Flags.UnionWith(values);
                    else if (op.StartsWith('-')) e.Message.Flags.ExceptWith(values);
                    else { e.Message.Flags.Clear(); e.Message.Flags.UnionWith(values); }
                }
                if (!silent)
                {
                    var all = f.Entries().ToList();
                    foreach (var e in entries.Where(all.Contains))
                        await c.WriteLineAsync($"* {all.IndexOf(e) + 1} FETCH (UID {e.Uid} FLAGS ({string.Join(' ', e.Message.Flags)}))", ct);
                }
                await c.WriteLineAsync($"{cmd.Tag} OK STORE completed", ct);
                return;
            }
            case "MOVE":
            {
                if (!s.Capabilities.Contains("MOVE")) break;
                var set = Set(f, rest[0]);
                var dest = s.Folders.FirstOrDefault(x => x.Name == rest[1]);
                if (dest is null) { await c.WriteLineAsync($"{cmd.Tag} NO [TRYCREATE] no such folder", ct); return; }
                var moving = f.Entries().Where(e => set.Contains(e.Uid)).ToList();
                var placed = moving.Select(e => dest.Put(e.Message)).ToList();
                if (s.Capabilities.Contains("UIDPLUS"))
                    await c.WriteLineAsync($"* OK [COPYUID {dest.UidValidity} {string.Join(',', moving.Select(e => e.Uid))} {string.Join(',', placed.Select(e => e.Uid))}] moved", ct);
                foreach (var e in moving)
                {
                    var seq = f.Entries().ToList().IndexOf(e) + 1;
                    f.Messages.Remove(e);
                    await c.WriteLineAsync($"* {seq} EXPUNGE", ct);
                }
                await c.WriteLineAsync($"{cmd.Tag} OK MOVE completed", ct);
                return;
            }
        }
        await c.WriteLineAsync($"{cmd.Tag} BAD unknown UID command {sub}", ct);
    }

    /// <summary>Gmail: a label is a folder (\Inbox is INBOX). Adding puts the message there; removing takes it out.</summary>
    private static void Label(FakeImapServer s, FakeMessage m, IReadOnlyList<string> labels, bool add)
    {
        foreach (var label in labels)
        {
            var folder = label.Equals(@"\Inbox", StringComparison.OrdinalIgnoreCase) ? s.Folder("INBOX") : s.Folders.FirstOrDefault(x => x.Name == label);
            if (folder is null) continue;
            var there = folder.Entries().FirstOrDefault(e => ReferenceEquals(e.Message, m));
            if (add && there is null) folder.Put(m);
            else if (!add && there is not null) folder.Messages.Remove(there);
        }
    }

    private static string Fetched(FakeImapServer s, FakeEntry e, List<string> items)
    {
        var parts = new List<string> { $"UID {e.Uid}" };
        foreach (var item in items)
        {
            switch (item)
            {
                case "UID": break;
                case "FLAGS": parts.Add($"FLAGS ({string.Join(' ', e.Message.Flags)})"); break;
                case "INTERNALDATE":
                    parts.Add("INTERNALDATE \"" + e.Message.InternalDate.ToUniversalTime().ToString("dd-MMM-yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " +0000\"");
                    break;
                case "X-GM-MSGID" when Gmail(s): parts.Add($"X-GM-MSGID {e.Message.GmailMessageId}"); break;
                case "X-GM-THRID" when Gmail(s): parts.Add($"X-GM-THRID {e.Message.GmailThreadId}"); break;
                case "RFC822.SIZE": parts.Add($"RFC822.SIZE {Encoding.UTF8.GetByteCount(e.Message.Raw)}"); break;
                case "BODY.PEEK[]" or "BODY[]" or "RFC822":
                    // a literal: the CRLF after {n} is written by the line, the bytes follow
                    parts.Add($"BODY[] {{{Encoding.UTF8.GetByteCount(e.Message.Raw)}}}\r\n{e.Message.Raw}");
                    if (item != "BODY.PEEK[]") e.Message.Flags.Add(@"\Seen");
                    break;
                default: throw new InvalidOperationException("the fake cannot FETCH " + item);
            }
        }
        return string.Join(' ', parts);
    }

    /// <summary>The items of a parenthesized list (or the one atom).</summary>
    public static List<string> Inner(string token) =>
        token.StartsWith('(') && token.EndsWith(')') ? FakeImapServer.Tokens(token[1..^1]) : [token];

    /// <summary>A UID set: 3, 1:4, 2:*, 1,3,5.</summary>
    private static HashSet<uint> Set(FakeFolder f, string text)
    {
        var max = f.Entries().Select(e => e.Uid).DefaultIfEmpty(0u).Max();
        var set = new HashSet<uint>();
        foreach (var part in text.Split(','))
        {
            var range = part.Split(':');
            uint Num(string x) => x == "*" ? max : uint.Parse(x, CultureInfo.InvariantCulture);
            var a = Num(range[0]);
            var b = range.Length > 1 ? Num(range[1]) : a;
            for (var u = Math.Min(a, b); u <= Math.Max(a, b) && u != 0; u++) set.Add(u);
        }
        return set;
    }

    // ------------------------------------------------------------------ SEARCH

    private static bool All(FakeImapServer s, FakeFolder f, FakeEntry e, List<string> keys, ref int pos)
    {
        var ok = true;
        while (pos < keys.Count) ok &= One(s, f, e, keys, ref pos);
        return ok;
    }

    private static bool One(FakeImapServer s, FakeFolder f, FakeEntry e, List<string> keys, ref int pos)
    {
        var key = keys[pos++];
        var m = e.Message;
        string Next(ref int p) => keys[p++];
        if (key.StartsWith('('))
        {
            var inner = FakeImapServer.Tokens(key[1..^1]);
            var p = 0;
            return All(s, f, e, inner, ref p);
        }
        switch (key.ToUpperInvariant())
        {
            case "ALL": return true;
            case "SEEN": return m.Seen;
            case "UNSEEN": return !m.Seen;
            case "CHARSET": pos++; return true;
            case "SINCE":
                return m.InternalDate.UtcDateTime.Date >= DateTime.ParseExact(Next(ref pos), "d-MMM-yyyy", CultureInfo.InvariantCulture);
            case "TEXT": return m.Raw.Contains(Next(ref pos), StringComparison.OrdinalIgnoreCase);
            case "BODY": return (m.Mime.TextBody ?? m.Mime.HtmlBody ?? "").Contains(Next(ref pos), StringComparison.OrdinalIgnoreCase);
            case "SUBJECT": return (m.Mime.Subject ?? "").Contains(Next(ref pos), StringComparison.OrdinalIgnoreCase);
            case "FROM": return m.Mime.From.ToString().Contains(Next(ref pos), StringComparison.OrdinalIgnoreCase);
            case "HEADER":
            {
                var name = Next(ref pos);
                var value = Next(ref pos);
                return m.Mime.Headers.Where(h => h.Field.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .Any(h => h.Value.Contains(value, StringComparison.OrdinalIgnoreCase));
            }
            case "UID": return Set(f, Next(ref pos)).Contains(e.Uid);
            case "NOT": return !One(s, f, e, keys, ref pos);
            case "OR":
            {
                var a = One(s, f, e, keys, ref pos);
                var b = One(s, f, e, keys, ref pos);
                return a || b;
            }
            case "X-GM-MSGID": return m.GmailMessageId == ulong.Parse(Next(ref pos), CultureInfo.InvariantCulture);
            case "X-GM-THRID": return m.GmailThreadId == ulong.Parse(Next(ref pos), CultureInfo.InvariantCulture);
            case "X-GM-RAW": return GmailRaw(m, Next(ref pos));
            default: throw new InvalidOperationException("the fake cannot SEARCH " + key);
        }
    }

    /// <summary>A little of Gmail's search syntax: from:, subject:, is:unread, is:read, and words anywhere.</summary>
    private static bool GmailRaw(FakeMessage m, string query)
    {
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var ok = word.ToLowerInvariant() switch
            {
                "is:unread" => !m.Seen,
                "is:read" => m.Seen,
                var w when w.StartsWith("from:", StringComparison.Ordinal) => m.Mime.From.ToString().Contains(word[5..], StringComparison.OrdinalIgnoreCase),
                var w when w.StartsWith("subject:", StringComparison.Ordinal) => (m.Mime.Subject ?? "").Contains(word[8..], StringComparison.OrdinalIgnoreCase),
                _ => m.Raw.Contains(word, StringComparison.OrdinalIgnoreCase),
            };
            if (!ok) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ messages

    private static int _seq;

    /// <summary>A plain-text message.</summary>
    public static string Plain(string from, string to, string subject, string body, string? messageId = null, string? inReplyTo = null, string? references = null, string? cc = null)
    {
        var sb = new StringBuilder();
        sb.Append($"From: {from}\r\nTo: {to}\r\n");
        if (cc is not null) sb.Append($"Cc: {cc}\r\n");
        sb.Append($"Subject: {subject}\r\nMessage-ID: <{messageId ?? $"m{Interlocked.Increment(ref _seq)}@example.test"}>\r\n");
        if (inReplyTo is not null) sb.Append($"In-Reply-To: <{inReplyTo}>\r\n");
        if (references is not null) sb.Append($"References: {references}\r\n");
        sb.Append("Date: Mon, 23 Nov 2026 09:00:00 +0000\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n");
        sb.Append(body.Replace("\n", "\r\n")).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>multipart/alternative (text and HTML) with a PDF attachment.</summary>
    public static string MultipartWithAttachment(string from, string to, string subject, string text, string html) => $"""
        From: {from}
        To: {to}
        Subject: {subject}
        Message-ID: <mp{Interlocked.Increment(ref _seq)}@example.test>
        MIME-Version: 1.0
        Content-Type: multipart/mixed; boundary="outer"

        --outer
        Content-Type: multipart/alternative; boundary="alt"

        --alt
        Content-Type: text/plain; charset=utf-8

        {text}
        --alt
        Content-Type: text/html; charset=utf-8

        {html}
        --alt--
        --outer
        Content-Type: application/pdf; name="quote.pdf"
        Content-Disposition: attachment; filename="quote.pdf"
        Content-Transfer-Encoding: base64

        JVBERi0xLjQKJcfsj6IKCg==
        --outer--

        """;

    /// <summary>An HTML-only message.</summary>
    public static string HtmlOnly(string from, string to, string subject, string html) => $"""
        From: {from}
        To: {to}
        Subject: {subject}
        Message-ID: <h{Interlocked.Increment(ref _seq)}@example.test>
        MIME-Version: 1.0
        Content-Type: text/html; charset=utf-8

        {html}

        """;
}
