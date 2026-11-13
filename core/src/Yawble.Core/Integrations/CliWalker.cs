using System.Collections.Concurrent;
using System.Diagnostics;

namespace Yawble.Core.Integrations;

/// <summary>The limits of a walk (the count is 4000 so az fits). One record, overridable in tests.</summary>
public sealed record WalkOptions
{
    public int MaxDepth { get; init; } = 4;
    public int MaxCommands { get; init; } = 4000;
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan TotalTime { get; init; } = TimeSpan.FromMinutes(5);
    public int Parallelism { get; init; } = 4;
}

public sealed record WalkProgress(int Found, int Walked, string Current, TimeSpan Elapsed);

/// <summary>A command found by the walk. Help is the first line plus a hash; the help body is not kept.</summary>
public sealed record WalkedCommand(string Path, string ParentPath, string Summary, bool HasChildren, string? HelpHash, bool HelpUnreadable);

public sealed record WalkResult(IReadOnlyList<WalkedCommand> Commands, bool Partial, string? PartialReason, string? HelpFlag, string? Error)
{
    public static WalkResult Failed(string error) => new([], false, null, null, error);
}

/// <summary>Runs "&lt;cli&gt; &lt;words&gt; &lt;help flag&gt;" and returns what it printed. The real one is CliWalker.ForExecutable.</summary>
public delegate Task<CliOutput> HelpRunner(IReadOnlyList<string> args, CancellationToken ct);

/// <summary>
/// Walks a CLI's help into a tree of commands and never runs a real command. Every call it makes is the
/// words of a command path taken from a parsed help list (^[a-z0-9][a-z0-9-]*$) followed by the help flag as the last
/// argument; anything else throws before a process starts. A path the credential floor hits is recorded, not walked.
/// </summary>
public static class CliWalker
{
    /// <summary>CLIs whose "--help" opens a browser or man page on some systems: only these flags are ever tried.</summary>
    private static readonly Dictionary<string, string[]> FlagsByCli = new(StringComparer.OrdinalIgnoreCase)
    {
        ["git"] = ["-h"],
    };

    private static readonly IReadOnlyDictionary<string, string> NonInteractiveEnv = new Dictionary<string, string>
    {
        ["CI"] = "1", ["NO_COLOR"] = "1", ["TERM"] = "dumb", ["PAGER"] = "cat", ["GIT_PAGER"] = "cat", ["MANPAGER"] = "cat",
        ["GIT_TERMINAL_PROMPT"] = "0", ["BROWSER"] = "",
    };

    /// <summary>A runner for a real program: no shell, stdin closed, an empty temp folder as the working directory, the non-interactive environment.</summary>
    public static HelpRunner ForExecutable(string path, CliEntry? entry, string emptyCwd, TimeSpan callTimeout)
    {
        var env = new Dictionary<string, string>(NonInteractiveEnv);
        foreach (var (k, v) in entry?.Env ?? new Dictionary<string, string>()) env[k] = v;
        return (args, ct) => CliRunner.RunAsync(path, args, emptyCwd, env, callTimeout, ct);
    }

    public static async Task<WalkResult> WalkAsync(string cli, CliEntry? entry, HelpRunner run, WalkOptions options, IProgress<WalkProgress>? progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(options.TotalTime);
        string? partial = null;
        void MarkPartial(string why) => Interlocked.CompareExchange(ref partial, why, null);

        // the help flag, once: a known entry says; git only ever -h; any other command --help only, never -h (Ruling 11: "shutdown -h")
        var flags = entry is not null && !FlagsByCli.ContainsKey(cli) ? [entry.HelpFlag] : FlagsByCli.TryGetValue(cli, out var f) ? f : new[] { "--help" };
        string? helpFlag = null;
        CliOutput? rootOut = null;
        foreach (var flag in flags)
        {
            try
            {
                var o = await Call(run, [], flag, limit.Token);
                if (Readable(o)) { helpFlag = flag; rootOut = o; break; }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        if (helpFlag is null || rootOut is null) return WalkResult.Failed("could not read its help");

        var found = new ConcurrentDictionary<string, WalkedCommand>(StringComparer.Ordinal);
        var hashes = new ConcurrentDictionary<string, string>(StringComparer.Ordinal); // path -> help hash, for the cycle guard
        var order = new ConcurrentQueue<string>();
        var walked = 0;
        var count = 0;

        // one node: record its children, return the ones to walk next
        List<(string Path, string[] Words, string Hash)> Expand(string[] words, string path, CliOutput o)
        {
            var text = Text(o);
            var hash = HelpParser.Hash(text);
            hashes[path] = hash;
            var next = new List<(string, string[], string)>();
            foreach (var child in HelpParser.Parse(text).Commands)
            {
                if (!HelpParser.WordRegex().IsMatch(child.Name)) continue;
                var childWords = words.Append(child.Name).ToArray();
                var childPath = string.Join(' ', childWords);
                if (childWords.Length > options.MaxDepth) continue;
                if (found.ContainsKey(childPath)) continue; // once per path
                if (Interlocked.Increment(ref count) > options.MaxCommands) { Interlocked.Decrement(ref count); MarkPartial($"more than {options.MaxCommands} commands: the list is partial"); continue; }
                var locked = CredentialFloor.Check(childWords) is not null;
                found[childPath] = new WalkedCommand(childPath, path, child.Description, false, null, false);
                order.Enqueue(childPath);
                if (!locked && childWords.Length < options.MaxDepth) next.Add((childPath, childWords, hash));
            }
            return next;
        }

        var level = Expand([], "", rootOut);
        var depthGuard = 0;
        while (level.Count > 0 && depthGuard++ < options.MaxDepth + 1)
        {
            var nextLevel = new ConcurrentBag<(string Path, string[] Words, string Hash)>();
            var gate = new SemaphoreSlim(Math.Max(1, options.Parallelism));
            var tasks = level.Select(async node =>
            {
                await gate.WaitAsync(limit.Token).ConfigureAwait(false);
                try
                {
                    if (limit.IsCancellationRequested) return;
                    CliOutput o;
                    try { o = await Call(run, node.Words, helpFlag, limit.Token); }
                    catch (OperationCanceledException) { if (ct.IsCancellationRequested) throw; MarkPartial("the walk's time ran out: the list is partial"); return; }
                    var n = Interlocked.Increment(ref walked);
                    progress?.Report(new WalkProgress(found.Count, n, node.Path, watch.Elapsed));
                    var prev = found[node.Path];
                    if (!Readable(o))
                    {
                        found[node.Path] = prev with { HelpUnreadable = true };
                        return;
                    }
                    var hash = HelpParser.Hash(Text(o));
                    // a help that is the same as an ancestor's is a cycle (or a CLI that prints its top help for anything): a leaf
                    if (AncestorHasHash(node.Path, hashes, hash))
                    {
                        found[node.Path] = prev with { HelpHash = hash };
                        return;
                    }
                    var kids = Expand(node.Words, node.Path, o);
                    found[node.Path] = found[node.Path] with { HelpHash = hash, Summary = prev.Summary.Length > 0 ? prev.Summary : HelpParser.Parse(Text(o)).Summary };
                    foreach (var k in kids) nextLevel.Add(k);
                }
                finally { gate.Release(); }
            }).ToList();
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { MarkPartial("the walk's time ran out: the list is partial"); }
            if (limit.IsCancellationRequested && !ct.IsCancellationRequested) MarkPartial("the walk's time ran out: the list is partial");
            ct.ThrowIfCancellationRequested();
            level = nextLevel.OrderBy(n => n.Path, StringComparer.Ordinal).ToList();
        }
        // nodes whose help was never fetched (a limit stopped the walk) are partial info: HasChildren stays false
        var parents = found.Values.Select(c => c.ParentPath).ToHashSet(StringComparer.Ordinal);
        var all = order.Select(p => found[p]).Select(c => c with { HasChildren = parents.Contains(c.Path) }).ToList();
        if (partial is null && all.Any(c => c.HelpHash is null && !c.HelpUnreadable && c.Path.Split(' ').Length < options.MaxDepth && CredentialFloor.Check(c.Path.Split(' ')) is null))
            MarkPartial("some commands' help was not read: the list is partial");
        return new WalkResult(all, partial is not null, partial, helpFlag, null);
    }

    private static bool AncestorHasHash(string path, ConcurrentDictionary<string, string> hashes, string hash)
    {
        var words = path.Split(' ');
        for (var n = 0; n < words.Length; n++)
        {
            var ancestor = string.Join(' ', words.Take(n));
            if (hashes.TryGetValue(ancestor, out var h) && h == hash) return true;
        }
        return false;
    }

    /// <summary>The guard: the arguments are path words from a help list, then the help flag last. Nothing else is ever run.</summary>
    public static IReadOnlyList<string> GuardedArgs(IReadOnlyList<string> words, string helpFlag)
    {
        if (helpFlag is not ("--help" or "-h" or "help" or "--usage")) throw new InvalidOperationException($"'{helpFlag}' is not a help flag");
        foreach (var w in words)
            if (!HelpParser.WordRegex().IsMatch(w)) throw new InvalidOperationException($"'{w}' is not a command word");
        if (CredentialFloor.Check(words) is not null) throw new InvalidOperationException("a credential path is never walked");
        return [.. words, helpFlag];
    }

    private static Task<CliOutput> Call(HelpRunner run, IReadOnlyList<string> words, string helpFlag, CancellationToken ct) =>
        run(GuardedArgs(words, helpFlag), ct);

    private static string Text(CliOutput o) => o.Stdout.Trim().Length > 0 ? o.Stdout : o.Stderr;

    /// <summary>Help when it printed something: lines of text, not cut by a time-out. A non-zero exit with output is still help (many CLIs do that).</summary>
    private static bool Readable(CliOutput o) => !o.TimedOut && !o.Truncated && Text(o).Split('\n').Count(l => l.Trim().Length > 0) >= 2;
}
