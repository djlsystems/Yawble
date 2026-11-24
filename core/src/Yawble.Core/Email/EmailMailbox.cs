using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;

namespace Yawble.Core.Email;

/// <summary>
/// The Email integration's read actions over one signed-in IMAP session (list, get, search, thread, markRead, move; scripts-design.md
/// section 9). Reads open folders read-only (EXAMINE), so reading never marks a message read; markRead and move open the folder read-write.
/// Never deletes: there is no delete action, and move refuses a server that could only move by COPY then EXPUNGE without UIDPLUS.
/// A bad argument (no such folder, a malformed or unknown id) throws <see cref="ArgumentException"/>; the server failing throws
/// <see cref="MailException"/>. No message text, password or address is written to a log here.
/// </summary>
public sealed class EmailMailbox(IImapSession session)
{
    /// <summary>The most messages one list, search or thread returns (list: the first in its order; search and thread: the newest).</summary>
    public const int MaxMessages = 200;

    /// <summary>Gmail's All Mail by name, when the server does not mark a folder \All.</summary>
    public const string GmailAllMail = "[Gmail]/All Mail";

    /// <summary>The Gmail label for the inbox.</summary>
    public const string GmailInboxLabel = "\\Inbox";

    private IImapClient Client => session.Client;

    public bool IsGmail => session.IsGmail;

    // ------------------------------------------------------------------ the actions

    /// <summary><see cref="ListAsync(string, DateTimeOffset?, bool, int?, string?, CancellationToken)"/> without a cursor.</summary>
    public Task<IReadOnlyList<EmailMessage>> ListAsync(string folder, DateTimeOffset? since, bool unreadOnly, int? max, CancellationToken ct) =>
        ListAsync(folder, since, unreadOnly, max, after: null, ct);

    /// <summary>
    /// The messages of <paramref name="folder"/>: only unread ones with <paramref name="unreadOnly"/>, and of those the FIRST <paramref name="max"/>
    /// (at most <see cref="MaxMessages"/>) in the list's order. Three ways to pick where it starts:
    /// <list type="bullet">
    /// <item><paramref name="after"/>, a message's <see cref="EmailMessage.Cursor"/> from this folder: the messages with a higher UID, in UID order.
    /// UIDs only grow as mail arrives in a folder, so a script that saves the last message's cursor and passes it back as after gets every message
    /// exactly once, whatever max is and whatever the dates. A cursor of another UIDVALIDITY (the folder was renumbered) or a malformed one is an
    /// <see cref="ArgumentException"/>: never a silent restart.</item>
    /// <item><paramref name="since"/>: a plain time filter, receivedAt strictly later than since (exclusive), ordered by (INTERNALDATE, UID).
    /// receivedAt is whole seconds, so since = a message's receivedAt also leaves out the others of that second: polling uses after.</item>
    /// <item>neither: the whole folder, in UID order (the order after walks in), so the first poll of a cursor walk is this.</item>
    /// </list>
    /// after and since together are an <see cref="ArgumentException"/>.
    /// </summary>
    public async Task<IReadOnlyList<EmailMessage>> ListAsync(string folder, DateTimeOffset? since, bool unreadOnly, int? max, string? after, CancellationToken ct)
    {
        if (after is not null && since is not null) throw new ArgumentException("give after or since, not both (poll with after)");
        var cursor = after is null ? (EmailCursor?)null : EmailCursor.Parse(after);
        var f = await OpenAsync(folder, FolderAccess.ReadOnly, ct);
        var keep = Math.Clamp(max ?? MaxMessages, 0, MaxMessages);
        SearchQuery query = unreadOnly ? SearchQuery.NotSeen : SearchQuery.All;

        if (cursor is { } c)
        {
            if (c.UidValidity != f.UidValidity)
                throw new ArgumentException($"the cursor '{c}' is from an earlier numbering of {f.FullName} (its UIDVALIDITY was {c.UidValidity}, it is now {f.UidValidity}): "
                    + "the server renumbered the folder, so this poll cannot tell which messages are new; start the poll again without after");
            if (c.Uid == uint.MaxValue || keep == 0) return [];
            // UID SEARCH UID n+1:*; a server answers n+1:* with its last message even when that is n or lower (RFC 3501's * rule), so filter here too
            var range = SearchQuery.Uids(new UniqueIdRange(f.UidValidity, c.Uid + 1, uint.MaxValue));
            var found = await Guard("search", () => f.SearchAsync(unreadOnly ? SearchQuery.NotSeen.And(range) : range, ct));
            var next = found.Where(u => u.Id > c.Uid).Distinct().OrderBy(u => u.Id).Take(keep).ToList();
            var summaries = (await SummariesAsync(f, next, ct)).Where(m => !unreadOnly || !Seen(m)).OrderBy(m => m.UniqueId.Id).ToList();
            return await MessagesAsync(f, summaries, withBody: false, ct);
        }

        // SINCE is by date only, in the server's time zone: ask from the day before, then compare the exact INTERNALDATE here
        if (since is { } s) query = query.And(SearchQuery.DeliveredAfter(s.UtcDateTime.Date.AddDays(-1)));
        var uids = await Guard("search", () => f.SearchAsync(query, ct));
        var filtered = (await SummariesAsync(f, uids, ct))
            .Where(m => since is not { } from || Received(m) > from.ToUniversalTime())
            .Where(m => !unreadOnly || !Seen(m));
        var picked = (since is null ? filtered.OrderBy(m => m.UniqueId.Id) : filtered.OrderBy(Received).ThenBy(m => m.UniqueId.Id)).Take(keep).ToList();
        return await MessagesAsync(f, picked, withBody: false, ct);
    }

    /// <summary>One message by its id, with its plain-text body. <paramref name="folder"/>, when given, is where to look (a hint: faster).</summary>
    public async Task<EmailMessage> GetAsync(string id, string? folder, CancellationToken ct)
    {
        var (f, uid) = await FindAsync(id, folder, FolderAccess.ReadOnly, ct);
        return await OneAsync(f, uid, id, withBody: true, ct);
    }

    /// <summary>
    /// The messages of <paramref name="folder"/> that match <paramref name="query"/>, oldest first (the newest <see cref="MaxMessages"/>). Gmail:
    /// X-GM-RAW, so Gmail's search syntax works (<c>from:pat is:unread</c>); otherwise IMAP SEARCH TEXT (the words anywhere in the message).
    /// </summary>
    public async Task<IReadOnlyList<EmailMessage>> SearchAsync(string folder, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("search needs a query");
        var f = await OpenAsync(folder, FolderAccess.ReadOnly, ct);
        var q = IsGmail ? SearchQuery.GMailRawSearch(query.Trim()) : SearchQuery.MessageContains(query.Trim());
        var uids = await Guard("search", () => f.SearchAsync(q, ct));
        return await NewestAsync(f, uids, ct);
    }

    /// <summary>
    /// A conversation's messages, oldest first. Gmail: X-GM-THRID across All Mail (Sent included). Otherwise the thread id is the root
    /// Message-ID, looked for in the Message-ID, In-Reply-To and References of the INBOX and the Sent folders' messages.
    /// </summary>
    public async Task<IReadOnlyList<EmailMessage>> ThreadAsync(string threadId, CancellationToken ct)
    {
        var t = (threadId ?? "").Trim();
        if (t.Length == 0) throw new ArgumentException("thread needs a threadId");
        if (IsGmail)
        {
            if (!ulong.TryParse(t, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var thrid))
                throw new ArgumentException("a Gmail threadId is a number");
            var all = await AllMailAsync(ct);
            await Guard("open " + all.FullName, () => all.OpenAsync(FolderAccess.ReadOnly, ct));
            var uids = await Guard("search", () => all.SearchAsync(SearchQuery.GMailThreadId(thrid), ct));
            return await NewestAsync(all, uids, ct);
        }

        var root = t.Trim('<', '>');
        var found = new List<EmailMessage>();
        foreach (var f in await ThreadFoldersAsync(ct))
        {
            await Guard("open " + f.FullName, () => f.OpenAsync(FolderAccess.ReadOnly, ct));
            var q = SearchQuery.HeaderContains("Message-ID", root)
                .Or(SearchQuery.HeaderContains("In-Reply-To", root))
                .Or(SearchQuery.HeaderContains("References", root));
            var uids = await Guard("search", () => f.SearchAsync(q, ct));
            found.AddRange(await MessagesAsync(f, (await SummariesAsync(f, uids, ct)).ToList(), withBody: false, ct));
        }
        // the thread id is the root's: a message that only mentions it in passing (another thread quoting it) is not in this thread
        var mine = found.Where(m => m.ThreadId == root || m.MessageId == root).ToList();
        return mine.DistinctBy(m => (m.Folder, m.Id)).OrderBy(m => m.ReceivedAt).ThenBy(m => m.Id, StringComparer.Ordinal)
            .TakeLast(MaxMessages).ToList();
    }

    /// <summary>Marks a message read (adds \Seen); the message as it is now.</summary>
    public async Task<EmailMessage> MarkReadAsync(string id, string? folder, CancellationToken ct)
    {
        var (f, uid) = await FindAsync(id, folder, FolderAccess.ReadWrite, ct);
        await Guard("mark read", () => f.StoreAsync(uid, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Seen) { Silent = true }, ct));
        return await OneAsync(f, uid, id, withBody: false, ct);
    }

    /// <summary>
    /// Moves a message to <paramref name="to"/> (which must exist); the message in its new place. Gmail: a label move: the label
    /// <paramref name="to"/> is added and the inbox label taken off (moving to INBOX only adds the inbox label); the id stays. Otherwise IMAP
    /// MOVE (or COPY, then \Deleted and UID EXPUNGE of that one message with UIDPLUS); the id is then the message's id in <paramref name="to"/>.
    /// </summary>
    public async Task<EmailMessage> MoveAsync(string id, string to, string? from, CancellationToken ct)
    {
        var target = (to ?? "").Trim();
        if (target.Length == 0) throw new ArgumentException("move needs a folder");
        if (IsGmail)
        {
            var toInbox = target.Equals("INBOX", StringComparison.OrdinalIgnoreCase);
            if (!toInbox) await FolderAsync(target, ct);
            var (all, uid) = await FindAsync(id, null, FolderAccess.ReadWrite, ct);
            if (toInbox)
                await Guard("move", () => all.StoreAsync(uid, new StoreLabelsRequest(StoreAction.Add, [GmailInboxLabel]) { Silent = true }, ct));
            else
            {
                await Guard("move", () => all.StoreAsync(uid, new StoreLabelsRequest(StoreAction.Add, [target]) { Silent = true }, ct));
                await Guard("move", () => all.StoreAsync(uid, new StoreLabelsRequest(StoreAction.Remove, [GmailInboxLabel]) { Silent = true }, ct));
            }
            var moved = await OneAsync(all, uid, id, withBody: false, ct);
            return moved with { Folder = toInbox ? "INBOX" : target };
        }

        var caps = Client.Capabilities;
        if (!caps.Contains(ImapCapability.Move) && !caps.Contains(ImapCapability.UidPlus))
            throw new MailException(MailFailure.Protocol, "this IMAP server can move a message only by copying it and expunging the folder; the Email integration does not do that");
        var dest = await FolderAsync(target, ct);
        var (src, srcUid) = await FindAsync(id, from, FolderAccess.ReadWrite, ct);
        if (string.Equals(src.FullName, dest.FullName, StringComparison.Ordinal)) return await OneAsync(src, srcUid, id, withBody: false, ct);
        // its Message-ID finds it in the folder it went to when the server does not say its new UID (no COPYUID)
        var messageId = (await Guard("fetch", () => src.GetMessageAsync(srcUid, ct))).MessageId;
        var map = await Guard("move", () => src.MoveToAsync([srcUid], new MoveRequest(dest), ct));

        await Guard("open " + dest.FullName, () => dest.OpenAsync(FolderAccess.ReadOnly, ct));
        UniqueId? newUid = map is not null && map.TryGetValue(srcUid, out var mapped) ? mapped : null;
        if (newUid is null && !string.IsNullOrWhiteSpace(messageId))
        {
            var hits = await Guard("search", () => dest.SearchAsync(SearchQuery.HeaderContains("Message-ID", messageId.Trim('<', '>')), ct));
            if (hits.Count > 0) newUid = hits.Max();
        }
        if (newUid is not { } nu) throw new MailException(MailFailure.Protocol, $"the message was moved to {dest.FullName}, but the server did not say where it is now");
        return await OneAsync(dest, nu, EmailMessageId.Imap(dest.UidValidity, nu.Id), withBody: false, ct);
    }

    // ------------------------------------------------------------------ folders and ids

    /// <summary>The folder by its full name (INBOX in any case is the inbox). No such folder: ArgumentException.</summary>
    public async Task<IMailFolder> FolderAsync(string name, CancellationToken ct)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new ArgumentException("which folder?");
        if (n.Equals("INBOX", StringComparison.OrdinalIgnoreCase)) return Client.Inbox;
        try { return await Client.GetFolderAsync(n, ct); }
        catch (FolderNotFoundException) { throw new ArgumentException($"the mailbox has no folder named '{Clip(n)}'"); }
        catch (Exception e) when (e is IOException or ProtocolException or CommandException) { throw Failed("find the folder", e); }
    }

    private async Task<IMailFolder> OpenAsync(string name, FolderAccess access, CancellationToken ct)
    {
        var f = await FolderAsync(name, ct);
        await Guard("open " + f.FullName, () => f.OpenAsync(access, ct));
        return f;
    }

    private async Task<IReadOnlyList<IMailFolder>> AllFoldersAsync(CancellationToken ct)
    {
        var ns = Client.PersonalNamespaces.Count > 0 ? Client.PersonalNamespaces[0] : new FolderNamespace('/', "");
        var list = await Guard("list folders", () => Client.GetFoldersAsync(ns, cancellationToken: ct));
        return list.Where(f => f.FullName.Length > 0 && !f.Attributes.HasFlag(FolderAttributes.NoSelect) && !f.Attributes.HasFlag(FolderAttributes.NonExistent))
            .OrderBy(f => f.FullName.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Gmail's All Mail: the folder marked \All, else "[Gmail]/All Mail".</summary>
    private async Task<IMailFolder> AllMailAsync(CancellationToken ct) =>
        (await AllFoldersAsync(ct)).FirstOrDefault(f => f.Attributes.HasFlag(FolderAttributes.All)) ?? await FolderAsync(GmailAllMail, ct);

    /// <summary>Where a thread is looked for without Gmail: the inbox and the folders marked \Sent (or named Sent).</summary>
    private async Task<IReadOnlyList<IMailFolder>> ThreadFoldersAsync(CancellationToken ct)
    {
        var all = await AllFoldersAsync(ct);
        var sent = all.Where(f => f.Attributes.HasFlag(FolderAttributes.Sent)).ToList();
        if (sent.Count == 0) sent = all.Where(f => f.Name.Equals("Sent", StringComparison.OrdinalIgnoreCase)).ToList();
        return [Client.Inbox, .. sent.Where(f => !f.FullName.Equals(Client.Inbox.FullName, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// The folder (opened with <paramref name="access"/>) and UID of a message by its id. Gmail: All Mail, by X-GM-MSGID. Otherwise the folder
    /// whose UIDVALIDITY the id names and that has the UID: <paramref name="hint"/> only when given, else every folder, INBOX first; two
    /// folders that both fit make the id ambiguous (refused rather than acting on the wrong message).
    /// </summary>
    private async Task<(IMailFolder Folder, UniqueId Uid)> FindAsync(string id, string? hint, FolderAccess access, CancellationToken ct)
    {
        var parsed = EmailMessageId.Parse(id, IsGmail);
        if (parsed.GmailId is { } gid)
        {
            var all = await AllMailAsync(ct);
            await Guard("open " + all.FullName, () => all.OpenAsync(access, ct));
            var hits = await Guard("search", () => all.SearchAsync(SearchQuery.GMailMessageId(gid), ct));
            if (hits.Count == 0) throw new ArgumentException($"no message with id {id} in the mailbox");
            return (all, hits[0]);
        }

        var candidates = string.IsNullOrWhiteSpace(hint) ? await AllFoldersAsync(ct) : [await FolderAsync(hint, ct)];
        var matches = new List<IMailFolder>();
        var uid = new UniqueId(parsed.UidValidity, parsed.Uid);
        foreach (var f in candidates)
        {
            await Guard("open " + f.FullName, () => f.OpenAsync(FolderAccess.ReadOnly, ct));
            if (f.UidValidity != parsed.UidValidity) continue;
            var hits = await Guard("search", () => f.SearchAsync(SearchQuery.Uids([uid]), ct));
            if (hits.Contains(uid)) matches.Add(f);
        }
        if (matches.Count == 0)
            throw new ArgumentException($"no message with id {id} in {(string.IsNullOrWhiteSpace(hint) ? "the mailbox" : Clip(hint.Trim()))} (moved, deleted, or the folder was rebuilt)");
        if (matches.Count > 1)
            throw new ArgumentException($"id {id} fits messages in {string.Join(" and ", matches.Select(m => m.FullName))}: this server reuses UIDVALIDITY across folders; give the folder");
        var found = matches[0];
        await Guard("open " + found.FullName, () => found.OpenAsync(access, ct));
        return (found, uid);
    }

    // ------------------------------------------------------------------ fetching

    /// <summary>A message's receivedAt: its INTERNALDATE in UTC, whole seconds (what IMAP gives). Not unique: list's after is the cursor.</summary>
    private static DateTimeOffset Received(IMessageSummary m)
    {
        var at = (m.InternalDate ?? DateTimeOffset.MinValue).ToUniversalTime();
        return at.AddTicks(-(at.UtcTicks % TimeSpan.TicksPerSecond));
    }

    private static bool Seen(IMessageSummary m) => m.Flags is { } f && f.HasFlag(MessageFlags.Seen);

    private async Task<IList<IMessageSummary>> SummariesAsync(IMailFolder f, IList<UniqueId> uids, CancellationToken ct, MessageSummaryItems extra = MessageSummaryItems.None)
    {
        if (uids.Count == 0) return [];
        var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate | extra;
        if (IsGmail) items |= MessageSummaryItems.GMailMessageId | MessageSummaryItems.GMailThreadId;
        return await Guard("fetch", () => f.FetchAsync(uids, new FetchRequest(items), ct));
    }

    /// <summary>The newest <see cref="MaxMessages"/> of these, oldest first, without bodies.</summary>
    private async Task<IReadOnlyList<EmailMessage>> NewestAsync(IMailFolder f, IList<UniqueId> uids, CancellationToken ct)
    {
        var sorted = (await SummariesAsync(f, uids, ct)).OrderBy(Received).ThenBy(m => m.UniqueId.Id).TakeLast(MaxMessages).ToList();
        return await MessagesAsync(f, sorted, withBody: false, ct);
    }

    private async Task<IReadOnlyList<EmailMessage>> MessagesAsync(IMailFolder f, IReadOnlyList<IMessageSummary> summaries, bool withBody, CancellationToken ct)
    {
        var list = new List<EmailMessage>(summaries.Count);
        foreach (var s in summaries)
        {
            var mime = await Guard("fetch", () => f.GetMessageAsync(s.UniqueId, ct));
            list.Add(Build(f, s, mime, withBody));
        }
        return list;
    }

    private async Task<EmailMessage> OneAsync(IMailFolder f, UniqueId uid, string id, bool withBody, CancellationToken ct)
    {
        var s = (await SummariesAsync(f, [uid], ct)).FirstOrDefault(m => m.UniqueId.Id == uid.Id)
            ?? throw new ArgumentException($"no message with id {id} in {f.FullName}");
        var mime = await Guard("fetch", () => f.GetMessageAsync(uid, ct));
        return Build(f, s, mime, withBody);
    }

    private EmailMessage Build(IMailFolder f, IMessageSummary s, MimeMessage mime, bool withBody)
    {
        string id;
        string? thread = null;
        if (IsGmail && s.GMailMessageId is { } gid)
        {
            id = EmailMessageId.Gmail(gid);
            if (s.GMailThreadId is { } tid) thread = EmailMessageId.Gmail(tid);
        }
        else id = EmailMessageId.Imap(f.UidValidity, s.UniqueId.Id);
        return EmailMessage.Build(id, thread, f.FullName, EmailCursor.Of(f.UidValidity, s.UniqueId.Id), Received(s), !Seen(s), mime, withBody);
    }

    // ------------------------------------------------------------------ failures

    private static async Task<T> Guard<T>(string what, Func<Task<T>> step)
    {
        try { return await step(); }
        catch (FolderNotFoundException e) { throw new ArgumentException($"the mailbox has no folder named '{Clip(e.FolderName)}'"); }
        catch (Exception e) when (e is IOException or ProtocolException or CommandException or ServiceNotConnectedException or FolderNotOpenException)
        {
            throw Failed(what, e);
        }
    }

    private static async Task Guard(string what, Func<Task> step) => await Guard<bool>(what, async () => { await step(); return true; });

    private static MailException Failed(string what, Exception e) =>
        new(MailFailure.Protocol, $"the IMAP server could not {what} ({e.GetType().Name}{(e is ImapCommandException ice && ice.Response != ImapCommandResponse.Ok ? ": " + Clip(ice.ResponseText ?? "") : "")})", e);

    private static string Clip(string s) => s.Length <= 80 ? s : s[..80] + "...";
}
