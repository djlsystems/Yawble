using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// ONE WEBSITE COMMAND TEAM CONNECTIONS ASKED FOR (CON 6), as its <c>website</c> record names it:
/// <c>{"t":"website","command":"check|session|act","name":"...","path":"/x","expect":"...","minutes":30,
/// "steps":[{"action":"click|fill|submit","selector":"...","value":"..."}],"source":"&lt;team&gt;/&lt;member&gt;"}</c>.
/// <see cref="Source"/> is the work item's source the command came with: the member a session is handed to.
/// </summary>
internal sealed record WebsiteAsk(
    string Command, string Name, string? Path, string? Expect, int? Minutes, IReadOnlyList<WebsiteStep>? Steps, string? Source)
{
    public const int MaximumSteps = 20;

    public static WebsiteAsk? From(JsonObject record)
    {
        string? Text(string field) => record[field] is JsonValue v && v.TryGetValue<string>(out var t) && !string.IsNullOrWhiteSpace(t) ? t.Trim() : null;

        if (Text("command") is not ({ } command and (WebsiteProbe.Check or WebsiteProbe.Session or WebsiteProbe.Act)) || Text("name") is not { } name)
        {
            return null;
        }

        int? minutes = record["minutes"] is JsonValue m && m.TryGetValue<int>(out var given) ? given : null;
        List<WebsiteStep>? steps = null;
        if (record["steps"] is JsonArray list)
        {
            steps = [];
            foreach (var item in list.OfType<JsonObject>().Take(MaximumSteps))
            {
                string? Cell(string field) => item[field] is JsonValue v && v.TryGetValue<string>(out var t) ? t : null;
                if (Cell("action") is { } action && Cell("selector") is { Length: > 0 } selector) steps.Add(new WebsiteStep(action, selector, Cell("value")));
            }
        }

        return new WebsiteAsk(command, name, Text("path"), Text("expect"), minutes, steps, Text("source"));
    }
}

public sealed partial class PluginMemberRunner
{
    /// <summary>The folder of the team's documents a website check's screenshots go in.</summary>
    public const string ScreenshotFolder = "Team Connections";

    /// <summary>The folder of the asking member's workspace a website session is handed over in.</summary>
    public const string HandoffFolder = ".team-connections-sessions";

    /// <summary>
    /// TEAM CONNECTIONS' WEBSITE COMMAND: the website connection of that name assigned to the member's
    /// team, used through <see cref="Connections.WebsiteAsync"/> in the platform's browser on a worker.
    /// The answer names the page, its text and its screenshot (in the team's documents, named by the
    /// connection and the time - never a value), or the session handed to the member that asked (in its
    /// own workspace, readable by it alone, deleted when it expires). Never a value in the answer.
    /// </summary>
    private async Task<string> WebsiteByNameAsync(MemberInvocation invocation, WebsiteAsk ask, Gathered gathered, CancellationToken ct)
    {
        var member = invocation.Member;
        var assigned = await AssignedConnectionsAsync(member.Team, ct);
        var connection = assigned.FirstOrDefault(c => string.Equals(c.Name.Trim(), ask.Name, StringComparison.OrdinalIgnoreCase));
        var what = $"{ask.Command} '{ask.Name}'";

        if (connection is null)
        {
            return $"{Capital(what)}: no connection of that name is assigned to this team. "
                + (assigned.Count == 0
                    ? "This team has none; a person assigns them in Team settings → Connections."
                    : $"This team's connections: {string.Join(", ", assigned.Select(c => c.Name))}.");
        }

        if (connection.Kind != ConnectionKinds.Website)
        {
            return $"{Capital(what)}: {connection.Name} is {Article(ConnectionKinds.Display(connection.Kind))}, not a website; check, session and act are a website's. `test {connection.Name}` tests it.";
        }

        if (ConnectionTests is null) return $"{Capital(what)}: no worker can run a website's browser here.";

        WebsiteAnswer answer;
        try
        {
            answer = await connections!.WebsiteAsync(member, connection.Id, ask.Command, ConnectionTests, ct, ask.Path, ask.Expect, ask.Minutes, ask.Steps);
        }
        catch (System.Data.Common.DbException)
        {
            return $"{Capital(what)}: its result could not be recorded. Nothing was changed; try again.";
        }

        if (answer.Failed && gathered.WebsiteFailure is null) gathered.WebsiteFailure = answer;

        var text = new StringBuilder(answer.Report ?? answer.Sentence);
        if (answer.Screenshot is { Length: > 0 } png && Screenshot(member.Team, connection.Name, png) is { } shot)
        {
            text.Append('\n').Append("  screenshot: ").Append(shot).Append(" (in the team's documents)");
        }

        if (answer.Handoff is { } handoff)
        {
            text.Clear().Append(Handoff(invocation, ask, connection.Name, handoff));
        }

        return text.ToString();
    }

    /// <summary>
    /// A check's screenshot, saved in the team's documents as <c>Team Connections/&lt;connection&gt;-&lt;UTC time&gt;.png</c>:
    /// the connection's name made a file name, never a value. Its path in the documents, or null when there is
    /// no documents folder to keep it in.
    /// </summary>
    private string? Screenshot(string team, string connection, byte[] png)
    {
        if (Documents is null) return null;
        try
        {
            var folder = Path.Combine(Documents.EnsureFor(team), ScreenshotFolder);
            Directory.CreateDirectory(folder);
            var name = $"{FileName(connection)}-{DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.png";
            File.WriteAllBytes(Path.Combine(folder, name), png);
            return $"{ScreenshotFolder}/{name}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DocumentPathException)
        {
            return null;
        }
    }

    /// <summary>
    /// THE SESSION, HANDED TO THE MEMBER THAT ASKED, as Web Credentials hands one: a file in that member's
    /// own workspace (<see cref="HandoffFolder"/>, 0750, the file 0640 - the agent's group, no other user),
    /// never in documents, a site file, a commit, the card or the ledger, deleted when it expires. A member
    /// of another team, a person or a schedule is handed none.
    /// </summary>
    private string Handoff(MemberInvocation invocation, WebsiteAsk ask, string connection, ConnectionTestSecret handoff)
    {
        string? kind = null, header = null, expires = null;
        IReadOnlyList<string> hosts = [];
        try
        {
            using var document = JsonDocument.Parse(handoff.Value);
            var root = document.RootElement;
            kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;
            header = root.TryGetProperty("header", out var h) ? h.GetString() : null;
            expires = root.TryGetProperty("expiresAt", out var e) ? e.GetString() : null;
            if (root.TryGetProperty("hosts", out var list) && list.ValueKind == JsonValueKind.Array) hosts = [.. list.EnumerateArray().Select(x => x.GetString() ?? "")];
        }
        catch (JsonException)
        {
            return $"{connection}: the session could not be read, so none was handed over.";
        }

        var asker = invocation.Work.Select(w => w.Source).FirstOrDefault(s => string.Equals(s, ask.Source, StringComparison.OrdinalIgnoreCase));
        if (Paths is null || asker is null || !ContainerId.TryParse(asker, out var to)
            || !string.Equals(to.Team, invocation.Member.Team, StringComparison.OrdinalIgnoreCase) || to.Equals(invocation.Member))
        {
            return $"{connection}: a session is handed only to a member of this team that asks for one; none was handed over.";
        }

        var until = DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.UtcNow.AddMinutes(30);
        string file;
        try
        {
            var folder = Path.Combine(Paths.WorkspaceFor(to), HandoffFolder);
            var made = Directory.CreateDirectory(folder);
            if (!OperatingSystem.IsWindows()) made.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
            SweepHandoffs(folder);
            var name = $"{FileName(connection)}-{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6))}.json";
            file = Path.Combine(folder, name);
            using (var stream = new FileStream(file, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            }))
            {
                stream.Write(Encoding.UTF8.GetBytes(handoff.Value));
            }

            _ = DeleteAtAsync(file, until);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"{connection}: the session could not be written to {to.Name}'s workspace, so none was handed over.";
        }

        var where = $"{to.Name}:{HandoffFolder}/{Path.GetFileName(file)}";
        var good = until.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return kind == "token"
            ? $"{connection}: a token handoff for {string.Join(", ", hosts)} is at {where}, header {header}, good until {good}. It is deleted when it expires."
            : $"{connection}: a browser session for {string.Join(", ", hosts)} is at {where}, good until {good}. Start your browser with its storageState; it is deleted when it expires.";
    }

    /// <summary>Every handed-over session in <paramref name="folder"/> whose time is up, deleted.</summary>
    internal static void SweepHandoffs(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (!document.RootElement.TryGetProperty("expiresAt", out var at)
                    || !DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until)
                    || until <= DateTimeOffset.UtcNow)
                {
                    File.Delete(file);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }
    }

    private static async Task DeleteAtAsync(string file, DateTimeOffset until)
    {
        var wait = until - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait > TimeSpan.FromHours(3) ? TimeSpan.FromHours(3) : wait);
        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A connection's name as a file name: letters, digits, '-' and '_', lower case, at most 60.</summary>
    internal static string FileName(string name)
    {
        var slug = FileNameRun().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "website" : slug.Length > 60 ? slug[..60] : slug;
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Article(string words) => ("aeiou".Contains(char.ToLowerInvariant(words[0])) ? "an " : "a ") + words;

    [GeneratedRegex("[^a-z0-9_-]+")]
    private static partial Regex FileNameRun();
}
