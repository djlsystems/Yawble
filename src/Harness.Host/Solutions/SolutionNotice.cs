using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host.Solutions;

/// <summary>
/// THE BOARD'S NOTICE FOR A PACKAGE A WORKFLOW WROTE. When a workflow is declared complete, every
/// folder directly in the team's documents folder that holds <c>solution.json</c> and has a file
/// written since the workflow began is checked through <see cref="SolutionService"/> - the one door
/// a folder is checked through - and one <c>solution.checked</c> row is appended per package, inside
/// that workflow, with the declarer as its source. The board shows it on the declarer's card and
/// the backlog item reads it off its dispatch's workflow.
///
/// <para>
/// A PACKAGE NOT WRITTEN DURING THE WORKFLOW IS NOT CHECKED AGAIN: a notice answers "what did this
/// workflow deliver", and a folder left from an earlier round did not come from it. Nothing here
/// installs or writes anything but the rows.
/// </para>
///
/// <para>
/// ONE PASSING NOTICE PER PACKAGE VERSION ON A TEAM. Two workflows open while a package is written
/// both see it written during them; each would post "ready. **Review and install**" for the same
/// thing. So every notice carries <c>contentHash</c>, a digest of the folder's files, and a pass is
/// not posted when the team already has a passing notice for the same folder, version and digest.
/// A changed folder has a new digest and gets a new notice; a failing notice is always posted.
/// </para>
/// </summary>
public sealed class SolutionNotice(SolutionService solutions, TeamDocuments documents, IMessageLog log)
{
    /// <summary>The install wizard's route; <see cref="LinkFor"/> adds the folder.</summary>
    public const string InstallRoute = "#/solutions/install";

    /// <summary>How many files one package folder is walked for before it counts as not written:
    /// a package is a handful of files, and the walk runs inside a declaration.</summary>
    private const int FilesWalked = 5000;

    /// <summary>How many earlier notices are read per page when looking for a passing one.</summary>
    private const int NoticesPerPage = 500;

    public static string LinkFor(string folder) => $"{InstallRoute}?folder={Uri.EscapeDataString(folder)}";

    /// <summary>The notice a passing package gets, in the words the board and the backlog show.</summary>
    public static string ReadyText(string name, string? version) =>
        $"{Label(name, version)} is ready. **Review and install**";

    /// <returns>The appended rows, one per package, in folder order.</returns>
    public async Task<IReadOnlyList<Message>> PostAsync(
        string team, long correlation, ContainerId declarer, Message declaration, CancellationToken ct)
    {
        var root = documents.RootFor(team);
        if (!Directory.Exists(root)) return [];

        // THE WORKFLOW BEGAN AT ITS ROOT ROW. No root (purged) means nothing can be said to have
        // been written during it, so nothing is checked.
        if (await log.FindAsync(correlation, ct) is not { } began) return [];

        var appended = new List<Message>();
        HashSet<(string Path, string? Version, string Hash)>? passed = null;

        foreach (var folder in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(folder, SolutionManifest.FileName))) continue;
            if (!WrittenSince(folder, began.OccurredAt)) continue;

            var payload = Payload(folder, solutions.Check(folder));
            var hash = ContentHash(folder);
            payload[PayloadFields.ContentHash] = hash;

            if (payload[PayloadFields.Ok] is true)
            {
                passed ??= await PassedAsync(team, ct);
                if (!passed.Add((folder, payload[PayloadFields.Version] as string, hash))) continue;
            }

            appended.Add(await log.AppendAsync(
                new NewMessage(MessageTypes.SolutionChecked, JsonSerializer.Serialize(payload), declarer.ToString(), declaration.Seq),
                ct));
        }

        return appended;
    }

    /// <summary>
    /// Every passing notice already on the team, as folder, version and digest. The type is read in
    /// the query, so this walks only <c>solution.checked</c> rows, then keeps the team's. A notice
    /// written before digests were carried has none and matches nothing.
    /// </summary>
    private async Task<HashSet<(string Path, string? Version, string Hash)>> PassedAsync(string team, CancellationToken ct)
    {
        var passed = new HashSet<(string, string?, string)>();
        string[] types = [MessageTypes.SolutionChecked];
        long after = 0;

        while (true)
        {
            var page = await log.ReadAfterAsync(after, types, NoticesPerPage, ct);
            foreach (var row in page)
            {
                if (!string.Equals(MessageTeam.Of(row), team, StringComparison.Ordinal)) continue;
                if (Passed(row) is { } key) passed.Add(key);
            }

            if (page.Count < NoticesPerPage) return passed;
            after = page[^1].Seq;
        }
    }

    private static (string, string?, string)? Passed(Message row)
    {
        try
        {
            var payload = JsonDocument.Parse(row.Payload).RootElement;
            if (payload.ValueKind != JsonValueKind.Object) return null;
            if (!payload.TryGetProperty(PayloadFields.Ok, out var ok) || ok.ValueKind != JsonValueKind.True) return null;
            if (Text(payload, PayloadFields.Path) is not { } path) return null;
            if (Text(payload, PayloadFields.ContentHash) is not { } hash) return null;
            return (path, Text(payload, PayloadFields.Version), hash);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A digest of the folder's files: each relative path and its bytes, in ordinal path order, so
    /// the same files give the same digest wherever and whenever they were written. Links are not
    /// followed; a file that cannot be read counts by its path alone.
    /// </summary>
    public static string ContentHash(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", options)
                .Take(FilesWalked)
                .Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            files = [];
        }

        foreach (var relative in files)
        {
            var name = Encoding.UTF8.GetBytes(relative);
            hash.AppendData(BitConverter.GetBytes(name.Length));
            hash.AppendData(name);

            try
            {
                var bytes = File.ReadAllBytes(Path.Combine(folder, relative));
                hash.AppendData(BitConverter.GetBytes((long)bytes.Length));
                hash.AppendData(bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                hash.AppendData(BitConverter.GetBytes(-1L));
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static Dictionary<string, object?> Payload(string folder, SolutionFolderCheck answer)
    {
        // A folder refused (a link leaving the data root) is never read: the notice names it by its folder.
        var (id, name, version) = answer.FolderRefused
            ? (null, Path.GetFileName(folder), null)
            : Named(folder, answer.Check);

        var problems = answer.FolderRefused
            ? [answer.Error!]
            : answer.Check!.Refusals.Select(r => r.ToString()).ToList();

        var ok = !answer.FolderRefused && answer.Check!.Ok;

        return new Dictionary<string, object?>
        {
            [PayloadFields.Path] = folder,
            [PayloadFields.Ok] = ok,
            [PayloadFields.Solution] = id,
            [PayloadFields.Name] = name,
            [PayloadFields.Version] = version,
            [PayloadFields.Text] = ok
                ? ReadyText(name, version)
                : FailingText(name, version, problems),
            [PayloadFields.Link] = ok ? LinkFor(folder) : null,
            [PayloadFields.Problems] = ok ? Array.Empty<string>() : problems,
        };
    }

    /// <summary>
    /// The failing notice: the package, then each problem on its own line ending in one full stop - a
    /// reason that already ends a sentence is not given a second, and none is joined to the next.
    /// </summary>
    public static string FailingText(string name, string? version, IReadOnlyList<string> problems)
    {
        var lines = problems.Select(p => p.TrimEnd()).Where(p => p.Length > 0).Select(Sentence).ToList();
        return lines.Count == 0
            ? $"{Label(name, version)} did not pass the check."
            : $"{Label(name, version)} did not pass the check:\n{string.Join("\n", lines)}";
    }

    private static string Sentence(string text) =>
        text.EndsWith('.') || text.EndsWith('?') || text.EndsWith('!') ? text : text + ".";

    /// <summary>
    /// The package's id, name and version: the checked manifest's when it passed, otherwise what
    /// <c>solution.json</c> says as plain strings, so a failing notice still names the package. The
    /// folder's name when it says nothing readable.
    /// </summary>
    private static (string? Id, string Name, string? Version) Named(string folder, SolutionCheck? check)
    {
        if (check?.Package?.Manifest is { } manifest) return (manifest.Id, manifest.Name, manifest.Version);

        // A link leaving the package stopped the check before any read; the notice reads nothing either.
        if (check is not null && check.Refusals.Any(r => r.Field == SolutionChecker.LinkField)) return (null, Path.GetFileName(folder), null);

        string? id = null, name = null, version = null;

        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, SolutionManifest.FileName)));
            if (json.RootElement.ValueKind == JsonValueKind.Object)
            {
                id = Text(json.RootElement, "id");
                name = Text(json.RootElement, "name");
                version = Text(json.RootElement, "version");
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // The check has already named what is wrong with the file; this only labels the notice.
        }

        return (id, name ?? Path.GetFileName(folder), version);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static string Label(string name, string? version) => version is null ? name : $"{name} {version}";

    /// <summary>Whether any file in the folder was written at or after <paramref name="since"/>.
    /// Links are not followed.</summary>
    private static bool WrittenSince(string folder, DateTimeOffset since)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", options)
                .Take(FilesWalked)
                .Any(file => file.LastWriteTimeUtc >= since.UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
