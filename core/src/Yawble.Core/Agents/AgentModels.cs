using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Yawble.Core.Policy;

namespace Yawble.Core.Agents;

/// <summary>Where a run's model came from: the member's own setting, the agent's default, or the CLI's own default.</summary>
public static class ModelSources
{
    public const string Member = "member", Agent = "agent", Cli = "cli";
}

/// <summary>The model a run uses and why. <see cref="Model"/> is empty only for <see cref="ModelSources.Cli"/>: the CLI picks its own.</summary>
public sealed record ModelResolution(string Model, string Source);

/// <summary>Default model resolution: member override, then the agent's default, then the CLI's default.</summary>
public static class AgentModels
{
    public static ModelResolution Resolve(string? memberModel, string? agentDefault)
    {
        if (!string.IsNullOrWhiteSpace(memberModel)) return new(memberModel.Trim(), ModelSources.Member);
        if (!string.IsNullOrWhiteSpace(agentDefault)) return new(agentDefault.Trim(), ModelSources.Agent);
        return new("", ModelSources.Cli);
    }

    /// <summary>A model name is passed as one argv item; refuse what is not a plain model id (also what the compiler accepts).</summary>
    public static bool IsValidModel(string model) => model.Length is > 0 and <= 128 && Regex.IsMatch(model, "^[A-Za-z0-9._:/@-]+$");

    /// <summary>The models the app itself knows for Claude Code (its CLI cannot list them): the aliases, then full ids.</summary>
    public static IReadOnlyList<string> ClaudeModels { get; } =
        ["opus", "sonnet", "haiku", "claude-fable-5-2", "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"];
}

/// <summary>The default model per built-in agent (agent_defaults, 0033). Empty or absent: the CLI's own default.</summary>
public sealed class AgentDefaultsStore(string connectionString, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>A default changed (which changes every member following it): the old and new value, "" for the CLI's default.</summary>
    public event Action<AgentCli, string, string>? Changed;

    public string Get(AgentCli cli)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT default_model FROM agent_defaults WHERE cli = $c;";
        cmd.Parameters.AddWithValue("$c", cli.Id());
        return cmd.ExecuteScalar() as string ?? "";
    }

    public IReadOnlyDictionary<AgentCli, string> All() =>
        Enum.GetValues<AgentCli>().ToDictionary(c => c, Get);

    /// <summary>Sets the default; returns the old one. Raises <see cref="Changed"/> only when it differs.</summary>
    public string Set(AgentCli cli, string model)
    {
        model = model.Trim();
        if (model.Length > 0 && !AgentModels.IsValidModel(model))
            throw new ArgumentException("a model is 1-128 of [A-Za-z0-9._:/@-]");
        var before = Get(cli);
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO agent_defaults (cli, default_model, updated_at) VALUES ($c, $m, $at)
                ON CONFLICT (cli) DO UPDATE SET default_model = excluded.default_model, updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$c", cli.Id());
            cmd.Parameters.AddWithValue("$m", model);
            cmd.Parameters.AddWithValue("$at", _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
        if (!string.Equals(before, model, StringComparison.Ordinal)) Changed?.Invoke(cli, before, model);
        return before;
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(connectionString);
        c.Open();
        return c;
    }
}

/// <summary>
/// The known models per CLI: a list the app carries, never asked of the CLI. Asking ("grok models", "codex models") would
/// run the CLI on the host, outside the sandbox, with the person's credentials, hidden and automatically;
/// and none of those commands has been checked against its CLI's own help. A list here may lag the
/// vendor's: the person can always type any other model name (custom text), which is passed exactly as typed.
/// </summary>
public sealed class ModelCatalog
{
    public sealed record Listing(IReadOnlyList<string> Models, string Source);

    /// <summary>The list for <paramref name="cli"/>. Pure: starts no process and asks nothing.</summary>
    public Listing List(AgentCli cli) => new(Known(cli), "app");

    public static IReadOnlyList<string> Known(AgentCli cli) => cli switch
    {
        AgentCli.Claude => AgentModels.ClaudeModels,
        AgentCli.Codex => CodexModels,
        AgentCli.Grok => GrokModels,
        AgentCli.Copilot => CopilotModels,
        AgentCli.Antigravity => AntigravityModels,
        _ => [],
    };

    public static IReadOnlyList<string> CodexModels { get; } = ["gpt-5.1-codex", "gpt-5.1-codex-mini", "gpt-5-codex", "gpt-5.1", "gpt-5", "o3", "o4-mini"];
    public static IReadOnlyList<string> GrokModels { get; } = ["grok-code-fast-1", "grok-4", "grok-4-fast", "grok-3", "grok-3-mini"];
    public static IReadOnlyList<string> CopilotModels { get; } = ["claude-sonnet-4.5", "claude-haiku-4.5", "gpt-5", "gpt-5-mini", "gpt-5.1-codex"];
    public static IReadOnlyList<string> AntigravityModels { get; } = ["gemini-3-pro", "gemini-2.5-pro", "gemini-2.5-flash"];
}

/// <summary>The command line as recorded on a run: the prompt cut, secrets and run tokens redacted.</summary>
public static class RunArgv
{
    public const int PromptLimit = 200;
    public const string Redacted = "<redacted>";

    private static readonly Regex TokenShaped = new(@"(sk-[A-Za-z0-9_-]{8,}|gh[opsu]_[A-Za-z0-9_]{8,}|xai-[A-Za-z0-9_-]{8,}|Bearer\s+\S+|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_.-]+)", RegexOptions.Compiled);

    public static IReadOnlyList<string> ForRecord(IReadOnlyList<string> argv, string? prompt, IEnumerable<string?> secrets)
    {
        var secretList = secrets.Where(s => !string.IsNullOrEmpty(s) && s.Length >= 4).Select(s => s!).ToList();
        var result = new List<string>(argv.Count);
        foreach (var a in argv)
        {
            var x = a;
            if (prompt is not null && a == prompt && a.Length > PromptLimit) x = a[..PromptLimit] + "…";
            foreach (var s in secretList) x = x.Replace(s, Redacted, StringComparison.Ordinal);
            result.Add(TokenShaped.Replace(x, Redacted));
        }
        return result;
    }
}

/// <summary>The CLI's own update command, run in a visible terminal by the person (never silent, never in tests).</summary>
public static class UpdateCommands
{
    /// <summary>Null when the app does not know an update command for the CLI.</summary>
    public static IReadOnlyList<string>? For(AgentCli cli, string executable) => cli switch
    {
        AgentCli.Claude => [executable, "update"],
        AgentCli.Grok => [executable, "update"],
        AgentCli.Codex => ["npm", "install", "-g", "@openai/codex@latest"],
        AgentCli.Copilot => [executable, "update"],
        AgentCli.Antigravity => [executable, "update"],
        _ => null,
    };
}

/// <summary>Opens a visible terminal that runs a command and stays open. A fake in tests.</summary>
public interface IUpdateTerminal
{
    void Open(string title, IReadOnlyList<string> command);
}

/// <summary>Windows: a new console window (cmd /k) so the person sees the update and its result.</summary>
public sealed class WindowsUpdateTerminal : IUpdateTerminal
{
    public void Open(string title, IReadOnlyList<string> command)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("opening a terminal for an update is supported on Windows only");
        // cmd /k "<"item" "item">": each item quoted (cmd strips the outer pair), so no metacharacter is interpreted.
        var line = string.Join(' ', command.Select(c => "\"" + c.Replace("\"", "") + "\""));
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = true, Arguments = "/k \"" + line + "\"" });
    }
}
