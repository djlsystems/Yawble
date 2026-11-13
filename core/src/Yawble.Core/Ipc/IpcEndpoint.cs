using System.IO.Pipes;
using System.Net.Sockets;

namespace Yawble.Core.Ipc;

/// <summary>
/// The core's listening end: a named pipe on Windows, a Unix domain socket on Linux and macOS.
/// Only the current user can connect (pipe: PipeOptions.CurrentUserOnly; socket: 0700 folder, 0600 file),
/// and the first frame must still carry the per-launch token.
/// </summary>
public abstract class IpcEndpoint : IAsyncDisposable
{
    /// <summary>What a client connects to: a socket path, or \\.\pipe\name on Windows.</summary>
    public abstract string Address { get; }

    public abstract Task<Stream> AcceptAsync(CancellationToken ct);

    public abstract ValueTask DisposeAsync();

    public static IpcEndpoint Create(string address) =>
        OperatingSystem.IsWindows() ? new NamedPipeEndpoint(address)
        : OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() ? new UnixSocketEndpoint(address)
        : throw new PlatformNotSupportedException("the core runs on Windows, Linux and macOS");

    /// <summary>A fresh, unguessable address for one launch (used by tests and as a default).</summary>
    public static string NewAddress(string runtimeDir)
    {
        var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        return OperatingSystem.IsWindows() ? $@"\\.\pipe\yawble-{id}" : Path.Combine(runtimeDir, $"yawble-{id}.sock");
    }
}

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal sealed class UnixSocketEndpoint : IpcEndpoint
{
    private readonly Socket _socket;
    private readonly string _path;

    public UnixSocketEndpoint(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("the socket path must be absolute", nameof(path));
        _path = path;
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (File.Exists(path)) File.Delete(path); // a stale socket from a crashed core
        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _socket.Bind(new UnixDomainSocketEndPoint(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _socket.Listen(8);
    }

    public override string Address => _path;

    public override async Task<Stream> AcceptAsync(CancellationToken ct)
    {
        var client = await _socket.AcceptAsync(ct).ConfigureAwait(false);
        return new NetworkStream(client, ownsSocket: true);
    }

    public override ValueTask DisposeAsync()
    {
        _socket.Dispose();
        try { File.Delete(_path); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}

internal sealed class NamedPipeEndpoint : IpcEndpoint
{
    private const string Prefix = @"\\.\pipe\";
    private readonly string _name;
    // A pipe name exists only while a server instance is open, and a client that connects when none is
    // (Node's net.connect) fails at once with ENOENT. So the first instance is created here, before the core
    // prints YAWBLE-READY, and AcceptAsync opens the next one before it hands out a connected one.
    //
    // _gate guards _listening and _disposed: IpcServer disposes the endpoint while its accept loop may be in AcceptAsync.
    private readonly Lock _gate = new();
    private NamedPipeServerStream? _listening;
    private bool _disposed;

    public NamedPipeEndpoint(string address)
    {
        _name = address.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? address[Prefix.Length..] : address;
        // FirstPipeInstance: fail rather than join a pipe someone else created first under our name.
        _listening = NewInstance(PipeOptions.FirstPipeInstance);
    }

    public override string Address => Prefix + _name;

    // With the default 0-byte in/out buffers a write blocks until the other end reads it, so two ends that each
    // write before reading wait for each other forever. Give the pipe buffers so it behaves like the Unix socket.
    private const int BufferSize = 64 * 1024;

    private NamedPipeServerStream NewInstance(PipeOptions extra = PipeOptions.None) =>
        new(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | extra, BufferSize, BufferSize);

    public override async Task<Stream> AcceptAsync(CancellationToken ct)
    {
        NamedPipeServerStream pipe;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            pipe = _listening!;
        }
        try
        {
            // Disposing the endpoint disposes this instance, which also ends the wait (it does not rely on ct alone).
            await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Cancelled, disposed, or a client that connected and went away before we saw it (which breaks this
            // instance): drop it, and replace it unless the endpoint is shutting down.
            await pipe.DisposeAsync().ConfigureAwait(false);
            Replace(pipe);
            throw;
        }
        Replace(pipe);
        return pipe;
    }

    /// <summary>Opens the next listening instance in place of <paramref name="used"/>, never after dispose.</summary>
    private void Replace(NamedPipeServerStream used)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_listening, used)) return;
            _listening = NewInstance();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        NamedPipeServerStream? listening;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            listening = _listening;
            _listening = null;
        }
        if (listening is not null) await listening.DisposeAsync().ConfigureAwait(false);
    }
}
