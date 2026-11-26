using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Yawble.Core.Policy;

namespace Yawble.Core.Agents;

/// <summary>Where a model in a picker's list comes from (<see cref="ModelCatalog"/>), in the order the sources are merged.</summary>
public static class ModelListFrom
{
    public const string Cli = "cli", Settings = "settings", Runs = "runs", Builtin = "builtin";

    /// <summary>What the picker says about the source: "the CLI", "your Codex settings", "your runs", "built-in".</summary>
    public static string Label(string from, AgentCli cli) => from switch
    {
        Cli => "the CLI",
        Settings => $"your {cli.DisplayName()} settings",
        Runs => "your runs",
        _ => "built-in",
    };
}

/// <summary>The model the person's own CLI settings name as its default. Read only, read fresh at each call.</summary>
public interface ICliModelSettings
{
    /// <summary>The model the settings file names (a valid model id), or null when there is none or it cannot be read.</summary>
    string? DefaultModel(AgentCli cli);
}

/// <summary>A CLI's own list of models, asked without prompting or signing in. Null: the CLI gives none, or it failed.</summary>
public interface ICliModelListing
{
    Task<IReadOnlyList<string>?> ListAsync(AgentCli cli, CancellationToken ct);
}

/// <summary>The models runs of a CLI really used (the gateway's usage_events), newest first.</summary>
public interface IModelsUsed
{
    IReadOnlyList<string> Used(AgentCli cli, TimeSpan within);
}

/// <summary>
/// The person's CLI settings files, read only (never written, never created), in the person's own home:
/// <list type="bullet">
/// <item>Codex: <c>$CODEX_HOME/config.toml</c> (else <c>~/.codex/config.toml</c>), the top-level <c>model</c>.</item>
/// <item>Grok: <c>$GROK_HOME/config.toml</c> (else <c>~/.grok/config.toml</c>), the top-level <c>model</c> or <c>default_model</c>;
/// then <c>~/.grok/user-settings.json</c>, <c>defaultModel</c> or <c>model</c>.</item>
/// <item>GitHub Copilot: <c>$COPILOT_HOME</c> (else <c>~/.copilot</c>) <c>settings.json</c>, then <c>config.json</c>, <c>model</c>.</item>
/// <item>Antigravity: <c>~/.gemini/antigravity-cli/settings.json</c>, <c>model</c> (a name, or an object with <c>name</c>).</item>
/// <item>Claude Code: <c>$CLAUDE_CONFIG_DIR/settings.json</c> (else <c>~/.claude/settings.json</c>), <c>model</c>.</item>
/// </list>
/// A name that is not a plain model id (<see cref="AgentModels.IsValidModel"/>) is ignored.
/// </summary>
public sealed partial class CliModelSettings(string home, Func<string, string?> env) : ICliModelSettings
{
    private const int ReadLimit = 1024 * 1024;

    public static CliModelSettings ForThisComputer() =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable);

    public string? DefaultModel(AgentCli cli) => cli switch
    {
        AgentCli.Codex => TomlModel(Path.Combine(Dir("CODEX_HOME", ".codex"), "config.toml"), "model"),
        AgentCli.Grok => TomlModel(Path.Combine(Dir("GROK_HOME", ".grok"), "config.toml"), "model", "default_model")
            ?? JsonModel(Path.Combine(home, ".grok", "user-settings.json"), "defaultModel", "model"),
        AgentCli.Copilot => JsonModel(Path.Combine(Dir("COPILOT_HOME", ".copilot"), "settings.json"), "model")
            ?? JsonModel(Path.Combine(Dir("COPILOT_HOME", ".copilot"), "config.json"), "model"),
        AgentCli.Antigravity => JsonModel(Path.Combine(home, ".gemini", "antigravity-cli", "settings.json"), "model"),
        AgentCli.Claude => JsonModel(Path.Combine(Dir("CLAUDE_CONFIG_DIR", ".claude"), "settings.json"), "model"),
        _ => null,
    };

    private string Dir(string variable, string folder) =>
        env(variable) is { Length: > 0 } v && Path.IsPathRooted(v) ? v : Path.Combine(home, folder);

    /// <summary>A top-level key (before the first [table]) of a TOML file, as a basic or literal string.</summary>
    public static string? TomlModel(string file, params string[] keys)
    {
        if (ReadText(file) is not { } text) return null;
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('[')) break; // the top level ends at the first table
            var m = TomlString().Match(line);
            if (m.Success && keys.Contains(m.Groups["key"].Value) && !found.ContainsKey(m.Groups["key"].Value))
                found[m.Groups["key"].Value] = m.Groups["basic"].Success ? m.Groups["basic"].Value : m.Groups["literal"].Value;
        }
        foreach (var key in keys)
            if (found.TryGetValue(key, out var v) && Valid(v) is { } model) return model;
        return null;
    }

    /// <summary>A top-level key of a JSON settings file (comments and trailing commas allowed): a string, or an object's "name".</summary>
    public static string? JsonModel(string file, params string[] keys)
    {
        if (ReadText(file) is not { } text) return null;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in keys)
            {
                if (!doc.RootElement.TryGetProperty(key, out var v)) continue;
                var name = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Object when v.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String => n.GetString(),
                    _ => null,
                };
                if (Valid(name) is { } model) return model;
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string? Valid(string? model) =>
        model?.Trim() is { Length: > 0 } m && AgentModels.IsValidModel(m) ? m : null;

    /// <summary>Reads a small file, shared and read-only; null when it is missing, too big or cannot be read.</summary>
    public static string? ReadText(string file, int limit = ReadLimit)
    {
        try
        {
            if (!File.Exists(file)) return null;
            using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (f.Length > limit) return null;
            using var reader = new StreamReader(f, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    [GeneratedRegex("""^(?<key>[A-Za-z0-9_-]+)\s*=\s*(?:"(?<basic>[^"\\]*)"|'(?<literal>[^']*)')\s*(?:#.*)?$""")]
    private static partial Regex TomlString();
}

/// <summary>
/// Each CLI's own list of models, where it gives one without a prompt or a sign-in:
/// <list type="bullet">
/// <item>Grok: what the keeper's own <c>grok models</c> run printed last (<see cref="Credentials.GrokKeeper.ModelsOutput"/>; it is
/// never run for a list), and the <c>models_cache.json</c> grok keeps in its home.</item>
/// <item>Codex: the <c>models_cache.json</c> Codex keeps in <c>$CODEX_HOME</c> (else <c>~/.codex</c>): each model's
/// <c>slug</c>, the ones Codex lists (visibility "list"), by Codex's own priority. Codex has no listing command.</item>
/// <item>GitHub Copilot: <c>copilot --help</c>, the choices of <c>--model</c>, run with a throwaway home and stdin closed
/// (<see cref="RealCliProcessRunner"/>) and a timeout, so it can neither prompt nor write the person's Copilot folder.</item>
/// <item>Antigravity and Claude Code: none (no listing command and no models cache).</item>
/// </list>
/// Files are only read. A listing that fails, times out or prints nothing usable is null, and the other sources stand.
/// </summary>
public sealed partial class CliModelListing(
    string home,
    Func<string, string?> env,
    ICliProcessRunner runner,
    Func<AgentCli, string?> executable,
    Func<string?>? grokModelsOutput = null,
    TimeSpan? timeout = null) : ICliModelListing
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private const int CacheReadLimit = 8 * 1024 * 1024;

    public async Task<IReadOnlyList<string>?> ListAsync(AgentCli cli, CancellationToken ct)
    {
        IReadOnlyList<string>? list = cli switch
        {
            AgentCli.Grok => Grok(),
            AgentCli.Codex => CodexCache(Path.Combine(Dir("CODEX_HOME", ".codex"), "models_cache.json")),
            AgentCli.Copilot => await CopilotHelpAsync(ct),
            _ => null,
        };
        return list is { Count: > 0 } ? list : null;
    }

    private string Dir(string variable, string folder) =>
        env(variable) is { Length: > 0 } v && Path.IsPathRooted(v) ? v : Path.Combine(home, folder);

    private IReadOnlyList<string>? Grok()
    {
        var models = new List<string>();
        if (grokModelsOutput?.Invoke() is { Length: > 0 } printed) models.AddRange(ParseListing(printed));
        if (CliModelSettings.ReadText(Path.Combine(Dir("GROK_HOME", ".grok"), "models_cache.json"), CacheReadLimit) is { } cache)
            models.AddRange(JsonModels(cache));
        return models.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Codex's models_cache.json: {"models": [{"slug", "visibility", "priority", ...}]}.</summary>
    public static IReadOnlyList<string>? CodexCache(string file)
    {
        if (CliModelSettings.ReadText(file, CacheReadLimit) is not { } text) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Array) return null;
            var listed = new List<(string Slug, int Priority, int Index)>();
            var i = 0;
            foreach (var m in models.EnumerateArray())
            {
                i++;
                if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("slug", out var slug) || slug.ValueKind != JsonValueKind.String) continue;
                if (m.TryGetProperty("visibility", out var vis) && vis.ValueKind == JsonValueKind.String && vis.GetString() != "list") continue;
                var priority = m.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : int.MaxValue;
                if (slug.GetString() is { } s && AgentModels.IsValidModel(s)) listed.Add((s, priority, i));
            }
            return [.. listed.OrderBy(x => x.Priority).ThenBy(x => x.Index).Select(x => x.Slug).Distinct(StringComparer.Ordinal)];
        }
        catch (JsonException) { return null; }
    }

    private async Task<IReadOnlyList<string>?> CopilotHelpAsync(CancellationToken ct)
    {
        // A Node.js entry point cannot be started on its own: no listing then.
        if (executable(AgentCli.Copilot) is not { Length: > 0 } exe || exe.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || exe.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase)) return null;
        CliProbeResult r;
        try
        {
            r = await runner.RunAsync(new CliProbeCommand(exe, ["--help"], new Dictionary<string, string>(), timeout ?? DefaultTimeout), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
        if (r.StartError is not null || r.TimedOut || r.ExitCode != 0) return null;
        return HelpChoices(r.Stdout, "--model");
    }

    /// <summary>The quoted choices of an option in commander-style help: <c>--model &lt;model&gt; ... (choices: "a", "b")</c>, wrapped or not.</summary>
    public static IReadOnlyList<string> HelpChoices(string help, string option)
    {
        var at = help.IndexOf(option + " ", StringComparison.Ordinal);
        while (at >= 0)
        {
            var next = NextOption().Match(help, at + option.Length);
            var segment = next.Success ? help[at..next.Index] : help[at..];
            var choices = segment.IndexOf("choices:", StringComparison.Ordinal);
            if (choices >= 0)
            {
                var end = segment.IndexOf(')', choices);
                var inside = end < 0 ? segment[choices..] : segment[choices..end];
                return [.. Quoted().Matches(inside).Select(m => m.Groups[1].Value).Where(AgentModels.IsValidModel).Distinct(StringComparer.Ordinal)];
            }
            at = help.IndexOf(option + " ", at + option.Length, StringComparison.Ordinal);
        }
        return [];
    }

    /// <summary>
    /// What a "models" command printed: JSON (an array, or an object with "models" / "data"), else one model per line, the
    /// first word of each line that looks like a model id (lower case, with a digit or a dash; bullets and markers dropped).
    /// </summary>
    public static IReadOnlyList<string> ParseListing(string printed)
    {
        var trimmed = printed.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('[')) return JsonModels(trimmed);
        var models = new List<string>();
        foreach (var raw in printed.Split('\n'))
        {
            var line = raw.Trim().TrimStart('*', '-', '•', '>', '+', ' ', '\t');
            var word = line.Split([' ', '\t', ',', '|', '(', ')'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (word is not null && ModelWord().IsMatch(word) && word.Any(c => char.IsDigit(c) || c == '-') && AgentModels.IsValidModel(word))
                models.Add(word);
        }
        return models.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Model ids in a JSON list: strings, or objects' id / slug / model / name; at the root or under "models" / "data".</summary>
    public static IReadOnlyList<string> JsonModels(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "models", "data" })
                {
                    if (!root.TryGetProperty(key, out var inner)) continue;
                    if (inner.ValueKind == JsonValueKind.Object)
                        return [.. inner.EnumerateObject().Select(p => p.Name).Where(AgentModels.IsValidModel).Distinct(StringComparer.Ordinal)];
                    root = inner;
                    break;
                }
            }
            if (root.ValueKind != JsonValueKind.Array) return [];
            var models = new List<string>();
            foreach (var e in root.EnumerateArray())
            {
                string? id = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                if (e.ValueKind == JsonValueKind.Object)
                    foreach (var key in new[] { "id", "slug", "model", "name" })
                        if (e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) { id = v.GetString(); break; }
                if (id is not null && AgentModels.IsValidModel(id)) models.Add(id);
            }
            return models.Distinct(StringComparer.Ordinal).ToList();
        }
        catch (JsonException) { return []; }
    }

    [GeneratedRegex(@"\n\s+-{1,2}[A-Za-z]")]
    private static partial Regex NextOption();

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex Quoted();

    [GeneratedRegex("^[a-z0-9][a-z0-9._:/@-]*$")]
    private static partial Regex ModelWord();
}

/// <summary>The models runs of a CLI used: usage_events (the gateway's rows) joined to run_records for the run's CLI.</summary>
public sealed class SqliteModelsUsed(string connectionString, TimeProvider? clock = null) : IModelsUsed
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public IReadOnlyList<string> Used(AgentCli cli, TimeSpan within)
    {
        using var c = Storage.Db.Open(connectionString);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT u.model FROM usage_events u JOIN run_records r ON r.run_key = u.run_key
            WHERE r.cli = $cli AND u.model IS NOT NULL AND u.model <> '' AND u.at >= $since
            GROUP BY u.model
            ORDER BY max(u.at) DESC
            LIMIT 100;
            """;
        cmd.Parameters.AddWithValue("$cli", cli.Id());
        cmd.Parameters.AddWithValue("$since", (_clock.GetUtcNow() - within).ToString("O", CultureInfo.InvariantCulture));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read())
            if (r.GetString(0) is var m && AgentModels.IsValidModel(m)) list.Add(m);
        return list;
    }
}
