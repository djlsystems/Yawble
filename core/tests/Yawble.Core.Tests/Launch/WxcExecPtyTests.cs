using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Yawble.Core.Launch;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// On Windows an interactive run is wxc-exec.exe --config &lt;file&gt; inside a ConPTY the core
/// owns, because the sandbox's native PTY call refuses ProcessContainer. Against the real sandbox: output arrives as a
/// terminal stream, input typed into the PTY reaches the sandboxed program, a resize reaches it, the exit code comes
/// back, and Kill leaves no process of the tree behind.
/// </summary>
[Collection(RealSandboxCollection.Name)]
public class WxcExecPtyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Pty : IDisposable
    {
        private readonly StringBuilder _text = new();
        private readonly TcpListener _proxy = new(IPAddress.Loopback, 0);
        public ISandboxProcess Process { get; }

        public Pty(string commandLine, PtySize size)
        {
            _proxy.Start();
            var cwd = Directory.CreateTempSubdirectory("yawble-pty-").FullName;
            var request = RealSandboxHost.WindowsRequest(commandLine, cwd, ((IPEndPoint)_proxy.LocalEndpoint).Port);
            Process = RealSandboxHost.Backend().SpawnWithPty(request.ToJsonString(), size);
            var output = Process.Output!;
            new Thread(() =>
            {
                var buf = new byte[4096];
                try
                {
                    int n;
                    while ((n = output.Read(buf, 0, buf.Length)) > 0)
                        lock (_text) _text.Append(Encoding.UTF8.GetString(buf, 0, n));
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }) { IsBackground = true }.Start();
        }

        public string Text { get { lock (_text) return _text.ToString(); } }

        public async Task<string> WaitFor(string needle, int seconds = 60)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                if (Text.Contains(needle, StringComparison.Ordinal)) return Text;
                await Task.Delay(50, Ct);
            }
            throw new TimeoutException($"'{needle}' never appeared; the terminal showed:\n{Text}");
        }

        public void Type(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            Process.Input!.Write(bytes, 0, bytes.Length);
            Process.Input.Flush();
        }

        public async Task<SandboxExit> WaitExit(int seconds = 60)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            SandboxExit? exit;
            while ((exit = Process.TryWait()) is null && DateTime.UtcNow < deadline) await Task.Delay(50, Ct);
            return exit ?? throw new TimeoutException("the PTY run did not exit; the terminal showed:\n" + Text);
        }

        public void Dispose()
        {
            Process.Dispose();
            _proxy.Stop();
        }
    }

    /// <summary>Windows PowerShell 5.1 running <paramref name="script"/> (-EncodedCommand: no quoting to get wrong).</summary>
    private static string PowerShell(string script) => Yawble.Core.Sandboxing.CommandLine.Join(Yawble.Core.Policy.TargetOs.Windows,
        [@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "-NoProfile", "-NoLogo", "-ExecutionPolicy", "Bypass",
         "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]); // interactive: Read-Host must work

    [Fact]
    public async Task OutputInputResizeAndTheExitCodeGoThroughTheConPty()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the ConPTY wrapper around wxc-exec is Windows-only");
        RealSandboxHost.SkipUnlessAvailable();
        // Reads one line from the terminal, prints the window width, waits for a resize, prints it again, exits 7.
        const string script =
            "$w = $Host.UI.RawUI.WindowSize.Width; Write-Host \"@ready width=$w\"; " +
            "$line = Read-Host; Write-Host \"@got=$line\"; " +
            "for ($i = 0; $i -lt 100 -and $Host.UI.RawUI.WindowSize.Width -eq $w; $i++) { Start-Sleep -Milliseconds 100 }; " +
            "Write-Host \"@width=$($Host.UI.RawUI.WindowSize.Width)\"; exit 7";
        using var pty = new Pty(PowerShell(script), new PtySize(30, 100));

        await pty.WaitFor("@ready width=100");
        pty.Type("hello from the host\r");
        await pty.WaitFor("@got=hello from the host");
        pty.Process.Resize(new PtySize(30, 132));
        await pty.WaitFor("@width=132");
        var exit = await pty.WaitExit();
        Assert.Equal(7, exit.ExitCode);
        Assert.False(exit.TimedOut);
    }

    [Fact]
    public async Task KillEndsTheWholeTreeWithinFiveSeconds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the ConPTY wrapper around wxc-exec is Windows-only");
        RealSandboxHost.SkipUnlessAvailable();
        // The sandboxed PowerShell starts a child that would outlive it, then waits for ever.
        var marker = "yawble-pty-kill-" + Guid.NewGuid().ToString("N")[..8];
        const string script = "Start-Process -NoNewWindow ping.exe -ArgumentList '-n','600','127.0.0.1'; Write-Host '@ready'; Start-Sleep 600";
        using var pty = new Pty(PowerShell(script + " # " + marker), PtySize.Default);
        await pty.WaitFor("@ready");
        var wxcId = pty.Process.Id;
        var before = TreeOf(wxcId);
        Assert.Contains(before, p => p.Name.Equals("powershell", StringComparison.OrdinalIgnoreCase));

        var sw = Stopwatch.StartNew();
        pty.Process.Kill();
        while (sw.Elapsed < TimeSpan.FromSeconds(5) && before.Any(p => Alive(p.Id))) await Task.Delay(50, Ct);
        Assert.True(before.All(p => !Alive(p.Id)),
            "still running after Kill: " + string.Join(", ", before.Where(p => Alive(p.Id)).Select(p => $"{p.Name}({p.Id})")));
        Assert.NotNull(pty.Process.TryWait());
    }

    private static bool Alive(int id)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(id); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>The process and every descendant by parent id (WMI-free: a snapshot via NtQuery is overkill here).</summary>
    private static List<(int Id, string Name)> TreeOf(int root)
    {
        var all = System.Diagnostics.Process.GetProcesses()
            .Select(p => { try { return (p.Id, p.ProcessName, Parent: ParentOf(p.Id)); } catch { return (p.Id, p.ProcessName, Parent: -1); } })
            .ToList();
        var result = new List<(int, string)>();
        var queue = new Queue<int>([root]);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var p in all.Where(p => p.Parent == id && p.Id != id)) { result.Add((p.Id, p.ProcessName)); queue.Enqueue(p.Id); }
        }
        return result;
    }

    private static int ParentOf(int id)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, id);
        if (h == IntPtr.Zero) return -1;
        try
        {
            var info = new long[6];
            return NtQueryInformationProcess(h, 0, info, info.Length * 8, out _) == 0 ? (int)info[5] : -1;
        }
        finally { CloseHandle(h); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int id);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr h, int cls, long[] info, int len, out int ret);
}
