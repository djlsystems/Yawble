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
            answer = await connections!.WebsiteAsync(member, connection.Id, ask.Command, ConnectionTests, ct, ask.Path, ask.Expect, ask.Minutes, ask.Steps,
                handoff => Handoff(invocation, ask, connection.Name, handoff));
        }
        catch (System.Data.Common.DbException)
        {
            return $"{Capital(what)}: its result could not be recorded. Nothing was changed; try again.";
        }

        if (answer.Failed && gathered.WebsiteFailure is null)
        {
            gathered.WebsiteFailure = answer;
            gathered.WebsiteFailureSource = ask.Source;
        }

        var text = new StringBuilder(answer.Report ?? answer.Sentence);
        if (answer.Screenshot is { Length: > 0 } png && Screenshot(member.Team, connection.Name, png) is { } shot)
        {
            text.Append('\n').Append("  screenshot: ").Append(shot).Append(" (in the team's documents)");
        }

        if (answer.HandedOver is { } handed)
        {
            text.Clear().Append(handed.Sentence);
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
    /// <remarks>
    /// CON 14: a browser session's file is the PLAIN PLAYWRIGHT STORAGE STATE, <c>{ cookies, origins }</c>,
    /// and nothing else, so the member passes its path as it is as <c>storageState</c>; its hosts and expiry
    /// are in the sentence, and the expiry in the file's name (<see cref="HandoffName"/>), which the sweep
    /// reads after a Host restart. A token's file keeps its own shape - <c>{ kind, header, value, hosts,
    /// expiresAt }</c> - with the header named in the sentence.
    /// </remarks>
    private HandedOver Handoff(MemberInvocation invocation, WebsiteAsk ask, string connection, ConnectionTestSecret handoff)
    {
        string? kind = null, header = null, expires = null, state = null;
        IReadOnlyList<string> hosts = [];
        try
        {
            using var document = JsonDocument.Parse(handoff.Value);
            var root = document.RootElement;
            kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;
            header = root.TryGetProperty("header", out var h) ? h.GetString() : null;
            expires = root.TryGetProperty("expiresAt", out var e) ? e.GetString() : null;
            if (root.TryGetProperty("hosts", out var list) && list.ValueKind == JsonValueKind.Array) hosts = [.. list.EnumerateArray().Select(x => x.GetString() ?? "")];
            if (kind != "token")
            {
                state = root.TryGetProperty("storageState", out var s) && s.ValueKind == JsonValueKind.Object ? s.GetRawText() : null;
                if (state is null) throw new JsonException("no storageState");
            }
        }
        catch (JsonException)
        {
            return new HandedOver(false, null, "unreadable", $"{connection}: the session could not be read, so none was handed over.");
        }

        var asker = invocation.Work.Select(w => w.Source).FirstOrDefault(s => string.Equals(s, ask.Source, StringComparison.OrdinalIgnoreCase));
        if (Paths is null || asker is null || !ContainerId.TryParse(asker, out var to)
            || !string.Equals(to.Team, invocation.Member.Team, StringComparison.OrdinalIgnoreCase) || to.Equals(invocation.Member))
        {
            return new HandedOver(false, ask.Source, "not-a-member",
                $"{connection}: a session is handed only to a member of this team that asks for one; none was handed over.");
        }

        var until = DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.UtcNow.AddMinutes(30);
        string file;
        try
        {
            file = WriteHandoff(Paths.WorkspaceFor(to), HandoffName(connection, until), state ?? handoff.Value, runAs);
            _ = DeleteAtAsync(file, until);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new HandedOver(false, to.ToString(), "not-written",
                $"{connection}: the session could not be written to {to.Name}'s workspace ({HandoffFolder}: {exception.Message.TrimEnd('.')}), so none was handed over.");
        }

        return new HandedOver(true, to.ToString(), null, HandoffSentence(connection, kind, header, hosts, $"{to.Name}:{HandoffFolder}/{Path.GetFileName(file)}", until));
    }

    /// <summary>
    /// The sentence a handoff is answered with (CON 14): where the file is, the site's hosts, until when -
    /// and, for a browser session, in plain words, that the path is passed as it is as <c>storageState</c>;
    /// for a token, the header to send its value in.
    /// </summary>
    internal static string HandoffSentence(string connection, string? kind, string? header, IReadOnlyList<string> hosts, string where, DateTimeOffset until)
    {
        var good = until.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return kind == "token"
            ? $"{connection}: a token handoff for {string.Join(", ", hosts)} is at {where}, good until {good}: a JSON file whose `value` you send as the {header} header. It is deleted when it expires."
            : $"{connection}: a browser session for {string.Join(", ", hosts)} is at {where}, good until {good}. It is a Playwright storageState file: pass its path as it is as `storageState` (for example `browser.newContext({{ storageState: '<path>' }})`). It is deleted when it expires.";
    }

    /// <summary>
    /// A handoff's file name, <c>&lt;connection&gt;-&lt;random&gt;.until-&lt;yyyyMMddTHHmmssZ&gt;.json</c>: the
    /// expiry is in the name, so the sweep needs nothing from inside the file (CON 14).
    /// </summary>
    public static string HandoffName(string connection, DateTimeOffset until) =>
        $"{FileName(connection)}-{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6))}"
        + $".until-{until.ToUniversalTime().ToString(HandoffExpiryFormat, CultureInfo.InvariantCulture)}.json";

    private const string HandoffExpiryFormat = "yyyyMMdd'T'HHmmss'Z'";

    /// <summary>The expiry a handoff's name carries (<see cref="HandoffName"/>), or null when it carries none.</summary>
    public static DateTimeOffset? HandoffExpiry(string file)
    {
        var match = HandoffExpiryRun().Match(Path.GetFileName(file));
        return match.Success && DateTimeOffset.TryParseExact(match.Groups[1].Value, HandoffExpiryFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var until)
            ? until
            : null;
    }

    /// <summary>
    /// Writes a handed-over session as <paramref name="name"/> in the <see cref="HandoffFolder"/> of the
    /// member's <paramref name="workspace"/> and answers its path: the folder 0750 and the file 0640, both
    /// of the AGENT'S group, because the member runs as the agent and reads it by its group. The folder
    /// keeps setgid, so it takes the workspace's group (prepare-volume.sh makes every team folder setgid
    /// to the agent's group) - a mode set without the bit would drop it and leave the file the Host's own
    /// group - and <paramref name="runAs"/> gives both to the agent's group where the Host switches users,
    /// as every other file the Host writes for an agent. Expired handoffs in the folder go first.
    /// </summary>
    public static string WriteHandoff(string workspace, string name, string value, AgentLaunchUser? runAs)
        => WriteHandoff(workspace, name, value, runAs, static (folder, mode) => folder.UnixFileMode = mode);

    /// <summary>
    /// <see cref="WriteHandoff(string, string, string, AgentLaunchUser?)"/> with the folder's mode set by
    /// <paramref name="setMode"/>. A folder the Host does not own (one a member made, agent:agent) refuses
    /// the mode with EPERM; that is no reason to hand nothing over, so the folder keeps its own mode and the
    /// file is written if the folder lets the Host write - a member's 2770 folder of the agent's group does.
    /// </summary>
    public static string WriteHandoff(string workspace, string name, string value, AgentLaunchUser? runAs, Action<DirectoryInfo, UnixFileMode> setMode)
    {
        var folder = Path.Combine(workspace, HandoffFolder);
        var made = Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                setMode(made, UnixFileMode.SetGroup | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }

            runAs?.Share(folder);
        }

        SweepHandoffs(folder);
        var file = Path.Combine(folder, name);
        using var stream = new FileStream(file, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
        });
        stream.Write(Encoding.UTF8.GetBytes(value));
        runAs?.Share(stream.SafeFileHandle);
        return file;
    }

    /// <summary>
    /// Every handed-over session in <paramref name="folder"/> whose time is up, deleted: the expiry read
    /// from the file's NAME (<see cref="HandoffExpiry"/>), so a plain storage state is swept after a Host
    /// restart as well as the Host's own timer would have (CON 14). A file named before CON 14 carries its
    /// expiry inside, as <c>expiresAt</c>; one with neither is deleted.
    /// </summary>
    public static void SweepHandoffs(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                if (HandoffExpiry(file) is { } named)
                {
                    if (named <= DateTimeOffset.UtcNow) File.Delete(file);
                    continue;
                }

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

    /// <summary>
    /// AT THE HOST'S START, a member's handed-over sessions (CON 14): every one whose time is up is deleted
    /// (<see cref="SweepHandoffs"/>), and each still good is deleted when its time is up, read from its name -
    /// the timer the last Host set went with it. Never throws: a folder that cannot be read is left for the
    /// next handoff's sweep.
    /// </summary>
    public static void SweepHandoffsAtStart(string workspace)
    {
        var folder = Path.Combine(workspace, HandoffFolder);
        try
        {
            SweepHandoffs(folder);
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                if (HandoffExpiry(file) is { } until) _ = DeleteAtAsync(file, until);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
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

    [GeneratedRegex(@"\.until-(\d{8}T\d{6}Z)\.json$")]
    private static partial Regex HandoffExpiryRun();
}
