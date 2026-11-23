using System.Net;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace Yawble.Core.Email;

/// <summary>
/// <see cref="IMailClientFactory"/> on MailKit (Windows, macOS and Linux alike). Security by port: 993 (IMAP) and 465 (SMTP) are TLS from
/// the first byte; any other port must upgrade with STARTTLS, so a password is never sent in the clear. Only a server on this computer
/// (localhost, 127.0.0.1, ::1) may go without TLS when it offers none. Certificates are checked as the operating system checks them.
/// No protocol log is kept, so no sign-in reaches a log.
/// </summary>
/// <param name="connect">How the byte stream to a server is opened; null (the app): a TCP connection by MailKit itself. Tests pass an
/// in-memory one to reach the fake server without binding a port.</param>
public sealed class MailKitClientFactory(TimeSpan? timeout = null, MailConnector? connect = null) : IMailClientFactory
{
    /// <summary>How long one network step may take before the session fails as <see cref="MailFailure.Connect"/>.</summary>
    public TimeSpan Timeout { get; } = timeout ?? TimeSpan.FromSeconds(30);

    /// <summary>The TLS choice for a host and port (see the class summary). Pure.</summary>
    public static SecureSocketOptions SecurityFor(string host, int port) =>
        port is 993 or 465 ? SecureSocketOptions.SslOnConnect
        : IsThisComputer(host) ? SecureSocketOptions.StartTlsWhenAvailable
        : SecureSocketOptions.StartTls;

    public static bool IsThisComputer(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);

    public async Task<IImapSession> OpenImapAsync(EmailAccount account, CancellationToken ct)
    {
        var s = account.Settings;
        var client = new ImapClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            await Guard("IMAP", s.ImapHost, s.ImapPort, async () =>
            {
                if (connect is null) await client.ConnectAsync(s.ImapHost, s.ImapPort, SecurityFor(s.ImapHost, s.ImapPort), ct);
                else await client.ConnectAsync(await connect(s.ImapHost, s.ImapPort, ct), s.ImapHost, s.ImapPort, SecurityFor(s.ImapHost, s.ImapPort), ct);
                await client.AuthenticateAsync(s.User, account.Password, ct);
            }, ct);
            return new ImapSession(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<ISmtpSession> OpenSmtpAsync(EmailAccount account, CancellationToken ct)
    {
        var s = account.Settings;
        var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            await Guard("SMTP", s.SmtpHost, s.SmtpPort, async () =>
            {
                if (connect is null) await client.ConnectAsync(s.SmtpHost, s.SmtpPort, SecurityFor(s.SmtpHost, s.SmtpPort), ct);
                else await client.ConnectAsync(await connect(s.SmtpHost, s.SmtpPort, ct), s.SmtpHost, s.SmtpPort, SecurityFor(s.SmtpHost, s.SmtpPort), ct);
                if (!client.Capabilities.Contains(SmtpCapability.Authentication))
                    throw new MailException(MailFailure.Protocol, $"the SMTP server at {s.SmtpHost}:{s.SmtpPort} does not offer a sign-in");
                await client.AuthenticateAsync(s.User, account.Password, ct);
            }, ct);
            return new SmtpSession(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Runs a connect and sign-in, turning MailKit's and the socket's failures into a <see cref="MailException"/> the person can read.</summary>
    internal static async Task Guard(string protocol, string host, int port, Func<Task> step, CancellationToken ct)
    {
        var at = $"{host}:{port}";
        try { await step(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (MailException) { throw; }
        catch (AuthenticationException e) { throw new MailException(MailFailure.SignIn, $"the {protocol} server at {at} refused the user name or password", e); }
        catch (SslHandshakeException e) { throw new MailException(MailFailure.Connect, $"no secure connection could be made to the {protocol} server at {at} (its certificate, or it does not speak TLS on this port)", e); }
        catch (NotSupportedException e) { throw new MailException(MailFailure.Connect, $"the {protocol} server at {at} does not offer a secure connection (STARTTLS); use its TLS port", e); }
        catch (SocketException e) { throw new MailException(MailFailure.Connect, $"could not reach the {protocol} server at {at} ({Words(e.SocketErrorCode)})", e); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { throw new MailException(MailFailure.Connect, $"the {protocol} server at {at} did not answer in time", e); }
        catch (Exception e) when (e is IOException or ProtocolException or CommandException or ImapCommandException)
        {
            throw new MailException(MailFailure.Protocol, $"the {protocol} server at {at} broke off the sign-in ({e.GetType().Name})", e);
        }
    }

    private static string Words(SocketError code) => code switch
    {
        SocketError.ConnectionRefused => "connection refused",
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "no such host",
        SocketError.TimedOut => "timed out",
        SocketError.NetworkUnreachable or SocketError.HostUnreachable => "network unreachable",
        _ => code.ToString(),
    };

    private sealed class ImapSession(ImapClient client) : IImapSession
    {
        public IImapClient Client => client;

        public bool IsGmail => client.Capabilities.Contains(ImapCapability.GMailExt1);

        public async Task<IReadOnlyList<string>> ListFoldersAsync(CancellationToken ct)
        {
            var ns = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', "");
            IList<IMailFolder> folders;
            try { folders = await client.GetFoldersAsync(ns, cancellationToken: ct); }
            catch (Exception e) when (e is IOException or ProtocolException or CommandException or ImapCommandException)
            {
                throw new MailException(MailFailure.Protocol, $"the IMAP server did not list its folders ({e.GetType().Name})", e);
            }
            return folders.Select(f => f.FullName).Where(n => n.Length > 0)
                .OrderBy(n => string.Equals(n, "INBOX", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public async ValueTask DisposeAsync()
        {
            try { if (client.IsConnected) await client.DisconnectAsync(true, CancellationToken.None); }
            catch (Exception) { /* the server went first: nothing to sign out of */ }
            client.Dispose();
        }
    }

    private sealed class SmtpSession(SmtpClient client) : ISmtpSession
    {
        public ISmtpClient Client => client;

        public async ValueTask DisposeAsync()
        {
            try { if (client.IsConnected) await client.DisconnectAsync(true, CancellationToken.None); }
            catch (Exception) { /* the server went first */ }
            client.Dispose();
        }
    }
}
