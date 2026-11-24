using Xunit;
using Yawble.Core.Email;
using Yawble.Core.Tests.Email;

namespace Yawble.Core.Tests.Scripts.Actions;

/// <summary>
/// B133: polling a folder with list's after (the id of the last message an earlier list gave) gets every message once, on the plain IMAP fake
/// and the Gmail fake; a renumbered folder (another UIDVALIDITY) resets the cursor instead of failing.
/// </summary>
public class B133VerifyTests
{
    private const string Address = "reader@example.test";
    private const string Password = "test-password";
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeMailServer Server(bool gmail)
    {
        var mail = new FakeMailServer(Address, Password);
        if (gmail)
        {
            mail.Imap.Capabilities.Add("X-GM-EXT-1");
            mail.Imap.Folders.Add(new FakeFolder(EmailMailbox.GmailAllMail, @"\All"));
        }
        mail.Imap.WithMailbox();
        return mail;
    }

    /// <summary>A message in INBOX; on Gmail with an X-GM-MSGID (<paramref name="key"/>) and in All Mail too.</summary>
    private static FakeMessage Add(FakeMailServer mail, bool gmail, string subject, DateTimeOffset at, ulong key)
    {
        var m = new FakeMessage(FakeImapMailbox.Plain("sender@example.test", Address, subject, "body"), at) { GmailMessageId = gmail ? key : 0, GmailThreadId = gmail ? key : 0 };
        mail.Imap.Folder("INBOX").Put(m);
        if (gmail) mail.Imap.Folder(EmailMailbox.GmailAllMail).Put(m);
        return m;
    }

    private static async Task<T> With<T>(FakeMailServer mail, Func<EmailMailbox, Task<T>> work)
    {
        await using var session = await mail.Factory().OpenImapAsync(new EmailAccount(mail.Settings(Address), Password), Ct);
        return await work(new EmailMailbox(session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastSeenWithLimitedBatchDoesNotLoseMessagesAtSameTimestamp(bool gmail)
    {
        await using var mail = Server(gmail);
        Add(mail, gmail, "First", Timestamp, 101);
        Add(mail, gmail, "Second", Timestamp, 102);
        var first = await With(mail, m => m.ListAsync("INBOX", null, true, 1, after: null, Ct));
        Assert.Single(first.Messages);
        var second = await With(mail, m => m.ListAsync("INBOX", null, true, 1, first.Messages[0].Id, Ct));
        Assert.False(second.CursorReset);
        Assert.Single(second.Messages);
        Assert.NotEqual(first.Messages[0].Id, second.Messages[0].Id);
        Assert.Equal(["First", "Second"], first.Messages.Concat(second.Messages).Select(m => m.Subject));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastSeenWithLimitedBatchDoesNotSkipOlderUnreadMessages(bool gmail)
    {
        await using var mail = Server(gmail);
        for (var i = 0; i < 2; i++) Add(mail, gmail, $"Message {i}", Timestamp.AddMinutes(i), 200UL + (ulong)i);
        var first = await With(mail, m => m.ListAsync("INBOX", null, true, 1, after: null, Ct));
        Assert.Equal("Message 0", Assert.Single(first.Messages).Subject);
        var second = await With(mail, m => m.ListAsync("INBOX", null, true, 1, first.Messages[0].Id, Ct));
        Assert.Equal("Message 1", Assert.Single(second.Messages).Subject);
        Assert.NotEqual(first.Messages[0].Id, second.Messages[0].Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NewMessageInCursorSecondWithLowerTieBreakIsNotLost(bool gmail)
    {
        // keys 9,999,999 and 10,000,000 in one second: as UIDs on plain IMAP, as X-GM-MSGIDs on Gmail
        await using var mail = Server(gmail);
        var folder = mail.Imap.Folder("INBOX");
        var firstMessage = new FakeMessage(FakeImapMailbox.Plain("sender@example.test", Address, "First", "body"), Timestamp) { GmailMessageId = 9999999, GmailThreadId = 9999999 };
        if (gmail) folder.Put(firstMessage); else folder.Messages.Add(new FakeEntry(9999999, firstMessage));
        var first = Assert.Single((await With(mail, m => m.ListAsync("INBOX", null, false, 1, after: null, Ct))).Messages);
        var next = new FakeMessage(FakeImapMailbox.Plain("sender@example.test", Address, "Next", "body"), Timestamp) { GmailMessageId = 10000000, GmailThreadId = 10000000 };
        folder.Put(next);
        var second = await With(mail, m => m.ListAsync("INBOX", null, false, 1, first.Id, Ct));
        Assert.Equal("Next", Assert.Single(second.Messages).Subject);
        Assert.Empty((await With(mail, m => m.ListAsync("INBOX", null, false, 1, second.Messages[0].Id, Ct))).Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaxOnePagesThroughABacklogOnce(bool gmail)
    {
        await using var mail = Server(gmail);
        // the last message an earlier poll saw, then a backlog: a shared second for most, then dates going backwards (arrival order is UID order)
        Add(mail, gmail, "seen before", Timestamp.AddHours(-1), 9_100);
        string? after = Assert.Single((await With(mail, m => m.ListAsync("INBOX", null, false, 1, after: null, Ct))).Messages).Id;
        var subjects = Enumerable.Range(0, 12).Select(i => "B" + i).ToList();
        for (var i = 0; i < subjects.Count; i++) Add(mail, gmail, subjects[i], i < 6 ? Timestamp : Timestamp.AddMinutes(-i), 9_000UL - (ulong)i);
        var got = new List<string>();
        for (var poll = 0; poll < 20; poll++)
        {
            var r = await With(mail, m => m.ListAsync("INBOX", null, false, 1, after, Ct));
            Assert.False(r.CursorReset);
            if (r.Messages.Count == 0) continue;
            got.Add(Assert.Single(r.Messages).Subject);
            after = r.Messages[^1].Id;
        }
        Assert.Equal(subjects, got);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewMailArrivingBetweenPollsIsPickedUp(bool gmail)
    {
        await using var mail = Server(gmail);
        Add(mail, gmail, "A", Timestamp, 500);
        var a = Assert.Single((await With(mail, m => m.ListAsync("INBOX", Timestamp.AddMinutes(-1), true, 5, after: null, Ct))).Messages);
        Assert.Empty((await With(mail, m => m.ListAsync("INBOX", Timestamp.AddMinutes(-1), true, 5, a.Id, Ct))).Messages);
        // two arrive between polls, in A's second and one dated a second earlier
        Add(mail, gmail, "B", Timestamp, 499);
        Add(mail, gmail, "C", Timestamp.AddSeconds(-1), 498);
        var next = await With(mail, m => m.ListAsync("INBOX", Timestamp.AddMinutes(-1), true, 5, a.Id, Ct));
        Assert.False(next.CursorReset);
        Assert.Equal(["B", "C"], next.Messages.Select(m => m.Subject));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CursorSurvivesMarkReadAndMoveOfProcessedMessage(bool gmail)
    {
        await using var mail = Server(gmail);
        foreach (var (subject, key) in new[] { ("First", 101UL), ("Second", 102UL), ("Third", 103UL) }) Add(mail, gmail, subject, Timestamp, key);
        var first = Assert.Single((await With(mail, m => m.ListAsync("INBOX", null, true, 1, after: null, Ct))).Messages);
        var again = Assert.Single((await With(mail, m => m.ListAsync("INBOX", null, true, 1, after: null, Ct))).Messages);
        Assert.Equal(first.Id, again.Id);
        await With(mail, m => m.MarkReadAsync(first.Id, "INBOX", Ct));
        await With(mail, m => m.MoveAsync(first.Id, "Archive", "INBOX", Ct));
        var second = Assert.Single((await With(mail, m => m.ListAsync("INBOX", null, true, 1, first.Id, Ct))).Messages);
        Assert.Equal("Second", second.Subject);
        var third = Assert.Single((await With(mail, m => m.ListAsync("INBOX", null, true, 1, second.Id, Ct))).Messages);
        Assert.Equal("Third", third.Subject);
        Assert.Empty((await With(mail, m => m.ListAsync("INBOX", null, true, 1, third.Id, Ct))).Messages);
    }

    // ------------------------------------------------------------------ UIDVALIDITY changes

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStaleAfterWithSinceFallsBackToTheSinceList(bool gmail)
    {
        await using var mail = Server(gmail);
        Add(mail, gmail, "Old", Timestamp, 11);
        var old = Assert.Single((await With(mail, m => m.ListAsync("INBOX", null, false, 5, after: null, Ct))).Messages);
        Add(mail, gmail, "New", Timestamp.AddMinutes(10), 12);
        mail.Imap.Folder("INBOX").Renumber();
        var r = await With(mail, m => m.ListAsync("INBOX", Timestamp.AddMinutes(5), false, 5, old.Id, Ct));
        Assert.True(r.CursorReset);
        Assert.Equal(["New"], r.Messages.Select(m => m.Subject));
        Assert.EndsWith("2:2", r.Messages[0].Id); // UIDVALIDITY 2, renumbered from 1
        Assert.True(r.ToJson()["cursorReset"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStaleAfterWithoutSinceIsEmpty(bool gmail)
    {
        await using var mail = Server(gmail);
        Add(mail, gmail, "Old", Timestamp, 11);
        var old = Assert.Single((await With(mail, m => m.ListAsync("INBOX", null, false, 5, after: null, Ct))).Messages);
        Add(mail, gmail, "New", Timestamp.AddMinutes(10), 12);
        mail.Imap.Folder("INBOX").Renumber();
        var r = await With(mail, m => m.ListAsync("INBOX", null, false, 5, old.Id, Ct));
        Assert.True(r.CursorReset);
        Assert.Empty(r.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANormalAfterDoesNotResetTheCursor(bool gmail)
    {
        await using var mail = Server(gmail);
        Add(mail, gmail, "Old", Timestamp, 11);
        var old = Assert.Single((await With(mail, m => m.ListAsync("INBOX", null, false, 5, after: null, Ct))).Messages);
        Add(mail, gmail, "New", Timestamp.AddMinutes(10), 12);
        var r = await With(mail, m => m.ListAsync("INBOX", Timestamp.AddDays(1), false, 5, old.Id, Ct)); // since is ignored while after applies
        Assert.False(r.CursorReset);
        Assert.Equal(["New"], r.Messages.Select(m => m.Subject));
        Assert.False(r.ToJson()["cursorReset"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(false, "not-an-id")]
    [InlineData(false, "2026-11-20T09:00:00Z")]
    [InlineData(false, "1:0")]
    [InlineData(true, "1:1")]
    [InlineData(true, "gm:11")]
    [InlineData(true, "11")]
    public async Task AMalformedAfterIsAnArgumentError(bool gmail, string after)
    {
        await using var mail = Server(gmail);
        Add(mail, gmail, "Old", Timestamp, 11);
        var e = await Assert.ThrowsAsync<ArgumentException>(() => With(mail, m => m.ListAsync("INBOX", Timestamp, false, 5, after, Ct)));
        Assert.Contains("is not a message id", e.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceivedAtIsInWholeSeconds(bool gmail)
    {
        await using var mail = Server(gmail);
        Add(mail, gmail, "Odd time", new DateTimeOffset(2026, 11, 20, 9, 10, 0, 987, TimeSpan.FromHours(2)), 11);
        var m = Assert.Single((await With(mail, x => x.ListAsync("INBOX", null, false, 5, after: null, Ct))).Messages);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 7, 10, 0, TimeSpan.Zero), m.ReceivedAt);
        Assert.Equal(0, m.ReceivedAt.UtcTicks % TimeSpan.TicksPerSecond);
        Assert.Equal("2026-11-20T07:10:00Z", m.ToJson()["receivedAt"]!.GetValue<string>());
    }
}
