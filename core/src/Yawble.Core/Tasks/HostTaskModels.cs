using System.Text.RegularExpressions;

namespace Yawble.Core.Tasks;

public static class PinModes
{
    public const string Files = "files", Commit = "commit";
}

/// <summary>What the person defines. Nothing in it ever comes from an agent.</summary>
public sealed record HostTaskDef(
    string Name, string Repo, string Program, IReadOnlyList<string> Args, string Workdir, int TimeoutSeconds,
    IReadOnlyList<string> EnvNames, IReadOnlyList<string> Watch, string PinMode = PinModes.Files, bool CommandLineOnly = false)
{
    public static HostTaskDef Make(string name, string program, IEnumerable<string>? args = null, string repo = "main", string workdir = ".",
        int timeoutSeconds = 600, IEnumerable<string>? envNames = null, IEnumerable<string>? watch = null, string pinMode = PinModes.Files,
        bool commandLineOnly = false) =>
        new(name, repo, program, (args ?? []).ToArray(), workdir, timeoutSeconds, (envNames ?? []).ToArray(), (watch ?? []).ToArray(), pinMode, commandLineOnly);
}

public sealed record HostTask(long Id, long TeamId, HostTaskDef Def, string UpdatedAt, string? UpdatedBy);

public sealed record ApprovalRow(long TaskId, string DefinitionHash, string FilesHash, string DefinitionJson, string FilesJson, string CommitSha,
    string? ApprovedBy, string? Via, string ApprovedAt);

public sealed record TaskRunRow(long Id, long? TaskId, long TeamId, string TaskName, string State, string StartedAt, string? EndedAt, string? CommitSha,
    string? HeadAfter, string? DefinitionHash, string? FilesHash, string? ProgramResolved, int? ExitCode, bool TimedOut, string? KilledBy, long? OutputBytes,
    bool Truncated, string? LogPath, string? LogSha256, bool? DirtyAfter, long? AskId, string? RequestedBy, string? MissingEnv);

/// <summary>A refusal before any ask: which precondition and what the person must fix.</summary>
public sealed record TaskRefusal(int Precondition, string Message);

public enum RequestKind { Refused, Asked, AlreadyWaiting, Started }

public sealed record TaskRequestResult(RequestKind Kind, string Text, long? AskId = null, long? RunId = null, int? Precondition = null);

/// <summary>The message that wakes the requesting agent once a task run, or an ask about it, is over.</summary>
public sealed record TaskWake(long TeamId, string Member, long Workflow, long CausationSeq, string Text);

/// <summary>Validation of a definition. Throws <see cref="ArgumentException"/> with the reason.</summary>
public static partial class HostTaskRules
{
    public const int MinTimeoutSeconds = 10, MaxTimeoutSeconds = 7200;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,39}$")]
    private static partial Regex NameShape();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvShape();

    /// <summary>Variables an app run or a sign-in lives in: a task can never be handed one, whatever the person types.</summary>
    private static readonly string[] CredentialPrefixes =
        ["YAWBLE_", "ANTHROPIC_", "OPENAI_", "XAI_", "GROK_", "GEMINI_", "GOOGLE_API", "COPILOT_", "CODEX_", "CLAUDE_CODE_", "HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY"];

    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase) { "cmd", "pwsh", "powershell", "bash", "sh", "zsh", "dash", "fish" };

    public static bool IsCredentialName(string name) =>
        CredentialPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static void Validate(HostTaskDef d)
    {
        if (!NameShape().IsMatch(d.Name)) throw new ArgumentException("the name is 1-40 characters of a-z, 0-9, '.', '_' or '-', starting with a letter or digit");
        if (string.IsNullOrWhiteSpace(d.Repo)) throw new ArgumentException("which repository?");
        if (string.IsNullOrWhiteSpace(d.Program)) throw new ArgumentException("which program?");
        if (d.TimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
            throw new ArgumentException($"the time limit is {MinTimeoutSeconds} to {MaxTimeoutSeconds} seconds; there is no 'no limit'");
        if (d.PinMode is not (PinModes.Files or PinModes.Commit)) throw new ArgumentException("pin mode is 'files' or 'commit'");
        if (d.Args.Any(a => a.Contains('\0') || a.Contains('\n') || a.Contains('\r'))) throw new ArgumentException("an argument holds a line break or a NUL");
        foreach (var n in d.EnvNames)
        {
            if (!EnvShape().IsMatch(n)) throw new ArgumentException($"'{n}' is not an environment variable name (names only, never values)");
            if (IsCredentialName(n)) throw new ArgumentException($"{n} is the app's own and can never be passed to a task");
        }
        if (!IsInsideRelative(d.Workdir)) throw new ArgumentException("the working folder must be a relative path inside the repository");
        foreach (var w in d.Watch)
            if (!IsInsideRelative(w.Replace('*', 'x').Replace('?', 'x'))) throw new ArgumentException($"watch path '{w}' must be relative and inside the repository");
        var program = Path.GetFileNameWithoutExtension(d.Program.Replace('\\', '/').Split('/')[^1]);
        if (Shells.Contains(program) && d.Args.Any(IsInlineCommandFlag))
            throw new ArgumentException("a shell with an inline command is not allowed (it could not be pinned): put the commands in a script in the repository and run it with -File or by path");
    }

    private static bool IsInlineCommandFlag(string a) =>
        a.Equals("-c", StringComparison.OrdinalIgnoreCase) || a.Equals("/c", StringComparison.OrdinalIgnoreCase) || a.Equals("/k", StringComparison.OrdinalIgnoreCase)
        || a.StartsWith("-command", StringComparison.OrdinalIgnoreCase) || a.StartsWith("-encodedcommand", StringComparison.OrdinalIgnoreCase)
        || a.Equals("-ec", StringComparison.OrdinalIgnoreCase) || a.Equals("-e", StringComparison.OrdinalIgnoreCase)
        || (a.StartsWith('-') && !a.StartsWith("--") && a.Length > 2 && a.Skip(1).All(char.IsLetter) && a.Contains('c') && a.Length <= 4);

    /// <summary>A relative path with no rooted part and no ".." segment.</summary>
    public static bool IsInsideRelative(string path)
    {
        if (path.Length == 0 || Path.IsPathRooted(path) || path.Contains(':')) return false;
        return !path.Replace('\\', '/').Split('/').Any(s => s == "..");
    }
}
