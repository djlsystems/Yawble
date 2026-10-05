using System.Threading.Channels;
using Harness.Containers;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WRITES ADMISSION'S HOLDS DOWN WITHOUT HOLDING ADMISSION UP. <c>WipLedger</c> reports each hold
/// opened and ended under its own lock, where nothing may do I/O; this queues the write and returns,
/// and one loop writes them to <c>admission_holds</c> in the order they happened.
///
/// <para>
/// A WRITE THAT FAILS IS LOGGED AND DROPPED. The hold itself proceeds - admission never waits on its
/// record - and nothing is estimated in the row's place: the chart shows plain waiting there.
/// </para>
/// </summary>
public sealed class AdmissionHoldWriter : IAdmissionHolds
{
    private readonly IAdmissionHoldLedger _ledger;
    private readonly Channel<Func<Task>> _writes =
        Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });

    public AdmissionHoldWriter(IAdmissionHoldLedger ledger, ILogger? logger = null)
    {
        _ledger = ledger;
        Logger = logger;
        _ = Task.Run(WriteAllAsync);
    }

    /// <summary>Where a failed write is reported; set once the Host's logging exists. Until then,
    /// standard error.</summary>
    public ILogger? Logger { get; set; }

    public void Held(string team, string member, long? deliverySeq, DateTimeOffset at, string reasonKind, string reason) =>
        Queue(
            () => _ledger.OpenAsync(team, member, deliverySeq, at, reasonKind, reason),
            $"open {team}/{member}'s admission hold ({reasonKind})");

    public void Released(string team, string member, DateTimeOffset at) =>
        Queue(() => _ledger.CloseAsync(team, member, at), $"close {team}/{member}'s admission hold");

    /// <summary>Completes once every hold reported before this call has been written, or has failed.</summary>
    public Task WrittenAsync(CancellationToken ct = default)
    {
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _writes.Writer.TryWrite(() =>
        {
            written.TrySetResult();
            return Task.CompletedTask;
        });
        return written.Task.WaitAsync(ct);
    }

    private void Queue(Func<Task> write, string what) =>
        _writes.Writer.TryWrite(async () =>
        {
            try
            {
                await write();
            }
            catch (Exception exception)
            {
                if (Logger is { } logger)
                {
                    logger.LogWarning(exception, "Could not {What}; admission carried on without it.", what);
                }
                else
                {
                    Console.Error.WriteLine($"Could not {what}; admission carried on without it: {exception.Message}");
                }
            }
        });

    private async Task WriteAllAsync()
    {
        await foreach (var write in _writes.Reader.ReadAllAsync())
        {
            await write();
        }
    }
}
