using System.Diagnostics;
using System.Text;
using Yawble.Core.Bus;
using Yawble.Core.Credentials;

namespace Yawble.Core.Ui;

/// <summary>
/// The two unsandboxed host panes of Settings → Model credentials: Anthropic's own
/// `claude setup-token` and xAI's own `grok login` against the app-private host Grok home. Each runs the person's own CLI
/// by the absolute path detection found, never through PATH. The app drives no OAuth flow and never reads the pane's
/// output for a secret: the person copies the setup-token out of the pane and pastes it in; grok writes its own
/// session into the host Grok home.
///
/// The process runs on pipes, not a PTY, so the pane is a plain line stream: keystrokes go
/// to stdin, output is shown as it comes. A CLI that insists on a TTY says so in the pane.
/// </summary>
public sealed class HostPaneTerminals(TerminalHub hub, Func<string, string?> cliPath, GrokKeeper keeper) : IHostTerminals
{
    /// <summary>Variables of the core's environment that never reach a host pane: other credentials and the IPC token.</summary>
    private static readonly string[] Withheld =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_OAUTH_TOKEN", "XAI_API_KEY",
        "GROK_API_KEY", "YAWBLE_IPC_TOKEN", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy",
    ];

    /// <summary>What the browser sign-in needs to open a browser on the person's desktop; the rest stays built.</summary>
    private static readonly string[] Desktop =
        ["DISPLAY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS", "BROWSER", "LANG", "TERM"];

    public Task StartAsync(string kind, string channel, Action<int> onExit, CancellationToken ct)
    {
        var (psi, label) = kind switch
        {
            IHostTerminals.ClaudeSetupToken => (ClaudeSetupToken(), "claude setup-token"),
            IHostTerminals.GrokLogin => (GrokLogin(), "grok login"),
            _ => throw new BusRefusal("bad_kind", $"no host pane named '{kind}'"),
        };
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("did not start");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new BusRefusal("not_started", $"{label} could not start: {e.Message}");
        }
        var input = new PaneInput(process);
        hub.Begin(channel, new TerminalSession("host", null, DateTimeOffset.UtcNow.ToString("O"), true, label), input, clearReplay: true);
        hub.Write(channel, $"\x1b[2m$ {label}   (unsandboxed host pane{(kind == IHostTerminals.GrokLogin ? ", the app's host Grok home" : "")})\x1b[0m\r\n");
        _ = Task.Run(async () =>
        {
            await Task.WhenAll(Pump(process.StandardOutput.BaseStream, channel), Pump(process.StandardError.BaseStream, channel));
            await process.WaitForExitAsync(CancellationToken.None);
            var code = input.Stopped ? 130 : process.ExitCode;
            hub.End(channel, code, input.Stopped ? "stopped" : $"exited {code}");
            process.Dispose();
            onExit(code);
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    private ProcessStartInfo ClaudeSetupToken()
    {
        var path = Cli("claude");
        // The person's own environment (Anthropic's browser flow needs their desktop), minus every other credential.
        var psi = new ProcessStartInfo(path) { WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        psi.ArgumentList.Add("setup-token");
        foreach (var name in Withheld) psi.Environment.Remove(name);
        return psi;
    }

    private ProcessStartInfo GrokLogin()
    {
        Cli("grok");
        keeper.Home.EnsureCreated();
        // xAI's own grok, with the keeper's built environment (GROK_HOME = the host Grok home, no XAI_API_KEY).
        var psi = keeper.LoginStartInfo(deviceAuth: false);
        foreach (var name in Desktop)
            if (Environment.GetEnvironmentVariable(name) is { } value) psi.Environment[name] = value;
        return psi;
    }

    private string Cli(string name) =>
        cliPath(name) ?? throw new BusRefusal("not_installed",
            $"{name} is not installed on this computer; install it (see Agents), then try again");

    private async Task Pump(Stream stream, string channel)
    {
        var buffer = new byte[4096];
        var lastWasCr = false;
        int n;
        while ((n = await stream.ReadAsync(buffer)) > 0)
        {
            // The pane is an xterm: a bare \n from a pipe needs its \r.
            var text = new StringBuilder(n + 16);
            foreach (var ch in Encoding.UTF8.GetString(buffer, 0, n))
            {
                if (ch == '\n' && !lastWasCr) text.Append('\r');
                text.Append(ch);
                lastWasCr = ch == '\r';
            }
            hub.Write(channel, text.ToString());
        }
    }

    private sealed class PaneInput(Process process) : ITerminalInput
    {
        public bool Stopped { get; private set; }

        public void Write(ReadOnlySpan<byte> data)
        {
            // An xterm sends \r for Enter; a line-reading CLI on a pipe wants \n.
            var text = Encoding.UTF8.GetString(data).Replace('\r', '\n');
            try
            {
                process.StandardInput.Write(text);
                process.StandardInput.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }

        public void Resize(int cols, int rows) { }

        public Task StopAsync()
        {
            Stopped = true;
            try { process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return Task.CompletedTask;
        }
    }
}
