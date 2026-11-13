using System.Diagnostics;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;

namespace Yawble.Core.Launch;

/// <summary>
/// Windows: the core runs the bundled wxc-exec.exe from the same sandbox release with the request
/// in an owner-only file (<see cref="WxcExec.Arguments"/>: --config, never --config-base64). The wxc-exec route
/// never reports timedOut, so the supervisor's own wall-clock timer ends the run on Windows. Interactive runs put
/// wxc-exec on a ConPTY the core owns (<see cref="SpawnWithPty"/>).
/// </summary>
public sealed class WxcExecBackend(string executable, string requestDir) : ISandboxBackend
{
    public string Name => "wxc-exec";

    /// <summary>
    /// The request without the denied paths that do not exist on the host. The sandbox (wxc-exec 1.0.1) cannot start a
    /// run that denies a path whose parent folder is missing too (CreateProcessSecurityEnvironment, 0x80070003: a new
    /// Windows profile has no ~/.config, so ~/.config/gh stopped every run); a path that does not exist holds nothing to
    /// deny, and every run checks again. The request is unchanged when every denied path exists.
    /// </summary>
    public static string WithoutMissingDenied(string requestJson, Func<string, bool>? exists = null)
    {
        exists ??= Path.Exists;
        if (System.Text.Json.Nodes.JsonNode.Parse(requestJson) is not System.Text.Json.Nodes.JsonObject request
            || request["filesystem"]?["deniedPaths"] is not System.Text.Json.Nodes.JsonArray denied) return requestJson;
        var missing = denied.Where(n => n?.GetValue<string>() is not { } p || !exists(p)).ToList();
        if (missing.Count == 0) return requestJson;
        foreach (var n in missing) denied.Remove(n);
        return request.ToJsonString();
    }

    public ISandboxProcess Spawn(string requestJson)
    {
        if (!File.Exists(executable)) throw new SandboxStartException(4, $"wxc-exec.exe is missing at {executable}");
        requestJson = WithoutMissingDenied(requestJson);
        Directory.CreateDirectory(requestDir);
        var file = Path.Combine(requestDir, $"request-{Guid.NewGuid():N}.json");
        // The request carries the run token: the file lives in the core's data folder, which no sandbox is granted.
        File.WriteAllText(file, requestJson);
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in WxcExec.Arguments(file)) info.ArgumentList.Add(a);
        var process = Process.Start(info) ?? throw new SandboxStartException(12, "wxc-exec.exe did not start");
        process.StandardInput.Close();
        return new WxcExecProcess(process, file);
    }

    /// <summary>
    /// An interactive run: wxc-exec.exe --config &lt;file&gt; on a ConPTY the core owns, in a kill-on-close job.
    /// Policy errors on this route come back as terminal text and an exit code (the request was validated before).
    /// </summary>
    public ISandboxProcess SpawnWithPty(string requestJson, PtySize size)
    {
        if (!OperatingSystem.IsWindows()) throw new SandboxStartException(2, "the wxc-exec backend runs only on Windows");
        if (!File.Exists(executable)) throw new SandboxStartException(4, $"wxc-exec.exe is missing at {executable}");
        requestJson = WithoutMissingDenied(requestJson);
        Directory.CreateDirectory(requestDir);
        var file = Path.Combine(requestDir, $"request-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, requestJson);
        try
        {
            var commandLine = CommandLine.Join(TargetOs.Windows, [executable, .. WxcExec.Arguments(file)]);
            return new WxcExecPtyProcess(ConPtyProcess.Start(commandLine, size), file);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            try { File.Delete(file); } catch (IOException) { }
            throw new SandboxStartException(12, "wxc-exec.exe did not start on a ConPTY: " + e.Message);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class WxcExecPtyProcess(ConPtyProcess pty, string requestFile) : ISandboxProcess
    {
        private bool _timedOut;

        public int Id => pty.Id;
        public Stream? Output => pty.Output;
        public Stream? Error => null;
        public Stream? Input => pty.Input;

        public SandboxExit? TryWait() => pty.TryGetExitCode() is { } code ? new SandboxExit(code, _timedOut) : null;

        public void Kill() => pty.Kill();

        public void KillForTimeout()
        {
            _timedOut = true;
            pty.Kill();
        }

        public void Resize(PtySize size) => pty.Resize(size);

        public void Dispose()
        {
            pty.Dispose();
            try { File.Delete(requestFile); } catch (IOException) { }
        }
    }

    private sealed class WxcExecProcess(Process process, string requestFile) : ISandboxProcess
    {
        private bool _timedOut;

        public int Id => process.Id;
        public Stream? Output => process.StandardOutput.BaseStream;
        public Stream? Error => process.StandardError.BaseStream;
        public Stream? Input => null;

        public SandboxExit? TryWait() => process.HasExited ? new SandboxExit(process.ExitCode, _timedOut) : null;

        public void Kill()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }

        public void KillForTimeout()
        {
            _timedOut = true;
            Kill();
        }

        public void Resize(PtySize size) { }

        public void Dispose()
        {
            try { Kill(); } catch (InvalidOperationException) { }
            process.Dispose();
            try { File.Delete(requestFile); } catch (IOException) { }
        }
    }
}
