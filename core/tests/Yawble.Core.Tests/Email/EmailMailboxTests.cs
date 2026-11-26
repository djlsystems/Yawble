using Yawble.Core.Email;
using Xunit;

namespace Yawble.Core.Tests.Email;

/// <summary>The Email integration's read actions on <see cref="EmailMailbox"/> against the fake IMAP server, Gmail extensions off and on.</summary>
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

    private static async Task<IReadOnlyList<EmailMessage>> List(FakeMailServer mail, DateTimeOffset? since, bool unreadOnly, int? max, string? after = null)
    {
        var r = await With(mail, m => m.ListAsync("INBOX", since, unreadOnly, max, after, Ct));
        Assert.False(r.CursorReset);
        return r.Messages;
    }

    [Fact]
    public async Task ListIsOldestFirstWithEveryField()
    {
        await using var mail = Server();
        mail.Imap.Folder("INBOX").UidValidity = 777;
        // put in out of date order: without after the list is by (INTERNALDATE, UID), the oldest first
        Add(mail, 30, "Second");
        Add(mail, 10, "First", seen: true, raw: FakeImapMailbox.Plain("\"Lee Client\" <lee@example.test>", $"{Me}, Ann <ann@example.test>", "First", "Hello there.", cc: "boss@example.test"));
        var list = await List(mail, null, false, null);

        Assert.Equal(["First", "Second"], list.Select(m => m.Subject));
        var first = list[0];
        Assert.Equal("777:2", first.Id);
        Assert.Equal("INBOX", first.Folder);
        Assert.Equal(new EmailAddress("Lee Client", "lee@example.test"), first.From);
        Assert.Equal([new EmailAddress(null, Me), new EmailAddress("Ann", "ann@example.test")], first.To);
        Assert.Equal([new EmailAddress(null, "boss@example.test")], first.Cc);
        Assert.Equal(T0.AddMinutes(10), first.ReceivedAt); // the INTERNALDATE, nothing added
        Assert.Equal("Hello there.", first.Snippet);
        Assert.False(first.Unread);
        Assert.True(list[1].Unread);
        Assert.False(first.HasAttachments);
        Assert.Null(first.Body);
        Assert.NotNull(first.MessageId);
        Assert.Equal(first.MessageId, first.ThreadId); // no Gmail: the thread's first Message-ID, here its own

        var json = first.ToJson();
        Assert.Equal("2026-11-20T09:10:00Z", json["receivedAt"]!.GetValue<string>());
        Assert.Equal("777:2", json["id"]!.GetValue<string>());
        Assert.False(json.ContainsKey("cursor"));
        Assert.False(json.ContainsKey("body"));
        Assert.Empty(Yawble.Core.Workflows.Language.BuiltInSchemas.EmailMessage.Validate(json));

        var result = await With(mail, m => m.ListAsync("INBOX", null, false, null, Ct));
        var resultJson = result.ToJson();
        Assert.False(resultJson["cursorReset"]!.GetValue<bool>());
        Assert.Equal(2, resultJson["messages"]!.AsArray().Count);
        Assert.Empty(Yawble.Core.Workflows.Language.BuiltInSchemas.EmailList.Validate(resultJson, Yawble.Core.Workflows.Language.BuiltInSchemas.Find));
    }

    [Fact]
    public async Task ListSinceIsAnExclusiveTimeFilterByWholeSeconds()
    {
        await using var mail = Server();
        Add(mail, 0, "A");
        Add(mail, 5, "B");
        Add(mail, 5, "B2"); // B's second
        Add(mail, 10, "C");
        var all = await List(mail, null, false, null);
        Assert.Equal(T0.AddMinutes(5), all[1].ReceivedAt);
        Assert.Equal(all[1].ReceivedAt, all[2].ReceivedAt); // not unique: since is not a cursor

        // later than since: B's receivedAt leaves out B and B2, one second earlier keeps both
        Assert.Equal(["C"], (await List(mail, all[1].ReceivedAt, false, null)).Select(m => m.Subject));
        Assert.Equal(["B", "B2", "C"], (await List(mail, all[1].ReceivedAt.AddSeconds(-1), false, null)).Select(m => m.Subject));
        Assert.Equal(["B", "B2"], (await List(mail, T0, false, 2)).Select(m => m.Subject)); // the oldest max
        Assert.Empty(await List(mail, all[3].ReceivedAt, false, null));
        // a day boundary: SINCE is by date, the exact time is compared here
        Assert.Contains(mail.Imap.Lines(), l => l.Contains("UID SEARCH", StringComparison.Ordinal) && l.Contains("SINCE 19-Nov-2026", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListUnreadOnlyAndMaxKeepTheFirst()
    {
        await using var mail = Server();
        for (var i = 0; i < 6; i++) Add(mail, i, "M" + i, seen: i % 2 == 0);
        var unread = await List(mail, null, true, null);
        Assert.Equal(["M1", "M3", "M5"], unread.Select(m => m.Subject));
        Assert.All(unread, m => Assert.True(m.Unread));

        var oldest = await List(mail, null, false, 2);
        Assert.Equal(["M0", "M1"], oldest.Select(m => m.Subject));
        var oldestUnread = await List(mail, null, true, 2);
        Assert.Equal(["M1", "M3"], oldestUnread.Select(m => m.Subject));
        var afterM1 = await List(mail, null, false, 2, oldest[1].Id);
        Assert.Equal(["M2", "M3"], afterM1.Select(m => m.Subject));
        Assert.Equal(["M3", "M5"], (await List(mail, null, true, 5, oldest[1].Id)).Select(m => m.Subject));
        Assert.Empty(await List(mail, null, false, 0));
        Assert.Empty(await List(mail, null, false, 0, oldest[1].Id));
    }

    // ------------------------------------------------------------------ list after an id

    /// <summary>
    /// Polls like the documented workflow: list after the saved id (none the first time), saving the last message's id only when there is one.
    /// </summary>
    private static async Task<(List<string> Got, string? Last)> PollAsync(FakeMailServer mail, string? after, int max, bool unreadOnly = true, int polls = 1,
        Action<int>? between = null, DateTimeOffset? since = null)
    {
        var got = new List<string>();
        for (var poll = 0; poll < polls; poll++)
        {
            between?.Invoke(poll);
            var r = await With(mail, m => m.ListAsync("INBOX", since, unreadOnly, max, after, Ct));
            Assert.False(r.CursorReset);
            Assert.True(r.Messages.Count <= max);
            if (r.Messages.Count == 0) continue;
            got.AddRange(r.Messages.Select(b => b.Subject));
            after = r.Messages[^1].ToJson()["id"]!.GetValue<string>(); // what a workflow saves: the written string
        }
        return (got, after);
    }

    /// <summary>Gmail X-GM-MSGIDs need not follow arrival order: here they fall, by large steps, so any order by them would scramble the poll.</summary>
    private static ulong GmId(bool gmail, ref ulong gm)
    {
        if (!gmail) return 0;
        gm -= 7_000_000_000_013;
        return gm;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PollingAfterTheIdWithMaxOneGetsABacklogOnceInArrivalOrder(bool gmail)
    {
        await using var mail = Server(gmail);
        ulong gm = 1_789_000_000_000_000_000;
        // the last message an earlier run saw, then a backlog: dates out of arrival order, a shared second, a read one
        Add(mail, -60, "seen before", gmId: GmId(gmail, ref gm));
        var (_, start) = await PollAsync(mail, null, max: 1, unreadOnly: false);
        Add(mail, 9, "A", gmId: GmId(gmail, ref gm));
        Add(mail, 0, "B", gmId: GmId(gmail, ref gm));
        Add(mail, 0, "C", gmId: GmId(gmail, ref gm));
        Add(mail, 5, "read", seen: true, gmId: GmId(gmail, ref gm));
        Add(mail, 5, "D", gmId: GmId(gmail, ref gm));

        var (got, last) = await PollAsync(mail, start, max: 1, polls: 4, between: poll =>
        {
            if (poll == 2) Add(mail, -600, "E: appended with an old date", gmId: GmId(gmail, ref gm)); // after two polls, dated before everything
        });
        Assert.Equal(["A", "B", "C", "D"], got);
        var (rest, end) = await PollAsync(mail, last, max: 1, polls: 3);
        Assert.Equal(["E: appended with an old date"], rest);
        Assert.Empty((await PollAsync(mail, end, max: 1)).Got);
        Assert.Contains(mail.Imap.Lines(), l => l.Contains("UID SEARCH", StringComparison.Ordinal) && l.Contains("UID 2:*", StringComparison.Ordinal) && l.Contains("UNSEEN", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManyMessagesInOneSecondAreEachPolledOnce(bool gmail)
    {
        await using var mail = Server(gmail);
        ulong gm = 1_789_000_000_000_000_000;
        var subjects = Enumerable.Range(0, 25).Select(i => "S" + i).ToList();
        foreach (var s in subjects) Add(mail, 0, s, gmId: GmId(gmail, ref gm));
        foreach (var max in new[] { 1, 3, 7 })
        {
            var (got, _) = await PollAsync(mail, null, max, unreadOnly: false, polls: 30);
            Assert.Equal(subjects, got);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UidsNineMillionNineNinesThenTenMillionKeepTheirOrder(bool gmail)
    {
        await using var mail = Server(gmail);
        var inbox = mail.Imap.Folder("INBOX");
        // UID 9,999,999 and then 10,000,000 (Gmail: those X-GM-MSGIDs as well), the same second
        var first = new FakeMessage(FakeImapMailbox.Plain("lee@example.test", Me, "First", "x"), T0) { GmailMessageId = gmail ? 9_999_999UL : 0, GmailThreadId = gmail ? 9_999_999UL : 0 };
        inbox.Messages.Add(new FakeEntry(9_999_999, first));
        if (gmail) mail.Imap.Folder(EmailMailbox.GmailAllMail).Put(first);
        var (got, last) = await PollAsync(mail, null, max: 1, unreadOnly: false);
        Assert.Equal(["First"], got);
        Assert.Equal(gmail ? "gm:9999999:1:9999999" : "1:9999999", last);

        var next = new FakeMessage(FakeImapMailbox.Plain("lee@example.test", Me, "Next", "x"), T0) { GmailMessageId = gmail ? 10_000_000UL : 0, GmailThreadId = gmail ? 10_000_000UL : 0 };
        Assert.Equal(10_000_000u, inbox.Put(next).Uid);
        if (gmail) mail.Imap.Folder(EmailMailbox.GmailAllMail).Put(next);
        var (more, end) = await PollAsync(mail, last, max: 1, unreadOnly: false);
        Assert.Equal(["Next"], more);
        Assert.Equal(gmail ? "gm:10000000:1:10000000" : "1:10000000", end);
        Assert.Empty((await PollAsync(mail, end, max: 1, unreadOnly: false)).Got);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewMailInTheLastIdsSecondBetweenPollsIsNotLost(bool gmail)
    {
        await using var mail = Server(gmail);
        ulong gm = 1_789_000_000_000_000_000;
        Add(mail, 0, "A", gmId: GmId(gmail, ref gm));
        var (got, last) = await PollAsync(mail, null, max: 5);
        Assert.Equal(["A"], got);
        // two arrive in A's second, one even dated a second before it
        Add(mail, 0, "B", gmId: GmId(gmail, ref gm));
        var c = new FakeMessage(FakeImapMailbox.Plain("lee@example.test", Me, "C", "x"), T0.AddSeconds(-1)) { GmailMessageId = GmId(gmail, ref gm) };
        c.GmailThreadId = c.GmailMessageId;
        mail.Imap.Folder("INBOX").Put(c);
        if (gmail) mail.Imap.Folder(EmailMailbox.GmailAllMail).Put(c);
        var (more, _) = await PollAsync(mail, last, max: 1, polls: 3);
        Assert.Equal(["B", "C"], more);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkReadAndMoveOfProcessedMessagesBetweenPollsKeepTheLastId(bool gmail)
    {
        await using var mail = Server(gmail);
        ulong gm = 1_789_000_000_000_000_000;
        foreach (var s in new[] { "First", "Second", "Third" }) Add(mail, 0, s, gmId: GmId(gmail, ref gm));
        string? last = null;
        var got = new List<string>();
        for (var poll = 0; poll < 5; poll++)
        {
            var batch = await List(mail, null, true, 1, last);
            if (batch.Count == 0) continue;
            var msg = batch[0];
            got.Add(msg.Subject);
            last = msg.Id;
            // the workflow processes it: reads it, files it; on the first poll a new one arrives too
            await With(mail, m => m.MarkReadAsync(msg.Id, "INBOX", Ct));
            await With(mail, m => m.MoveAsync(msg.Id, "Archive", "INBOX", Ct));
            if (poll == 0) Add(mail, -5, "Fourth", gmId: GmId(gmail, ref gm));
        }
        Assert.Equal(["First", "Second", "Third", "Fourth"], got);
        Assert.Empty(mail.Imap.Folder("INBOX").Entries());
        Assert.Equal(4, mail.Imap.Folder("Archive").Entries().Count());
    }

    [Fact]
    public async Task AMessageMovedOutAndBackIsSeenAgainWithANewId()
    {
        await using var mail = Server();
        mail.Imap.Folder("Archive").UidValidity = 9;
        Add(mail, 0, "Bounce");
        var (got, last) = await PollAsync(mail, null, max: 5, unreadOnly: false);
        var moved = await With(mail, m => m.MoveAsync("1:1", "Archive", null, Ct));
        Assert.Equal("9:1", moved.Id); // the id is of the folder it is in now
        await With(mail, m => m.MoveAsync(moved.Id, "INBOX", null, Ct));
        var (again, end) = await PollAsync(mail, last, max: 5, unreadOnly: false);
        Assert.Equal(["Bounce"], got);
        Assert.Equal(["Bounce"], again); // a new UID in INBOX: the documented limit
        Assert.Equal("1:2", end);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAfterOfAnotherUidValidityResetsTheCursor(bool gmail)
    {
        await using var mail = Server(gmail);
        ulong gm = 1_789_000_000_000_000_000;
        Add(mail, 0, "A", gmId: GmId(gmail, ref gm));
        var (_, last) = await PollAsync(mail, null, max: 5);
        Assert.Equal(gmail ? $"gm:{gm}:1:1" : "1:1", last);
        Add(mail, 10, "B", gmId: GmId(gmail, ref gm));
        mail.Imap.Folder("INBOX").Renumber(); // the server rebuilt the folder: UIDVALIDITY 2, UIDs from 1 again

        // with since: the plain since list
        var withSince = await With(mail, m => m.ListAsync("INBOX", T0.AddMinutes(5), true, 5, last, Ct));
        Assert.True(withSince.CursorReset);
        Assert.Equal(["B"], withSince.Messages.Select(m => m.Subject));
        Assert.Equal(gmail ? $"gm:{gm}:2:2" : "2:2", withSince.Messages[0].Id);
        // without since: from now, nothing
        var withoutSince = await With(mail, m => m.ListAsync("INBOX", null, true, 5, last, Ct));
        Assert.True(withoutSince.CursorReset);
        Assert.Empty(withoutSince.Messages);
        Assert.True(withoutSince.ToJson()["cursorReset"]!.GetValue<bool>());
        // and the new ids apply again
        var after = await With(mail, m => m.ListAsync("INBOX", null, true, 5, withSince.Messages[0].Id, Ct));
        Assert.False(after.CursorReset);
        Assert.Empty(after.Messages);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "12")]
    [InlineData(false, "1:")]
    [InlineData(false, "0:5")]
    [InlineData(false, "1:0")]
    [InlineData(false, "x:1")]
    [InlineData(false, "2026-11-20T09:00:00Z")]
    [InlineData(false, "gm:5:1:1")]
    [InlineData(true, "1:1")]
    [InlineData(true, "gm:5")]
    [InlineData(true, "gm:5:1")]
    [InlineData(true, "gm:0:1:1")]
    [InlineData(true, "gm:x:1:1")]
    [InlineData(true, "1789000000000000001")]
    public async Task AMalformedAfterIsABadArgument(bool gmail, string after)
    {
        await using var mail = Server(gmail);
        Add(mail, 0, "A", gmId: gmail ? 5UL : 0);
        var e = await Assert.ThrowsAsync<ArgumentException>(() => With(mail, m => m.ListAsync("INBOX", null, false, 5, after, Ct)));
        Assert.Contains("is not a message id", e.Message);
    }

    [Fact]
    public async Task SinceIsIgnoredWhileAfterApplies()
    {
        await using var mail = Server();
        Add(mail, 0, "A");
        Add(mail, 1, "B");
        Assert.Equal(["B"], (await List(mail, T0.AddDays(1), false, 5, "1:1")).Select(m => m.Subject));
    }

    [Fact]
    public async Task GmailIdHoldsTheMsgIdAndTheLabelFoldersUid()
    {
        await using var mail = Server(gmail: true);
        mail.Imap.Folder("INBOX").UidValidity = 600;
        Add(mail, 0, "Hello", gmId: 1789000000000000001);
        var m = Assert.Single(await List(mail, null, false, null));
        Assert.Equal("gm:1789000000000000001:600:1", m.Id);
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
        var first = (await List(mail, null, false, null))[0];
        Add(mail, 5, "Later");
        var again = (await List(mail, null, false, null))[0];
        Assert.Equal("4242:1", first.Id);
        Assert.Equal(first.Id, again.Id);
        Assert.True(mail.Imap.Connections >= 2);
        var got = await With(mail, m => m.GetAsync(first.Id, null, Ct));
        Assert.Equal("Kept", got.Subject);
    }

    [Fact]
    public async Task GmailIdsHoldXGmMsgIdAndThreadIdAcrossReconnects()
    {
        await using var mail = Server(gmail: true);
        mail.Imap.Folder(EmailMailbox.GmailAllMail).UidValidity = 70;
        Add(mail, 0, "Hello", gmId: 1789000000000000001, thread: 1789000000000000000);
        var a = (await List(mail, null, false, null))[0];
        var b = (await List(mail, null, false, null))[0];
        Assert.Equal("gm:1789000000000000001:1:1", a.Id);
        Assert.Equal("1789000000000000000", a.ThreadId);
        Assert.Equal(a.Id, b.Id);
        // get finds it by the X-GM-MSGID: in All Mail, where its id holds All Mail's UID; in the folder given, where the id stays the same
        var got = await With(mail, m => m.GetAsync(a.Id, null, Ct));
        Assert.Equal("Hello", got.Subject);
        Assert.Equal(EmailMailbox.GmailAllMail, got.Folder);
        Assert.Equal("gm:1789000000000000001:70:1", got.Id);
        var inInbox = await With(mail, m => m.GetAsync(a.Id, "INBOX", Ct));
        Assert.Equal(a.Id, inInbox.Id);
        Assert.Equal("INBOX", inInbox.Folder);
        Assert.Contains(mail.Imap.Lines(), l => l.Contains("X-GM-MSGID 1789000000000000001", StringComparison.Ordinal));
        // the msgid part is what finds it: an id from All Mail works the same
        Assert.Equal("Hello", (await With(mail, m => m.MarkReadAsync(got.Id, null, Ct))).Subject);
    }

    [Fact]
    public async Task MalformedOrUnknownIdsAreBadArguments()
    {
        await using var mail = Server();
        Add(mail, 0, "One");
        Assert.Contains("UIDVALIDITY:UID", (await Assert.ThrowsAsync<ArgumentException>(() => With(mail, m => m.GetAsync("12345", null, Ct)))).Message);
        Assert.Contains("no message with id 1:9", (await Assert.ThrowsAsync<ArgumentException>(() => With(mail, m => m.GetAsync("1:9", null, Ct)))).Message);
        await using var gmail = Server(gmail: true);
        Assert.Contains("gm:MSGID:UIDVALIDITY:UID", (await Assert.ThrowsAsync<ArgumentException>(() => With(gmail, m => m.GetAsync("1:1", null, Ct)))).Message);
        Assert.Contains("gm:MSGID:UIDVALIDITY:UID", (await Assert.ThrowsAsync<ArgumentException>(() => With(gmail, m => m.GetAsync("555", null, Ct)))).Message);
        Assert.Contains("no message with id gm:9:1:1", (await Assert.ThrowsAsync<ArgumentException>(() => With(gmail, m => m.GetAsync("gm:9:1:1", null, Ct)))).Message);
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

        var list = await List(mail, null, false, null);
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
    public async Task MoveOnGmailIsALabelMoveAndGivesTheIdInTheLabel()
    {
        await using var mail = Server(gmail: true);
        mail.Imap.Folder("Archive").UidValidity = 9;
        var msg = Add(mail, 0, "File me", gmId: 555);
        var moved = await With(mail, x => x.MoveAsync("gm:555:1:1", "Archive", null, Ct));
        Assert.Equal("gm:555:9:1", moved.Id); // the same X-GM-MSGID, the Archive label's UIDVALIDITY and UID
        Assert.Equal("Archive", moved.Folder);
        Assert.Empty(mail.Imap.Folder("INBOX").Entries());
        Assert.Contains(mail.Imap.Folder("Archive").Entries(), e => ReferenceEquals(e.Message, msg));
        Assert.Contains(mail.Imap.Folder(EmailMailbox.GmailAllMail).Entries(), e => ReferenceEquals(e.Message, msg));
        Assert.Contains(mail.Imap.Lines(), l => l.Contains("+X-GM-LABELS.SILENT (Archive)", StringComparison.Ordinal));
        Assert.Contains(mail.Imap.Lines(), l => l.Contains("-X-GM-LABELS.SILENT (\\\\Inbox)", StringComparison.Ordinal) || l.Contains("-X-GM-LABELS.SILENT (\\Inbox)", StringComparison.Ordinal));
        Assert.DoesNotContain(mail.Imap.Lines(), l => l.Contains("UID MOVE", StringComparison.Ordinal));

        var back = await With(mail, x => x.MoveAsync(moved.Id, "INBOX", null, Ct));
        Assert.Equal("INBOX", back.Folder);
        Assert.Equal("gm:555:1:2", back.Id); // a new UID in INBOX
        Assert.Single(mail.Imap.Folder("INBOX").Entries());
    }
}
