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
}


