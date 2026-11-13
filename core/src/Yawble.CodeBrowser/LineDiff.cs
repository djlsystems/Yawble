namespace Yawble.CodeBrowser;

/// <summary>One change between two versions of a file: old lines [OldStart, OldStart+OldCount) became new lines [NewStart, NewStart+NewCount) (0-based).</summary>
public readonly record struct DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount);

/// <summary>
/// In-process line diff (Myers' O((N+M)D) algorithm, after the common head and tail are trimmed). Past
/// <see cref="MaxEdits"/> edits the middle is reported as one changed block, so a rewritten file costs little.
/// </summary>
public static class LineDiff
{
    public const int MaxEdits = 2000;

    public static IReadOnlyList<DiffHunk> Compute(IReadOnlyList<string> a, IReadOnlyList<string> b, int maxEdits = MaxEdits)
    {
        int start = 0;
        while (start < a.Count && start < b.Count && a[start] == b[start]) start++;
        int endA = a.Count, endB = b.Count;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }
        int n = endA - start, m = endB - start;
        if (n == 0 && m == 0) return [];
        if (n == 0 || m == 0) return [new DiffHunk(start, n, start, m)];

        var deleted = new bool[n];
        var inserted = new bool[m];
        if (!Myers(a, b, start, n, m, maxEdits, deleted, inserted))
            return [new DiffHunk(start, n, start, m)];

        var hunks = new List<DiffHunk>();
        int i = 0, j = 0;
        while (i < n || j < m)
        {
            if (i < n && deleted[i] || j < m && inserted[j])
            {
                int i0 = i, j0 = j;
                while (i < n && deleted[i]) i++;
                while (j < m && inserted[j]) j++;
                hunks.Add(new DiffHunk(start + i0, i - i0, start + j0, j - j0));
            }
            else
            {
                i++;
                j++;
            }
        }
        return hunks;
    }

    private static bool Myers(IReadOnlyList<string> a, IReadOnlyList<string> b, int off, int n, int m, int maxEdits, bool[] deleted, bool[] inserted)
    {
        var max = n + m;
        var limit = Math.Min(max, maxEdits);
        var v = new int[2 * max + 2];
        var trace = new List<int[]>();
        int found = -1;
        for (var d = 0; d <= limit && found < 0; d++)
        {
            for (var k = -d; k <= d; k += 2)
            {
                int x = k == -d || k != d && v[max + k - 1] < v[max + k + 1] ? v[max + k + 1] : v[max + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[off + x] == b[off + y]) { x++; y++; }
                v[max + k] = x;
                if (x >= n && y >= m) found = d;
            }
            var snap = new int[2 * d + 1];
            Array.Copy(v, max - d, snap, 0, 2 * d + 1);
            trace.Add(snap);
        }
        if (found < 0) return false;

        int cx = n, cy = m;
        for (var d = found; d > 0; d--)
        {
            var prev = trace[d - 1];
            int k = cx - cy;
            int At(int kk) => prev[kk + d - 1];
            int prevK = k == -d || k != d && At(k - 1) < At(k + 1) ? k + 1 : k - 1;
            int prevX = At(prevK);
            int prevY = prevX - prevK;
            if (prevK == k + 1) inserted[prevY] = true; // a step down: b[prevY] is new
            else deleted[prevX] = true;                // a step right: a[prevX] is gone
            cx = prevX;
            cy = prevY;
        }
        return true;
    }
}
