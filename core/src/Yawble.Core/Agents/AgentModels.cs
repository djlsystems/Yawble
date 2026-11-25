using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Yawble.Core.Policy;

namespace Yawble.Core.Agents;

/// <summary>
/// Where a run's model came from: the member's own setting, the agent's default, the model the person's own CLI settings
/// name (~/.codex/config.toml and the like, <see cref="ICliModelSettings"/>), or the CLI's own default.
/// </summary>
public static class ModelSources
{
    public const string Member = "member", Agent = "agent", Settings = "settings", Cli = "cli";
}

/// <summary>The model a run uses and why. <see cref="Model"/> is empty only for <see cref="ModelSources.Cli"/>: the CLI picks its own.</summary>
public sealed record ModelResolution(string Model, string Source);

/// <summary>Default model resolution: member override, then the agent's default, then the CLI settings' model, then the CLI's default.</summary>
public static class AgentModels
{
    public static ModelResolution Resolve(string? memberModel, string? agentDefault, string? settingsModel = null)
    {
        if (!string.IsNullOrWhiteSpace(memberModel)) return new(memberModel.Trim(), ModelSources.Member);
        if (!string.IsNullOrWhiteSpace(agentDefault)) return new(agentDefault.Trim(), ModelSources.Agent);
        if (!string.IsNullOrWhiteSpace(settingsModel) && IsValidModel(settingsModel.Trim())) return new(settingsModel.Trim(), ModelSources.Settings);
        return new("", ModelSources.Cli);
    }

    /// <summary>The settings' model, asked only when nothing above it names one (the file is not read otherwise).</summary>
    public static ModelResolution Resolve(string? memberModel, string? agentDefault, AgentCli cli, ICliModelSettings? settings) =>
        !string.IsNullOrWhiteSpace(memberModel) || !string.IsNullOrWhiteSpace(agentDefault) || settings is null
            ? Resolve(memberModel, agentDefault)
            : Resolve(memberModel, agentDefault, settings.DefaultModel(cli));

    /// <summary>How a resolution is shown: "Agent default (opus)", "Your Codex settings (gpt-6.1-sol)", "The CLI's default".</summary>
    public static string Label(ModelResolution r, AgentCli cli) => r.Source switch
    {
        ModelSources.Agent => $"Agent default ({r.Model})",
        ModelSources.Settings => $"Your {cli.DisplayName()} settings ({r.Model})",
        ModelSources.Cli => "The CLI's default",
        _ => r.Model,
    };

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
/// The models a picker offers per CLI: the union of four sources, in this order, each model once (the first source that
/// names it is the one shown):
/// <list type="number">
/// <item>the CLI's own listing (<see cref="ICliModelListing"/>: Grok's keeper's last <c>grok models</c>, Codex's models
/// cache, <c>copilot --help</c>); never a prompt or a sign-in, with a timeout;</item>
/// <item>the model the person's own CLI settings name (<see cref="ICliModelSettings"/>, read only);</item>
/// <item>the models this CLI's runs used over the last 90 days (<see cref="IModelsUsed"/>, usage_events);</item>
/// <item>the built-in list (<see cref="Known"/>).</item>
/// </list>
/// A source that fails or is not given adds nothing. The result is kept per CLI for <see cref="CacheFor"/>; a refresh asks
/// again. The person can still type any other model name (custom text), which is passed exactly as typed.
/// </summary>
public sealed class ModelCatalog(
    ICliModelListing? listing = null,
    ICliModelSettings? settings = null,
    IModelsUsed? used = null,
    TimeProvider? clock = null)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RunsWithin = TimeSpan.FromDays(90);

    /// <summary>One model and where it comes from (<see cref="ModelListFrom"/>), with the picker's label for that source.</summary>
    public sealed record Item(string Model, string From, string FromLabel);

    /// <summary><see cref="Source"/> is the first item's source (the best one that named any model).</summary>
    public sealed record Listing(IReadOnlyList<string> Models, string Source, IReadOnlyList<Item> Items);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Dictionary<AgentCli, (DateTimeOffset At, Listing Listing)> _cache = [];

    /// <summary>The person's CLI settings reader this catalog uses (also for model resolution), or null.</summary>
    public ICliModelSettings? Settings => settings;

    /// <summary>The list for <paramref name="cli"/>: from the cache when it is younger than <see cref="CacheFor"/>, unless <paramref name="refresh"/>.</summary>
    public async Task<Listing> ListAsync(AgentCli cli, bool refresh, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            if (!refresh && _cache.TryGetValue(cli, out var hit) && now - hit.At < CacheFor) return hit.Listing;
        }
        var items = new List<Item>();
        void Add(IEnumerable<string>? models, string from)
        {
            foreach (var m in models ?? [])
            {
                var model = m.Trim();
                if (!AgentModels.IsValidModel(model) || items.Any(i => string.Equals(i.Model, model, StringComparison.Ordinal))) continue;
                items.Add(new Item(model, from, ModelListFrom.Label(from, cli)));
            }
        }
        Add(await Safe(async () => listing is null ? null : await listing.ListAsync(cli, ct)), ModelListFrom.Cli);
        Add(await Safe(() => Task.FromResult<IReadOnlyList<string>?>(settings?.DefaultModel(cli) is { } s ? [s] : null)), ModelListFrom.Settings);
        Add(await Safe(() => Task.FromResult<IReadOnlyList<string>?>(used?.Used(cli, RunsWithin))), ModelListFrom.Runs);
        Add(Known(cli), ModelListFrom.Builtin);
        var result = new Listing([.. items.Select(i => i.Model)], items.Count > 0 ? items[0].From : ModelListFrom.Builtin, items);
        lock (_lock) _cache[cli] = (now, result);
        return result;

        async Task<IReadOnlyList<string>?> Safe(Func<Task<IReadOnlyList<string>?>> source)
        {
            try { return await source(); }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Logs.Diag.Debug("agents", $"a model list source for {cli.Id()} failed: {e.GetType().Name}: {e.Message}");
                return null;
            }
        }
    }

    /// <summary>The built-in list: the current names, last in the merge (the other sources come first).</summary>
    public static IReadOnlyList<string> Known(AgentCli cli) => cli switch
    {
        AgentCli.Claude => AgentModels.ClaudeModels,
        AgentCli.Codex => CodexModels,
        AgentCli.Grok => GrokModels,
        AgentCli.Copilot => CopilotModels,
        AgentCli.Antigravity => AntigravityModels,
        _ => [],
    };

    public static IReadOnlyList<string> CodexModels { get; } = ["gpt-6.1-sol", "gpt-6.1-terra"];
    public static IReadOnlyList<string> GrokModels { get; } = ["grok-4.7-build", "grok-4.6-build"];
    public static IReadOnlyList<string> CopilotModels { get; } = ["mai-code-2-flash"];
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

/// <summary>
/// The install command each CLI's maker documents for Windows, run in a visible terminal when the person asks (Settings >
/// Agents, Install): the person sees it, answers anything it asks, and signs in afterwards. Null when the maker offers no
/// command (Antigravity installs from its site). The app finds an installed CLI without PATH (each detector also looks
/// in its maker's install folder); PATH matters to the person, who runs the CLI once to sign in. Claude Code's installer
/// leaves %USERPROFILE%\.local\bin off PATH, so its command adds it to the person's PATH when missing. The PowerShell
/// window stays open, with PATH as it is now, for that sign-in.
/// </summary>
public static class InstallCommands
{
    /// <summary>Puts %USERPROFILE%\.local\bin on the person's PATH (and this window's) when it is not there yet.</summary>
    public const string ClaudeOnPath =
        "$b = Join-Path $HOME '.local\\bin'; $p = [string][Environment]::GetEnvironmentVariable('Path', 'User'); " +
        "if (($p -split ';') -notcontains $b) { [Environment]::SetEnvironmentVariable('Path', (($p.TrimEnd(';') + ';' + $b).TrimStart(';')), 'User'); " +
        "Write-Host ('Added ' + $b + ' to your PATH.') }; if (($env:Path -split ';') -notcontains $b) { $env:Path = $env:Path + ';' + $b }; " +
        "Write-Host 'Next: run claude here to sign in.'";

    public static IReadOnlyList<string>? For(AgentCli cli) => cli switch
    {
        AgentCli.Claude => PowerShell("irm https://claude.ai/install.ps1 | iex; " + ClaudeOnPath),
        AgentCli.Grok => PowerShell("irm https://x.ai/cli/install.ps1 | iex; Write-Host 'Next: run grok login here to sign in.'"),
        AgentCli.Codex => ["npm", "install", "-g", "@openai/codex"],
        AgentCli.Copilot => ["winget", "install", "--id", "GitHub.Copilot", "--exact", "--source", "winget"],
        _ => null,
    };

    private static IReadOnlyList<string> PowerShell(string script) => ["powershell", "-NoExit", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script];
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
