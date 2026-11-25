using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yawble.Core.Teams;

/// <summary>Runs one of the person's CLIs (gh, glab, az) on the host, signed in as they are, in a folder. Tests pass a fake.</summary>
public delegate Task<GitResult> HostCliRunner(string cli, string workingDirectory, IReadOnlyList<string> args, CancellationToken ct);

/// <summary>One pull request (a merge request on GitLab) as the app needs it: shown, then merged only if still open into the base.</summary>
public sealed record PullRequestInfo(long Number, string Title, string Head, string Base, bool Open, string Key);

/// <summary>
/// Pull requests on one provider, through the person's own CLI on this computer. Every call refuses with a
/// clear reason (an <see cref="ArgumentException"/>) when the CLI is missing, not signed in or answers something unreadable.
/// </summary>
public interface IPullRequestProvider
{
    RepoProviderKind Kind { get; }

    /// <summary>The CLI it runs (gh, glab, az).</summary>
    string Cli { get; }

    /// <summary>Opens one from <paramref name="head"/> into <paramref name="baseBranch"/>; what the CLI answered (its link).</summary>
    Task<string> OpenAsync(string repoPath, string head, string baseBranch, string title, string body, CancellationToken ct);

    Task<PullRequestInfo> ViewAsync(string repoPath, long number, CancellationToken ct);

    /// <summary>Merges it (a merge commit; never forced, never deleting the branch); what the CLI answered.</summary>
    Task<string> MergeAsync(string repoPath, long number, CancellationToken ct);

    /// <summary>The open ones by head branch ("#7 Title"); null when the CLI is missing, fails or answers something unreadable.</summary>
    Task<IReadOnlyDictionary<string, string>?> OpenListAsync(string repoPath, CancellationToken ct);

    /// <summary>
    /// The arguments (after <see cref="Cli"/>) that open one from <paramref name="head"/> into <paramref name="baseBranch"/>: an
    /// agent's pr_open, which the integration rules decide and the person may be asked about before it runs.
    /// </summary>
    IReadOnlyList<string> OpenArgs(string head, string baseBranch, string title, string body);

    /// <summary>The arguments (after <see cref="Cli"/>) that list the pull requests of a head branch in any state: an agent's pr_status.</summary>
    IReadOnlyList<string> StatusArgs(string head);
}

/// <summary>The pull request providers, chosen by the repository's provider: GitHub (gh), GitLab (glab), Azure DevOps (az repos).</summary>
public static class PullRequestProviders
{
    public static bool Supports(RepoProviderKind kind) => kind is RepoProviderKind.GitHub or RepoProviderKind.GitLab or RepoProviderKind.AzureDevOps;

    /// <summary>The provider for <paramref name="kind"/>, running its CLI through <paramref name="run"/>; null when there is none.</summary>
    public static IPullRequestProvider? For(RepoProviderKind kind, HostCliRunner run) => kind switch
    {
        RepoProviderKind.GitHub => new GitHubPullRequests(run),
        RepoProviderKind.GitLab => new GitLabMergeRequests(run),
        RepoProviderKind.AzureDevOps => new AzureDevOpsPullRequests(run),
        _ => null,
    };

    /// <summary>The CLI a provider's pull requests need, or null.</summary>
    public static string? CliOf(RepoProviderKind kind) => kind switch
    {
        RepoProviderKind.GitHub => "gh",
        RepoProviderKind.GitLab => "glab",
        RepoProviderKind.AzureDevOps => "az",
        _ => null,
    };

    /// <summary>How the person signs that CLI in on this computer.</summary>
    public static string SignInOf(string cli) => cli switch
    {
        "gh" => "gh auth login",
        "glab" => "glab auth login",
        "az" => "az login (with the azure-devops extension: az extension add --name azure-devops)",
        _ => cli + " login",
    };

    /// <summary>Whether a CLI is on this computer's PATH (no process is started). Tests pass their own to <see cref="TeamRepos.HostView"/>.</summary>
    public static Func<string, bool> IsInstalled { get; } = OnPath;

    private static bool OnPath(string cli)
    {
        var exts = OperatingSystem.IsWindows() ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries) : [""];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                try { if (File.Exists(Path.Combine(dir.Trim('"'), cli + ext.ToLowerInvariant())) || File.Exists(Path.Combine(dir.Trim('"'), cli + ext))) return true; }
                catch (ArgumentException) { }
            }
        }
        return false;
    }

    /// <summary>The person's CLIs on the host, with the variables that keep each from prompting.</summary>
    public static async Task<GitResult> RunOnHostAsync(string cli, string workingDirectory, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(cli) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = workingDirectory };
        switch (cli)
        {
            case "gh": psi.Environment["GH_PROMPT_DISABLED"] = "1"; break;
            case "glab": psi.Environment["NO_PROMPT"] = "1"; psi.Environment["GLAB_NO_UPDATE_CHECK"] = "1"; break;
            case "az": psi.Environment["AZURE_CORE_NO_COLOR"] = "1"; psi.Environment["AZURE_CORE_ONLY_SHOW_ERRORS"] = "1"; break;
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi) ?? throw new GitException($"{cli} could not be started");
            p.StandardInput.Close();
            var o = p.StandardOutput.ReadToEndAsync(ct);
            var e = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return new GitResult(p.ExitCode, (await o).Trim(), (await e).Trim());
        }
        catch (System.ComponentModel.Win32Exception) { return new GitResult(-1, "", $"{cli} is not installed on this computer"); }
    }
}

/// <summary>What the three providers share: running the CLI and turning its refusals into the person's reasons.</summary>
public abstract class CliPullRequests(HostCliRunner run) : IPullRequestProvider
{
    public abstract RepoProviderKind Kind { get; }
    public abstract string Cli { get; }
    public abstract Task<string> OpenAsync(string repoPath, string head, string baseBranch, string title, string body, CancellationToken ct);
    public abstract Task<PullRequestInfo> ViewAsync(string repoPath, long number, CancellationToken ct);
    public abstract Task<string> MergeAsync(string repoPath, long number, CancellationToken ct);
    public abstract IReadOnlyList<string> OpenArgs(string head, string baseBranch, string title, string body);
    public abstract IReadOnlyList<string> StatusArgs(string head);

    /// <summary>The arguments that list the open ones; <see cref="ReadList"/> reads the answer.</summary>
    protected abstract IReadOnlyList<string> ListArgs { get; }
    protected abstract IEnumerable<(string Head, long Number, string Title)> ReadList(JsonArray list);

    public async Task<IReadOnlyDictionary<string, string>?> OpenListAsync(string repoPath, CancellationToken ct)
    {
        var r = await run(Cli, repoPath, ListArgs, ct);
        if (r.ExitCode != 0) return null;
        try
        {
            if (JsonNode.Parse(r.Stdout) is not JsonArray list) return null;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (head, number, title) in ReadList(list))
                map.TryAdd(head, $"#{number} {title}".TrimEnd());
            return map;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Runs the CLI; its answer, redacted. A refusal says what to do (install, or sign in on this computer).</summary>
    protected async Task<string> RunAsync(string repoPath, IReadOnlyList<string> args, CancellationToken ct)
    {
        var r = await run(Cli, repoPath, args, ct);
        if (r.ExitCode == -1 && r.Stderr.Contains("not installed", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{Cli} is not available on this computer, so {RepoProviders.Label(Kind)} pull requests cannot be opened or merged from here; " +
                                        $"install {Cli} and sign it in ({PullRequestProviders.SignInOf(Cli)}), or push the branch (git_push_branch) and the person opens it on {RepoProviders.Label(Kind)}");
        if (r.ExitCode != 0)
            throw new ArgumentException($"{Cli} refused: {PersonGitPush.Redact(string.Join(' ', r.Stderr, r.Stdout).Trim())}" +
                                        $" (if {Cli} is not signed in on this computer: {PullRequestProviders.SignInOf(Cli)})");
        return PersonGitPush.Redact(r.Stdout);
    }

    protected static JsonObject Object(string text, string cli)
    {
        try { if (JsonNode.Parse(text) is JsonObject o) return o; }
        catch (JsonException) { }
        throw new ArgumentException($"{cli} answered something unreadable");
    }

    protected static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString() ?? "";

    protected static long Num(JsonNode? n) => n is JsonValue v && (v.TryGetValue<long>(out var l) || (v.TryGetValue<string>(out var s) && long.TryParse(s, out l))) ? l : 0;
}

/// <summary>GitHub through gh.</summary>
public sealed class GitHubPullRequests(HostCliRunner run) : CliPullRequests(run)
{
    public override RepoProviderKind Kind => RepoProviderKind.GitHub;
    public override string Cli => "gh";

    public override IReadOnlyList<string> OpenArgs(string head, string baseBranch, string title, string body) =>
        ["pr", "create", "--head", head, "--base", baseBranch, "--title", title, "--body", body];

    public override IReadOnlyList<string> StatusArgs(string head) =>
        ["pr", "list", "--head", head, "--state", "all", "--limit", "5", "--json", "number,title,state,url,reviewDecision,mergeable,statusCheckRollup"];

    public override Task<string> OpenAsync(string repoPath, string head, string baseBranch, string title, string body, CancellationToken ct) =>
        RunAsync(repoPath, OpenArgs(head, baseBranch, title, body), ct);

    public override async Task<PullRequestInfo> ViewAsync(string repoPath, long number, CancellationToken ct)
    {
        var text = await RunAsync(repoPath, ["pr", "view", number.ToString(), "--json", "number,title,headRefName,baseRefName,state,isDraft"], ct);
        var v = Object(text, Cli);
        return new(number, Str(v["title"]), Str(v["headRefName"]), Str(v["baseRefName"]), Str(v["state"]) == "OPEN", text);
    }

    public override async Task<string> MergeAsync(string repoPath, long number, CancellationToken ct) =>
        await RunAsync(repoPath, ["pr", "merge", number.ToString(), "--merge"], ct);

    protected override IReadOnlyList<string> ListArgs => ["pr", "list", "--state", "open", "--limit", "200", "--json", "number,title,headRefName"];

    protected override IEnumerable<(string, long, string)> ReadList(JsonArray list) =>
        list.OfType<JsonObject>().Where(p => p["headRefName"] is JsonValue).Select(p => (Str(p["headRefName"]), Num(p["number"]), Str(p["title"])));
}

/// <summary>GitLab merge requests through glab.</summary>
public sealed class GitLabMergeRequests(HostCliRunner run) : CliPullRequests(run)
{
    public override RepoProviderKind Kind => RepoProviderKind.GitLab;
    public override string Cli => "glab";

    public override IReadOnlyList<string> OpenArgs(string head, string baseBranch, string title, string body) =>
        ["mr", "create", "--source-branch", head, "--target-branch", baseBranch, "--title", title, "--description", body, "--yes"];

    public override IReadOnlyList<string> StatusArgs(string head) =>
        ["mr", "list", "--source-branch", head, "--all", "--per-page", "5", "--output", "json"];

    public override Task<string> OpenAsync(string repoPath, string head, string baseBranch, string title, string body, CancellationToken ct) =>
        RunAsync(repoPath, OpenArgs(head, baseBranch, title, body), ct);

    public override async Task<PullRequestInfo> ViewAsync(string repoPath, long number, CancellationToken ct)
    {
        var text = await RunAsync(repoPath, ["mr", "view", number.ToString(), "--output", "json"], ct);
        var v = Object(text, Cli);
        return new(number, Str(v["title"]), Str(v["source_branch"]), Str(v["target_branch"]), Str(v["state"]) == "opened",
            $"{Num(v["iid"])}|{Str(v["title"])}|{Str(v["source_branch"])}|{Str(v["target_branch"])}|{Str(v["state"])}|{Str(v["sha"])}");
    }

    public override Task<string> MergeAsync(string repoPath, long number, CancellationToken ct) =>
        RunAsync(repoPath, ["mr", "merge", number.ToString(), "--yes"], ct);

    protected override IReadOnlyList<string> ListArgs => ["mr", "list", "--per-page", "100", "--output", "json"];

    protected override IEnumerable<(string, long, string)> ReadList(JsonArray list) =>
        list.OfType<JsonObject>().Where(p => p["source_branch"] is JsonValue && (p["state"] is null || Str(p["state"]) == "opened"))
            .Select(p => (Str(p["source_branch"]), Num(p["iid"]), Str(p["title"])));
}

/// <summary>Azure DevOps pull requests through az repos (the organization and project detected from the origin).</summary>
public sealed class AzureDevOpsPullRequests(HostCliRunner run) : CliPullRequests(run)
{
    public override RepoProviderKind Kind => RepoProviderKind.AzureDevOps;
    public override string Cli => "az";

    public override IReadOnlyList<string> OpenArgs(string head, string baseBranch, string title, string body) =>
        ["repos", "pr", "create", "--source-branch", head, "--target-branch", baseBranch, "--title", title,
            .. body.Length > 0 ? new[] { "--description", body } : [], "--detect", "true", "--output", "json"];

    public override IReadOnlyList<string> StatusArgs(string head) =>
        ["repos", "pr", "list", "--source-branch", head, "--status", "all", "--top", "5", "--detect", "true", "--output", "json"];

    public override async Task<string> OpenAsync(string repoPath, string head, string baseBranch, string title, string body, CancellationToken ct)
    {
        var text = await RunAsync(repoPath, OpenArgs(head, baseBranch, title, body), ct);
        try
        {
            if (JsonNode.Parse(text) is JsonObject o && Num(o["pullRequestId"]) is > 0 and var id) return $"pull request {id}";
        }
        catch (JsonException) { }
        return text;
    }

    public override async Task<PullRequestInfo> ViewAsync(string repoPath, long number, CancellationToken ct)
    {
        var text = await RunAsync(repoPath, ["repos", "pr", "show", "--id", number.ToString(), "--detect", "true", "--output", "json"], ct);
        var v = Object(text, Cli);
        static string Branch(string r) => r.StartsWith("refs/heads/", StringComparison.Ordinal) ? r["refs/heads/".Length..] : r;
        return new(number, Str(v["title"]), Branch(Str(v["sourceRefName"])), Branch(Str(v["targetRefName"])), Str(v["status"]) == "active",
            $"{number}|{Str(v["title"])}|{Str(v["sourceRefName"])}|{Str(v["targetRefName"])}|{Str(v["status"])}|{Str(v["lastMergeSourceCommit"]?["commitId"])}");
    }

    public override async Task<string> MergeAsync(string repoPath, long number, CancellationToken ct)
    {
        await RunAsync(repoPath, ["repos", "pr", "update", "--id", number.ToString(), "--status", "completed", "--detect", "true", "--output", "json"], ct);
        return $"pull request {number} completed";
    }

    protected override IReadOnlyList<string> ListArgs => ["repos", "pr", "list", "--status", "active", "--top", "200", "--detect", "true", "--output", "json"];

    protected override IEnumerable<(string, long, string)> ReadList(JsonArray list) =>
        list.OfType<JsonObject>().Where(p => p["sourceRefName"] is JsonValue)
            .Select(p => (Str(p["sourceRefName"]) is var r && r.StartsWith("refs/heads/", StringComparison.Ordinal) ? r["refs/heads/".Length..] : Str(p["sourceRefName"]),
                Num(p["pullRequestId"]), Str(p["title"])));
}
