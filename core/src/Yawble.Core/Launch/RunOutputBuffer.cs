namespace Yawble.Core.Launch;

/// <summary>
/// A run's terminal bytes: the last <see cref="Capacity"/> bytes for replay when the window attaches, and a
/// live event for an attached window. Thread-safe; the supervisor's pumps write, the IPC layer reads.
/// </summary>
public sealed class RunOutputBuffer(int capacity = 1 << 20) : IRunOutput
{
    private readonly object _lock = new();
    private readonly byte[] _ring = new byte[capacity];
    private long _written;

    public int Capacity => capacity;

    /// <summary>Raised after each write, on the pump's thread. Handlers must not block.</summary>
    public event Action<ReadOnlyMemory<byte>>? Data;

    public long TotalWritten { get { lock (_lock) return _written; } }

    public void Write(ReadOnlySpan<byte> data)
    {
        byte[] copy;
        lock (_lock)
        {
            foreach (var b in data.Length > capacity ? data[^capacity..] : data)
            {
                _ring[(int)(_written % capacity)] = b;
                _written++;
            }
            copy = data.ToArray();
        }
        Data?.Invoke(copy);
    }

    /// <summary>What the window replays on attach: the retained tail, oldest first.</summary>
    public byte[] Snapshot()
    {
        lock (_lock)
        {
            var n = (int)Math.Min(_written, capacity);
            var result = new byte[n];
            var start = _written - n;
            for (var i = 0; i < n; i++) result[i] = _ring[(int)((start + i) % capacity)];
            return result;
        }
    }
}
