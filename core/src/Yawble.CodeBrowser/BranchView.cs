using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Yawble.CodeBrowser;

/// <summary>What a git command printed, and its exit code.</summary>
public sealed record GitReadResult(int ExitCode, string Stdout);

/// <summary>
/// Runs the host's hardened git (the core's HostGit) in a HOST-OWNED repository: the team repository or its shared view.
/// Never a member's clone: <see cref="BranchView"/> only ever passes those repositories as the working directory.
/// </summary>
public interface IGitReader
{
    Task<GitReadResult> RunAsync(string repository, IReadOnlyList<string> args, CancellationToken ct);
}

/// <summary>Where a member's branch started: the host-owned repositories to read from (first that answers), and the branch names.</summary>
public sealed record BranchSource(IReadOnlyList<string> Repositories, string BaseBranch, string? MemberBranch);

/// <summary>
/// "Changed on the branch": the files of the clone that differ from the commit the member's branch started from, and
/// their changed lines. The base is read from the team repository (or the shared view), never from the clone, with
/// read-only plumbing only (rev-parse, merge-base, ls-tree, cat-file), the repository's git folder named outright
/// (--git-dir: git never searches upward or anywhere else) and <see cref="Hardening"/> before every command;
/// the clone's files are compared by their git blob hash, computed here in process.
/// </summary>
public sealed class BranchView
{
    /// <summary>
    /// Put before every command (after the explicit --git-dir, so git never searches for a repository): a repository's
    /// own config must not make a read run a program, and replace refs must not change what an object id reads as.
    /// </summary>
    public static readonly IReadOnlyList<string> Hardening =
        ["--no-replace-objects", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"), "-c", "core.untrackedCache=false", "--no-optional-locks"];

    private static readonly Regex SafeRef = new(@"^(?!-)(?!.*\.\.)[A-Za-z0-9._/\-]+$", RegexOptions.CultureInvariant);

    /// <summary>Files over this are not hashed (they count as changed when they are not in the base, or their size is new).</summary>
    public const long MaxHashBytes = 20L * 1024 * 1024;

    private readonly IGitReader _git;
    private readonly SafeTree _tree;
    private readonly BranchSource _source;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (long Size, long Mtime, string Hash)> _hashes = new(StringComparer.Ordinal);
    private Dictionary<string, string>? _base; // path -> blob id at the base commit
    private HashSet<string> _changed = new(StringComparer.Ordinal);
    private string? _repository;
    private string? _gitDir;
    private Task? _refresh;

    public BranchView(IGitReader git, SafeTree tree, BranchSource source)
    {
        _git = git;
        _tree = tree;
        _source = source;
    }

    /// <summary>The base commit, once read (null before, or when no repository answered).</summary>
    public string? BaseCommit { get; private set; }

    /// <summary>Why the branch view has nothing, when it has nothing (no repository, no base branch).</summary>
    public string? Problem { get; private set; }

    /// <summary>Starts (or joins) reading the base and comparing the clone with it.</summary>
    public Task RefreshAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_refresh is { IsCompleted: false }) return _refresh;
            return _refresh = Task.Run(() => DoRefreshAsync(ct), ct);
        }
    }

    /// <summary>Waits for the running refresh, at most <paramref name="wait"/>.</summary>
    public async Task WaitAsync(TimeSpan wait)
    {
        Task? r;
        lock (_lock) r = _refresh;
        if (r is null) return;
        try { await r.WaitAsync(wait).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (Exception e) when (e is OperationCanceledException or IOException) { }
    }

    private async Task DoRefreshAsync(CancellationToken ct)
    {
        if (!SafeRef.IsMatch(_source.BaseBranch) || _source.MemberBranch is { } mb && !SafeRef.IsMatch(mb))
        {
            Problem = "the branch names cannot be read safely";
            return;
        }
        foreach (var repo in _source.Repositories)
        {
            if (!Directory.Exists(repo) || SafeTree.IsUnder(Path.GetFullPath(repo), _tree.Root)) continue;
            if (GitDirOf(repo) is not { } gitDir || SafeTree.IsUnder(gitDir, _tree.Root)) continue;
            var baseTip = await RevAsync(repo, gitDir, "refs/heads/" + _source.BaseBranch, ct).ConfigureAwait(false);
            if (baseTip is null) continue;
            var start = baseTip;
            if (_source.MemberBranch is { } branch && await RevAsync(repo, gitDir, "refs/heads/" + branch, ct).ConfigureAwait(false) is { } tip)
            {
                var mbase = await _git.RunAsync(repo, Args(gitDir, "merge-base", tip, baseTip), ct).ConfigureAwait(false);
                if (mbase.ExitCode == 0 && IsObjectId(mbase.Stdout.Trim())) start = mbase.Stdout.Trim();
            }
            var tree = await _git.RunAsync(repo, Args(gitDir, "ls-tree", "-r", "-z", "--full-tree", start), ct).ConfigureAwait(false);
            if (tree.ExitCode != 0) continue;
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in tree.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                // "<mode> <type> <id>\t<path>"
                var tab = item.IndexOf('\t');
                if (tab < 0) continue;
                var meta = item[..tab].Trim('\n', '\r').Split(' ');
                if (meta.Length != 3 || meta[1] != "blob" || meta[0] == "120000") continue;
                files[item[(tab + 1)..]] = meta[2];
            }
            lock (_lock)
            {
                _repository = repo;
                _gitDir = gitDir;
                _base = files;
                BaseCommit = start;
                Problem = null;
            }
            Recompute();
            return;
        }
        Problem = "no team repository has the base branch";
    }

    /// <summary>The command line: the repository named outright (never searched for), the hardening, the command.</summary>
    private static string[] Args(string gitDir, params string[] command) => ["--git-dir=" + gitDir, .. Hardening, .. command];

    /// <summary>
    /// The git folder of a host-owned repository: its .git folder (or the folder a .git file names), or the repository
    /// itself when it is bare. Null when it is neither.
    /// </summary>
    internal static string? GitDirOf(string repo)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo));
            var dotGit = Path.Combine(full, ".git");
            if (Directory.Exists(dotGit)) return dotGit;
            if (File.Exists(dotGit))
            {
                var line = File.ReadLines(dotGit).FirstOrDefault()?.Trim();
                if (line is not null && line.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    var target = Path.GetFullPath(Path.Combine(full, line["gitdir:".Length..].Trim()));
                    return Directory.Exists(target) ? Path.TrimEndingDirectorySeparator(target) : null;
                }
                return null;
            }
            return File.Exists(Path.Combine(full, "HEAD")) && Directory.Exists(Path.Combine(full, "objects")) ? full : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<string?> RevAsync(string repo, string gitDir, string reference, CancellationToken ct)
    {
        var r = await _git.RunAsync(repo, Args(gitDir, "rev-parse", "--verify", "-q", reference + "^{commit}"), ct).ConfigureAwait(false);
        var sha = r.Stdout.Trim();
        return r.ExitCode == 0 && IsObjectId(sha) ? sha : null;
    }

    private static bool IsObjectId(string s) => s.Length is 40 or 64 && s.All(Uri.IsHexDigit);

    /// <summary>Compares every file of the clone with the base (by blob hash; cached by size and time).</summary>
    private void Recompute()
    {
        Dictionary<string, string>? b;
        lock (_lock) b = _base;
        if (b is null) return;
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rel, info) in _tree.WalkFiles())
            if (Differs(rel, info, b)) changed.Add(rel);
        lock (_lock) _changed = changed;
    }

    /// <summary>Re-checks one file after it changed.</summary>
    public void Touch(string path)
    {
        Dictionary<string, string>? b;
        lock (_lock) b = _base;
        if (b is null || SafeTree.IsNoise(path)) return;
        var info = new FileInfo(_tree.Full(path));
        var differs = info.Exists && !SafeTree.IsLink(info) && !_tree.IsHidden(path) && Differs(path, info, b);
        lock (_lock)
        {
            if (differs) _changed.Add(path);
            else _changed.Remove(path);
        }
    }

    private bool Differs(string rel, FileInfo info, Dictionary<string, string> b)
    {
        if (!b.TryGetValue(rel, out var blob)) return true;
        long size, mtime;
        try { size = info.Length; mtime = info.LastWriteTimeUtc.Ticks; }
        catch (IOException) { return true; }
        if (size > MaxHashBytes) return true;
        lock (_lock)
        {
            if (_hashes.TryGetValue(rel, out var h) && h.Size == size && h.Mtime == mtime) return h.Hash != blob;
        }
        string hash;
        try
        {
            var (stream, _) = _tree.OpenFile(rel);
            using (stream)
            {
                var (bytes, _) = FileKinds.ReadBounded(stream, MaxHashBytes);
                if (bytes is null) return true;
                hash = BlobId(bytes, blob.Length == 64);
                // A file checked out with CRLF line ends (core.autocrlf) is stored with LF: compare that too.
                if (hash != blob && bytes.AsSpan().IndexOf((byte)'\r') >= 0)
                {
                    var lf = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(bytes).Replace("\r\n", "\n"));
                    if (BlobId(lf, blob.Length == 64) == blob) hash = blob;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CodeBrowserException)
        {
            return true;
        }
        lock (_lock) _hashes[rel] = (size, mtime, hash);
        return hash != blob;
    }

    /// <summary>git's object id of a blob: sha1 (or sha256) of "blob &lt;size&gt;\0" and the bytes.</summary>
    public static string BlobId(ReadOnlySpan<byte> content, bool sha256 = false)
    {
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        using var h = IncrementalHash.CreateHash(sha256 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
        h.AppendData(header);
        h.AppendData(content);
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }

    /// <summary>Whether a file (or, for a folder, anything inside it) is changed on the branch.</summary>
    public bool IsChanged(string path, bool folder)
    {
        if (SafeTree.IsNoise(path)) return false;
        lock (_lock)
        {
            if (!folder) return _changed.Contains(path);
            var prefix = path.Length == 0 ? "" : path + "/";
            foreach (var p in _changed)
                if (p.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    public IReadOnlyCollection<string> ChangedPaths()
    {
        lock (_lock) return [.. _changed];
    }

    /// <summary>The lines of a file as it is now that differ from the base (a new file: all of them). Empty when not changed or not known.</summary>
    public async Task<IReadOnlyList<(int From, int To)>> LinesAsync(string path, string[] current, CancellationToken ct)
    {
        string? repo, gitDir, blob;
        lock (_lock)
        {
            if (!_changed.Contains(path) || _base is null) return [];
            repo = _repository;
            gitDir = _gitDir;
            blob = _base.GetValueOrDefault(path);
        }
        if (blob is null) return current.Length == 0 ? [] : [(1, current.Length)];
        if (repo is null || gitDir is null) return [];
        var size = await _git.RunAsync(repo, Args(gitDir, "cat-file", "-s", blob), ct).ConfigureAwait(false);
        if (size.ExitCode != 0 || !long.TryParse(size.Stdout.Trim(), out var n) || n > FileKinds.MaxPreviewBytes) return [];
        var content = await _git.RunAsync(repo, Args(gitDir, "cat-file", "blob", blob), ct).ConfigureAwait(false);
        if (content.ExitCode != 0 || content.Stdout.Contains('\0')) return [];
        var old = FileKinds.Lines(content.Stdout);
        return LineDiff.Compute(old, current).Where(h => h.NewCount > 0).Select(h => (h.NewStart + 1, h.NewStart + h.NewCount)).ToList();
    }
}
