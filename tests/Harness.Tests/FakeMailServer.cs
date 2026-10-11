using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Harness.Tests;

/// <summary>
/// AN IMAP SERVER AND AN SMTP SERVER IN THE TEST PROCESS, on loopback ports, so the Host's login
/// test runs over a real socket and never reaches a real account. Each speaks just enough of its
/// protocol for a login: IMAP's greeting, CAPABILITY, STARTTLS, LOGIN, STATUS and LOGOUT; SMTP's
/// greeting, EHLO, STARTTLS, AUTH PLAIN and QUIT. Implicit TLS or STARTTLS per server, with a
/// self-signed certificate the test trusts through <see cref="Trust"/>.
/// </summary>
internal sealed class FakeMailServer : IAsyncDisposable
{
    private readonly TcpListener _imap = new(IPAddress.Loopback, 0);
    private readonly TcpListener _smtp = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly X509Certificate2 _certificate = SelfSigned();

    public FakeMailServer(string imapSecurity = "TLS", string smtpSecurity = "STARTTLS")
    {
        ImapSecurity = imapSecurity;
        SmtpSecurity = smtpSecurity;
        _imap.Start();
        _smtp.Start();
        _ = AcceptAsync(_imap, ImapAsync);
        _ = AcceptAsync(_smtp, SmtpAsync);
    }

    public string ImapSecurity { get; }

    public string SmtpSecurity { get; }

    public int ImapPort => ((IPEndPoint)_imap.LocalEndpoint).Port;

    public int SmtpPort => ((IPEndPoint)_smtp.LocalEndpoint).Port;

    /// <summary>The one username and password both servers accept.</summary>
    public string Username { get; set; } = "mailbox@example.com";

    public string Password { get; set; } = "";

    /// <summary>A username SMTP also accepts, as iCloud takes the full address there.</summary>
    public string? SmtpUsername { get; set; }

    public int Messages { get; set; } = 1240;

    /// <summary>Every login tried: "imap ok user", "smtp refused user". Never the password.</summary>
    public ConcurrentQueue<string> Logins { get; } = new();

    /// <summary>Whether a presented certificate is this server's: what the test's login trusts.</summary>
    public bool Trust(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        certificate is not null && certificate.GetCertHashString() == _certificate.GetCertHashString();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _imap.Stop();
        _smtp.Stop();
        _certificate.Dispose();
        _stop.Dispose();
    }

    private async Task AcceptAsync(TcpListener listener, Func<TcpClient, Task> serve)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException) { return; }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try { await serve(client); }
                    catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or System.Security.Authentication.AuthenticationException) { }
                }
            });
        }
    }

    private async Task<Stream> SecureAsync(Stream stream)
    {
        var tls = new SslStream(stream, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(_certificate, clientCertificateRequired: false, checkCertificateRevocation: false);
        return tls;
    }

    private async Task ImapAsync(TcpClient client)
    {
        Stream stream = client.GetStream();
        var secure = ImapSecurity == "TLS";
        if (secure) stream = await SecureAsync(stream);

        var startTls = secure ? "" : " STARTTLS";
        await WriteAsync(stream, $"* OK [CAPABILITY IMAP4rev1{startTls} AUTH=PLAIN] Fake IMAP ready");
        var loggedIn = false;

        while (await ReadLineAsync(stream) is { } line)
        {
            var parts = line.Split(' ', 3);
            if (parts.Length < 2) { await WriteAsync(stream, "* BAD what?"); continue; }

            var (tag, command, rest) = (parts[0], parts[1].ToUpperInvariant(), parts.Length > 2 ? parts[2] : "");

            switch (command)
            {
                case "CAPABILITY":
                    await WriteAsync(stream, $"* CAPABILITY IMAP4rev1{(secure ? "" : " STARTTLS")} AUTH=PLAIN");
                    await WriteAsync(stream, $"{tag} OK CAPABILITY completed");
                    break;
                case "STARTTLS" when !secure && ImapSecurity == "STARTTLS":
                    await WriteAsync(stream, $"{tag} OK Begin TLS negotiation now");
                    stream = await SecureAsync(stream);
                    secure = true;
                    break;
                case "LOGIN":
                    var (user, password) = ImapArguments(rest);
                    if (!secure)
                    {
                        await WriteAsync(stream, $"{tag} NO [PRIVACYREQUIRED] Use STARTTLS first");
                    }
                    else if (user == Username && password == Password)
                    {
                        loggedIn = true;
                        Logins.Enqueue($"imap ok {user}");
                        await WriteAsync(stream, $"{tag} OK [CAPABILITY IMAP4rev1] {user} authenticated (Success)");
                    }
                    else
                    {
                        Logins.Enqueue($"imap refused {user}");
                        await WriteAsync(stream, $"{tag} NO [AUTHENTICATIONFAILED] Invalid credentials (Failure)");
                    }

                    break;
                case "STATUS" when loggedIn:
                    await WriteAsync(stream, $"* STATUS \"INBOX\" (MESSAGES {Messages})");
                    await WriteAsync(stream, $"{tag} OK STATUS completed");
                    break;
                case "LOGOUT":
                    await WriteAsync(stream, "* BYE LOGOUT Requested");
                    await WriteAsync(stream, $"{tag} OK 73 good day (Success)");
                    return;
                default:
                    await WriteAsync(stream, $"{tag} BAD Unknown command");
                    break;
            }
        }
    }

    private async Task SmtpAsync(TcpClient client)
    {
        Stream stream = client.GetStream();
        var secure = SmtpSecurity == "TLS";
        if (secure) stream = await SecureAsync(stream);

        await WriteAsync(stream, "220 fake.example ESMTP ready");

        while (await ReadLineAsync(stream) is { } line)
        {
            var verb = line.Split(' ', 2)[0].ToUpperInvariant();

            switch (verb)
            {
                case "EHLO":
                    await WriteAsync(stream, "250-fake.example at your service");
                    if (!secure) await WriteAsync(stream, "250-STARTTLS");
                    if (secure) await WriteAsync(stream, "250-AUTH LOGIN PLAIN");
                    await WriteAsync(stream, "250 SMTPUTF8");
                    break;
                case "STARTTLS" when !secure:
                    await WriteAsync(stream, "220 2.0.0 Ready to start TLS");
                    stream = await SecureAsync(stream);
                    secure = true;
                    break;
                case "AUTH" when secure:
                    var words = line.Split(' ');
                    if (words.Length < 3 || !words[1].Equals("PLAIN", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsync(stream, "504 5.5.4 Use AUTH PLAIN with its response");
                        break;
                    }

                    var plain = Encoding.UTF8.GetString(Convert.FromBase64String(words[2])).Split('\0');
                    var (user, password) = (plain.Length == 3 ? plain[1] : "", plain.Length == 3 ? plain[2] : "");
                    if ((user == Username || user == SmtpUsername) && password == Password)
                    {
                        Logins.Enqueue($"smtp ok {user}");
                        await WriteAsync(stream, "235 2.7.0 Accepted");
                    }
                    else
                    {
                        Logins.Enqueue($"smtp refused {user}");
                        await WriteAsync(stream, "535 5.7.8 Username and Password not accepted.");
                    }

                    break;
                case "QUIT":
                    await WriteAsync(stream, "221 2.0.0 closing connection");
                    return;
                default:
                    await WriteAsync(stream, "502 5.5.1 Unrecognized command.");
                    break;
            }
        }
    }

    /// <summary>LOGIN's two arguments, each an atom or a quoted string with \" and \\ escapes.</summary>
    private static (string User, string Password) ImapArguments(string text)
    {
        var values = new List<string>();
        var i = 0;

        while (i < text.Length && values.Count < 2)
        {
            if (text[i] == ' ') { i++; continue; }

            var value = new StringBuilder();
            if (text[i] == '"')
            {
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) i++;
                    value.Append(text[i++]);
                }

                i++;
            }
            else
            {
                while (i < text.Length && text[i] != ' ') value.Append(text[i++]);
            }

            values.Add(value.ToString());
        }

        return (values.ElementAtOrDefault(0) ?? "", values.ElementAtOrDefault(1) ?? "");
    }

    private static Task WriteAsync(Stream stream, string line) =>
        stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n")).AsTask();

    private static async Task<string?> ReadLineAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];

        while (true)
        {
            if (await stream.ReadAsync(one) == 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString([.. bytes]);
            if (one[0] == '\n') return Encoding.UTF8.GetString([.. bytes]).TrimEnd('\r');
            bytes.Add(one[0]);
        }
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // Exported and loaded again, so the server stream holds a key it can use on every platform.
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
    }
}
