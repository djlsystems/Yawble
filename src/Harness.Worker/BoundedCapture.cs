using System.Globalization;
using System.Text;

namespace Harness.Host;

/// <summary>
/// One stream of a child's output, held to a fixed size: the first <c>headLimit</c> characters and
/// the last <c>tailLimit</c>, with a marker between them saying how much was dropped.
///
/// Unbounded, a member that cats a large file would hold every character of it in Host memory, twice
/// (stdout and the combined output), for as long as the run lasts. The head is where a CLI says what it is
/// doing and the tail is where it says how it ended - the usage envelope, the final answer, the
/// error - so those are the two ends worth keeping.
///
/// Thread-safe: an abandoned pump may still be appending while the runner reads the text.
/// </summary>
public sealed class BoundedCapture(int headLimit = BoundedCapture.DefaultHalf, int tailLimit = BoundedCapture.DefaultHalf)
{
    /// <summary>2 MiB of characters at each end - about 4 MiB per stream.</summary>
    public const int DefaultHalf = 2 * 1024 * 1024;

    private readonly StringBuilder _head = new();
    // Allocated on first overflow of the head: an ordinary run never needs it.
    private char[]? _tail;
    private int _tailStart;
    private int _tailCount;
    private long _dropped;

    /// <summary>Characters that were read and not kept.</summary>
    public long Dropped
    {
        get { lock (_head) return _dropped; }
    }

    public void Append(char[] buffer, int offset, int count)
    {
        lock (_head)
        {
            var toHead = Math.Min(count, headLimit - _head.Length);
            if (toHead > 0)
            {
                _head.Append(buffer, offset, toHead);
                offset += toHead;
                count -= toHead;
            }

            if (count == 0) return;

            if (tailLimit == 0)
            {
                _dropped += count;
                return;
            }

            // Only the last tailLimit characters of this chunk can survive it.
            if (count > tailLimit)
            {
                _dropped += count - tailLimit + _tailCount;
                offset += count - tailLimit;
                count = tailLimit;
                _tailStart = 0;
                _tailCount = 0;
            }

            _tail ??= new char[tailLimit];

            for (var i = 0; i < count; i++)
            {
                var end = (_tailStart + _tailCount) % tailLimit;
                _tail[end] = buffer[offset + i];

                if (_tailCount < tailLimit)
                {
                    _tailCount++;
                }
                else
                {
                    _tailStart = (_tailStart + 1) % tailLimit;
                    _dropped++;
                }
            }
        }
    }

    public override string ToString()
    {
        lock (_head)
        {
            var text = new StringBuilder(_head.Length + _tailCount + 128);
            text.Append(_head);

            if (_dropped > 0)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"\n[harness] {_dropped} characters of output were not kept; the first {_head.Length} and the last {_tailCount} are shown.\n");
            }

            if (_tail is not null)
            {
                var firstRun = Math.Min(_tailCount, tailLimit - _tailStart);
                text.Append(_tail, _tailStart, firstRun);
                text.Append(_tail, 0, _tailCount - firstRun);
            }

            return text.ToString();
        }
    }
}
