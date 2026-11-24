using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Yawble.Core.Email;

namespace Yawble.Core.Tests.Email;

/// <summary>
/// An in-process IMAP and SMTP server pair at 127.0.0.1, speaking just enough of each protocol for MailKit to connect, sign in and list
/// folders. No TLS (MailKitClientFactory allows plain text only to this computer), no real mail server, no account.
/// By default the pair is in memory: <see cref="Factory"/> gives MailKit a <see cref="MailConnector"/> that hands it a pipe to the fake
/// (agent sandboxes refuse loopback connects). With <c>realLoopback: true</c> (opt-in, YAWBLE_REAL_LOOPBACK=1) each fake is a TcpListener on
/// a loopback port and MailKit dials it itself. Shared by the email tests: the read-actions card adds IMAP commands (SELECT, FETCH,
/// SEARCH, X-GM-*) through <see cref="FakeImapServer.Commands"/> and mail through <see cref="FakeImapServer.Folders"/>.
/// </summary>
public sealed class FakeMailServer : IAsyncDisposable
{
    public const string User = "pat@example.test";
    public const string Password = "fake-app-pass-7Kq2";

    public FakeImapServer Imap { get; }
    public FakeSmtpServer Smtp { get; }
    public bool RealLoopback { get; }

    public FakeMailServer(string user = User, string password = Password, bool realLoopback = false)
    {
        RealLoopback = realLoopback;
        Imap = new FakeImapServer(user, password, realLoopback);
        Smtp = new FakeSmtpServer(user, password, realLoopback);
    }

    /// <summary>Settings pointing at this pair, for <paramref name="address"/> (the user unless given).</summary>
    public EmailSettings Settings(string? address = null, string? user = null) =>
        new(address ?? User, "127.0.0.1", Imap.Port, "127.0.0.1", Smtp.Port, user ?? address ?? User);

    /// <summary>
    /// In memory: a pipe to the fake listening on that port; any other port is refused (SocketException ConnectionRefused), as a closed
    /// TCP port is. Null with real loopback (MailKit opens the socket).
    /// </summary>
    public MailConnector? Connector => RealLoopback ? null : (host, port, ct) =>
        port == Imap.Port ? Imap.OpenAsync(ct)
        : port == Smtp.Port ? Smtp.OpenAsync(ct)
        : throw new SocketException((int)SocketError.ConnectionRefused);

    /// <summary>MailKit's factory pointed at this pair, with a short timeout so a hang fails fast.</summary>
    public MailKitClientFactory Factory(TimeSpan? timeout = null) => new(timeout ?? TimeSpan.FromSeconds(10), Connector);

    /// <summary>A port nothing listens on: in memory a number no fake has; on real loopback a port bound, then closed.</summary>
    public int RefusedPort()
    {
        if (!RealLoopback) return Gateway.InMemoryNetwork.AllocatePort();
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await Imap.DisposeAsync();
        await Smtp.DisposeAsync();
    }
}

/// <summary>One accepted client: line reads (with raw reads for IMAP literals) and line writes, both ASCII/UTF-8 with CRLF.</summary>
public sealed class FakeMailConnection(Stream stream)
{
    private readonly byte[] _buf = new byte[8192];
    private int _start, _end;

    /// <summary>Free for a protocol's state (the IMAP selected folder, the SMTP sender).</summary>
    public Dictionary<string, object?> State { get; } = new(StringComparer.Ordinal);

    public bool SignedIn { get; set; }

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var line = new List<byte>();
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await stream.ReadAsync(_buf, ct);
                if (_end == 0) return line.Count > 0 ? Encoding.UTF8.GetString([.. line]) : null;
            }
            var b = _buf[_start++];
            if (b == '\n')
            {
                if (line.Count > 0 && line[^1] == '\r') line.RemoveAt(line.Count - 1);
                return Encoding.UTF8.GetString([.. line]);
            }
            line.Add(b);
        }
    }

    /// <summary>Exactly <paramref name="count"/> bytes (an IMAP literal's body).</summary>
    public async Task<string> ReadExactAsync(int count, CancellationToken ct)
    {
        var bytes = new byte[count];
        var got = 0;
        while (got < count)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await stream.ReadAsync(_buf, ct);
                if (_end == 0) throw new EndOfStreamException();
            }
            var n = Math.Min(count - got, _end - _start);
            Array.Copy(_buf, _start, bytes, got, n);
            _start += n;
            got += n;
        }
        return Encoding.UTF8.GetString(bytes);
    }

    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"), ct);
        await stream.FlushAsync(ct);
    }
}

/// <summary>
/// What both fakes share: one task per client, all ended on dispose. In memory (the default) <see cref="OpenAsync"/> makes a client;
/// with real loopback a TcpListener accepts them.
/// </summary>
public abstract class FakeLineServer : IAsyncDisposable
{
    private readonly TcpListener? _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _clients = [];
    private readonly Task _accept = Task.CompletedTask;

    /// <summary>Every line the clients sent, in order (sign-ins included: this is the server's side, never the app's log).</summary>
    public List<string> Received { get; } = [];

    /// <summary>How many clients connected.</summary>
    public int Connections { get; private set; }

    public int Port { get; }

    protected FakeLineServer(bool realLoopback)
    {
        if (!realLoopback)
        {
            Port = Gateway.InMemoryNetwork.AllocatePort();
            return;
        }
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptAsync(_listener);
    }

    /// <summary>A new in-memory client: the returned stream is the client's end; the server serves the other.</summary>
    public Task<Stream> OpenAsync(CancellationToken ct)
    {
        if (_listener is not null) throw new InvalidOperationException("a real-loopback fake is dialed, not opened");
        var (client, server) = Gateway.DuplexPipeStream.CreatePair();
        Serve(server);
        return Task.FromResult<Stream>(client);
    }

    private void Serve(Stream stream, IDisposable? owner = null)
    {
        lock (_clients)
        {
            Connections++;
            _clients.Add(Task.Run(async () =>
            {
                try { await ServeAsync(new FakeMailConnection(stream), _stop.Token); }
                catch (Exception) { /* the client went away or the server stopped */ }
                finally
                {
                    await stream.DisposeAsync();
                    owner?.Dispose();
                }
            }));
        }
    }

    private async Task AcceptAsync(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            Serve(client.GetStream(), client);
        }
    }

    protected void Record(string line) { lock (Received) Received.Add(line); }

    public string[] Lines() { lock (Received) return [.. Received]; }

    protected abstract Task ServeAsync(FakeMailConnection c, CancellationToken ct);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener?.Stop();
        try { await _accept; } catch (Exception) { }
        Task[] clients;
        lock (_clients) clients = [.. _clients];
        try { await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        _stop.Dispose();
    }
}

/// <summary>A folder of the fake mailbox. <see cref="Messages"/> is for the read-actions card's FETCH and SEARCH.</summary>
public sealed class FakeFolder(string name, params string[] flags)
{
    public string Name { get; } = name;
    public List<string> Flags { get; } = [.. flags];
    public List<object> Messages { get; } = [];
    public uint UidValidity { get; set; } = 1;

    /// <summary>The lowest UID the next message may get (UIDNEXT): UIDs only grow, as on a real server, even after the highest one leaves.</summary>
    public uint UidNext { get; set; } = 1;

    /// <summary>
    /// What a server does when it rebuilds the folder: a new UIDVALIDITY (the old one plus one, or <paramref name="uidValidity"/>) and every
    /// message numbered again from UID 1 in its order. Earlier ids of the folder no longer apply.
    /// </summary>
    public void Renumber(uint? uidValidity = null)
    {
        UidValidity = uidValidity ?? UidValidity + 1;
        var entries = Messages.OfType<FakeEntry>().OrderBy(e => e.Uid).ToList();
        Messages.RemoveAll(m => m is FakeEntry);
        uint uid = 0;
        foreach (var e in entries) Messages.Add(new FakeEntry(++uid, e.Message));
        UidNext = uid + 1;
    }
}

/// <summary>An IMAP command as parsed: its tag, its name (upper case; "UID FETCH" is "UID" with "FETCH" first in <see cref="Args"/>) and its arguments (quotes and literals resolved).</summary>
public sealed record FakeImapCommand(string Tag, string Name, IReadOnlyList<string> Args, string Raw);

public delegate Task FakeImapHandler(FakeImapServer server, FakeMailConnection c, FakeImapCommand command, CancellationToken ct);

/// <summary>
/// IMAP4rev1, the part MailKit needs to sign in and list: CAPABILITY, LOGIN, AUTHENTICATE (refused, so LOGIN is used), NAMESPACE, LIST,
/// NOOP, LOGOUT. <see cref="Commands"/> is open: add SELECT, FETCH, SEARCH, X-GM-* there. A command it does not know is answered BAD.
/// </summary>
public sealed class FakeImapServer : FakeLineServer
{
    public string User { get; set; }
    public string Password { get; set; }

    /// <summary>What CAPABILITY says (without AUTH=: sign-in is LOGIN). Add "X-GM-EXT-1" for a Gmail-like server.</summary>
    public List<string> Capabilities { get; } = ["IMAP4rev1", "NAMESPACE"];

    public List<FakeFolder> Folders { get; } = [new("INBOX"), new("Sent", @"\Sent"), new("Archive"), new("Projects/2026")];

    public Dictionary<string, FakeImapHandler> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The sign-ins that were refused.</summary>
    public int Refused { get; private set; }

    public FakeImapServer(string user, string password, bool realLoopback = false) : base(realLoopback)
    {
        User = user;
        Password = password;
        Commands["CAPABILITY"] = async (s, c, cmd, ct) =>
        {
            await c.WriteLineAsync("* CAPABILITY " + string.Join(' ', s.Capabilities), ct);
            await c.WriteLineAsync($"{cmd.Tag} OK CAPABILITY completed", ct);
        };
        Commands["NOOP"] = (s, c, cmd, ct) => c.WriteLineAsync($"{cmd.Tag} OK NOOP completed", ct);
        Commands["LOGOUT"] = async (s, c, cmd, ct) =>
        {
            await c.WriteLineAsync("* BYE signing off", ct);
            await c.WriteLineAsync($"{cmd.Tag} OK LOGOUT completed", ct);
            c.State["closed"] = true;
        };
        Commands["AUTHENTICATE"] = (s, c, cmd, ct) => c.WriteLineAsync($"{cmd.Tag} NO use LOGIN", ct);
        Commands["LOGIN"] = async (s, c, cmd, ct) =>
        {
            if (cmd.Args.Count == 2 && cmd.Args[0] == s.User && cmd.Args[1] == s.Password)
            {
                c.SignedIn = true;
                await c.WriteLineAsync($"{cmd.Tag} OK [CAPABILITY {string.Join(' ', s.Capabilities)}] LOGIN completed", ct);
            }
            else
            {
                s.Refused++;
                await c.WriteLineAsync($"{cmd.Tag} NO [AUTHENTICATIONFAILED] Invalid credentials", ct);
            }
        };
        Commands["NAMESPACE"] = async (s, c, cmd, ct) =>
        {
            if (!await s.RequireSignInAsync(c, cmd, ct)) return;
            await c.WriteLineAsync("* NAMESPACE ((\"\" \"/\")) NIL NIL", ct);
            await c.WriteLineAsync($"{cmd.Tag} OK NAMESPACE completed", ct);
        };
        Commands["LIST"] = async (s, c, cmd, ct) =>
        {
            if (!await s.RequireSignInAsync(c, cmd, ct)) return;
            var pattern = cmd.Args.Count >= 2 ? cmd.Args[1] : "*";
            if (pattern.Length == 0) await c.WriteLineAsync("* LIST (\\Noselect) \"/\" \"\"", ct);
            else
            {
                var re = new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace("%", "[^/]*") + "$", RegexOptions.IgnoreCase);
                foreach (var f in s.Folders.Where(f => re.IsMatch(f.Name)))
                {
                    var hasChildren = s.Folders.Any(o => o.Name.StartsWith(f.Name + "/", StringComparison.Ordinal));
                    var flags = string.Join(' ', f.Flags.Append(hasChildren ? @"\HasChildren" : @"\HasNoChildren"));
                    await c.WriteLineAsync($"* LIST ({flags}) \"/\" \"{f.Name}\"", ct);
                }
            }
            await c.WriteLineAsync($"{cmd.Tag} OK LIST completed", ct);
        };
    }

    /// <summary>Answers NO and returns false when the client has not signed in.</summary>
    public async Task<bool> RequireSignInAsync(FakeMailConnection c, FakeImapCommand cmd, CancellationToken ct)
    {
        if (c.SignedIn) return true;
        await c.WriteLineAsync($"{cmd.Tag} NO sign in first", ct);
        return false;
    }

    protected override async Task ServeAsync(FakeMailConnection c, CancellationToken ct)
    {
        await c.WriteLineAsync("* OK [CAPABILITY " + string.Join(' ', Capabilities) + "] fake IMAP ready", ct);
        while (!ct.IsCancellationRequested && !c.State.ContainsKey("closed"))
        {
            var cmd = await ReadCommandAsync(c, ct);
            if (cmd is null) return;
            Record(cmd.Raw);
            if (Commands.TryGetValue(cmd.Name, out var handler)) await handler(this, c, cmd, ct);
            else await c.WriteLineAsync($"{cmd.Tag} BAD unknown command {cmd.Name}", ct);
        }
    }

    /// <summary>Reads one command, answering "+" to each synchronizing literal {n} and reading its bytes.</summary>
    private static async Task<FakeImapCommand?> ReadCommandAsync(FakeMailConnection c, CancellationToken ct)
    {
        var raw = new StringBuilder();
        var line = await c.ReadLineAsync(ct);
        if (line is null) return null;
        while (true)
        {
            var m = Regex.Match(line, @"\{(\d+)\+?\}$");
            if (!m.Success) { raw.Append(line); break; }
            raw.Append(line[..m.Index]);
            if (!line.EndsWith("+}", StringComparison.Ordinal)) await c.WriteLineAsync("+ go ahead", ct);
            var literal = await c.ReadExactAsync(int.Parse(m.Groups[1].Value), ct);
            raw.Append('"').Append(literal.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
            line = await c.ReadLineAsync(ct) ?? "";
        }
        var tokens = Tokens(raw.ToString());
        if (tokens.Count < 2) return new FakeImapCommand(tokens.FirstOrDefault() ?? "*", "", [], raw.ToString());
        return new FakeImapCommand(tokens[0], tokens[1].ToUpperInvariant(), tokens.Skip(2).ToList(), raw.ToString());
    }

    /// <summary>Atoms and quoted strings (with \" and \\); a parenthesized list stays one token, as written.</summary>
    public static List<string> Tokens(string text)
    {
        var list = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == ' ') { i++; continue; }
            if (text[i] == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) i++;
                    sb.Append(text[i++]);
                }
                i++;
                list.Add(sb.ToString());
            }
            else if (text[i] == '(')
            {
                var depth = 0;
                var start = i;
                do
                {
                    if (text[i] == '(') depth++;
                    else if (text[i] == ')') depth--;
                    i++;
                } while (i < text.Length && depth > 0);
                list.Add(text[start..i]);
            }
            else
            {
                var start = i;
                while (i < text.Length && text[i] != ' ') i++;
                list.Add(text[start..i]);
            }
        }
        return list;
    }
}

/// <summary>
/// ESMTP, the part MailKit needs to authenticate: EHLO/HELO, AUTH PLAIN and AUTH LOGIN, RSET, NOOP, QUIT. MAIL, RCPT and DATA are
/// recorded in <see cref="Messages"/> so a test can prove nothing was sent (the Connection's Test never sends).
/// </summary>
public sealed class FakeSmtpServer(string user, string password, bool realLoopback = false) : FakeLineServer(realLoopback)
{
    public string User { get; set; } = user;
    public string Password { get; set; } = password;

    /// <summary>Whether EHLO offers AUTH at all.</summary>
    public bool OffersAuth { get; set; } = true;

    /// <summary>Each MAIL FROM a client started (a send). Empty after a Test.</summary>
    public List<string> Messages { get; } = [];

    public int Refused { get; private set; }

    protected override async Task ServeAsync(FakeMailConnection c, CancellationToken ct)
    {
        await c.WriteLineAsync("220 fake.test ESMTP ready", ct);
        while (!ct.IsCancellationRequested)
        {
            var line = await c.ReadLineAsync(ct);
            if (line is null) return;
            Record(line);
            var verb = line.Split(' ', 2)[0].ToUpperInvariant();
            var rest = line.Length > verb.Length ? line[(verb.Length + 1)..] : "";
            switch (verb)
            {
                case "EHLO":
                    await c.WriteLineAsync("250-fake.test hello", ct);
                    if (OffersAuth) await c.WriteLineAsync("250-AUTH PLAIN LOGIN", ct);
                    await c.WriteLineAsync("250 8BITMIME", ct);
                    break;
                case "HELO":
                    await c.WriteLineAsync("250 fake.test", ct);
                    break;
                case "AUTH":
                    await AuthAsync(c, rest, ct);
                    break;
                case "MAIL":
                    lock (Messages) Messages.Add(rest);
                    await c.WriteLineAsync("250 OK", ct);
                    break;
                case "RCPT" or "RSET" or "NOOP":
                    await c.WriteLineAsync("250 OK", ct);
                    break;
                case "QUIT":
                    await c.WriteLineAsync("221 bye", ct);
                    return;
                default:
                    await c.WriteLineAsync("502 command not implemented", ct);
                    break;
            }
        }
    }

    private async Task AuthAsync(FakeMailConnection c, string rest, CancellationToken ct)
    {
        var parts = rest.Split(' ', 2);
        var mech = parts[0].ToUpperInvariant();
        string? u = null, p = null;
        if (mech == "PLAIN")
        {
            var b64 = parts.Length > 1 ? parts[1] : null;
            if (b64 is null) { await c.WriteLineAsync("334 ", ct); b64 = await c.ReadLineAsync(ct) ?? ""; }
            var fields = Decode(b64).Split('\0');
            if (fields.Length == 3) { u = fields[1]; p = fields[2]; }
        }
        else if (mech == "LOGIN")
        {
            if (parts.Length > 1) u = Decode(parts[1]);
            else { await c.WriteLineAsync("334 VXNlcm5hbWU6", ct); u = Decode(await c.ReadLineAsync(ct) ?? ""); }
            await c.WriteLineAsync("334 UGFzc3dvcmQ6", ct);
            p = Decode(await c.ReadLineAsync(ct) ?? "");
        }
        else
        {
            await c.WriteLineAsync("504 mechanism not supported", ct);
            return;
        }
        if (u == User && p == Password) { c.SignedIn = true; await c.WriteLineAsync("235 2.7.0 Authentication successful", ct); }
        else { Refused++; await c.WriteLineAsync("535 5.7.8 Authentication credentials invalid", ct); }
    }

    private static string Decode(string b64)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64.Trim())); }
        catch (FormatException) { return ""; }
    }
}
