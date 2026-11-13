using System.Text.Json;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;

namespace Yawble.Core.Launch;

/// <summary>A guardrail event: something appeared in the sandbox that must not, and what the core did.</summary>
public sealed record GuardrailEvent(string Kind, string Path, string Action);

/// <summary>
/// The post-run credentials-file check (it catches an in-sandbox sign-in attempt), run after every
/// sandbox exit, headless or interactive:
/// <list type="bullet">
/// <item>Claude: any .credentials.json in CLAUDE_CONFIG_DIR is deleted and recorded (a /login in the TUI).</item>
/// <item>Grok: auth.json in the sandbox's GROK_HOME is clean only in the exact shape Grok writes itself in login
///   mode, {"key": &lt;run token&gt;, "auth_mode": "external"}. Anything else is
///   deleted and recorded: a real login, an API key, or a token that is not this run's.</item>
/// </list>
/// The check never reads the host's own ~/.claude or ~/.grok, nor the app's host Grok home.
/// </summary>
public static class PostRunChecks
{
    public const string ClaudeCredentialsFile = "claude-credentials-file";
    public const string GrokAuthFile = "grok-auth-file";
    public const string CodexAuthFile = "codex-auth-file";

    public static IReadOnlyList<GuardrailEvent> Run(RunLayout layout, CredentialMode mode, string runToken)
    {
        var events = new List<GuardrailEvent>();
        if (AgentPresets.CliOf(mode) == AgentCli.Claude)
        {
            var file = Path.Combine(AgentPresets.ClaudeConfigDir(layout), ".credentials.json");
            if (File.Exists(file)) events.Add(Remove(ClaudeCredentialsFile, file));
        }
        else if (AgentPresets.CliOf(mode) == AgentCli.Grok)
        {
            var file = Path.Combine(AgentPresets.GrokHome(layout), "auth.json");
            if (File.Exists(file) && !IsPlaceholderOnly(File.ReadAllText(file), runToken)) events.Add(Remove(GrokAuthFile, file));
        }
        // Codex, host sign-in: auth.json in the member's CODEX_HOME is the copy of the person's own sign-in (CopyIn); the
        // launcher copies a refreshed one back and keeps it there for the next run, so it is not a finding.
        // Gateway-held: it must still be this run's placeholder; anything else (a login in the sandbox) is removed.
        else if (mode == CredentialMode.CodexGatewaySignIn)
        {
            var file = AgentPresets.CodexAuthFile(layout);
            if (File.Exists(file) && !Credentials.CodexHostSession.IsPlaceholderOnly(File.ReadAllText(file), runToken))
                events.Add(Remove(CodexAuthFile, file));
        }
        return events;
    }

    /// <summary>
    /// Before a run: a credentials file left by an earlier run (a crash before its post-run check) is removed and
    /// recorded. Grok's own external-mode placeholder from the previous run holds that run's dead token; it is
    /// removed quietly, because it is what a clean run leaves behind.
    /// </summary>
    public static IReadOnlyList<GuardrailEvent> PreRun(RunLayout layout, CredentialMode mode)
    {
        if (AgentPresets.CliOf(mode) == AgentCli.Grok)
        {
            var file = Path.Combine(AgentPresets.GrokHome(layout), "auth.json");
            if (File.Exists(file) && IsPlaceholderShape(File.ReadAllText(file), runToken: null))
            {
                File.Delete(file);
                return [];
            }
        }
        // Codex, gateway-held: the launcher has just written this run's placeholder over whatever an earlier run left.
        if (mode == CredentialMode.CodexGatewaySignIn) return [];
        // Any other file: no run token can match, so Run removes and records it.
        return Run(layout, mode, runToken: "\0 no run token matches before the run");
    }

    /// <summary>
    /// True only for Grok's external-mode file holding this run's token. Any other property whose name looks
    /// like it carries a secret (token, key, secret, refresh, session) makes it unclean.
    /// </summary>
    public static bool IsPlaceholderOnly(string json, string runToken) => IsPlaceholderShape(json, runToken);

    /// <summary>The external-mode shape; with a null token, any key that looks like a run token (yawrt_ + 43 URL-safe chars).</summary>
    private static bool IsPlaceholderShape(string json, string? runToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("auth_mode", out var mode) || mode.ValueKind != JsonValueKind.String || mode.GetString() != "external") return false;
            if (!root.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String) return false;
            if (runToken is not null ? key.GetString() != runToken : !RunTokenShape(key.GetString()!)) return false;
            foreach (var p in root.EnumerateObject())
            {
                if (p.Name is "key" or "auth_mode") continue;
                var name = p.Name.ToLowerInvariant();
                if (name.Contains("token") || name.Contains("key") || name.Contains("secret") || name.Contains("refresh") || name.Contains("session"))
                    return false;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool RunTokenShape(string s) =>
        s.StartsWith(Gateway.RunTokenRegistry.Prefix, StringComparison.Ordinal)
        && s.Length == Gateway.RunTokenRegistry.Prefix.Length + 43
        && s.AsSpan(Gateway.RunTokenRegistry.Prefix.Length).ToArray().All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static GuardrailEvent Remove(string kind, string file)
    {
        try
        {
            File.Delete(file);
            return new GuardrailEvent(kind, file, "deleted");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new GuardrailEvent(kind, file, "delete failed: " + ex.Message);
        }
    }
}
