using System.Text.RegularExpressions;

namespace Harness.Tests.Host;

/// <summary>
/// A RECURSIVE DELETE ONLY WHERE AGENTS CANNOT WRITE. Agents run as <c>agent</c> and make
/// owner-only directories the Host cannot empty, and <c>Directory.Delete(recursive: true)</c>
/// removes as it walks and then throws part-way, so anywhere agents can write is removed through
/// <c>FolderRemoval</c>. Every recursive delete left in <c>src/</c> is on <see cref="Reviewed"/>
/// with the reason agents cannot write inside that folder, and carries the same reason as a
/// comment at the call.
/// </summary>
public sealed class RecursiveDeleteInvariantTests
{
    /// <summary>The comment a reviewed call carries, on its line or one of the lines just above.</summary>
    private const string Marker = "RECURSIVE DELETE REVIEWED:";

    private const int MarkerReach = 4;

    /// <summary>
    /// THE REVIEWED LIST: one entry per call, by file. A second call in a listed file needs a
    /// second entry.
    /// </summary>
    private static readonly ReviewedCall[] Reviewed =
    [
        new("src/Harness.Host/McpLaunchConfig.cs",
            "A launch's MCP directory: the Host makes it owner-only and AgentLaunchUser.Share gives the agent's group read and traverse, never write."),
        new("src/Harness.Host/AgentToolListing.cs",
            "A tool listing's scratch folder: the Host makes it owner-only and AgentLaunchUser.Share gives the agent's group read and traverse, never write."),
        new("src/Harness.Host/PluginInstaller.cs",
            "A stage or outgoing folder inside the plugins folder, which is the Host's (harness:agent, directories 0750, no group write) by prepare-volume.sh."),
        new("src/Harness.Host/PluginRemover.cs",
            "A removed plugin set aside inside the plugins folder, which is the Host's (harness:agent, directories 0750, no group write) by prepare-volume.sh."),
        new("src/Harness.Host/LocalRepos.cs",
            "A bare local repository inside the repos folder, which is the Host's (harness:agent, directories 2750, no group write) by prepare-volume.sh; agents never push, the Host publishes as itself.",
            CommentPending: true),
    ];

    [Fact]
    public void Every_recursive_delete_in_src_is_reviewed_and_says_why_agents_cannot_write_there()
    {
        var root = RepoRoot();
        var problems = new List<string>();
        var found = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)) continue;

            var lines = File.ReadAllLines(file);
            var calls = RecursiveDeletes(string.Join('\n', lines));
            if (calls.Count > 0) found[relative] = calls;
        }

        foreach (var (file, calls) in found)
        {
            var entries = Reviewed.Where(r => r.File == file).ToList();

            if (calls.Count > entries.Count)
            {
                foreach (var line in calls.Skip(entries.Count))
                {
                    problems.Add($"{file}:{line} deletes a folder recursively and is not on the reviewed list. "
                        + "If agents can write anywhere inside that folder (a team root, a workspace, the documents, the sites, "
                        + "anything prepare-volume.sh gives to agent, anything shared to the agent with write), remove it through "
                        + "FolderRemoval (RemoveInsideAsync, or RemoveInsideOrThrowAsync where the old delete threw). If they cannot, "
                        + $"put \"// {Marker} agents cannot write here. <why>\" above the call and add the file and the same reason "
                        + $"to {nameof(Reviewed)} in tests/Harness.Tests/Host/{nameof(RecursiveDeleteInvariantTests)}.cs.");
                }
            }

            var source = File.ReadAllLines(Path.Combine(root, file));

            foreach (var line in calls.Take(entries.Count))
            {
                if (entries.All(e => e.CommentPending)) continue;

                var from = Math.Max(0, line - 1 - MarkerReach);
                var said = source[from..line].Any(l => l.Contains(Marker, StringComparison.Ordinal));

                if (!said)
                {
                    problems.Add($"{file}:{line} is on the reviewed list but does not say why at the call. "
                        + $"Put \"// {Marker} agents cannot write here. <why>\" on one of the {MarkerReach} lines above it, "
                        + $"with the reason its {nameof(Reviewed)} entry gives.");
                }
            }
        }

        foreach (var group in Reviewed.GroupBy(r => r.File))
        {
            var calls = found.TryGetValue(group.Key, out var c) ? c.Count : 0;

            if (group.Count() > calls)
            {
                problems.Add($"{group.Key} has {group.Count()} reviewed entr{(group.Count() == 1 ? "y" : "ies")} but {calls} recursive "
                    + $"delete(s). Remove the entries for calls that are gone from {nameof(Reviewed)}.");
            }

            foreach (var entry in group.Where(e => string.IsNullOrWhiteSpace(e.Reason)))
            {
                problems.Add($"{entry.File} is on the reviewed list with no reason. Say why agents cannot write inside that folder.");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_scan_finds_every_spelling_of_a_recursive_delete_and_nothing_else()
    {
        const string sample = """
            Directory.Delete(a, true);
            Directory.Delete(b, recursive: true);
            Directory.Delete(
                Path.Combine(c, "x"),
                recursive: true);
            new DirectoryInfo(d).Delete(true);
            Directory.Delete(e);
            Directory.Delete(f, recursive: false);
            Directory.Delete(g, false);
            // Directory.Delete(h, true);
            /// <c>Directory.Delete(recursive: true)</c>
            /* Directory.Delete(i, true); */
            Directory.Delete(j, recurse);
            """;

        Assert.Equal([1, 2, 3, 6, 13], RecursiveDeletes(sample));
    }

    /// <summary>
    /// The 1-based line of each recursive delete in <paramref name="source"/>: a
    /// <c>Directory.Delete</c> with any second argument but <c>false</c>, and a
    /// <c>DirectoryInfo.Delete(true)</c>. Comments are blanked first, line breaks kept.
    /// </summary>
    private static List<int> RecursiveDeletes(string source)
    {
        var code = Comments.Replace(source, m => m.Groups["comment"].Success ? Regex.Replace(m.Value, "[^\n]", " ") : m.Value);
        var lines = new List<int>();

        foreach (Match call in DirectoryDelete.Matches(code))
        {
            var arguments = Arguments(code, call.Index + call.Length);
            if (arguments.Count < 2) continue;

            var recursive = Regex.Replace(arguments[1], @"^\s*recursive\s*:\s*", "").Trim();
            if (recursive != "false") lines.Add(LineOf(code, call.Index));
        }

        foreach (Match call in InfoDelete.Matches(code))
        {
            lines.Add(LineOf(code, call.Index));
        }

        return [.. lines.Order()];
    }

    /// <summary>The top-level arguments of the call whose opening parenthesis ends at <paramref name="start"/>.</summary>
    private static List<string> Arguments(string code, int start)
    {
        var arguments = new List<string>();
        var depth = 0;
        var from = start;

        for (var i = start; i < code.Length; i++)
        {
            var ch = code[i];
            if (ch is '(' or '[' or '{') depth++;
            else if (ch is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    arguments.Add(code[from..i]);
                    break;
                }

                depth--;
            }
            else if (ch == ',' && depth == 0)
            {
                arguments.Add(code[from..i]);
                from = i + 1;
            }
        }

        return arguments;
    }

    private static int LineOf(string code, int index) => code.AsSpan(0, index).Count('\n') + 1;

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("No Harness.slnx above the test output.");
    }

    /// <summary>A string or character literal (kept, so a <c>//</c> or <c>/*</c> inside one is not
    /// read as a comment), or a line comment (<c>//</c>, <c>///</c>) or block comment (blanked).</summary>
    private static readonly Regex Comments = new(
        @"(?<literal>@""(?:""""|[^""])*""|""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])+')|(?<comment>//[^\n]*|/\*.*?\*/)",
        RegexOptions.Singleline);

    private static readonly Regex DirectoryDelete = new(@"\bDirectory\s*\.\s*Delete\s*\(");

    private static readonly Regex InfoDelete = new(@"\.\s*Delete\s*\(\s*(recursive\s*:\s*)?true\s*\)");

    /// <param name="File">The file, from the repository root, with forward slashes.</param>
    /// <param name="Reason">Why agents cannot write inside the folder that call deletes.</param>
    /// <param name="CommentPending">The call's file is not this list's to edit yet, so its comment
    /// is not required; drop the flag once the file carries the comment.</param>
    private sealed record ReviewedCall(string File, string Reason, bool CommentPending = false);
}
