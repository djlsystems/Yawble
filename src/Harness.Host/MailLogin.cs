using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;

namespace Harness.Host;

/// <summary>A mail server as a mailbox connection reaches it: TLS from the first byte, or STARTTLS.</summary>
public sealed record MailServer(string Host, int Port, string Security)
{
    public const string Tls = "TLS";

    public const string StartTls = "STARTTLS";
}

/// <summary>A mailbox connection's settings, as every route may show them: never the password, only
/// whether one is set.</summary>
public sealed record MailboxSettings(string Username, MailServer Imap, MailServer Smtp, string? Preset, bool PasswordSet)
{
    /// <summary>The connection kind, and the provider word a plugin slot names to admit one.</summary>
    public const string Kind = "imap";
}

/// <summary>A provider's published mail servers, offered as a starting point the person may edit.</summary>
public sealed record MailPreset(string Id, string Name, MailServer? Imap, MailServer? Smtp)
{
    public object View() => new
    {
        id = Id,
        name = Name,
        imap = Imap is null ? null : new { host = Imap.Host, port = Imap.Port, security = Imap.Security },
        smtp = Smtp is null ? null : new { host = Smtp.Host, port = Smtp.Port, security = Smtp.Security },
    };
}

/// <summary>
/// THE PRESETS, each checked against the provider's own published settings:
/// <list type="bullet">
/// <item>Gmail: IMAP imap.gmail.com:993 SSL, SMTP smtp.gmail.com:465 SSL (or 587 STARTTLS) -
/// https://developers.google.com/workspace/gmail/imap/imap-smtp; an app password needs 2-Step
/// Verification - https://support.google.com/accounts/answer/185833.</item>
/// <item>iCloud: IMAP imap.mail.me.com:993 SSL, SMTP smtp.mail.me.com:587 STARTTLS; the IMAP
/// username is usually the name part of the address, SMTP's the full address -
/// https://support.apple.com/en-us/102525; an app-specific password needs two-factor
/// authentication - https://support.apple.com/en-us/102654.</item>
/// <item>Yahoo: IMAP imap.mail.yahoo.com:993 SSL, SMTP smtp.mail.yahoo.com:465 SSL (or 587) -
/// https://help.yahoo.com/kb/SLN4075.html; app passwords - https://help.yahoo.com/kb/SLN15241.html.</item>
/// <item>Other: nothing filled in; the person types every field.</item>
/// </list>
/// </summary>
public static class MailPresets
{
    public const string Gmail = "gmail";

    public const string ICloud = "icloud";

    public const string Yahoo = "yahoo";

    public const string Other = "other";

    public static IReadOnlyList<MailPreset> All { get; } =
    [
        new(Gmail, "Gmail", new("imap.gmail.com", 993, MailServer.Tls), new("smtp.gmail.com", 465, MailServer.Tls)),
        new(ICloud, "iCloud", new("imap.mail.me.com", 993, MailServer.Tls), new("smtp.mail.me.com", 587, MailServer.StartTls)),
        new(Yahoo, "Yahoo", new("imap.mail.yahoo.com", 993, MailServer.Tls), new("smtp.mail.yahoo.com", 465, MailServer.Tls)),
        new(Other, "Other", null, null),
    ];

    public static bool IsKnown(string? id) => All.Any(p => p.Id == id);

    /// <summary>Which provider a mailbox is at, for the sentence that says what to fix: its preset,
    /// else the preset whose IMAP server it uses, else none.</summary>
    public static string? ProviderOf(MailboxSettings settings) =>
        settings.Preset is Gmail or ICloud or Yahoo ? settings.Preset
        : All.FirstOrDefault(p => p.Imap is { } imap && string.Equals(imap.Host, settings.Imap.Host, StringComparison.OrdinalIgnoreCase))?.Id is { } id && id != Other ? id
        : null;
}

/// <summary>How a login went: <see cref="Connected"/>, <see cref="Refused"/> (the server said no to
/// the username or password - a person must act) or <see cref="Failed"/> (not reached, not secure, or
/// not answering as it should - nothing the password can fix).</summary>
public enum MailLoginOutcome
{
    Connected,
    Refused,
    Failed,
}

/// <summary>A login's outcome and the one plain sentence a person reads.</summary>
public sealed record MailLoginResult(MailLoginOutcome Outcome, string Sentence)
{
    public bool Ok => Outcome == MailLoginOutcome.Connected;
}

/// <summary>THE LOGIN TEST: an IMAP login and an SMTP authentication with the given settings. Tests
/// point <see cref="SocketMailLogin"/> at a fake server in the test process; nothing reaches a real
/// account.</summary>
public interface IMailLogin
{
    Task<MailLoginResult> LoginAsync(string account, MailboxSettings settings, string password, CancellationToken ct = default);
}

/// <summary>
/// The login over a socket in the Host - no process. IMAP: greeting, STARTTLS when asked, LOGIN,
/// STATUS of INBOX for the sentence, LOGOUT. SMTP: greeting, EHLO, STARTTLS when asked, AUTH PLAIN (or
/// LOGIN), QUIT. The password goes only into the LOGIN and AUTH commands; it is never logged, never
/// put in a sentence or an exception, and the Host does not keep the conversation.
/// </summary>
/// <param name="certificateCheck">Null checks the server's certificate as the system does; the tests
/// trust their fake server's certificate only.</param>
public sealed partial class SocketMailLogin(RemoteCertificateValidationCallback? certificateCheck = null, TimeSpan? timeout = null) : IMailLogin
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private const int MaximumLine = 64 * 1024;

    public async Task<MailLoginResult> LoginAsync(string account, MailboxSettings settings, string password, CancellationToken ct = default)
    {
        var provider = MailPresets.ProviderOf(settings);

        var imap = await WithinAsync(settings.Imap, ct, token => ImapAsync(settings.Imap, settings.Username, password, provider, token));
        if (!imap.Ok) return imap;

        // iCloud's IMAP takes the name part of the address, its SMTP only the full address: a
        // username with no @ authenticates to SMTP as the account's address.
        var smtpUser = settings.Username.Contains('@') ? settings.Username : account;
        var smtp = await WithinAsync(settings.Smtp, ct, token => SmtpAsync(settings.Smtp, smtpUser, password, provider, token));

        return smtp.Ok ? imap : smtp;
    }

    // ---- sentences ------------------------------------------------------------------------------

    public static string ConnectedSentence(int? messages) => messages is { } count
        ? $"Connected: {count.ToString("N0", CultureInfo.InvariantCulture)} message{(count == 1 ? "" : "s")} in Inbox"
        : "Connected: Inbox is there";

    public static string RefusedSentence(string? provider, bool smtp) =>
        (smtp ? "The outgoing (SMTP) server refused the password. " : "The incoming (IMAP) server refused the password. ") + Hint(provider);

    private static string Hint(string? provider) => provider switch
    {
        MailPresets.Gmail => "For Gmail, make an app password: it needs 2-Step Verification.",
        MailPresets.ICloud => "For iCloud, make an app-specific password: it needs two-factor authentication.",
        MailPresets.Yahoo => "For Yahoo, make an app password in Account security.",
        _ => "Check the username, and use an app password where the provider issues one.",
    };

    private static MailLoginResult Unreachable(MailServer server) =>
        new(MailLoginOutcome.Failed, $"Could not reach {server.Host} on port {server.Port}.");

    private static MailLoginResult Failed(string sentence) => new(MailLoginOutcome.Failed, sentence);

    // ---- the conversation -----------------------------------------------------------------------

    /// <summary>One server's conversation within the time allowed, its failures turned into sentences.</summary>
    private async Task<MailLoginResult> WithinAsync(MailServer server, CancellationToken ct, Func<CancellationToken, Task<MailLoginResult>> talk)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? DefaultTimeout);

        try
        {
            return await talk(limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed($"{server.Host} on port {server.Port} did not answer in time.");
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            return Failed($"{server.Host} on port {server.Port} closed the connection before the login finished.");
        }
    }

    /// <summary>The socket, made secure from the start for TLS. A failure is the sentence.</summary>
    private async Task<(TcpClient? Client, Stream? Stream, MailLoginResult? Failure)> OpenAsync(MailServer server, CancellationToken ct)
    {
        var client = new TcpClient();

        try
        {
            await client.ConnectAsync(server.Host, server.Port, ct);
        }
        catch (SocketException)
        {
            client.Dispose();
            return (null, null, Unreachable(server));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            return (null, null, Unreachable(server));
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw;
        }

        Stream stream = client.GetStream();

        if (server.Security == MailServer.Tls)
        {
            var (secure, failure) = await SecureAsync(stream, server, ct);
            if (failure is not null)
            {
                client.Dispose();
                return (null, null, failure);
            }

            stream = secure!;
        }

        return (client, stream, null);
    }

    private async Task<(Stream? Stream, MailLoginResult? Failure)> SecureAsync(Stream stream, MailServer server, CancellationToken ct)
    {
        var tls = new SslStream(stream, leaveInnerStreamOpen: false);

        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = server.Host,
                RemoteCertificateValidationCallback = certificateCheck,
            }, ct);

            return (tls, null);
        }
        catch (AuthenticationException)
        {
            await tls.DisposeAsync();
            return (null, Failed(
                $"Could not make a secure connection to {server.Host} on port {server.Port}. "
                + (server.Security == MailServer.Tls ? "Check the port, or choose STARTTLS if this port starts in plain text." : "Its certificate was not accepted.")));
        }
        catch (IOException)
        {
            await tls.DisposeAsync();
            return (null, Failed(
                $"Could not make a secure connection to {server.Host} on port {server.Port}. "
                + "Check the port, or choose STARTTLS if this port starts in plain text."));
        }
    }

    private async Task<MailLoginResult> ImapAsync(MailServer server, string username, string password, string? provider, CancellationToken ct)
    {
        var (client, stream, failure) = await OpenAsync(server, ct);
        if (failure is not null) return failure;

        using (client)
        {
            var notImap = Failed($"{server.Host} on port {server.Port} did not answer as an IMAP server. Check the server and the port.");

            var greeting = await ReadLineAsync(stream!, ct);
            if (greeting is null || !(greeting.StartsWith("* OK", StringComparison.OrdinalIgnoreCase) || greeting.StartsWith("* PREAUTH", StringComparison.OrdinalIgnoreCase)))
            {
                return notImap;
            }

            if (server.Security == MailServer.StartTls)
            {
                var (startTls, _) = await ImapCommandAsync(stream!, "a1", "STARTTLS", ct);
                if (startTls != "OK")
                {
                    return Failed($"{server.Host} on port {server.Port} does not offer STARTTLS. Choose TLS for this port, or the port that offers STARTTLS.");
                }

                var (secure, refusal) = await SecureAsync(stream!, server, ct);
                if (refusal is not null) return refusal;
                stream = secure;
            }

            var (login, said) = await ImapCommandAsync(stream!, "a2", $"LOGIN {Quoted(username)} {Quoted(password)}", ct);
            if (login == "NO")
            {
                return said.Contains("[UNAVAILABLE]", StringComparison.OrdinalIgnoreCase)
                    ? Failed($"{server.Host} is not taking logins just now. Try again later.")
                    : new MailLoginResult(MailLoginOutcome.Refused, RefusedSentence(provider, smtp: false));
            }

            if (login != "OK") return notImap;

            var (status, lines) = await ImapCommandAsync(stream!, "a3", "STATUS INBOX (MESSAGES)", ct);
            int? messages = null;
            if (status == "OK" && MessagesPattern().Match(lines) is { Success: true } match
                && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                messages = count;
            }

            try
            {
                await ImapCommandAsync(stream!, "a4", "LOGOUT", ct);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // Logged in and counted: a server that hangs up on LOGOUT changes nothing.
            }

            await stream!.DisposeAsync();
            return new MailLoginResult(MailLoginOutcome.Connected, ConnectedSentence(messages));
        }
    }

    /// <summary>Sends one tagged command and reads to its tagged reply: (OK|NO|BAD or "", every line
    /// before it and the reply's own text).</summary>
    private static async Task<(string Result, string Said)> ImapCommandAsync(Stream stream, string tag, string command, CancellationToken ct)
    {
        await WriteAsync(stream, $"{tag} {command}", ct);

        var said = new StringBuilder();
        while (await ReadLineAsync(stream, ct) is { } line)
        {
            said.Append(line).Append('\n');
            if (!line.StartsWith(tag + " ", StringComparison.Ordinal)) continue;

            var result = line[(tag.Length + 1)..].Split(' ', 2)[0].ToUpperInvariant();
            return (result is "OK" or "NO" or "BAD" ? result : "", said.ToString());
        }

        return ("", said.ToString());
    }

    /// <summary>An IMAP quoted string. The values are checked to be printable ASCII before any login.</summary>
    private static string Quoted(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private async Task<MailLoginResult> SmtpAsync(MailServer server, string username, string password, string? provider, CancellationToken ct)
    {
        var (client, stream, failure) = await OpenAsync(server, ct);
        if (failure is not null) return failure;

        using (client)
        {
            var notSmtp = Failed($"{server.Host} on port {server.Port} did not answer as an SMTP server. Check the server and the port.");

            if ((await SmtpReplyAsync(stream!, ct)).Code != 220) return notSmtp;

            var (hello, capabilities) = await SmtpAsync(stream!, "EHLO localhost", ct);
            if (hello != 250) return notSmtp;

            if (server.Security == MailServer.StartTls)
            {
                if (!capabilities.Any(c => c.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase))
                    || (await SmtpAsync(stream!, "STARTTLS", ct)).Code != 220)
                {
                    return Failed($"{server.Host} on port {server.Port} does not offer STARTTLS. Choose TLS for this port, or the port that offers STARTTLS.");
                }

                var (secure, refusal) = await SecureAsync(stream!, server, ct);
                if (refusal is not null) return refusal;
                stream = secure;

                (hello, capabilities) = await SmtpAsync(stream!, "EHLO localhost", ct);
                if (hello != 250) return notSmtp;
            }

            var mechanisms = capabilities
                .Where(c => c.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase) && c.Length > 4 && c[4] is ' ' or '=')
                .SelectMany(c => c[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Select(m => m.ToUpperInvariant())
                .ToHashSet(StringComparer.Ordinal);

            int code;
            if (mechanisms.Contains("PLAIN"))
            {
                code = (await SmtpAsync(stream!, "AUTH PLAIN " + Base64("\0" + username + "\0" + password), ct)).Code;
            }
            else if (mechanisms.Contains("LOGIN"))
            {
                code = (await SmtpAsync(stream!, "AUTH LOGIN", ct)).Code;
                if (code == 334) code = (await SmtpAsync(stream!, Base64(username), ct)).Code;
                if (code == 334) code = (await SmtpAsync(stream!, Base64(password), ct)).Code;
            }
            else
            {
                return Failed($"{server.Host} on port {server.Port} does not offer to sign in with a password.");
            }

            if (code is 535 or 534 or 530 or 501)
            {
                return new MailLoginResult(MailLoginOutcome.Refused, RefusedSentence(provider, smtp: true));
            }

            if (code != 235) return Failed($"{server.Host} on port {server.Port} did not accept the sign-in (reply {code}). Try again later.");

            try
            {
                await SmtpAsync(stream!, "QUIT", ct);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // Signed in: a server that hangs up on QUIT changes nothing.
            }

            await stream!.DisposeAsync();
            return new MailLoginResult(MailLoginOutcome.Connected, "");
        }
    }

    private static async Task<(int Code, IReadOnlyList<string> Lines)> SmtpAsync(Stream stream, string command, CancellationToken ct)
    {
        await WriteAsync(stream, command, ct);
        return await SmtpReplyAsync(stream, ct);
    }

    /// <summary>One reply, every line of a multi-line one: its code and each line's text.</summary>
    private static async Task<(int Code, IReadOnlyList<string> Lines)> SmtpReplyAsync(Stream stream, CancellationToken ct)
    {
        var lines = new List<string>();

        while (await ReadLineAsync(stream, ct) is { } line)
        {
            if (line.Length < 3 || !int.TryParse(line.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var code)) return (0, lines);

            lines.Add(line.Length > 4 ? line[4..] : "");
            if (line.Length == 3 || line[3] != '-') return (code, lines);
        }

        return (0, lines);
    }

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static Task WriteAsync(Stream stream, string line, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"), ct).AsTask();

    /// <summary>One line, a byte at a time: nothing is read past it, so a STARTTLS upgrade starts on
    /// the next byte. Null at the end of the stream; a line past 64 KiB ends the conversation.</summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];

        while (bytes.Count < MaximumLine)
        {
            if (await stream.ReadAsync(one, ct) == 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString([.. bytes]);
            if (one[0] == '\n') return Encoding.UTF8.GetString([.. bytes]).TrimEnd('\r');
            bytes.Add(one[0]);
        }

        throw new IOException("A line longer than the server may send.");
    }

    [GeneratedRegex(@"MESSAGES\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MessagesPattern();
}
