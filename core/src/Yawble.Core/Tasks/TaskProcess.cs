using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Yawble.Core.Integrations;

namespace Yawble.Core.Tasks;

/// <summary>How a task process ended. <c>KilledBy</c>: timeout, person, or null.</summary>
public sealed record TaskProcessResult(int? ExitCode, bool TimedOut, string? KilledBy, long OutputBytes, bool Truncated, string Tail, string LogSha256, TimeSpan Duration);

public sealed class TaskProcessOptions
{
    public required string Program { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public required string WorkingDirectory { get; init; }
    public required IReadOnlyDictionary<string, string> Environment { get; init; }
    public required TimeSpan Timeout { get; init; }
    public required string LogPath { get; init; }
    /// <summary>Values (of 8+ characters) redacted exactly from the log and the tail, besides the token shapes.</summary>
    public IReadOnlyList<string> SecretValues { get; init; } = [];
    public long LogCapBytes { get; init; } = 50L * 1024 * 1024;
    public int TailLines { get; init; } = 200;
    public int TailBytes { get; init; } = 32 * 1024;
    public TimeSpan KillGrace { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Runs a task's program: never through a shell (<see cref="CliRunner.Prepare"/>), stdin closed,
/// no window, an environment that is exactly what the caller built, output streamed to a log file as it arrives (capped,
/// the cap never kills the process), a ring of the last lines for the wake-up, and the whole process tree killed on the
/// time limit or a cancel. On Windows the process lives in a Job Object with KILL_ON_JOB_CLOSE, so grandchildren that
/// detached from their parent die too, and so does everything if the app itself exits or crashes. On Linux and macOS
/// it is started under <c>setsid</c> and the process group is killed.
/// </summary>
public static class TaskProcess
{
    /// <summary>Values shorter than this are not masked in a task's log (they would mask ordinary words).</summary>
    public const int MinMaskedLength = 8;

    /// <summary>
    /// What to mask for one secret value: the value, and, since the log is masked a line at a time, each of its lines
    /// long enough to mask (a key file over several lines would otherwise never match as a whole).
    /// </summary>
    public static IEnumerable<string> MaskedParts(string value)
    {
        yield return value;
        foreach (var line in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (line.Length >= MinMaskedLength && line != value) yield return line;
    }

    public static string Redact(string line, IReadOnlyList<string> secrets)
    {
        var text = CliRunner.Redact(line);
        // Longest first: a shorter secret inside a longer one must not leave the rest of the longer one showing.
        foreach (var s in secrets.Where(s => s.Length >= MinMaskedLength).OrderByDescending(s => s.Length))
            text = text.Replace(s, "[redacted]", StringComparison.Ordinal);
        return text;
    }

    /// <summary>The variables a task child gets besides the ones it names.</summary>
    public static readonly string[] BasicEnvironment =
    [
        "PATH", "PATHEXT", "SystemRoot", "SYSTEMROOT", "windir", "ComSpec", "TEMP", "TMP", "TMPDIR", "USERPROFILE", "HOME", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData", "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432", "USERNAME", "USER",
        "COMPUTERNAME", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "LANG", "LC_ALL", "SHELL",
    ];

    /// <summary>Builds the child's whole environment: the basics and the named variables from <paramref name="read"/>; names that are not set are returned in <c>Missing</c>.</summary>
    public static (Dictionary<string, string> Env, List<string> Missing, List<string> SecretValues) BuildEnvironment(IEnumerable<string> names, Func<string, string?> read)
    {
        var env = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var b in BasicEnvironment) if (read(b) is { } v) env[b] = v;
        env["TERM"] = "dumb";
        env["CI"] = "1";
        env["NO_COLOR"] = "1";
        var missing = new List<string>();
        var secrets = new List<string>();
        foreach (var n in names)
        {
            if (read(n) is { } v) { env[n] = v; secrets.Add(v); }
            else missing.Add(n);
        }
        return (env, missing, secrets);
    }

    /// <summary>The variables <see cref="BuildEnvironment"/> sets to fixed values for every child.</summary>
    public static readonly string[] RuntimeSet = ["TERM", "CI", "NO_COLOR"];

    /// <summary>
    /// Reads each binding's secret from its Connection, as a run starts. <c>Values</c> holds every value read (variable to value);
    /// <c>Problems</c> one value-free line per binding that cannot be read: the Connection is gone, needs reconnecting, or the field is
    /// empty. A run with any problem does not start.
    /// </summary>
    public static (Dictionary<string, string> Values, List<string> Problems) ReadBindings(IEnumerable<EnvBinding> bindings, Connections.ConnectionService? connections)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var b in bindings)
        {
            string? why = null;
            if (connections is null || !long.TryParse(b.ConnectionId, out var id)) why = "the Connection is not there";
            else if (connections.Brief(id) is not { } brief) why = "the Connection was removed";
            else if (brief.Status == "needs_reconnect") why = "the Connection needs reconnecting";
            else
            {
                try
                {
                    if (!connections.Secrets(id).TryGetValue(b.Field, out var v) || v.Length == 0) why = $"its field '{b.Field}' is empty";
                    else if (v.Trim().Length < MinMaskedLength) why = $"its field '{b.Field}' is shorter than {MinMaskedLength} characters, too short to be masked in the task's log";
                    else values[b.Name] = v;
                }
                catch (Credentials.CredentialStoreException) { why = "the Connection could not be read on this computer"; }
            }
            if (why is not null) problems.Add($"{b.Shown}: {why}");
        }
        return (values, problems);
    }

    public static async Task<TaskProcessResult> RunAsync(TaskProcessOptions o, CancellationToken cancel)
    {
        var psi = CliRunner.Prepare(o.Program, o.Args);
        if (!OperatingSystem.IsWindows() && Setsid is { } setsid)
        {
            var inner = psi.FileName;
            var innerArgs = psi.ArgumentList.ToArray();
            psi.FileName = setsid;
            psi.ArgumentList.Clear();
            psi.ArgumentList.Add(inner);
            foreach (var a in innerArgs) psi.ArgumentList.Add(a);
        }
        psi.WorkingDirectory = o.WorkingDirectory;
        psi.Environment.Clear();
        foreach (var (k, v) in o.Environment) psi.Environment[k] = v;

        Directory.CreateDirectory(Path.GetDirectoryName(o.LogPath)!);
        var watch = Stopwatch.StartNew();
        using var log = new FileStream(o.LogPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        var gate = new object();
        long written = 0, bytes = 0;
        var truncated = false;
        var tail = new LinkedList<string>();
        var tailBytes = 0;
        void Take(string? line)
        {
            if (line is null) return;
            var text = Redact(line, o.SecretValues);
            var data = Encoding.UTF8.GetBytes(text + "\n");
            lock (gate)
            {
                bytes += data.Length;
                if (written + data.Length <= o.LogCapBytes) { log.Write(data); written += data.Length; }
                else if (!truncated)
                {
                    truncated = true;
                    var note = Encoding.UTF8.GetBytes($"log cut at {o.LogCapBytes / (1024 * 1024)} MB (the process keeps running until it ends or hits its time limit)\n");
                    log.Write(note);
                }
                tail.AddLast(text);
                tailBytes += data.Length;
                while (tail.Count > o.TailLines || (tailBytes > o.TailBytes && tail.Count > 1))
                {
                    tailBytes -= Encoding.UTF8.GetByteCount(tail.First!.Value) + 1;
                    tail.RemoveFirst();
                }
            }
        }

        using var tree = ProcessTree.Create();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => Take(e.Data);
        process.ErrorDataReceived += (_, e) => Take(e.Data);
        process.Start();
        tree.Adopt(process);
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(o.Timeout);
        string? killedBy = null;
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            killedBy = cancel.IsCancellationRequested ? "person" : "timeout";
            await tree.KillAsync(process, o.KillGrace);
            try { process.WaitForExit(5000); } catch (InvalidOperationException) { }
        }
        // Anything that outlived the process but is still in its tree goes with it: nothing is left behind.
        if (killedBy is null) tree.KillRemaining();

        int? exit = null;
        try { if (process.HasExited) exit = process.ExitCode; } catch (InvalidOperationException) { }
        string tailText;
        lock (gate)
        {
            log.Flush();
            tailText = string.Join('\n', tail);
        }
        log.Dispose();
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(o.LogPath))).ToLowerInvariant();
        return new TaskProcessResult(killedBy is null ? exit : null, killedBy == "timeout", killedBy, bytes, truncated, tailText, sha, watch.Elapsed);
    }

    private static readonly string? Setsid = OperatingSystem.IsWindows() ? null : CliRunner.Find("setsid");
}

/// <summary>A Job Object (Windows) or a process group (elsewhere) holding one task process and everything it starts.</summary>
internal abstract class ProcessTree : IDisposable
{
    public static ProcessTree Create() => OperatingSystem.IsWindows() ? new WindowsJob() : new PosixGroup();

    /// <summary>Puts a just-started process in the tree. (A grandchild started in the instants before this is not covered.)</summary>
    public abstract void Adopt(Process process);
    public abstract Task KillAsync(Process process, TimeSpan grace);
    /// <summary>Kills what is still running in the tree after the main process exited.</summary>
    public abstract void KillRemaining();
    public abstract void Dispose();
}

internal sealed class WindowsJob : ProcessTree
{
    private nint _job;

    public WindowsJob()
    {
        _job = CreateJobObjectW(0, null);
        if (_job == 0) throw new Win32Exception();
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(_job, 9, buffer, (uint)size)) throw new Win32Exception();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public override void Adopt(Process process)
    {
        if (!AssignProcessToJobObject(_job, process.Handle)) throw new Win32Exception();
    }

    public override Task KillAsync(Process process, TimeSpan grace)
    {
        if (_job != 0) TerminateJobObject(_job, 1);
        return Task.CompletedTask;
    }

    public override void KillRemaining()
    {
        if (_job != 0) TerminateJobObject(_job, 1);
    }

    public override void Dispose()
    {
        if (_job == 0) return;
        CloseHandle(_job); // KILL_ON_JOB_CLOSE: whatever is left dies with the handle
        _job = 0;
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(nint job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

/// <summary>The process is a session leader (started under setsid), so its process group id is its pid.</summary>
internal sealed class PosixGroup : ProcessTree
{
    private int _pgid;

    public override void Adopt(Process process) => _pgid = process.Id;

    public override async Task KillAsync(Process process, TimeSpan grace)
    {
        Signal("TERM");
        try
        {
            using var wait = new CancellationTokenSource(grace);
            await process.WaitForExitAsync(wait.Token);
        }
        catch (OperationCanceledException) { }
        Signal("KILL");
    }

    public override void KillRemaining() => Signal("KILL");

    public override void Dispose() { }

    private void Signal(string name)
    {
        if (_pgid <= 0) return;
        try
        {
            var psi = new ProcessStartInfo("kill") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-" + name);
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("-" + _pgid);
            using var p = Process.Start(psi);
            p?.WaitForExit(2000);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
    }
}
