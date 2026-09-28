using System.Diagnostics;
using System.Text;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// What a removal left: every path still on disk, or why it was refused outright. Complete only
/// when both are empty.
/// </summary>
public sealed record FolderRemovalReport(IReadOnlyList<string> Remaining, string? Refused = null)
{
    public static FolderRemovalReport Done { get; } = new([]);

    public bool Complete => Remaining.Count == 0 && Refused is null;
}

/// <summary>
/// THE ONE WAY A TEAM ROOT, A MEMBER'S WORKSPACE OR A FOLDER A RESET EMPTIES IS REMOVED. Shared by
/// <see cref="TeamDeletion"/>, <see cref="MemberDeletion"/> and <see cref="TeamReset"/>, and by
/// the retries of what they left.
///
/// <para>
/// WHY NOT <c>Directory.Delete(recursive: true)</c>. Agents run as <c>agent</c> and make
/// owner-only directories (<c>drwx------</c>); the Host runs as <c>harness</c> and cannot empty
/// them. A recursive delete removes as it walks, so a team root lost its <c>.harness-team</c>
/// marker early, then threw on the first directory it could not empty - and the next attempt
/// refused the folder as not the platform's. So:
/// </para>
/// <list type="number">
/// <item><b>The marker goes last.</b> A team root is emptied around its marker, then the marker is
/// removed, then the root. Anything left keeps the marker.</item>
/// <item><b>What the Host cannot remove is removed as the agent</b>, when the Host launches agents
/// as a separate user: <c>rm -rf --one-file-system</c>, on the entry of the folder that holds each
/// leftover (on the leftover itself when only named paths may go), through the same
/// <see cref="AgentLaunchUser.Prefix"/> every agent child gets, so it can do nothing the agent
/// could not already do, and adds no privilege. A path is handed to it only when it resolves
/// inside the folder being removed with no symbolic link on the way; <c>rm</c> removes a link
/// and never follows one. The Host then removes what is left (an emptied directory in a folder
/// it owns). When the Host does not switch users there is no second pass.</item>
/// <item><b>Symbolic links are removed as links</b> and never followed, by either pass.</item>
/// <item><b>What still remains is named, path by path, and recorded</b> in
/// <see cref="IUnfinishedRemovals"/>, so the Host retries it at start and a person can retry it
/// on request. A removal that finishes forgets its row.</item>
/// </list>
/// </summary>
public sealed class FolderRemoval(
    AgentLaunchUser? runAs = null,
    IUnfinishedRemovals? unfinished = null,
    FolderRemoval.HostDeletes? host = null)
{
    private readonly HostDeletes _host = host ?? HostDeletes.Real;

    /// <summary>
    /// How the Host itself removes one file or link, and one empty directory. A seam, so the suite
    /// can make the Host fail where the product's Host would (it cannot write inside an agent's
    /// owner-only directory) even when the suite runs as root.
    /// </summary>
    public sealed record HostDeletes(Action<string> DeleteFile, Action<string> DeleteEmptyDirectory)
    {
        public static HostDeletes Real { get; } = new(File.Delete, path => Directory.Delete(path, recursive: false));
    }

    /// <summary>The store rows are recorded in, when this was given one.</summary>
    public IUnfinishedRemovals? Unfinished => unfinished;

    /// <summary>
    /// Removes a team root: everything in it, then its marker, then the root. REFUSED when the
    /// root carries no marker (it is not a directory this platform created) or is itself a
    /// symbolic link. Whatever remains keeps the marker and is recorded.
    /// </summary>
    public async Task<FolderRemovalReport> RemoveTeamRootAsync(string root, string team, CancellationToken ct = default)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        if (!Exists(root))
        {
            await ForgetAsync(root, ct);
            return FolderRemovalReport.Done;
        }

        if (IsLink(root))
        {
            return new FolderRemovalReport([], $"{root} was left alone: it is a symbolic link, not a folder this platform created.");
        }

        var marker = TeamPaths.MarkerIn(root);

        if (!File.Exists(marker) || IsLink(marker))
        {
            return new FolderRemovalReport([],
                $"{root} was left alone: it carries no {TeamPaths.MarkerFileName} marker, so it is not a "
                + "directory this platform created. Remove it by hand if it is yours.");
        }

        var remaining = await PassesAsync(root, left => EmptyAsHost(root, marker, left), ct, keep: marker);

        if (remaining.Count == 0)
        {
            // LAST, and put back if the root cannot follow it: a folder without its marker is
            // refused by every later attempt as "not ours".
            var contents = await ReadQuietlyAsync(marker, ct);

            try
            {
                _host.DeleteFile(marker);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                remaining.Add(marker);
            }

            if (remaining.Count == 0)
            {
                try
                {
                    _host.DeleteEmptyDirectory(root);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Something arrived between the pass and here. The marker goes back first.
                    await RestoreMarkerAsync(marker, contents ?? $"{team}\n{DateTimeOffset.UtcNow:O}\n", ct);
                    remaining.AddRange(Entries(root, marker) is { Count: > 0 } arrived ? arrived : [root]);
                }
            }
        }

        return await SettleAsync(root, RemovalKinds.TeamRoot, team, null, remaining, ct);
    }

    /// <summary>Removes a member's workspace: everything in it, then the folder.</summary>
    public async Task<FolderRemovalReport> RemoveWorkspaceAsync(
        string workspace, string team, string member, CancellationToken ct = default)
    {
        workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));

        if (!Exists(workspace))
        {
            await ForgetAsync(workspace, ct);
            return FolderRemovalReport.Done;
        }

        List<string> remaining;

        if (IsLink(workspace))
        {
            // The link, never what it points at.
            remaining = [];
            RemoveAsHost(workspace, remaining);
        }
        else
        {
            remaining = await PassesAsync(workspace, left => RemoveAsHost(workspace, left), ct);
        }

        return await SettleAsync(workspace, RemovalKinds.Workspace, team, member, remaining, ct);
    }

    /// <summary>
    /// Empties a folder and keeps it - what a reset does to a workspace, a transcripts folder or
    /// the documents. What remains is recorded by path, and a retry removes only those paths.
    /// </summary>
    public async Task<FolderRemovalReport> EmptyAsync(string directory, string team, CancellationToken ct = default)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

        if (!Directory.Exists(directory) || IsLink(directory))
        {
            await ForgetAsync(directory, ct);
            return FolderRemovalReport.Done;
        }

        var remaining = await PassesAsync(directory, left => EmptyAsHost(directory, null, left), ct);

        return await SettleAsync(directory, RemovalKinds.Emptied, team, null, remaining, ct);
    }

    /// <summary>
    /// Finishes <paramref name="row"/> the way its kind says: a team root and a workspace are
    /// removed again, whole; an emptied folder has only the paths it named removed.
    /// </summary>
    public async Task<FolderRemovalReport> RetryAsync(UnfinishedRemoval row, CancellationToken ct = default)
    {
        switch (row.Kind)
        {
            case RemovalKinds.TeamRoot:
                var report = await RemoveTeamRootAsync(row.Path, row.Team, ct);

                // A root that no longer carries the marker is never deleted, and is not retried
                // again either: the platform no longer has evidence it is its own.
                if (report.Refused is not null) await ForgetAsync(row.Path, ct);
                return report;

            case RemovalKinds.Workspace:
                return await RemoveWorkspaceAsync(row.Path, row.Team, row.Member ?? "", ct);

            default:
                var boundary = row.Path;

                if (!Directory.Exists(boundary) || IsLink(boundary))
                {
                    await ForgetAsync(boundary, ct);
                    return FolderRemovalReport.Done;
                }

                var named = row.Remaining.Where(path => Confined(boundary, path)).ToList();
                var remaining = await PassesAsync(boundary, left =>
                {
                    foreach (var path in named.Where(Exists)) RemoveAsHost(path, left);
                }, ct, wholeEntries: false);

                return await SettleAsync(boundary, RemovalKinds.Emptied, row.Team, null, remaining, ct);
        }
    }

    /// <summary>
    /// For creating team <paramref name="team"/> at <paramref name="root"/>, which already exists:
    /// finishes the removal when the folder is recorded as the unfinished removal of a deleted
    /// team of that id and still carries its marker. Anything else - an unmarked folder, a folder
    /// nothing recorded - is refused, as before.
    /// </summary>
    public async Task<FolderRemovalReport> FinishBeforeCreateAsync(string root, string team, CancellationToken ct = default)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        var row = unfinished is null ? null : await unfinished.FindAsync(root, ct);

        if (row is not { Kind: RemovalKinds.TeamRoot }
            || !string.Equals(row.Team, team, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(await FirstLineAsync(TeamPaths.MarkerIn(root), ct), row.Team, StringComparison.OrdinalIgnoreCase))
        {
            return new FolderRemovalReport([], $"'{root}' already exists and is not the unfinished removal of a deleted team.");
        }

        return await RemoveTeamRootAsync(root, row.Team, ct);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is strictly inside <paramref name="boundary"/> once resolved,
    /// with no symbolic link between the two. The path itself may be a link: it is removed, not
    /// followed.
    /// </summary>
    public static bool Confined(string boundary, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(boundary));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
        if (LinkOrUnknown(root)) return false;

        for (var dir = Path.GetDirectoryName(full); dir is not null && dir.Length > root.Length; dir = Path.GetDirectoryName(dir))
        {
            if (LinkOrUnknown(dir)) return false;
        }

        return true;
    }

    // ---- The passes.

    /// <summary>
    /// The Host's pass; then, when something is left and the Host switches users, the agent's
    /// pass over what is left and the Host's pass again for what the agent emptied.
    /// </summary>
    private async Task<List<string>> PassesAsync(
        string boundary, Action<List<string>> hostPass, CancellationToken ct, string? keep = null, bool wholeEntries = true)
    {
        var remaining = new List<string>();
        hostPass(remaining);

        if (remaining.Count == 0 || runAs is not { Switches: true } agent) return remaining;

        // What the agent is handed. When the whole folder is going, the entry of the folder that
        // holds each leftover, so an agent directory nested in one of the Host's is removed in one
        // pass; when only named paths may go, those paths and nothing around them.
        var targets = wholeEntries
            ? remaining.Select(path => TopLevel(boundary, path)).OfType<string>()
            : remaining;

        await RemoveAsAgentAsync(
            agent, boundary, [.. targets.Where(path => !string.Equals(path, keep, StringComparison.Ordinal))], ct);

        remaining = [];
        hostPass(remaining);
        return remaining;
    }

    /// <summary>The entry directly inside <paramref name="boundary"/> that holds <paramref name="path"/>,
    /// or null for the boundary itself.</summary>
    private static string? TopLevel(string boundary, string path)
    {
        var relative = Path.GetRelativePath(boundary, path);

        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return null;

        return Path.Combine(boundary, relative.Split(Path.DirectorySeparatorChar, 2)[0]);
    }

    private void EmptyAsHost(string directory, string? keep, List<string> remaining)
    {
        List<string> entries;

        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot even be listed: the directory is what remains.
            remaining.Add(directory);
            return;
        }

        foreach (var entry in entries)
        {
            if (keep is not null && string.Equals(entry, keep, StringComparison.Ordinal)) continue;

            RemoveAsHost(entry, remaining);
        }
    }

    private void RemoveAsHost(string path, List<string> remaining)
    {
        bool directory;

        try
        {
            directory = !IsLink(path) && Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            remaining.Add(path);
            return;
        }

        if (!directory)
        {
            // A file, or a link to anything - the link goes, its target is not touched.
            try
            {
                _host.DeleteFile(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                remaining.Add(path);
            }

            return;
        }

        var before = remaining.Count;
        EmptyAsHost(path, null, remaining);

        if (remaining.Count != before) return;

        try
        {
            _host.DeleteEmptyDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            remaining.Add(path);
        }
    }

    /// <summary>
    /// <c>rm -rf --one-file-system -- &lt;paths&gt;</c> as the agent, through the launch prefix.
    /// Only paths confined to <paramref name="boundary"/>, and never the boundary itself (a team
    /// root's marker must go last, and by the Host).
    /// </summary>
    private static async Task RemoveAsAgentAsync(
        AgentLaunchUser agent, string boundary, IReadOnlyList<string> remaining, CancellationToken ct)
    {
        var targets = remaining.Where(path => Confined(boundary, path)).Distinct(StringComparer.Ordinal).ToList();

        if (targets.Count == 0 || SystemCommand.Find("rm") is not { } rm) return;

        foreach (var chunk in targets.Chunk(100))
        {
            await RunAsync([.. agent.Prefix, rm, "-rf", "--one-file-system", "--", .. chunk], ct);
        }
    }

    private static async Task RunAsync(IReadOnlyList<string> command, CancellationToken ct)
    {
        var start = new ProcessStartInfo(command[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start);
            if (process is null) return;

            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
                throw;
            }

            await Task.WhenAll(output, error);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Not startable: the Host's pass that follows names what is still there.
        }
    }

    // ---- The record.

    private async Task<FolderRemovalReport> SettleAsync(
        string path, string kind, string team, string? member, List<string> remaining, CancellationToken ct)
    {
        if (remaining.Count == 0)
        {
            await ForgetAsync(path, ct);
            return FolderRemovalReport.Done;
        }

        var sorted = remaining.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();

        if (unfinished is not null)
        {
            await unfinished.RecordAsync(
                new UnfinishedRemoval(path, kind, team, member, sorted, DateTimeOffset.UtcNow, 1), ct);
        }

        return new FolderRemovalReport(sorted);
    }

    private Task ForgetAsync(string path, CancellationToken ct) =>
        unfinished?.ForgetAsync(path, ct) ?? Task.CompletedTask;

    // ---- The file system, without following links.

    private static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>For confinement, where a path that cannot be examined must count as a link.</summary>
    private static bool LinkOrUnknown(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool Exists(string path) =>
        Directory.Exists(path) || new FileInfo(path) is { } file && (file.Exists || file.LinkTarget is not null);

    private static List<string> Entries(string directory, string except)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory)
                .Where(entry => !string.Equals(entry, except, StringComparison.Ordinal))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static async Task<string?> ReadQuietlyAsync(string path, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllTextAsync(path, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<string?> FirstLineAsync(string path, CancellationToken ct) =>
        (await ReadQuietlyAsync(path, ct))?.Split('\n', 2)[0].Trim();

    private static async Task RestoreMarkerAsync(string marker, string contents, CancellationToken ct)
    {
        try
        {
            await File.WriteAllTextAsync(marker, contents, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
