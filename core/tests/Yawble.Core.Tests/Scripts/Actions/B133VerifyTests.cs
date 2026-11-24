using Xunit;
using Yawble.Core.Email;
using Yawble.Core.Tests.Email;

namespace Yawble.Core.Tests.Scripts.Actions;

public class B133VerifyTests
{
    [Fact]
    public async Task LastSeenWithLimitedBatchDoesNotLoseMessagesAtSameTimestamp()
    {
        const string address = "reader@example.test";
        const string password = "test-password";
        var timestamp = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        await using var mail = new FakeMailServer(address, password);
        mail.Imap.WithMailbox();
        foreach (var subject in new[] { "First", "Second" })
            mail.Imap.Folder("INBOX").Put(new FakeMessage(FakeImapMailbox.Plain("sender@example.test", address, subject, "body"), timestamp));
        var ct = TestContext.Current.CancellationToken;
        await using var session = await mail.Factory().OpenImapAsync(new EmailAccount(mail.Settings(address), password), ct);
        var mailbox = new EmailMailbox(session);
        var first = await mailbox.ListAsync("INBOX", null, true, 1, ct);
        Assert.Single(first);
        var second = await mailbox.ListAsync("INBOX", first[0].ReceivedAt, true, 1, ct);
        Assert.Single(second);
        Assert.NotEqual(first[0].Id, second[0].Id);
    }
    [Fact]
    public async Task LastSeenWithLimitedBatchDoesNotSkipOlderUnreadMessages()
    {
        const string address = "reader@example.test";
        const string password = "test-password";
        var timestamp = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        await using var mail = new FakeMailServer(address, password);
        mail.Imap.WithMailbox();
        for (var i = 0; i < 2; i++)
            mail.Imap.Folder("INBOX").Put(new FakeMessage(FakeImapMailbox.Plain("sender@example.test", address, $"Message {i}", "body"), timestamp.AddMinutes(i)));
        var ct = TestContext.Current.CancellationToken;
        await using var session = await mail.Factory().OpenImapAsync(new EmailAccount(mail.Settings(address), password), ct);
        var mailbox = new EmailMailbox(session);
        var first = await mailbox.ListAsync("INBOX", null, true, 1, ct);
        Assert.Single(first);
        var second = await mailbox.ListAsync("INBOX", first[0].ReceivedAt, true, 1, ct);
        Assert.Single(second);
        Assert.NotEqual(first[0].Id, second[0].Id);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NewMessageInCursorSecondWithLowerTieBreakIsNotLost(bool gmail)
    {
        const string address = "reader@example.test";
        const string password = "test-password";
        var timestamp = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        await using var mail = new FakeMailServer(address, password);
        if (gmail) mail.Imap.Capabilities.Add("X-GM-EXT-1");
        mail.Imap.WithMailbox();
        var folder = mail.Imap.Folder("INBOX");
        var firstMessage = new FakeMessage(FakeImapMailbox.Plain("sender@example.test", address, "First", "body"), timestamp) { GmailMessageId = 9999999, GmailThreadId = 9999999 };
        if (gmail) folder.Put(firstMessage); else folder.Messages.Add(new FakeEntry(9999999, firstMessage));
        var ct = TestContext.Current.CancellationToken;
        await using var session = await mail.Factory().OpenImapAsync(new EmailAccount(mail.Settings(address), password), ct);
        var mailbox = new EmailMailbox(session);
        var first = Assert.Single(await mailbox.ListAsync("INBOX", null, false, 1, ct));
        var next = new FakeMessage(FakeImapMailbox.Plain("sender@example.test", address, "Next", "body"), timestamp) { GmailMessageId = 10000000, GmailThreadId = 10000000 };
        folder.Put(next);
        var second = await mailbox.ListAsync("INBOX", first.ReceivedAt, false, 1, ct);
        Assert.Equal("Next", Assert.Single(second).Subject);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CursorSurvivesMarkReadAndMoveOfProcessedMessage(bool gmail)
    {
        const string address = "reader@example.test";
        const string password = "test-password";
        var timestamp = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        await using var mail = new FakeMailServer(address, password);
        if (gmail)
        {
            mail.Imap.Capabilities.Add("X-GM-EXT-1");
            mail.Imap.Folders.Add(new FakeFolder(EmailMailbox.GmailAllMail, @"\All"));
        }
        mail.Imap.WithMailbox();
        foreach (var (subject, key) in new[] { ("First", 101UL), ("Second", 102UL), ("Third", 103UL) })
        {
            var message = new FakeMessage(FakeImapMailbox.Plain("sender@example.test", address, subject, "body"), timestamp) { GmailMessageId = gmail ? key : 0, GmailThreadId = gmail ? key : 0 };
            mail.Imap.Folder("INBOX").Put(message);
            if (gmail) mail.Imap.Folder(EmailMailbox.GmailAllMail).Put(message);
        }
        var ct = TestContext.Current.CancellationToken;
        await using var session = await mail.Factory().OpenImapAsync(new EmailAccount(mail.Settings(address), password), ct);
        var mailbox = new EmailMailbox(session);
        var first = Assert.Single(await mailbox.ListAsync("INBOX", null, true, 1, ct));
        var again = Assert.Single(await mailbox.ListAsync("INBOX", null, true, 1, ct));
        Assert.Equal(first.ReceivedAt, again.ReceivedAt);
        await mailbox.MarkReadAsync(first.Id, "INBOX", ct);
        await mailbox.MoveAsync(first.Id, "Archive", "INBOX", ct);
        var second = Assert.Single(await mailbox.ListAsync("INBOX", first.ReceivedAt, true, 1, ct));
        Assert.Equal("Second", second.Subject);
        var third = Assert.Single(await mailbox.ListAsync("INBOX", second.ReceivedAt, true, 1, ct));
        Assert.Equal("Third", third.Subject);
        Assert.Empty(await mailbox.ListAsync("INBOX", third.ReceivedAt, true, 1, ct));
    }
}






