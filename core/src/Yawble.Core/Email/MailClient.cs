using MailKit.Net.Imap;
using MailKit.Net.Smtp;

namespace Yawble.Core.Email;

/// <summary>
/// One mailbox's sign-in, as the host holds it for a moment: the settings and the password from the credential store. Never put it in
/// an RPC result, a log or an agent's output; <see cref="ToString"/> leaves the password out.
/// </summary>
public sealed class EmailAccount(EmailSettings settings, string password)
{
    public EmailSettings Settings { get; } = settings;

    /// <summary>The secret. Host only.</summary>
    public string Password { get; } = password;

    public override string ToString() => $"EmailAccount {{ {Settings.User} at {Settings.ImapHost}:{Settings.ImapPort} / {Settings.SmtpHost}:{Settings.SmtpPort} }}";
}

/// <summary>Opens the byte stream to a mail server (host, port); a refused or unreachable one throws <see cref="System.Net.Sockets.SocketException"/>.</summary>
public delegate Task<Stream> MailConnector(string host, int port, CancellationToken ct);

/// <summary>Why a mail session could not be opened.</summary>
public enum MailFailure
{
    /// <summary>The server refused the user name or password (an app password revoked, a typo).</summary>
    SignIn,
    /// <summary>The server could not be reached, or no secure connection could be made (wrong host or port, refused, timed out, bad certificate).</summary>
    Connect,
    /// <summary>The server answered something the client could not use.</summary>
    Protocol,
}

/// <summary>A mail session that could not be opened, or a command that failed. <see cref="Exception.Message"/> is for the person and never holds the password.</summary>
public sealed class MailException(MailFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public MailFailure Failure { get; } = failure;
}

/// <summary>
/// The mail-client seam: opens an authenticated IMAP or SMTP session for an email Connection. The app's own is
/// <see cref="MailKitClientFactory"/>; the Email integration's actions (list, get, search, thread, markRead, move; send later) are written
/// against these sessions. Opening never sends mail.
/// </summary>
public interface IMailClientFactory
{
    /// <summary>Connects and signs in to IMAP. Throws <see cref="MailException"/> when that fails.</summary>
    Task<IImapSession> OpenImapAsync(EmailAccount account, CancellationToken ct);

    /// <summary>Connects and authenticates to SMTP (sends nothing). Throws <see cref="MailException"/> when that fails.</summary>
    Task<ISmtpSession> OpenSmtpAsync(EmailAccount account, CancellationToken ct);
}

/// <summary>A signed-in IMAP session. Disposing signs out and closes the connection.</summary>
public interface IImapSession : IAsyncDisposable
{
    /// <summary>The signed-in MailKit client, for the integration's actions (SELECT, SEARCH, FETCH, STORE, MOVE).</summary>
    IImapClient Client { get; }

    /// <summary>Whether the server offers Gmail's extensions (X-GM-EXT-1: X-GM-MSGID, X-GM-THRID, X-GM-RAW, labels).</summary>
    bool IsGmail { get; }

    /// <summary>The full names of the mailbox's folders, INBOX first, then by name.</summary>
    Task<IReadOnlyList<string>> ListFoldersAsync(CancellationToken ct);
}

/// <summary>An authenticated SMTP session. A later send action goes through <see cref="Client"/>; nothing sends today. Disposing quits.</summary>
public interface ISmtpSession : IAsyncDisposable
{
    ISmtpClient Client { get; }
}
