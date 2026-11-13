using System.Runtime.InteropServices;
using System.Text;

namespace Yawble.Core.Launch;

/// <summary>
/// The handle-based (streaming) C ABI of the native mxc_ffi in Microsoft.Mxc.Sdk 1.0.1: mxc_spawn_json,
/// mxc_spawn_pty_json, try_wait, kill, kill_for_timeout, the stream accessors and free. The 1.0.1 managed SDK
/// does not expose exact-JSON spawning, so the core binds the exports directly, as <see cref="Sandboxing.NativeSandbox"/>
/// does for mxc_run_json. Signatures mirror src/ffi/mxc_ffi/src/streaming.rs and pty.rs at v1.0.1; every
/// export used here is present in runtimes/linux-x64/native/libmxc_ffi.so of the 1.0.1 package.
/// </summary>
internal static partial class SandboxStreamingAbi
{
    private const string Library = "mxc_ffi";

    [StructLayout(LayoutKind.Sequential)]
    internal struct ErrorDetail
    {
        public IntPtr Message;
        public IntPtr Operation;
        public IntPtr NativeCode;
        public IntPtr Remediation;
    }

    [LibraryImport(Library, EntryPoint = "mxc_spawn_json")]
    internal static partial int SpawnJson(byte[] requestJsonUtf8, int experimental, out IntPtr handle, ref ErrorDetail error);

    [LibraryImport(Library, EntryPoint = "mxc_spawn_pty_json")]
    internal static partial int SpawnPtyJson(byte[] requestJsonUtf8, int experimental, ushort rows, ushort cols, out IntPtr handle, ref ErrorDetail error);

    [LibraryImport(Library, EntryPoint = "mxc_error_detail_free")]
    internal static partial void ErrorDetailFree(ref ErrorDetail detail);

    [LibraryImport(Library, EntryPoint = "mxc_sandbox_id")]
    internal static partial uint SandboxId(IntPtr handle);

    [LibraryImport(Library, EntryPoint = "mxc_sandbox_take_stdin")]
    internal static partial IntPtr TakeStdin(IntPtr handle);

    [LibraryImport(Library, EntryPoint = "mxc_sandbox_take_stdout")]
    internal static partial IntPtr TakeStdout(IntPtr handle);

    [LibraryImport(Library, EntryPoint = "mxc_sandbox_take_stderr")]
    internal static partial IntPtr TakeStderr(IntPtr handle);

    [LibraryImport(Library, EntryPoint = "mxc_sandbox_try_wait")]
    internal static partial int TryWait(IntPtr handle, out int exit, out int running, out int timedOut);

    [LibraryImport(Library, EntryPoint = "mxc_sandbox_kill")]
    internal static partial int Kill(IntPtr handle);

    [LibraryImport(Library, EntryPoint = "mxc_sandbox_kill_for_timeout")]
    internal static partial int KillForTimeout(IntPtr handle);

    [LibraryImport(Library, EntryPoint = "mxc_sandbox_pty_resize")]
    internal static partial int PtyResize(IntPtr handle, ushort rows, ushort cols);

    [LibraryImport(Library, EntryPoint = "mxc_sandbox_free")]
    internal static partial void SandboxFree(IntPtr handle);

    [LibraryImport(Library, EntryPoint = "mxc_stream_read")]
    internal static unsafe partial int StreamRead(IntPtr stream, byte* buffer, nuint capacity, out nuint read);

    [LibraryImport(Library, EntryPoint = "mxc_stream_write")]
    internal static unsafe partial int StreamWrite(IntPtr stream, byte* buffer, nuint length, out nuint written);

    [LibraryImport(Library, EntryPoint = "mxc_stream_flush")]
    internal static partial int StreamFlush(IntPtr stream);

    [LibraryImport(Library, EntryPoint = "mxc_read_stream_free")]
    internal static partial void ReadStreamFree(IntPtr stream);

    [LibraryImport(Library, EntryPoint = "mxc_write_stream_free")]
    internal static partial void WriteStreamFree(IntPtr stream);

    internal static byte[] Utf8Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    internal static string Describe(int status, ref ErrorDetail error)
    {
        var message = Marshal.PtrToStringUTF8(error.Message);
        var remediation = Marshal.PtrToStringUTF8(error.Remediation);
        ErrorDetailFree(ref error);
        var text = $"the sandbox refused the request (status {status})";
        if (!string.IsNullOrWhiteSpace(message)) text += ": " + message;
        if (!string.IsNullOrWhiteSpace(remediation)) text += " (" + remediation + ")";
        return text;
    }
}

/// <summary>
/// Linux and macOS: the core spawns the sandbox in process through the native sandbox library (mxc_ffi). On
/// Linux, the sandbox's kill SIGKILLs bwrap, and bwrap runs with --unshare-pid and --die-with-parent
/// (microsoft/mxc bubblewrap/common/bwrap_command.rs:427-437 at v1.0.1), so the kernel ends the whole PID namespace:
/// the kill reaches processes that left the process group, which a process-group kill does not.
/// </summary>
public sealed class NativeSandboxBackend : ISandboxBackend
{
    public string Name => "yawble-ffi";

    /// <summary>MXC_STATUS_* is never negative: this marks the core's own refusal before the sandbox was called.</summary>
    public const int PreflightStatus = -2;

    public const string MissingGrantsReason =
        "not started: granted path(s) missing on the host (the sandbox cannot bind a missing path; the install is incomplete): ";

    /// <summary>
    /// The sandbox (1.0.1) cannot bind a granted path that is missing on the host, and on Linux it then reports a misleading
    /// "slirp4netns ... nsenter: cannot open /proc/&lt;pid&gt;/ns/net". The paths are named instead, and the sandbox is not called.
    /// </summary>
    public static void Preflight(string requestJson)
    {
        if (System.Text.Json.Nodes.JsonNode.Parse(requestJson)?["filesystem"] is not { } fs) return;
        var missing = (fs["readonlyPaths"]?.AsArray() ?? []).Concat(fs["readwritePaths"]?.AsArray() ?? [])
            .Select(n => n!.GetValue<string>()).Where(p => !Path.Exists(p)).ToList();
        if (missing.Count > 0) throw new SandboxStartException(PreflightStatus, MissingGrantsReason + string.Join(", ", missing));
    }

    public ISandboxProcess Spawn(string requestJson)
    {
        Preflight(requestJson);
        var error = default(SandboxStreamingAbi.ErrorDetail);
        var (status, handle) = SpawnThread.Run(() =>
        {
            var s = SandboxStreamingAbi.SpawnJson(SandboxStreamingAbi.Utf8Z(requestJson), 0, out var h, ref error);
            return (s, h);
        });
        if (status != 0 || handle == IntPtr.Zero) throw new SandboxStartException(status, SandboxStreamingAbi.Describe(status, ref error));
        SandboxStreamingAbi.ErrorDetailFree(ref error);
        var process = new NativeSandboxProcess(handle, pty: false);
        // The headless CLI gets an empty, closed stdin: claude -p and grok -p must never wait on it.
        process.CloseInput();
        return process;
    }

    public ISandboxProcess SpawnWithPty(string requestJson, PtySize size)
    {
        Preflight(requestJson);
        var error = default(SandboxStreamingAbi.ErrorDetail);
        var (status, handle) = SpawnThread.Run(() =>
        {
            var s = SandboxStreamingAbi.SpawnPtyJson(SandboxStreamingAbi.Utf8Z(requestJson), 0, size.Rows, size.Cols, out var h, ref error);
            return (s, h);
        });
        if (status != 0 || handle == IntPtr.Zero) throw new SandboxStartException(status, SandboxStreamingAbi.Describe(status, ref error));
        SandboxStreamingAbi.ErrorDetailFree(ref error);
        return new NativeSandboxProcess(handle, pty: true);
    }
}

/// <summary>
/// Every sandbox is spawned from ONE thread that lives as long as the process. Sandbox release 1.0.1 runs bwrap with
/// --die-with-parent, which arms PR_SET_PDEATHSIG, and Linux delivers it when the THREAD that forked bwrap exits, not
/// the process. 1.0.1 forks on the caller's thread, so a sandbox spawned from a .NET thread-pool thread was SIGKILLed
/// (exit -1, output cut short) as soon as the pool retired that thread - typically when another burst of work had grown
/// the pool, i.e. when several runs or other work were going on at once. Upstream microsoft/mxc forks bwrap from a
/// process-lifetime thread too (1cc2dbd0, #1526), after 1.0.1; this keeps the core correct on 1.0.1 and is harmless
/// on a release that carries the fix. The sandbox still dies with the
/// core process (the thread ends only with it). Spawns are serialised; each is a short blocking call.
/// </summary>
internal static class SpawnThread
{
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> s_work = Start();

    private static System.Collections.Concurrent.BlockingCollection<Action> Start()
    {
        var work = new System.Collections.Concurrent.BlockingCollection<Action>();
        var thread = new Thread(() =>
        {
            foreach (var item in work.GetConsumingEnumerable()) item();
        })
        { IsBackground = true, Name = "sandbox-spawner" };
        thread.Start();
        return work;
    }

    internal static T Run<T>(Func<T> spawn)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_work.Add(() =>
        {
            try { done.SetResult(spawn()); }
            catch (Exception e) { done.SetException(e); }
        });
        return done.Task.GetAwaiter().GetResult();
    }
}

internal sealed class NativeSandboxProcess : ISandboxProcess
{
    private readonly object _lock = new();
    private IntPtr _handle;
    private NativeWriteStream? _input;

    public NativeSandboxProcess(IntPtr handle, bool pty)
    {
        _handle = handle;
        Id = (int)SandboxStreamingAbi.SandboxId(handle);
        // A PTY hands out its writer and a reader over the master; the reader must be taken first
        // (PtySandbox in streaming.rs keeps them as one "individual" access mode).
        var stdout = SandboxStreamingAbi.TakeStdout(handle);
        Output = stdout == IntPtr.Zero ? null : new NativeReadStream(stdout);
        if (!pty)
        {
            var stderr = SandboxStreamingAbi.TakeStderr(handle);
            Error = stderr == IntPtr.Zero ? null : new NativeReadStream(stderr);
        }
        var stdin = SandboxStreamingAbi.TakeStdin(handle);
        _input = stdin == IntPtr.Zero ? null : new NativeWriteStream(stdin);
    }

    public int Id { get; }
    public Stream? Output { get; }
    public Stream? Error { get; }
    public Stream? Input => _input;

    internal void CloseInput()
    {
        _input?.Dispose();
        _input = null;
    }

    public SandboxExit? TryWait()
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeSandboxProcess));
            var status = SandboxStreamingAbi.TryWait(_handle, out var exit, out var running, out var timedOut);
            if (status != 0) throw new IOException($"mxc_sandbox_try_wait failed (status {status})");
            return running != 0 ? null : new SandboxExit(exit, timedOut != 0);
        }
    }

    public void Kill() => Call(SandboxStreamingAbi.Kill, "mxc_sandbox_kill");

    public void KillForTimeout() => Call(SandboxStreamingAbi.KillForTimeout, "mxc_sandbox_kill_for_timeout");

    public void Resize(PtySize size)
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) return;
            var status = SandboxStreamingAbi.PtyResize(_handle, size.Rows, size.Cols);
            if (status != 0) throw new IOException($"mxc_sandbox_pty_resize failed (status {status})");
        }
    }

    private void Call(Func<IntPtr, int> call, string name)
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) return;
            var status = call(_handle);
            if (status != 0) throw new IOException($"{name} failed (status {status})");
        }
    }

    /// <summary>Frees the handle, which kills the tree if it still runs (mxc_sandbox_free).</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) return;
            SandboxStreamingAbi.SandboxFree(_handle);
            _handle = IntPtr.Zero;
        }
        _input?.Dispose();
    }
}

/// <summary>A read-only Stream over an MxcReadStream. One reader thread; freed once its read has returned.</summary>
internal sealed class NativeReadStream(IntPtr stream) : Stream
{
    private IntPtr _stream = stream;
    private readonly object _lock = new();

    public override unsafe int Read(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (_stream == IntPtr.Zero || count == 0) return 0;
            fixed (byte* p = &buffer[offset])
            {
                var status = SandboxStreamingAbi.StreamRead(_stream, p, (nuint)count, out var read);
                // A backend error after the child is killed is the end of the stream, not a failure.
                return status != 0 ? 0 : (int)read;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        lock (_lock)
        {
            if (_stream != IntPtr.Zero) SandboxStreamingAbi.ReadStreamFree(_stream);
            _stream = IntPtr.Zero;
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A write-only Stream over an MxcWriteStream (the PTY's input). Freeing it sends EOF.</summary>
internal sealed class NativeWriteStream(IntPtr stream) : Stream
{
    private IntPtr _stream = stream;
    private readonly object _lock = new();

    public override unsafe void Write(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (_stream == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeWriteStream));
            while (count > 0)
            {
                fixed (byte* p = &buffer[offset])
                {
                    var status = SandboxStreamingAbi.StreamWrite(_stream, p, (nuint)count, out var written);
                    if (status != 0) throw new IOException($"mxc_stream_write failed (status {status})");
                    offset += (int)written;
                    count -= (int)written;
                }
            }
        }
    }

    public override void Flush()
    {
        lock (_lock)
        {
            if (_stream != IntPtr.Zero) SandboxStreamingAbi.StreamFlush(_stream);
        }
    }

    protected override void Dispose(bool disposing)
    {
        lock (_lock)
        {
            if (_stream != IntPtr.Zero) SandboxStreamingAbi.WriteStreamFree(_stream);
            _stream = IntPtr.Zero;
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
