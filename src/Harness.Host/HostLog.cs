using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Harness.Host;

/// <summary>
/// How much room the host log may take, and how much of it one segment may take.
///
/// <para>
/// <b>BOUNDED, AND THE BOUND IS NOT A CONSTANT.</b> There are exactly two ways to a
/// number here and neither is a figure tuned to this machine:
/// </para>
///
/// <list type="number">
///   <item>
///     <b>DECLARED.</b> <c>HostLog:Budget</c> - an operator names a size outright and it wins. It is
///     deliberately absent from <c>appsettings.json</c>: a value shipped in the repository IS the
///     constant this rule refuses, and it would follow every clone onto every machine.
///   </item>
///   <item>
///     <b>DERIVED.</b> Otherwise, <see cref="Share"/> - one hundredth of what is actually free on
///     the volume the log lands on, read at start-up.
///   </item>
/// </list>
///
/// <para>
/// <b>IT DEGRADES ON A SMALL HOST RATHER THAN REFUSING.</b> A hundredth of a nearly-full disk is a
/// small number, and a small number is a correct answer: the host keeps fewer runs and carries on.
/// The only thing that never happens is "there is not enough room, so nothing is captured" - a
/// nearly-full machine is exactly the one whose output is most needed.
/// </para>
///
/// <para>
/// <see cref="Floor"/> and <see cref="Ceiling"/> bound the DERIVATION; they are not the bound. The
/// floor is there because a hundredth of a very full disk can round to nothing, and a log too small
/// to hold one run's start-up lines answers no question at all. The ceiling is there because a
/// hundredth of a 2 TB disk is twenty gigabytes of text nobody will ever read - more bytes is not
/// more evidence. A DECLARED size is not clamped, because an operator who names a number has
/// answered the question this class is guessing at.
/// </para>
/// </summary>
/// <param name="TotalBytes">What every host log in the directory may occupy between them.</param>
/// <param name="SegmentBytes">What one file may reach before the next one is started.</param>
public readonly record struct HostLogBudget(long TotalBytes, long SegmentBytes)
{
    /// <summary>One hundredth of the free space. See the type's own comment for why a fraction.</summary>
    public const long Share = 100;

    /// <summary>Enough for one run's start-up lines and a stack trace, and not chosen for a disk size.</summary>
    public const long Floor = 1L * 1024 * 1024;

    /// <summary>Past here it is not evidence, it is a file nobody opens.</summary>
    public const long Ceiling = 256L * 1024 * 1024;

    /// <summary>
    /// How many files the budget is cut into. More than one because a host that runs all night -
    /// which is precisely the run whose log matters most - must not be truncated at the point
    /// it filled up and then keep running silently for six more hours. Rolling keeps the RECENT
    /// output and lets retention take the oldest.
    /// </summary>
    public const long Segments = 4;

    /// <summary>The smallest segment worth rolling; below this the rolling itself is the noise.</summary>
    public const long SmallestSegment = 64L * 1024;

    /// <summary>
    /// The declared size if one was given and could be read, otherwise a hundredth of what is free.
    /// </summary>
    /// <param name="declared">
    /// <c>HostLog:Budget</c> as configured, or null. Unparseable is treated as absent rather than
    /// refused: a typo in a size must not be the reason a host captures nothing.
    /// </param>
    /// <param name="freeBytes">
    /// Free space on the volume the log lands on. Zero or less means "could not be read", and the
    /// answer to not knowing how much room there is, is to keep the least.
    /// </param>
    public static HostLogBudget Resolve(string? declared, long freeBytes)
    {
        if (TryParseSize(declared, out var stated)) return Cut(stated);

        return Cut(freeBytes <= 0 ? Floor : Math.Clamp(freeBytes / Share, Floor, Ceiling));
    }

    private static HostLogBudget Cut(long total)
    {
        var segment = Math.Max(total / Segments, SmallestSegment);

        // A declared budget smaller than one segment is honoured as a single file rather than
        // rounded up: the operator's number is the ceiling, and quietly exceeding it by a factor of
        // sixteen because of an internal division is how a bound stops being one.
        return new HostLogBudget(total, Math.Min(segment, total));
    }

    /// <summary>
    /// A plain byte count, or one with a <c>KB</c>, <c>MB</c> or <c>GB</c> suffix, 1024-based.
    /// Whitespace either side of the unit is allowed because an operator typing "64 MB" means it.
    /// </summary>
    public static bool TryParseSize(string? text, out long bytes)
    {
        bytes = 0;

        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        var multiplier = 1L;

        foreach (var (suffix, scale) in Units)
        {
            if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            multiplier = scale;
            trimmed = trimmed[..^suffix.Length].TrimEnd();
            break;
        }

        if (!long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var count)) return false;
        if (count <= 0) return false;

        // A number so large it overflows is a typo, not a request for every byte on the machine.
        try
        {
            bytes = checked(count * multiplier);
        }
        catch (OverflowException)
        {
            return false;
        }

        return true;
    }

    /// <summary>Longest suffix first, so <c>KB</c> is never read as a bare <c>B</c>.</summary>
    private static readonly (string Suffix, long Scale)[] Units =
    [
        ("KB", 1024L),
        ("MB", 1024L * 1024),
        ("GB", 1024L * 1024 * 1024),
        ("B", 1L),
    ];
}

/// <summary>
/// The host's own stdout and stderr, on their way to the console AND to a file that outlives the
/// process.
///
/// <para>
/// <b>A diagnostics table cannot record the failure that stops the process writing to a
/// table.</b> Without this file the only host output on the machine is a terminal window - close it
/// and the evidence is gone, leave it and the scrollback is bounded - so an agent stopping with
/// "role skill did not load" would leave nothing that could say why.
/// </para>
///
/// <para>
/// <b>THE CONSOLE IS NOT REPLACED, IT IS COPIED.</b> Every write reaches the original writer FIRST
/// and the file second, so a terminal shows exactly what it shows today even when the file write is
/// failing. Taking the terminal away to gain a file would trade one lost audience for another.
/// </para>
///
/// <para>
/// <b>WRITING NEVER THROWS.</b> The inherited rule from <c>tenant_events</c> - "a failure to record
/// must not fail the act being recorded" - is harder here than anywhere else, because the act being
/// recorded is <c>Console.WriteLine</c> and the call sites are the whole codebase. A sink that can
/// throw from there would take the host down from a line nobody thinks of as risky. Every path in
/// this class ends in a catch; the first failure closes the file, says so once ON THE CONSOLE (the
/// original writer, so the message cannot recurse into the failure), and goes quiet for good.
/// </para>
///
/// <para>
/// <b>TWO PHASES.</b> <see cref="Tee"/> runs as the host's first statement, before the data root
/// is even known, and holds what is printed in memory. <see cref="OpenIn"/> runs once the root
/// exists and replays it, so the lines that answer "what was this instance configured as" are not
/// lost to that ordering.
/// </para>
///
/// <para>
/// <b>NO NEW SECRET EXPOSURE.</b> This captures exactly what the host already prints to a terminal
/// any passer-by can read, and it lands inside the data root beside <c>messages.db</c>, which holds
/// the accounts and the credentials. Redaction belongs at the WRITE, and the write that matters is the
/// one that put the text on the console in the first place.
/// </para>
/// </summary>
public sealed class HostLog : IDisposable
{
    /// <summary>
    /// What is held in memory before the data root is known. Bounded for the obvious reason: a host
    /// that never reaches <see cref="OpenIn"/> - a bad instance name, an operator command - must not
    /// grow a buffer nobody will ever read. Generous next to the dozen or so lines that fit here.
    /// </summary>
    public const int PreambleBytes = 64 * 1024;

    /// <summary>The directory the log lands in, under the data root. Named so a person can guess it.</summary>
    public const string DirectoryName = "logs";

    /// <summary>
    /// AT MOST ONE TEE PER PROCESS, and this is not a tidiness rule.
    ///
    /// <para>
    /// <c>Console.Out</c> is process-wide, so a second <see cref="Tee"/> would WRAP THE FIRST rather
    /// than replace it, and a tenth would put ten locked, flushed file writes behind every single
    /// <c>Console.WriteLine</c> in the process. That is not hypothetical: the host suites boot the
    /// host IN PROCESS, a dozen at a time, and a tee per test host shows up as unrelated tests
    /// timing out and a member "reporting every 500ms" being stopped by a 2s silence limit. The cost of a diagnostic must not be paid by the thing it
    /// is diagnosing.
    /// </para>
    /// </summary>
    private static readonly object Installation = new();

    private static HostLog? Installed;

    private readonly object _gate = new();
    private readonly StringBuilder _preamble = new();

    private TextWriter? _console;
    private string? _directory;
    private FileStream? _stream;
    private HostLogBudget _budget;
    private long _written;
    private int _segment;
    private bool _dropped;
    private bool _off;

    private HostLog()
    {
    }

    /// <summary>
    /// A log that is not attached to anything yet - it writes what it is GIVEN, and reports its own
    /// failure to <paramref name="console"/>.
    ///
    /// <para>
    /// Separate from <see cref="Tee"/> because CONSTRUCTING one and INSTALLING one over
    /// <c>Console.Out</c> are different acts with different blast radii, and only the second is
    /// process-wide. It is public for the reason the rest of this surface is: there is no
    /// <c>InternalsVisibleTo</c> from <c>Harness.Host</c> to its test projects, and a test that
    /// replaced the runner's own console to reach this would be racing every other test in a suite
    /// that runs collections in parallel.
    /// </para>
    /// </summary>
    public static HostLog Create(TextWriter? console = null) => new() { _console = console };

    /// <summary>The file being written, once there is one. Null while buffering, and after a failure.</summary>
    public string? Path { get; private set; }

    /// <summary>What the log may occupy in total, once <see cref="OpenIn"/> has resolved it.</summary>
    public HostLogBudget Budget => _budget;

    /// <summary>
    /// Copy <c>Console.Out</c> and <c>Console.Error</c> into this log. Call it as the host's FIRST
    /// statement: anything printed before this reaches the terminal and nowhere else, and the
    /// lines printed first are the configuration facts worth the most on a cold start.
    ///
    /// <para>
    /// Both streams land in ONE file, interleaved in the order they were written, because that is
    /// what the terminal showed and re-deriving the order from two files is exactly the work this
    /// is meant to save.
    /// </para>
    /// </summary>
    public static HostLog Tee()
    {
        lock (Installation)
        {
            if (Installed is not null) return Installed;

            // THE STDOUT BEING CAPTURED HAS TO ACTUALLY BE THE HOST'S. When this assembly is not the
            // one that started the process, the console belongs to somebody else - the test runner,
            // in every case that exists today - and `Program`'s top-level statements are being run
            // IN PROCESS, a dozen hosts at a time, none of which owns that console. Taking it would
            // divert a runner's own output into some test's temp folder and put a locked, flushed
            // file write behind every line it prints.
            //
            // This is not a test exemption wearing a rule's clothes; "the host's own stdout" is what
            // is captured, and an in-process host does not have one.
            if (Assembly.GetEntryAssembly() != typeof(HostLog).Assembly)
            {
                var quiet = Create();
                quiet._off = true;

                return Installed = quiet;
            }

            var log = Create(Console.Out);

            try
            {
                Console.SetOut(new HostLogTee(Console.Out, log));
                Console.SetError(new HostLogTee(Console.Error, log));
            }
            catch (Exception exception)
            {
                log.Disable(exception);
            }

            return Installed = log;
        }
    }

    /// <summary>
    /// Start writing under <paramref name="dataRoot"/>, replay what was printed before now, and say
    /// where it went. Call it AFTER the data root has been created and before anything that can
    /// fail - an operator command, a migration - so those failures are in the file.
    /// </summary>
    /// <param name="dataRoot">The instance's own folder. One log directory per instance, like everything else here.</param>
    /// <param name="declaredBudget"><c>HostLog:Budget</c>, or null to derive one from free space.</param>
    public void OpenIn(string dataRoot, string? declaredBudget)
    {
        lock (_gate)
        {
            if (_off) return;

            var directory = System.IO.Path.Combine(dataRoot, DirectoryName);

            if (_stream is not null)
            {
                // Already writing where it was asked to. The ordinary case is one host and one call.
                if (string.Equals(_directory, directory, StringComparison.OrdinalIgnoreCase)) return;

                // A SECOND DATA ROOT IN ONE PROCESS - which only the in-process test hosts do, and
                // they do it a dozen at a time. Follow the newest rather than keeping a handle on a
                // root somebody is about to delete. Nothing is lost by moving: every write is
                // already flushed.
                try
                {
                    _stream.Flush();
                    _stream.Dispose();
                }
                catch (Exception)
                {
                    // The old root is going away regardless.
                }

                _stream = null;
                _segment = 0;
            }

            try
            {
                _directory = directory;
                Directory.CreateDirectory(_directory);
                _budget = HostLogBudget.Resolve(declaredBudget, FreeBytes(_directory));

                OpenSegment();

                if (_preamble.Length > 0)
                {
                    var buffered = _preamble.ToString();
                    _preamble.Clear();
                    Append(buffered);
                }

                if (_dropped)
                {
                    Append(
                        $"(The host printed more than {PreambleBytes} bytes before its log was open; "
                        + "the overflow reached the console only.)"
                        + Environment.NewLine);
                }

                Prune();
            }
            catch (Exception exception)
            {
                Disable(exception);
            }
        }

        // OUTSIDE the lock, and to both places by hand rather than through `Console` - which for a
        // log that was never installed over the console would put the line somewhere this log
        // cannot see. A log nobody can find is one nobody reads, and the file is the one thing here
        // worth never guessing at.
        if (Path is null) return;

        var said = $"Host log: {Path}" + Environment.NewLine;

        try
        {
            _console?.Write(said);
        }
        catch (Exception)
        {
            // No console. The file below is the half that matters.
        }

        Write(said);
    }

    /// <summary>
    /// Take a piece of console output. Called from every <c>Console</c> write in the process, so it
    /// returns quietly on every failure and never throws.
    /// </summary>
    public void Write(string text)
    {
        if (text.Length == 0) return;

        lock (_gate)
        {
            if (_off) return;

            if (_stream is null)
            {
                if (_preamble.Length + text.Length > PreambleBytes)
                {
                    _dropped = true;
                    return;
                }

                _preamble.Append(text);
                return;
            }

            try
            {
                Append(text);
            }
            catch (Exception exception)
            {
                Disable(exception);
            }
        }
    }

    /// <summary>Flush what is held. Every write already flushes; this is for an explicit console flush.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_off || _stream is null) return;

            try
            {
                _stream.Flush();
            }
            catch (Exception exception)
            {
                Disable(exception);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _stream?.Flush();
                _stream?.Dispose();
            }
            catch (Exception)
            {
                // Closing a file is the last thing this class does and there is nobody left to tell.
            }

            _stream = null;
            _off = true;
        }
    }

    /// <summary>Caller holds the lock and handles the throw.</summary>
    private void Append(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        // Rolled BEFORE the write rather than after, so a segment is a ceiling rather than a
        // high-water mark. `_written > 0` keeps a single write larger than a whole segment in the
        // file it started in instead of opening an empty one for it and rolling again immediately.
        if (_written > 0 && _written + bytes.Length > _budget.SegmentBytes)
        {
            _stream!.Flush();
            _stream.Dispose();
            _stream = null;

            OpenSegment();
            Prune();
        }

        _stream!.Write(bytes);
        _written += bytes.Length;

        // FLUSHED ON EVERY WRITE, AND THIS LINE IS THE ACCEPTANCE CRITERION. "The host's stdout
        // survives the host" means survives a KILL, and a kill runs nothing: whatever is still in a
        // managed buffer at that moment was never written. The host's own stdout is start-up lines,
        // warnings and exceptions - tens of lines where the database does thousands of rows - so
        // the syscall per line costs nothing next to being able to answer the question at all.
        _stream.Flush();
    }

    /// <summary>Caller holds the lock and handles the throw.</summary>
    private void OpenSegment()
    {
        _segment++;

        // Stamped and stamped with the process id: one file per host RUN, which makes retention a
        // question about whole runs rather than about where inside a file to cut. The id also keeps
        // two hosts started in the same second - different instances, different data roots - from
        // ever choosing one name.
        // The segment number is always present and always padded, so ORDINAL NAME ORDER and time
        // order are the same thing. Omitting it from the first file looks tidier and sorts wrong:
        // `-02.log` precedes `.log` on any ordinal comparison, which would hand retention the
        // newest file when two segments share a second.
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var name = $"host-{stamp}-{Environment.ProcessId}-{_segment:D2}.log";

        Path = System.IO.Path.Combine(_directory!, name);

        // Append, never truncate, and shared for reading: somebody tailing the file while the host
        // runs is the whole point of it being a file, and a reader must never be able to make the
        // host's own write fail.
        _stream = new FileStream(
            Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        _written = _stream.Length;
    }

    /// <summary>
    /// Take the oldest files until the directory is inside its budget. Retention BY SIZE rather than
    /// by age: the question a host log answers is "what did this machine print", and how long ago
    /// that was is not what makes it expensive.
    ///
    /// <para>Caller holds the lock. Every failure here is survivable - an over-full log directory is
    /// a far better outcome than a host that could not start - so this reports nothing.</para>
    /// </summary>
    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(_directory!)
                .GetFiles("host-*.log")
                .OrderBy(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Name, StringComparer.Ordinal)
                .ToList();

            var total = files.Sum(file => file.Length);

            foreach (var file in files)
            {
                if (total <= _budget.TotalBytes) return;

                // NEVER THE FILE BEING WRITTEN. Deleting it would leave this run with nowhere to
                // land, which is how a bound turns into a silence.
                if (string.Equals(file.FullName, Path, StringComparison.OrdinalIgnoreCase)) continue;

                var size = file.Length;

                try
                {
                    file.Delete();
                    total -= size;
                }
                catch (Exception)
                {
                    // Another process holding it, or a permission this host does not have. Try the
                    // next one rather than giving up on the whole sweep.
                }
            }
        }
        catch (Exception)
        {
            // Retention is maintenance, not the feature. A directory that cannot be read leaves the
            // log over budget and still readable, and nothing else changes.
        }
    }

    /// <summary>
    /// Free space on the volume the log lands on. Zero when it cannot be read - and not knowing how
    /// much room there is resolves to keeping the least, never to keeping the most.
    /// </summary>
    private static long FreeBytes(string directory)
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(directory));

            return string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Stop, once and for good. Said out loud on the ORIGINAL console writer: routing it through
    /// <c>Console</c> would send the report of the failure back into the thing that just failed.
    /// </summary>
    private void Disable(Exception why)
    {
        _off = true;
        Path = null;

        try
        {
            _stream?.Dispose();
        }
        catch (Exception)
        {
            // Already failing. There is nothing a second failure here would add.
        }

        _stream = null;

        try
        {
            _console?.WriteLine(
                $"The host log stopped and this host is no longer capturing its own output: "
                + $"{why.GetType().Name}: {why.Message}");
        }
        catch (Exception)
        {
            // No console either. The host still serves, which is the only thing this must not break.
        }
    }
}

/// <summary>
/// One console stream, going to two places. The original writer is served FIRST on every path, so
/// the terminal is unaffected by anything the file does - including failing.
/// </summary>
internal sealed class HostLogTee(TextWriter console, HostLog log) : TextWriter
{
    public override Encoding Encoding => console.Encoding;

    public override IFormatProvider FormatProvider => console.FormatProvider;

    /// <summary>
    /// <c>[AllowNull]</c> because <see cref="TextWriter.NewLine"/> declares it that way: null means
    /// "back to the default", and an override that tightened it would not compile.
    /// </summary>
    [AllowNull]
    public override string NewLine
    {
        get => console.NewLine;
        set => console.NewLine = value;
    }

    public override void Write(char value)
    {
        console.Write(value);
        log.Write(value.ToString());
    }

    public override void Write(string? value)
    {
        console.Write(value);
        if (value is not null) log.Write(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        console.Write(buffer, index, count);
        log.Write(new string(buffer, index, count));
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        console.Write(buffer);
        log.Write(new string(buffer));
    }

    public override void WriteLine()
    {
        console.WriteLine();
        log.Write(NewLine);
    }

    public override void WriteLine(string? value)
    {
        console.WriteLine(value);
        log.Write((value ?? string.Empty) + NewLine);
    }

    public override void WriteLine(ReadOnlySpan<char> buffer)
    {
        console.WriteLine(buffer);
        log.Write(string.Concat(buffer, NewLine));
    }

    public override void Flush()
    {
        console.Flush();
        log.Flush();
    }
}
