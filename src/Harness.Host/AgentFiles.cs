using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using System.Text.Json;

namespace Harness.Host;

/// <summary>
/// Reading what an agent CLI left under its home, for a person: finding a transcript the
/// agent named itself, and reading a finished one whole. The agent's session folders are its own
/// and stay that way: when the Host switches users every read is a system command
/// (<c>find</c>, <c>head</c>, <c>cat</c>) started through the same <c>setpriv</c> prefix as every
/// agent child, and when it cannot switch it reads the files itself, as it already can.
/// </summary>
public static class AgentFiles
{
    /// <summary>
    /// What a read found: the text, that the file is not there, or why it is there but could not
    /// be read (<paramref name="Unreadable"/>, e.g. permission denied).
    /// </summary>
    public sealed record Read(string? Text, string? Unreadable = null)
    {
        public bool Gone => Text is null && Unreadable is null;
    }

    /// <summary>The whole of <paramref name="path"/>, or <see cref="Read.Gone"/> when it no longer exists.</summary>
    public static Task<Read> ReadAllAsync(string path, AgentLaunchUser? runAs, CancellationToken ct) =>
        Prefix(runAs) is { } prefix ? ReadAllByCommandAsync(prefix, path, ct) : ReadAllDirectlyAsync(path, ct);

    /// <summary>Whether <paramref name="path"/> is a file the agent can read.</summary>
    public static async Task<bool> ExistsAsync(string path, AgentLaunchUser? runAs, CancellationToken ct) =>
        Prefix(runAs) is { } prefix
            ? (await RunAsync([.. prefix, Command("head"), "-c", "0", "--", path], ct)).Exit == 0
            : File.Exists(path);

    /// <summary>
    /// The newest file under <paramref name="find"/>'s folder that matches its pattern, was written
    /// at or after <paramref name="since"/>, and whose session belongs to
    /// <paramref name="workspace"/>; null when there is none yet.
    /// </summary>
    public static Task<string?> FindAsync(
        AgentLiveViewFind find, string home, string workspace, DateTimeOffset since, AgentLaunchUser? runAs,
        CancellationToken ct) =>
        Prefix(runAs) is { } prefix
            ? FindByCommandAsync(prefix, find, home, workspace, since, ct)
            : FindDirectlyAsync(find, home, workspace, since, ct);

    /// <summary>The agent prefix when this Host reads as the agent, or null when it reads directly.</summary>
    private static IReadOnlyList<string>? Prefix(AgentLaunchUser? runAs) =>
        runAs is { Switches: true } ? runAs.Prefix : null;

    // ---- As the agent. Public so the suite can drive the command half where it cannot switch
    // ---- users, with an empty prefix.

    public static async Task<Read> ReadAllByCommandAsync(IReadOnlyList<string> prefix, string path, CancellationToken ct)
    {
        var (exit, output, error) = await RunAsync([.. prefix, Command("cat"), "--", path], ct);
        if (exit == 0) return new Read(output);
        if (error.Contains("No such file", StringComparison.Ordinal)) return new Read(null);

        return new Read(null, error.Trim() is { Length: > 0 } why ? why : $"cat exited {exit}.");
    }

    public static async Task<string?> FindByCommandAsync(
        IReadOnlyList<string> prefix, AgentLiveViewFind find, string home, string workspace, DateTimeOffset since,
        CancellationToken ct)
    {
        var root = Root(find, home);
        var depth = find.Pattern.Split('/').Length.ToString(CultureInfo.InvariantCulture);

        // `-newermt` prunes by whole seconds; the exact bound is applied below.
        var (_, output, _) = await RunAsync(
        [
            .. prefix, Command("find"), root, "-mindepth", depth, "-maxdepth", depth, "-type", "f",
            "-newermt", "@" + since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            "-printf", "%T@\t%p\n",
        ], ct);

        var candidates = new List<(string Path, DateTimeOffset Written)>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0 || !decimal.TryParse(line[..tab], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                continue;
            }

            candidates.Add((line[(tab + 1)..], DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000))));
        }

        return await ChooseAsync(find, root, workspace, since, candidates, async (path, lines) =>
        {
            var (exit, text, _) = await RunAsync([.. prefix, Command("head"), "-n", lines.ToString(CultureInfo.InvariantCulture), "--", path], ct);
            return exit == 0 ? text : null;
        });
    }

    // ---- Directly, as the Host.

    private static async Task<Read> ReadAllDirectlyAsync(string path, CancellationToken ct)
    {
        try
        {
            return new Read(await File.ReadAllTextAsync(path, ct));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new Read(null);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return new Read(null, exception.Message);
        }
    }

    private static async Task<string?> FindDirectlyAsync(
        AgentLiveViewFind find, string home, string workspace, DateTimeOffset since, CancellationToken ct)
    {
        var root = Root(find, home);
        var candidates = new List<(string Path, DateTimeOffset Written)>();
        var segments = find.Pattern.Split('/');

        void Walk(string folder, int level)
        {
            try
            {
                if (level == segments.Length - 1)
                {
                    foreach (var file in Directory.EnumerateFiles(folder))
                    {
                        if (FileSystemName.MatchesSimpleExpression(segments[level], Path.GetFileName(file), ignoreCase: false))
                        {
                            candidates.Add((file, File.GetLastWriteTimeUtc(file)));
                        }
                    }

                    return;
                }

                foreach (var child in Directory.EnumerateDirectories(folder))
                {
                    if (FileSystemName.MatchesSimpleExpression(segments[level], Path.GetFileName(child), ignoreCase: false))
                    {
                        Walk(child, level + 1);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Not there yet, or not ours to read: nothing found here.
            }
        }

        Walk(root, 0);

        return await ChooseAsync(find, root, workspace, since, candidates, async (path, lines) =>
        {
            try
            {
                var text = new StringBuilder();
                using var reader = new StreamReader(path);
                for (var i = 0; i < lines && await reader.ReadLineAsync(ct) is { } line; i++) text.Append(line).Append('\n');
                return text.ToString();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });
    }

    // ---- The rule, shared by both halves.

    /// <summary>
    /// The newest candidate written at or after <paramref name="since"/> whose pattern matches
    /// segment by segment and whose workspace is <paramref name="workspace"/>.
    /// <paramref name="head"/> reads a file's first lines, or null when it cannot.
    /// </summary>
    private static async Task<string?> ChooseAsync(
        AgentLiveViewFind find, string root, string workspace, DateTimeOffset since,
        IEnumerable<(string Path, DateTimeOffset Written)> candidates, Func<string, int, Task<string?>> head)
    {
        var segments = find.Pattern.Split('/');
        var wanted = Normal(workspace);

        foreach (var (path, _) in candidates.Where(c => c.Written >= since).OrderByDescending(c => c.Written))
        {
            var relative = Path.GetRelativePath(root, path).Split('/');
            if (relative.Length != segments.Length
                || !relative.Zip(segments).All(pair => FileSystemName.MatchesSimpleExpression(pair.Second, pair.First, ignoreCase: false)))
            {
                continue;
            }

            var cwd = find.CwdFrom switch
            {
                LiveView.CwdFromFolderName => Uri.UnescapeDataString(relative[0]),
                LiveView.CwdFromWorkspaceYaml => YamlCwd(await head(Path.Combine(Path.GetDirectoryName(path)!, "workspace.yaml"), 50)),
                LiveView.CwdFromFirstLine => FirstLineCwd(await head(path, 1)),
                _ => null,
            };

            if (cwd is not null && Normal(cwd) == wanted) return path;
        }

        return null;
    }

    /// <summary>The <c>cwd:</c> line of a Copilot <c>workspace.yaml</c>, unquoted.</summary>
    internal static string? YamlCwd(string? yaml) =>
        yaml?.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("cwd:", StringComparison.Ordinal))
            .Select(line => line[4..].Trim().Trim('"', '\''))
            .FirstOrDefault();

    /// <summary>The <c>cwd</c> of a first JSON line, or of its <c>payload</c> (Codex's <c>session_meta</c>).</summary>
    internal static string? FirstLineCwd(string? text)
    {
        var first = text?.Split('\n')[0];
        if (string.IsNullOrWhiteSpace(first)) return null;

        try
        {
            using var document = JsonDocument.Parse(first);
            var root = document.RootElement;
            return ClaudeTranscriptLines.Text(root, "cwd")
                ?? ClaudeTranscriptLines.Text(TranscriptLines.Property(root, "payload"), "cwd");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Root(AgentLiveViewFind find, string home) => Path.Combine(home, find.Folder[2..]);

    private static string Normal(string path) => path.Length > 1 ? path.TrimEnd('/') : path;

    private static string Command(string name) =>
        SystemCommand.Find(name) ?? throw new InvalidOperationException(
            $"{name} is not in a root-owned system directory ({string.Join(", ", SystemCommand.Directories)}), so the agent's files cannot be read as the agent.");

    private static async Task<(int Exit, string Output, string Error)> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
    {
        var start = new ProcessStartInfo(command[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{command[0]} did not start.");
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

        return (process.ExitCode, await output, await error);
    }
}
