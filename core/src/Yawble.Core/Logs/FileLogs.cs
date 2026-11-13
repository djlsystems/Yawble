using System.Globalization;
using System.Text;

namespace Yawble.Core.Logs;

/// <summary>
/// A log kept as text files in the data folder, one entry per line, fields separated by tabs, the first field the time
/// (ISO 8601). Read, filtered, sorted and pruned in memory: these files are small and bounded by their retention.
/// </summary>
public sealed class FileLogSource(
    string id, string title, string group, string about, IReadOnlyList<LogColumn> columns,
    Func<string, IReadOnlyList<string>> files, int? defaultDays, string loss, bool memberInText, bool wholeFiles)
{
    public string Id => id;
    public string Title => title;
    public string Group => group;
    public string About => about;
    public IReadOnlyList<LogColumn> Columns => columns;
    public int? DefaultDays => defaultDays;
    public string Loss => loss;
    /// <summary>Team and member filters match the entry's text (the entries name them in passing).</summary>
    public bool MemberInText => memberInText;

    /// <summary>The files of this log in <paramref name="dataDir"/>.</summary>
    public IReadOnlyList<string> Files(string dataDir) => files(dataDir);

    /// <summary>Every entry: its fields, padded to the columns, and where it came from (file, line).</summary>
    public IEnumerable<(string[] Fields, string File, int Line)> Read(string dataDir)
    {
        foreach (var file in Files(dataDir))
        {
            string[] lines;
            try { lines = ReadShared(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                var parts = lines[i].Split('\t', columns.Count);
                if (parts.Length < columns.Count) Array.Resize(ref parts, columns.Count);
                yield return (parts, file, i);
            }
        }
    }

    /// <summary>
    /// Deletes the entries before <paramref name="cutoff"/> (ISO, compared as text). Daily files whose name dates them
    /// before the cutoff day are deleted whole; another file is rewritten without its old lines.
    /// </summary>
    public int Prune(string dataDir, string cutoff)
    {
        var removed = 0;
        foreach (var file in Files(dataDir))
        {
            try
            {
                var lines = ReadShared(file);
                var keep = lines.Where(l => l.Length > 0 && string.CompareOrdinal(TimeOf(l), cutoff) >= 0).ToList();
                var gone = lines.Count(l => l.Length > 0) - keep.Count;
                if (gone == 0) continue;
                if (keep.Count == 0 && wholeFiles) File.Delete(file);
                else File.WriteAllText(file, keep.Count == 0 ? "" : string.Join('\n', keep) + "\n");
                removed += gone;
            }
            catch (IOException) { /* in use: the next sweep */ }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    public static string TimeOf(string line)
    {
        var tab = line.IndexOf('\t');
        return tab < 0 ? line : line[..tab];
    }

    /// <summary>Reads a file another process (or this one) may be appending to.</summary>
    private static string[] ReadShared(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n").Split('\n');
    }
}

/// <summary>The logs kept as files.</summary>
public static class FileLogs
{
    public const string DiagnosticsFolder = "logs";

    public static readonly FileLogSource Diagnostics = new(
        "diagnostics", "Diagnostics", "Diagnostics",
        "What the app itself reported while it ran: start-up, cloning and syncing, background work, and errors. For finding out why something went wrong.",
        [new("at", "Time", "", "time"), new("origin", "From", "", Filter: true), new("text", "Message", "")],
        dataDir =>
        {
            var folder = Path.Combine(dataDir, DiagnosticsFolder);
            return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.log").Order(StringComparer.Ordinal).ToArray() : [];
        },
        14, "the app's own messages from those days. Nothing the app shows depends on them.", memberInText: true, wholeFiles: true);

    public static readonly FileLogSource CloudLink = new(
        "cloudlink", "Cloud Link", "Accountability",
        "Each change a Cloud Link viewer made, and each call refused: when, who (their sign-in), which call, and the outcome.",
        [new("at", "Time", "", "time"), new("user", "Viewer", "", Filter: true), new("method", "Call", "", Filter: true), new("outcome", "Outcome", "")],
        dataDir =>
        {
            var file = Path.Combine(dataDir, "cloud-link-audit.log");
            return File.Exists(file) ? [file] : [];
        },
        365, "the record of what Cloud Link viewers changed and were refused before the cutoff.", memberInText: false, wholeFiles: false);

    public static readonly IReadOnlyList<FileLogSource> All = [Diagnostics, CloudLink];

    public static FileLogSource? Find(string id) => All.FirstOrDefault(s => s.Id == id);
}

/// <summary>
/// The app's diagnostic log: each line it writes to standard error is also appended, with its time and origin, to
/// &lt;data&gt;/logs/&lt;origin&gt;-&lt;yyyy-MM-dd&gt;.log (one file a day). Writes never throw.
/// </summary>
public sealed class DiagnosticLog(string dataDir, string origin, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _lock = new();

    public string Folder => Path.Combine(dataDir, FileLogs.DiagnosticsFolder);

    public void Write(string text)
    {
        var now = _clock.GetUtcNow();
        var at = now.ToString("O", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            sb.Append(at).Append('\t').Append(origin).Append('\t').Append(line.Replace('\t', ' ')).Append('\n');
        }
        if (sb.Length == 0) return;
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, $"{origin}-{now:yyyy-MM-dd}.log"), sb.ToString());
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A writer that passes everything to <paramref name="inner"/> and records each finished line here.</summary>
    public TextWriter Tee(TextWriter inner) => new TeeWriter(inner, this);

    private sealed class TeeWriter(TextWriter inner, DiagnosticLog log) : TextWriter
    {
        private readonly StringBuilder _line = new();
        private readonly Lock _lock = new();

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            inner.Write(value);
            lock (_lock)
            {
                if (value == '\n') Flush(true);
                else if (value != '\r') _line.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            inner.Write(value);
            lock (_lock)
            {
                foreach (var c in value)
                {
                    if (c == '\n') Flush(true);
                    else if (c != '\r') _line.Append(c);
                }
            }
        }

        public override void WriteLine(string? value)
        {
            inner.WriteLine(value);
            lock (_lock)
            {
                _line.Append(value);
                Flush(true);
            }
        }

        public override void Flush() => inner.Flush();

        private void Flush(bool _)
        {
            if (_line.Length == 0) return;
            log.Write(_line.ToString());
            _line.Clear();
        }
    }
}
