using Yawble.Core.Email;
using Xunit;

namespace Yawble.Core.Tests.Email;

/// <summary>The Email integration's read actions (card 590) on <see cref="EmailMailbox"/> against the fake IMAP server, Gmail extensions off and on.</summary>
public class EmailMailboxTests
{
    private const string Pw = "test-password";
    private const string Me = "pat@example.test";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    private static FakeMailServer Server(bool gmail = false, bool move = true)
    {
        var mail = new FakeMailServer(Me, Pw);
        if (gmail)
        {
            mail.Imap.Capabilities.Add("X-GM-EXT-1");
            mail.Imap.Folders.Add(new FakeFolder(EmailMailbox.GmailAllMail, @"\All"));
        }
        mail.Imap.WithMailbox(move);
        return mail;
    }

    private static async Task<T> With<T>(FakeMailServer mail, Func<EmailMailbox, Task<T>> work)
    {
        await using var s = await mail.Factory().OpenImapAsync(new EmailAccount(mail.Settings(), Pw), Ct);
        return await work(new EmailMailbox(s));
    }

    /// <summary>A message in INBOX (and, on Gmail, All Mail with ids), received at T0 plus <paramref name="minutes"/>.</summary>
    private static FakeMessage Add(FakeMailServer mail, int minutes, string subject, bool seen = false, string? raw = null, ulong gmId = 0, ulong thread = 0, string folder = "INBOX")
    {
        var m = new FakeMessage(raw ?? FakeImapMailbox.Plain("Lee Client <lee@example.test>", Me, subject, $"Body of {subject}."), T0.AddMinutes(minutes));
        if (seen) m.Flags.Add(@"\Seen");
        m.GmailMessageId = gmId;
        m.GmailThreadId = thread == 0 ? gmId : thread;
        mail.Imap.Folder(folder).Put(m);
        if (gmId != 0) mail.Imap.Folder(EmailMailbox.GmailAllMail).Put(m);
        return m;
    }

    // ------------------------------------------------------------------ list

    [Fact]
    public async Task ListIsOldestFirstWithEveryField()
    {
        await using var mail = Server();
        mail.Imap.Folder("INBOX").UidValidity = 777;
        // put in out of order: the list orders by INTERNALDATE, not UID
        Add(mail, 30, "Second");
        Add(mail, 10, "First", seen: true, raw: FakeImapMailbox.Plain("\"Lee Client\" <lee@example.test>", $"{Me}, Ann <ann@example.test>", "First", "Hello there.", cc: "boss@example.test"));
        var list = await With(mail, m => m.ListAsync("INBOX", null, false, null, Ct));

        Assert.Equal(["First", "Second"], list.Select(m => m.Subject));
        var first = list[0];
        Assert.Equal("777:2", first.Id);
        Assert.Equal("INBOX", first.Folder);
        Assert.Equal(new EmailAddress("Lee Client", "lee@example.test"), first.From);
        Assert.Equal([new EmailAddress(null, Me), new EmailAddress("Ann", "ann@example.test")], first.To);
        Assert.Equal([new EmailAddress(null, "boss@example.test")], first.Cc);
        Assert.Equal(T0.AddMinutes(10).AddTicks(2), first.ReceivedAt); // INTERNALDATE plus the tie-break: UID 2 in 100 ns ticks
        Assert.Equal("Hello there.", first.Snippet);
        Assert.False(first.Unread);
        Assert.True(list[1].Unread);
        Assert.False(first.HasAttachments);
        Assert.Null(first.Body);
        Assert.NotNull(first.MessageId);
        Assert.Equal(first.MessageId, first.ThreadId); // no Gmail: the thread's first Message-ID, here its own

        var json = first.ToJson();
        Assert.Equal("2026-11-20T09:10:00.0000002Z", json["receivedAt"]!.GetValue<string>());
        Assert.False(json.ContainsKey("body"));
        Assert.Empty(Yawble.Core.Scripts.Language.BuiltInSchemas.EmailMessage.Validate(json));
    }

    [Fact]
    public async Task ListSinceIsExclusiveSoALastSeenNeverRepeats()
    {
        await using var mail = Server();
        Add(mail, 0, "A");
        Add(mail, 5, "B");
        Add(mail, 10, "C");
        var all = await With(mail, m => m.ListAsync("INBOX", null, false, null, Ct));
        var lastSeen = all[1].ReceivedAt; // B

        var after = await With(mail, m => m.ListAsync("INBOX", lastSeen, false, null, Ct));
        Assert.Equal(["C"], after.Select(m => m.Subject));
        Assert.Empty(await With(mail, m => m.ListAsync("INBOX", all[2].ReceivedAt, false, null, Ct)));
        // a day boundary: SINCE is by date, the exact time is compared here
        Assert.Contains(mail.Imap.Lines(), l => l.Contains("UID SEARCH", StringComparison.Ordinal) && l.Contains("SINCE 19-Nov-2026", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListUnreadOnlyAndMaxKeepTheOldestStillOldestFirst()
    {
        await using var mail = Server();
        for (var i = 0; i < 6; i++) Add(mail, i, "M" + i, seen: i % 2 == 0);
        var unread = await With(mail, m => m.ListAsync("INBOX", null, true, null, Ct));
        Assert.Equal(["M1", "M3", "M5"], unread.Select(m => m.Subject));
        Assert.All(unread, m => Assert.True(m.Unread));

        var oldest = await With(mail, m => m.ListAsync("INBOX", null, false, 2, Ct));
        Assert.Equal(["M0", "M1"], oldest.Select(m => m.Subject));
        var oldestUnread = await With(mail, m => m.ListAsync("INBOX", null, true, 2, Ct));
        Assert.Equal(["M1", "M3"], oldestUnread.Select(m => m.Subject));
        var afterM1 = await With(mail, m => m.ListAsync("INBOX", oldest[1].ReceivedAt, false, 2, Ct));
        Assert.Equal(["M2", "M3"], afterM1.Select(m => m.Subject));
        Assert.Empty(await With(mail, m => m.ListAsync("INBOX", null, false, 0, Ct)));
    }

    /// <summary>
    /// B133: a script polling with max 1 and its saved lastSeen gets every message exactly once, older unread ones first and messages that share a
    /// second included, also when new mail (some in an already-seen second) arrives between polls.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PollingWithMaxOneFromTheSavedReceivedAtGetsEveryMessageOnce(bool gmail)
    {
        await using var mail = Server(gmail);
        ulong gm = 1789000000000000000;
        // A and B share a second, C is later; D comes later but in A's second (a server's INTERNALDATE need not grow with the UID)
        Add(mail, 0, "A", gmId: gmail ? ++gm : 0);
        Add(mail, 0, "B", gmId: gmail ? ++gm : 0);
        Add(mail, 5, "C", gmId: gmail ? ++gm : 0);
        Add(mail, 5, "read", seen: true, gmId: gmail ? ++gm : 0);

        var got = new List<string>();
        string? lastSeen = null;
        for (var poll = 0; poll < 10; poll++)
        {
            if (poll == 2) Add(mail, 10, "E", gmId: gmail ? ++gm : 0); // arrives after two polls
            if (poll == 3) Add(mail, 5, "D", gmId: gmail ? ++gm : 0); // a later UID in C's second, not yet past the cursor
            var since = lastSeen is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(lastSeen, System.Globalization.CultureInfo.InvariantCulture);
            var batch = await With(mail, m => m.ListAsync("INBOX", since, true, 1, Ct));
            Assert.True(batch.Count <= 1);
            if (batch.Count == 0) continue;
            got.Add(batch[0].Subject);
            lastSeen = batch[0].ToJson()["receivedAt"]!.GetValue<string>(); // what a script saves: the written string
        }
        Assert.Equal(["A", "B", "C", "D", "E"], got);
    }

    [Fact]
    public async Task MessagesOfTheSameSecondHaveDistinctReceivedAtThatSortsThem()
    {
        await using var mail = Server();
        for (var i = 0; i < 4; i++) Add(mail, 0, "S" + i);
        var all = await With(mail, m => m.ListAsync("INBOX", null, false, null, Ct));
        Assert.Equal(["S0", "S1", "S2", "S3"], all.Select(m => m.Subject));
        Assert.Equal(4, all.Select(m => m.ReceivedAt).Distinct().Count());
        Assert.All(all, m => Assert.Equal(T0, m.ReceivedAt.AddTicks(-(m.ReceivedAt.UtcTicks % TimeSpan.TicksPerSecond))));
        // a since in the middle of the second: exactly those after it
        var rest = await With(mail, m => m.ListAsync("INBOX", all[1].ReceivedAt, false, null, Ct));
        Assert.Equal(["S2", "S3"], rest.Select(m => m.Subject));
        // a whole-second since (e.g. a lastSeen set to "now") is before every message of that second: the tie-break puts them after it
        var fromSecond = await With(mail, m => m.ListAsync("INBOX", T0, false, null, Ct));
        Assert.Equal(["S0", "S1", "S2", "S3"], fromSecond.Select(m => m.Subject));
    }

    [Fact]
    public async Task ListingNeverMarksMessagesRead()
    {
        await using var mail = Server();
        var msg = Add(mail, 0, "Unread one");
        await With(mail, m => m.ListAsync("INBOX", null, false, null, Ct));
        await With(mail, m => m.GetAsync(EmailMessageId.Imap(1, 1), null, Ct));
        Assert.False(msg.Seen);
        Assert.Contains(mail.Imap.Lines(), l => l.Contains("EXAMINE", StringComparison.Ordinal));
        Assert.DoesNotContain(mail.Imap.Lines(), l => l.Contains(" SELECT ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownFolderIsABadArgument()
    {
        await using var mail = Server();
        var e = await Assert.ThrowsAsync<ArgumentException>(() => With(mail, m => m.ListAsync("Nope", null, false, null, Ct)));
        Assert.Contains("no folder named 'Nope'", e.Message);
    }

    // ------------------------------------------------------------------ ids

    [Fact]
    public async Task IdIsStableAcrossReconnects()
    {
        await using var mail = Server();
        mail.Imap.Folder("INBOX").UidValidity = 4242;
        Add(mail, 0, "Kept");
        var first = (await With(mail, m => m.ListAsync("INBOX", null, false, null, Ct)))[0];
        Add(mail, 5, "Later");
        var again = (await With(mail, m => m.ListAsync("INBOX", null, false, null, Ct)))[0];
        Assert.Equal("4242:1", first.Id);
        Assert.Equal(first.Id, again.Id);
        Assert.True(mail.Imap.Connections >= 2);
        var got = await With(mail, m => m.GetAsync(first.Id, null, Ct));
        Assert.Equal("Kept", got.Subject);
    }

    [Fact]
    public async Task GmailIdsAreXGmMsgIdAndThreadIdAcrossReconnects()
    {
        await using var mail = Server(gmail: true);
        Add(mail, 0, "Hello", gmId: 1789000000000000001, thread: 1789000000000000000);
        var a = (await With(mail, m => m.ListAsync("INBOX", null, false, null, Ct)))[0];
        var b = (await With(mail, m => m.ListAsync("INBOX", null, false, null, Ct)))[0];
        Assert.Equal("1789000000000000001", a.Id);
        Assert.Equal("1789000000000000000", a.ThreadId);
        Assert.Equal(a.Id, b.Id);
        var got = await With(mail, m => m.GetAsync(a.Id, null, Ct));
        Assert.Equal("Hello", got.Subject);
        Assert.Equal(EmailMailbox.GmailAllMail, got.Folder);
        Assert.Contains(mail.Imap.Lines(), l => l.Contains("X-GM-MSGID 1789000000000000001", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MalformedOrUnknownIdsAreBadArguments()
    {
        await using var mail = Server();
        Add(mail, 0, "One");
        Assert.Contains("UIDVALIDITY:UID", (await Assert.ThrowsAsync<ArgumentException>(() => With(mail, m => m.GetAsync("12345", null, Ct)))).Message);
        Assert.Contains("no message with id 1:9", (await Assert.ThrowsAsync<ArgumentException>(() => With(mail, m => m.GetAsync("1:9", null, Ct)))).Message);
        await using var gmail = Server(gmail: true);
        Assert.Contains("Gmail id is a number", (await Assert.ThrowsAsync<ArgumentException>(() => With(gmail, m => m.GetAsync("1:1", null, Ct)))).Message);
    }

    [Fact]
    public async Task AnIdTwoFoldersFitIsRefusedUnlessTheFolderIsGiven()
    {
        await using var mail = Server();
        // a server that numbers every folder with UIDVALIDITY 1: 1:1 is in INBOX and in Archive
        Add(mail, 0, "In inbox");
        Add(mail, 1, "In archive", folder: "Archive");
        var e = await Assert.ThrowsAsync<ArgumentException>(() => With(mail, m => m.GetAsync("1:1", null, Ct)));
        Assert.Contains("INBOX and Archive", e.Message);
        Assert.Equal("In archive", (await With(mail, m => m.GetAsync("1:1", "Archive", Ct))).Subject);
    }

    // ------------------------------------------------------------------ get: bodies and snippets

    [Fact]
    public async Task GetGivesThePlainTextOfAMultipartAndSaysItHasAttachments()
    {
        await using var mail = Server();
        Add(mail, 0, "Quote", raw: FakeImapMailbox.MultipartWithAttachment("lee@example.test", Me, "Quote", "The plain part.", "<p>The <b>HTML</b> part.</p>"));
        var m = await With(mail, x => x.GetAsync("1:1", null, Ct));
        Assert.Equal("The plain part.", m.Body);
        Assert.Equal("The plain part.", m.Snippet);
        Assert.True(m.HasAttachments);
        Assert.Equal("The plain part.", m.ToJson()["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetConvertsAnHtmlOnlyMessageToText()
    {
        await using var mail = Server();
        Add(mail, 0, "Html", raw: FakeImapMailbox.HtmlOnly("lee@example.test", Me, "Html", "<html><body><p>Hello <b>Pat</b>,</p><p>See you.</p></body></html>"));
        var m = await With(mail, x => x.GetAsync("1:1", null, Ct));
        Assert.Contains("Hello Pat,", m.Body);
        Assert.Contains("See you.", m.Body);
        Assert.DoesNotContain("<", m.Body);
        Assert.DoesNotContain("<", m.Snippet);
        Assert.False(m.HasAttachments);
    }

    [Fact]
    public async Task SnippetIsTheFirst500CharactersWhileTheBodyIsWhole()
    {
        await using var mail = Server();
        var text = string.Concat(Enumerable.Range(0, 120).Select(i => $"word{i:000}\n"));
        Add(mail, 0, "Long", raw: FakeImapMailbox.Plain("lee@example.test", Me, "Long", text));
        var m = await With(mail, x => x.GetAsync("1:1", null, Ct));
        Assert.Equal(500, m.Snippet.Length);
        Assert.StartsWith("word000 word001 ", m.Snippet);
        Assert.DoesNotContain("\n", m.Snippet);
        Assert.True(m.Body!.Length > 900);
        Assert.Contains("word119", m.Body);
    }

    // ------------------------------------------------------------------ search and thread

    [Fact]
    public async Task SearchIsImapTextWithoutGmail()
    {
        await using var mail = Server();
        Add(mail, 0, "Invoice 12");
        Add(mail, 1, "Lunch");
        Add(mail, 2, "Invoice 13");
        var hits = await With(mail, m => m.SearchAsync("INBOX", "invoice", Ct));
        Assert.Equal(["Invoice 12", "Invoice 13"], hits.Select(m => m.Subject));
        Assert.Contains(mail.Imap.Lines(), l => l.Contains("UID SEARCH TEXT invoice", StringComparison.Ordinal));
        Assert.DoesNotContain(mail.Imap.Lines(), l => l.Contains("X-GM-RAW", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchIsXGmRawOnGmail()
    {
        await using var mail = Server(gmail: true);
        Add(mail, 0, "Invoice 12", gmId: 11);
        Add(mail, 1, "Invoice 13", seen: true, gmId: 12);
        var hits = await With(mail, m => m.SearchAsync("INBOX", "invoice is:unread", Ct));
        Assert.Equal(["Invoice 12"], hits.Select(m => m.Subject));
        Assert.Contains(mail.Imap.Lines(), l => l.Contains("X-GM-RAW \"invoice is:unread\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThreadOnGmailIsXGmThrIdAcrossAllMailIncludingSent()
    {
        await using var mail = Server(gmail: true);
        Add(mail, 0, "Question", gmId: 100, thread: 100);
        Add(mail, 5, "Other", gmId: 200, thread: 200);
        // the reply was sent: it is in Sent and All Mail, not the inbox
        Add(mail, 10, "Re: Question", gmId: 101, thread: 100, folder: "Sent");
        var thread = await With(mail, m => m.ThreadAsync("100", Ct));
        Assert.Equal(["Question", "Re: Question"], thread.Select(m => m.Subject));
        Assert.All(thread, m => Assert.Equal("100", m.ThreadId));
        Assert.Contains(mail.Imap.Lines(), l => l.Contains("X-GM-THRID 100", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThreadWithoutGmailFollowsReferencesInInboxAndSent()
    {
        await using var mail = Server();
        mail.Imap.Folder("Sent").UidValidity = 2;
        Add(mail, 0, "Question", raw: FakeImapMailbox.Plain("lee@example.test", Me, "Question", "Q?", messageId: "q1@example.test"));
        Add(mail, 5, "Unrelated", raw: FakeImapMailbox.Plain("lee@example.test", Me, "Unrelated", "x", messageId: "u1@example.test"));
        Add(mail, 10, "Re: Question", folder: "Sent",
            raw: FakeImapMailbox.Plain(Me, "lee@example.test", "Re: Question", "A.", messageId: "r1@example.test", inReplyTo: "q1@example.test", references: "<q1@example.test>"));
        Add(mail, 20, "Re: Re: Question",
            raw: FakeImapMailbox.Plain("lee@example.test", Me, "Re: Re: Question", "Thanks", messageId: "r2@example.test", inReplyTo: "r1@example.test", references: "<q1@example.test> <r1@example.test>"));

        var list = await With(mail, m => m.ListAsync("INBOX", null, false, null, Ct));
        var root = list[0];
        Assert.Equal("q1@example.test", root.ThreadId);
        Assert.Equal("q1@example.test", list.Single(m => m.Subject == "Re: Re: Question").ThreadId);

        var thread = await With(mail, m => m.ThreadAsync(root.ThreadId, Ct));
        Assert.Equal(["Question", "Re: Question", "Re: Re: Question"], thread.Select(m => m.Subject));
        Assert.Equal(["INBOX", "Sent", "INBOX"], thread.Select(m => m.Folder));
    }

    // ------------------------------------------------------------------ markRead and move

    [Fact]
    public async Task MarkReadSetsSeenAndReturnsTheMessage()
    {
        await using var mail = Server();
        var msg = Add(mail, 0, "Read me");
        var m = await With(mail, x => x.MarkReadAsync("1:1", null, Ct));
        Assert.True(msg.Seen);
        Assert.False(m.Unread);
        Assert.Contains(mail.Imap.Lines(), l => l.Contains("UID STORE 1 +FLAGS.SILENT (\\Seen)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MoveWithoutGmailIsImapMoveAndGivesTheNewId()
    {
        await using var mail = Server();
        mail.Imap.Folder("Archive").UidValidity = 9;
        Add(mail, 0, "Old");
        var moved = await With(mail, x => x.MoveAsync("1:1", "Archive", null, Ct));
        Assert.Equal("Archive", moved.Folder);
        Assert.Equal("9:1", moved.Id);
        Assert.Empty(mail.Imap.Folder("INBOX").Entries());
        Assert.Single(mail.Imap.Folder("Archive").Entries());
        Assert.Contains(mail.Imap.Lines(), l => l.Contains("UID MOVE 1 Archive", StringComparison.Ordinal));
        Assert.DoesNotContain(mail.Imap.Lines(), l => l.Contains("EXPUNGE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MoveIsRefusedWhereItWouldNeedAFolderWideExpunge()
    {
        await using var mail = Server(move: false);
        Add(mail, 0, "Stay");
        var e = await Assert.ThrowsAsync<MailException>(() => With(mail, x => x.MoveAsync("1:1", "Archive", null, Ct)));
        Assert.Contains("does not do that", e.Message);
        Assert.Single(mail.Imap.Folder("INBOX").Entries());
    }

    [Fact]
    public async Task MoveToAFolderThatDoesNotExistIsABadArgument()
    {
        await using var mail = Server();
        Add(mail, 0, "Stay");
        await Assert.ThrowsAsync<ArgumentException>(() => With(mail, x => x.MoveAsync("1:1", "Nowhere", null, Ct)));
        Assert.Single(mail.Imap.Folder("INBOX").Entries());
    }

    [Fact]
    public async Task MoveOnGmailIsALabelMoveAndKeepsTheId()
    {
        await using var mail = Server(gmail: true);
        var msg = Add(mail, 0, "File me", gmId: 555);
        var moved = await With(mail, x => x.MoveAsync("555", "Archive", null, Ct));
        Assert.Equal("555", moved.Id);
        Assert.Equal("Archive", moved.Folder);
        Assert.Empty(mail.Imap.Folder("INBOX").Entries());
        Assert.Contains(mail.Imap.Folder("Archive").Entries(), e => ReferenceEquals(e.Message, msg));
        Assert.Contains(mail.Imap.Folder(EmailMailbox.GmailAllMail).Entries(), e => ReferenceEquals(e.Message, msg));
        Assert.Contains(mail.Imap.Lines(), l => l.Contains("+X-GM-LABELS.SILENT (Archive)", StringComparison.Ordinal));
        Assert.Contains(mail.Imap.Lines(), l => l.Contains("-X-GM-LABELS.SILENT (\\\\Inbox)", StringComparison.Ordinal) || l.Contains("-X-GM-LABELS.SILENT (\\Inbox)", StringComparison.Ordinal));
        Assert.DoesNotContain(mail.Imap.Lines(), l => l.Contains("UID MOVE", StringComparison.Ordinal));

        var back = await With(mail, x => x.MoveAsync("555", "INBOX", null, Ct));
        Assert.Equal("INBOX", back.Folder);
        Assert.Single(mail.Imap.Folder("INBOX").Entries());
    }
}
