using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// A worker in the Host's own process: its <see cref="WorkerHost"/> joined to control's event
/// handler by an <see cref="InProcessTransport"/>. Control holds <see cref="Worker"/> and talks to
/// runs through it alone; disposing closes the connection.
/// </summary>
public sealed class InProcessWorker : IDisposable
{
    private InProcessWorker(InProcessTransport transport, WorkerHost host)
    {
        Transport = transport;
        Host = host;
    }

    public InProcessTransport Transport { get; }

    public WorkerHost Host { get; }

    /// <summary>Control's handle on this worker.</summary>
    public IRunWorker Worker => Transport;

    /// <summary>
    /// A worker made by <paramref name="host"/>, whose events go to <paramref name="control"/>. It says
    /// it is ready before this returns.
    /// </summary>
    public static InProcessWorker Connect(
        WorkerId id, Func<IRunEvents, WorkerHost> host, Func<WorkerEnvelope, CancellationToken, Task> control)
    {
        var transport = new InProcessTransport(id);
        var worker = host(transport);
        transport.Connect(worker.ApplyAsync, control);
        worker.ReadyAsync().GetAwaiter().GetResult();
        return new InProcessWorker(transport, worker);
    }

    public void Dispose() => Transport.Dispose();
}
