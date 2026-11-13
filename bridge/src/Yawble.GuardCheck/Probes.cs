using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Yawble.GuardCheck;

/// <summary>
/// The guardrail check's escape attempts, made from inside the sandbox. Each one reports what it observed;
/// "allowed" means only that the attempt worked from inside. Every attempt is bounded (3 s per network try).
/// </summary>
public sealed partial class Probes(ProbePlan plan, ProbeOutput output, Func<string, string?> env)
{
    private static readonly TimeSpan NetTimeout = TimeSpan.FromSeconds(3);
    private const string Blocked = "blocked", Allowed = "allowed", Skip = "skip", Info = "info";

    public async Task RunAllAsync()
    {
        Context();
        foreach (var r in plan.Reads) Read(r);
        foreach (var w in plan.Writes) Write(w);
        await Task.WhenAll(plan.Tcp.Select(TcpAsync).Concat(plan.Dns.Select(DnsAsync)));
        await GatewayAsync();
        Processes();
        CredentialStores();
        Display();
        SecretScan();
    }

    private void Context()
    {
        var uid = OperatingSystem.IsWindows() ? "n/a" : Libc.getuid().ToString();
        output.Result("sandbox.context", "context", Info,
            $"os={RuntimeInformation.OSDescription}; uid={uid}; HOME={env("HOME") ?? env("USERPROFILE")}; cwd={Environment.CurrentDirectory}; " +
            $"proxy={(env("HTTP_PROXY") ?? env("http_proxy") ?? "none")}");
    }

    // ---- filesystem ----

    private void Read(ReadProbe r)
    {
        try
        {
            switch (r.Mode)
            {
                case "list":
                    if (!Directory.Exists(r.Path)) { output.Result(r.Id, r.Category, Blocked, $"not visible: {r.Path}", w => w.WriteNumber("entries", -1)); return; }
                    // The names (never contents) go to the core, which tells the host's entries from the sandbox's own mount points.
                    var names = Directory.EnumerateFileSystemEntries(r.Path).Take(200).Select(Path.GetFileName).ToList();
                    var n = names.Count;
                    output.Result(r.Id, r.Category, n > 0 ? Allowed : Blocked, n > 0 ? $"listable, {n} entries: {r.Path}" : $"listable but empty: {r.Path}",
                        w =>
                        {
                            w.WriteNumber("entries", n);
                            w.WriteStartArray("names");
                            foreach (var name in names) w.WriteStringValue(name);
                            w.WriteEndArray();
                        });
                    return;
                case "canary":
                    var buffer = new byte[256];
                    int read;
                    using (var f = File.OpenRead(r.Path)) read = f.Read(buffer);
                    var text = Encoding.UTF8.GetString(buffer, 0, read).Trim();
                    output.Result(r.Id, r.Category, text == r.Canary ? Allowed : Blocked,
                        text == r.Canary ? $"read the core's canary through {r.Path}" : $"a file exists at {r.Path} but it is not the host's canary");
                    return;
                default:
                    using (File.OpenHandle(r.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { }
                    output.Result(r.Id, r.Category, Allowed, $"openable for reading (no bytes read): {r.Path}");
                    return;
            }
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            output.Result(r.Id, r.Category, Blocked, $"not visible: {r.Path}", r.Mode == "list" ? w => w.WriteNumber("entries", -1) : null);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            output.Result(r.Id, r.Category, Blocked, $"visible but not openable ({e.GetType().Name}: {e.Message}): {r.Path}",
                r.Mode == "list" ? w => w.WriteNumber("entries", -1) : null);
        }
    }

    private void Write(WriteProbe p)
    {
        if (p.Mode == "open")
        {
            try
            {
                // FileMode.Open never truncates; nothing is written.
                using (new FileStream(p.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
                output.Result(p.Id, p.Category, Allowed, $"opened for writing (nothing written): {p.Path}", w => w.WriteBoolean("sandboxWrote", false));
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                output.Result(p.Id, p.Category, Blocked, $"cannot open for writing ({e.GetType().Name}: {e.Message}): {p.Path}", w => w.WriteBoolean("sandboxWrote", false));
            }
            return;
        }
        try
        {
            using (var f = new FileStream(p.Path, FileMode.CreateNew, FileAccess.Write))
                f.Write(Encoding.UTF8.GetBytes(plan.Nonce));
            output.Result(p.Id, p.Category, Allowed, $"created inside the sandbox: {p.Path}", w => w.WriteBoolean("sandboxWrote", true));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            output.Result(p.Id, p.Category, Blocked, $"cannot create ({e.GetType().Name}: {e.Message}): {p.Path}", w => w.WriteBoolean("sandboxWrote", false));
        }
    }

    // ---- network ----

    private async Task TcpAsync(TcpProbe t)
    {
        try
        {
            var ip = IPAddress.Parse(t.Host);
            using var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            using var cts = new CancellationTokenSource(NetTimeout);
            await s.ConnectAsync(new IPEndPoint(ip, t.Port), cts.Token);
            output.Result(t.Id, t.Category, Allowed, $"TCP connected to {Endpoint(t)}");
        }
        catch (OperationCanceledException)
        {
            output.Result(t.Id, t.Category, Blocked, $"no TCP to {Endpoint(t)} (timed out after {NetTimeout.TotalSeconds:0} s)");
        }
        catch (SocketException e)
        {
            output.Result(t.Id, t.Category, Blocked, $"no TCP to {Endpoint(t)} ({e.SocketErrorCode})");
        }
    }

    private static string Endpoint(TcpProbe t) => t.Host.Contains(':') ? $"[{t.Host}]:{t.Port}" : $"{t.Host}:{t.Port}";

    private async Task DnsAsync(DnsProbe d)
    {
        try
        {
            using var cts = new CancellationTokenSource(NetTimeout);
            var addresses = await System.Net.Dns.GetHostAddressesAsync(d.Name, cts.Token);
            output.Result(d.Id, d.Category, addresses.Length > 0 ? Allowed : Blocked,
                addresses.Length > 0 ? $"{d.Name} resolved to {addresses.Length} address(es)" : $"{d.Name} did not resolve");
        }
        catch (OperationCanceledException)
        {
            output.Result(d.Id, d.Category, Blocked, $"{d.Name} did not resolve (timed out)");
        }
        catch (SocketException e)
        {
            output.Result(d.Id, d.Category, Blocked, $"{d.Name} did not resolve ({e.SocketErrorCode})");
        }
    }

    private async Task GatewayAsync()
    {
        if (plan.Gateway is not { } g) return;
        var proxyText = env("HTTP_PROXY") ?? env("http_proxy");
        if (proxyText is null || !Uri.TryCreate(proxyText, UriKind.Absolute, out var proxy))
        {
            output.Result("gateway.reachable", "gateway", Blocked, "no HTTP_PROXY in the sandbox: the gateway cannot be reached, so the gateway attempts were not made");
            return;
        }
        var token = env("YAWBLE_RUN_TOKEN") ?? "";
        const string ping = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";

        var reach = await HttpAsync(proxy, "POST", "http://bus.yawble.internal/mcp", "bus.yawble.internal", ping, "Bearer " + token);
        output.Result("gateway.reachable", "gateway", reach is > 0 and not 401 and not 407 ? Allowed : Blocked,
            reach > 0 ? $"bus.yawble.internal answered HTTP {reach} with this run's token" : $"the gateway at {proxy.Authority} did not answer ({Describe(reach)})");

        var guessed = await HttpAsync(proxy, "POST", "http://bus.yawble.internal/mcp", "bus.yawble.internal", ping, "Bearer yawrt_" + new string('A', 43));
        output.Result("gateway.guessed-token", "gateway", guessed == 401 ? Blocked : Allowed, $"a guessed run token got {Describe(guessed)}");

        var none = await HttpAsync(proxy, "POST", "http://bus.yawble.internal/mcp", "bus.yawble.internal", ping, null);
        output.Result("gateway.no-token", "gateway", none == 401 ? Blocked : Allowed, $"no run token got {Describe(none)}");

        var http = await HttpAsync(proxy, "GET", $"http://{g.DeniedHost}/", g.DeniedHost, null, null);
        output.Result("gateway.http-denied-host", "gateway", http is 200 or (>= 300 and < 400) ? Allowed : Blocked,
            $"GET http://{g.DeniedHost}/ through the gateway got {Describe(http)}");

        var connect = await ConnectAsync(proxy, g.DeniedHost);
        output.Result("gateway.connect-denied-host", "gateway", connect == 200 ? Allowed : Blocked,
            $"CONNECT {g.DeniedHost}:443 got {Describe(connect)}", w => w.WriteString("host", g.DeniedHost));

        foreach (var vendor in g.VendorHosts)
        {
            var v = await ConnectAsync(proxy, vendor);
            output.Result("gateway.connect-vendor:" + vendor, "gateway", v == 200 ? Allowed : Blocked,
                $"CONNECT {vendor}:443 (a vendor host only the gateway may reach, through its vhost) got {Describe(v)}", w => w.WriteString("host", vendor));
        }

        if (g.AllowedHost is { } allowed)
        {
            var a = await ConnectAsync(proxy, allowed);
            // 200: tunnel open. 502/504: the gateway allowed it but could not reach the host (offline machine).
            output.Result("gateway.connect-allowed-host", "gateway", a is 200 or 502 or 504 ? Allowed : Blocked,
                a is 502 or 504 ? $"CONNECT {allowed}:443 was allowed by the gateway, but the host was unreachable ({Describe(a)})" : $"CONNECT {allowed}:443 got {Describe(a)}",
                w => w.WriteString("host", allowed));
        }
    }

    private static string Describe(int status) => status switch
    {
        > 0 => $"HTTP {status}",
        -1 => "no connection",
        -2 => "no answer in time",
        _ => "no valid HTTP answer",
    };

    /// <summary>One HTTP/1.1 request through the proxy (absolute-form, as the CLIs send). Returns the status, or &lt;0.</summary>
    private static async Task<int> HttpAsync(Uri proxy, string method, string url, string host, string? body, string? authorization)
    {
        var head = new StringBuilder();
        head.Append($"{method} {url} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n");
        if (authorization is not null) head.Append($"Authorization: {authorization}\r\n");
        var bytes = body is null ? [] : Encoding.UTF8.GetBytes(body);
        if (body is not null) head.Append($"Content-Type: application/json\r\nAccept: application/json, text/event-stream\r\nContent-Length: {bytes.Length}\r\n");
        head.Append("\r\n");
        return await ExchangeAsync(proxy, [.. Encoding.ASCII.GetBytes(head.ToString()), .. bytes]);
    }

    private static Task<int> ConnectAsync(Uri proxy, string host) =>
        ExchangeAsync(proxy, Encoding.ASCII.GetBytes($"CONNECT {host}:443 HTTP/1.1\r\nHost: {host}:443\r\n\r\n"));

    private static async Task<int> ExchangeAsync(Uri proxy, byte[] request)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(proxy.Host, proxy.Port, cts.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(request, cts.Token);
            var buffer = new byte[512];
            var total = 0;
            while (total < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(total), cts.Token);
                if (n == 0) break;
                total += n;
                if (Array.IndexOf(buffer, (byte)'\n', 0, total) >= 0) break;
            }
            var line = Encoding.ASCII.GetString(buffer, 0, total);
            var m = StatusLine().Match(line);
            return m.Success ? int.Parse(m.Groups[1].Value) : -3;
        }
        catch (OperationCanceledException)
        {
            return -2;
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            return -1;
        }
    }

    [GeneratedRegex(@"^HTTP/1\.[01] (\d{3})")]
    private static partial Regex StatusLine();

    // ---- processes ----

    private void Processes()
    {
        if (OperatingSystem.IsLinux() && Directory.Exists("/proc"))
        {
            var pids = Directory.EnumerateDirectories("/proc").Count(d => int.TryParse(Path.GetFileName(d), out _));
            output.Result("proc.visible", "process", Info, $"{pids} process(es) visible in /proc");
        }
        if (plan.HostPid is not { } pid) return;
        if (OperatingSystem.IsWindows())
        {
            var h = Kernel32.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h != IntPtr.Zero) { Kernel32.CloseHandle(h); output.Result("proc.host-open", "process", Allowed, $"OpenProcess({pid}) succeeded"); }
            else output.Result("proc.host-open", "process", Blocked, $"OpenProcess({pid}) failed (error {Marshal.GetLastPInvokeError()})");
            var vm = Kernel32.OpenProcess(0x0010 | 0x0020 /* VM_READ | VM_OPERATION */, false, pid);
            if (vm != IntPtr.Zero) { Kernel32.CloseHandle(vm); output.Result("proc.host-memory", "process", Allowed, $"OpenProcess({pid}, VM_READ) succeeded"); }
            else output.Result("proc.host-memory", "process", Blocked, $"OpenProcess({pid}, VM_READ) failed (error {Marshal.GetLastPInvokeError()})");
            return;
        }
        if (Libc.kill(pid, 0) == 0) output.Result("proc.host-signal", "process", Allowed, $"kill(host core pid {pid}, 0) succeeded");
        else output.Result("proc.host-signal", "process", Blocked, $"cannot signal host core pid {pid} (errno {Marshal.GetLastPInvokeError()})");
        if (OperatingSystem.IsLinux())
        {
            Read(new ReadProbe("proc.host-environ", "process", $"/proc/{pid}/environ", "open", null));
            if (Libc.ptrace(16 /* PTRACE_ATTACH */, pid, IntPtr.Zero, IntPtr.Zero) == 0)
            {
                Libc.waitpid(pid, out _, 0);
                Libc.ptrace(17 /* PTRACE_DETACH */, pid, IntPtr.Zero, IntPtr.Zero);
                output.Result("proc.host-ptrace", "process", Allowed, $"PTRACE_ATTACH to host core pid {pid} succeeded");
            }
            else output.Result("proc.host-ptrace", "process", Blocked, $"PTRACE_ATTACH to host core pid {pid} failed (errno {Marshal.GetLastPInvokeError()})");
        }
    }

    // ---- credential stores, display ----

    private void CredentialStores()
    {
        if (OperatingSystem.IsLinux())
        {
            var bus = $"/run/user/{Libc.getuid()}/bus";
            var reached = false;
            if (File.Exists(bus) || Directory.Exists(Path.GetDirectoryName(bus)))
            {
                try
                {
                    using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    s.Connect(new UnixDomainSocketEndPoint(bus));
                    reached = true;
                }
                catch (SocketException) { }
            }
            output.Result("credstore.dbus-session", "credential-store", reached ? Allowed : Blocked,
                reached ? $"connected to the D-Bus session bus at {bus} (the Secret Service is behind it)" : $"no D-Bus session bus at {bus}");
            var address = env("DBUS_SESSION_BUS_ADDRESS");
            output.Result("credstore.dbus-env", "credential-store", address is null ? Blocked : Allowed,
                address is null ? "DBUS_SESSION_BUS_ADDRESS is not set" : "DBUS_SESSION_BUS_ADDRESS is set");
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (var service in plan.KeychainServices)
            {
                var code = RunQuiet("/usr/bin/security", ["find-generic-password", "-s", service], out _);
                output.Result("credstore.keychain:" + service, "credential-store", code == 0 ? Allowed : Blocked,
                    code == 0 ? $"the keychain item '{service}' is visible (its password was not requested)" : $"security find-generic-password -s '{service}' failed (exit {code})");
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            var code = RunQuiet("cmdkey.exe", ["/list"], out var text);
            var targets = Regex.Matches(text, "Target:").Count;
            output.Result("credstore.cmdkey", "credential-store", targets > 0 ? Allowed : Blocked,
                targets > 0 ? $"cmdkey /list listed {targets} credential(s)" : $"cmdkey /list listed no credentials (exit {code})");
        }
    }

    private void Display()
    {
        var display = env("DISPLAY") ?? env("WAYLAND_DISPLAY");
        if (!OperatingSystem.IsWindows())
            output.Result("display.env", "display", display is null ? Blocked : Allowed, display is null ? "no DISPLAY or WAYLAND_DISPLAY" : "a display variable is set");
        if (OperatingSystem.IsLinux())
        {
            var x = Directory.Exists("/tmp/.X11-unix") && Directory.EnumerateFileSystemEntries("/tmp/.X11-unix").Any();
            output.Result("display.x11-socket", "display", x ? Allowed : Blocked, x ? "an X11 socket is visible in /tmp/.X11-unix" : "no X11 socket");
        }
        if (OperatingSystem.IsMacOS())
        {
            var code = RunQuiet("/usr/bin/pbpaste", [], out _);
            output.Result("display.clipboard", "display", code == 0 ? Allowed : Blocked, $"pbpaste exited {code}");
        }
    }

    private static int RunQuiet(string file, IEnumerable<string> arguments, out string stdout)
    {
        stdout = "";
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in arguments) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var read = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000)) { p.Kill(true); return -2; }
            stdout = read.Result;
            return p.ExitCode;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }

    // ---- secrets: only the run token may be in the sandbox ----

    private static readonly (string Name, Regex Pattern)[] SecretShapes =
    [
        ("Anthropic key or token (sk-ant-)", new Regex(@"sk-ant-[A-Za-z0-9]+-[A-Za-z0-9_\-]{20,}", RegexOptions.CultureInvariant)),
        ("xAI API key (xai-)", new Regex(@"\bxai-[A-Za-z0-9]{40,}", RegexOptions.CultureInvariant)),
        ("GitHub token", new Regex(@"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}|\bgithub_pat_[A-Za-z0-9_]{40,}", RegexOptions.CultureInvariant)),
        ("AWS access key id", new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.CultureInvariant)),
        ("private key", new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)),
    ];

    private void SecretScan()
    {
        var token = env("YAWBLE_RUN_TOKEN");
        var holders = new List<string>();
        var findings = new List<string>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var name = (string)e.Key;
            var value = e.Value as string ?? "";
            if (!string.IsNullOrEmpty(token) && value.Contains(token, StringComparison.Ordinal)) holders.Add(name);
            foreach (var (shape, pattern) in SecretShapes)
                if (pattern.IsMatch(value)) findings.Add($"env {name}: {shape}");
        }
        holders.Sort(StringComparer.Ordinal);
        output.Result("secrets.run-token", "secrets", Info,
            holders.Count == 0 ? "the run token is in no environment variable" : "the run token (a placeholder, revoked when the run ends) is in: " + string.Join(", ", holders));

        var files = 0;
        foreach (var dir in plan.ScanDirs.Distinct())
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> all;
            try
            {
                all = Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8, AttributesToSkip = FileAttributes.ReparsePoint });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in all)
            {
                if (++files > 5000) break;
                var name = Path.GetFileName(file);
                if (name == ".credentials.json") findings.Add($"file {file}: a Claude credentials file");
                try
                {
                    // Size 0: FIFOs, sockets and devices (e.g. the .NET runtime's debug pipes in TMPDIR), which would block a
                    // read; an empty regular file holds no secret either.
                    var length = new FileInfo(file).Length;
                    if (length == 0 || length > 1_048_576) continue;
                    var text = File.ReadAllText(file);
                    foreach (var (shape, pattern) in SecretShapes)
                        if (pattern.IsMatch(text)) findings.Add($"file {file}: {shape}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        output.Result("secrets.scan", "secrets", findings.Count == 0 ? Blocked : Allowed,
            findings.Count == 0
                ? $"no credential-shaped value in the environment or in {files} file(s) under {string.Join(", ", plan.ScanDirs)}"
                : "found (values not shown): " + string.Join("; ", findings.Distinct().Take(20)));
    }
}

internal static partial class Libc
{
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int kill(int pid, int sig);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial long ptrace(int request, int pid, IntPtr addr, IntPtr data);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int waitpid(int pid, out int status, int options);

    [LibraryImport("libc")]
    internal static partial uint getuid();
}

internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);
}
