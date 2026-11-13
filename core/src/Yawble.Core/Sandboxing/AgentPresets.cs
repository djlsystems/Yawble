using System.Text.Json.Nodes;
using Yawble.Core.Policy;

namespace Yawble.Core.Sandboxing;

/// <summary>Which credential the gateway swaps in. Never a value.</summary>
public enum CredentialMode
{
    /// <summary>Claude Code with the person's claude setup-token. Sandbox: CLAUDE_CODE_OAUTH_TOKEN=&lt;run token&gt;.</summary>
    ClaudeSubscription,

    /// <summary>Claude Code with an Anthropic Console API key. Sandbox: ANTHROPIC_AUTH_TOKEN=&lt;run token&gt;.</summary>
    ClaudeApiKey,

    /// <summary>Grok with the person's own Grok login. Sandbox: GROK_AUTH_PROVIDER_COMMAND=yawble-grok-auth.</summary>
    GrokLogin,

    /// <summary>Grok with an xAI API key (a fallback). Sandbox: XAI_API_KEY=&lt;run token&gt;.</summary>
    GrokApiKey,

    /// <summary>
    /// Host sign-in: Claude Code uses the host's own sign-in, read through
    /// CLAUDE_SECURESTORAGE_CONFIG_DIR from the exact .credentials.json granted. No credential variable; the gateway
    /// forwards the CLI's own Authorization unchanged.
    /// </summary>
    ClaudeHostSignIn,

    /// <summary>
    /// Claude Code with the person's own sign-in held by the GATEWAY: the sandbox gets
    /// CLAUDE_CODE_OAUTH_TOKEN=&lt;run token&gt; and no sign-in file; the gateway reads the host's ~/.claude/.credentials.json
    /// at every request and puts its access token on the request (Credentials/ClaudeHostSession). The default for Claude.
    /// </summary>
    ClaudeGatewaySignIn,

    /// <summary>Host sign-in: Grok uses the host's own ~/.grok/auth.json through GROK_AUTH_PATH; the gateway forwards it.</summary>
    GrokHostSignIn,

    /// <summary>
    /// Grok with the person's own sign-in held by the GATEWAY (the default for Grok).
    /// The sandbox is GrokLogin's: GROK_AUTH_PROVIDER_COMMAND=yawble-grok-auth (a placeholder) and no sign-in file. The
    /// gateway reads the person's OWN Grok home (GROK_HOME or ~/.grok) at every request, and the keeper has the person's
    /// grok renew it before it expires (`grok models` renews an expired session).
    /// </summary>
    GrokGatewaySignIn,

    /// <summary>
    /// Codex with the person's own sign-in (~/.codex/auth.json), COPIED into the member's CODEX_HOME before each run
    /// and copied back to the host when the run refreshed it (OpenAI rotates refresh tokens: a refresh in the sandbox
    /// that never reached the host would sign the person out). Codex's traffic goes by CONNECT to OpenAI's hosts.
    /// </summary>
    CodexHostSignIn,

    /// <summary>
    /// Codex with the person's own sign-in held by the GATEWAY (the default for Codex).
    /// The member's CODEX_HOME gets a placeholder auth.json whose tokens are the run token, and a config that sends
    /// everything to http://codex.yawble.internal/backend-api/ over plain HTTP (a model provider with requires_openai_auth
    /// and no WebSockets). The gateway reads the person's ~/.codex/auth.json at every
    /// request and puts its access token and account on the request to chatgpt.com.
    /// </summary>
    CodexGatewaySignIn,

    /// <summary>
    /// GitHub Copilot with the person's own sign-in: its token from the OS credential store (Windows Credential
    /// Manager, read by the host at launch; the sandbox cannot read the store) in COPILOT_GITHUB_TOKEN. Copilot's
    /// traffic goes by CONNECT to GitHub's Copilot hosts.
    /// </summary>
    CopilotHostSignIn,

    /// <summary>
    /// GitHub Copilot with the person's own sign-in held by the GATEWAY (the default for
    /// Copilot). The sandbox gets COPILOT_GITHUB_TOKEN=gho_&lt;run token&gt; (Copilot CLI ignores a token that is not
    /// GitHub-shaped) and COPILOT_API_URL=http://copilot.yawble.internal; the gateway reads the person's
    /// token from the OS credential store at every request and puts it on the request to api.githubcopilot.com.
    /// </summary>
    CopilotGatewaySignIn,

    /// <summary>Antigravity with the person's own sign-in. Not usable: its sign-in is in the OS credential store and
    /// it has no supported way to be given it, so runs refuse with the reason.</summary>
    AntigravityHostSignIn,
}

public enum RunKind
{
    /// <summary>A member run: the batch is the prompt (claude -p / grok -p).</summary>
    Headless,

    /// <summary>The person's interactive session in the terminal window (the CLI's TUI).</summary>
    Interactive,
}

/// <summary>One built-in argument (a flag and its value): Locked = the app depends on it; Tail = it carries the prompt and stays last.</summary>
public sealed record ArgGroup(IReadOnlyList<string> Tokens, bool Locked, bool Tail = false);

/// <summary>A custom agent's changes to the built-in argv: extra arguments before/after, and built-in ones removed (by flag, or "flag value").</summary>
public sealed record ArgvCustomization(IReadOnlyList<string> Before, IReadOnlyList<string> After, IReadOnlyList<string> Removed)
{
    public static ArgvCustomization None { get; } = new([], [], []);
    public bool IsEmpty => Before.Count == 0 && After.Count == 0 && Removed.Count == 0;
}

/// <summary>One built-in group as the person sees it (placeholders as in Describe); Removed = the customization in effect removes it; Reason = why it is locked.</summary>
public sealed record ArgGroupView(IReadOnlyList<string> Tokens, bool Locked, bool Tail, bool Removed, string? Reason);

/// <summary>A run kind's command line in parts: the executable / launcher tokens and the built-in groups, in launch order.</summary>
public sealed record CommandLineGroups(RunKind Kind, IReadOnlyList<string> Launch, IReadOnlyList<ArgGroupView> Groups);

/// <summary>What the shell shows for one run kind: the argv with placeholders and the environment variable NAMES.</summary>
public sealed record CommandLineDescription(RunKind Kind, IReadOnlyList<string> Argv, IReadOnlyList<string> EnvNames);

/// <summary>A file the host writes into the member's folders before launch.</summary>
public sealed record SeedFile(string Path, string Content);

/// <summary>One virtual host the run's gateway serves.</summary>
public sealed record GatewayVhost(string Host, string Kind, string? Upstream, string? Credential, IReadOnlyList<string> AllowedPaths);

/// <summary>What the per-run gateway is told: its port, the CONNECT allowlist and its virtual hosts.</summary>
public sealed record GatewayPlan(int Port, IReadOnlyList<string> AllowHosts, IReadOnlyList<GatewayVhost> Vhosts);

/// <summary>
/// The agent CLI presets: arguments, isolation variables, the base-URL and placeholder-credential variables,
/// and the seeded config folders.
/// </summary>
public static class AgentPresets
{
    public const string BusHost = "bus.yawble.internal";
    public const string ClaudeModelHost = "model.yawble.internal";
    public const string GrokLoginHost = "grok.yawble.internal";
    public const string GrokApiKeyHost = "xai.yawble.internal";
    public const string CopilotHost = "copilot.yawble.internal";
    public const string CodexHost = "codex.yawble.internal";

    /// <summary>The member's Codex sign-in file: the placeholder in the gateway-held mode, the copy in the host sign-in mode.</summary>
    public static string CodexAuthFile(RunLayout l) => Join(l, CodexHome(l), "auth.json");

    /// <summary>What a gateway-held Copilot run presents as its GitHub token: the run token, GitHub-shaped.</summary>
    public static string CopilotPlaceholder(string runToken) => "gho_" + runToken;

    /// <summary>Every variable that carries (or selects) a model credential. Only the placeholder may appear.</summary>
    public static IReadOnlyList<string> CredentialVariables { get; } =
    [
        "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_REFRESH_TOKEN",
        "CLAUDE_CODE_HOST_CREDS_FILE", "XAI_API_KEY", "GROK_CODE_XAI_API_KEY", "GROK_AUTH_PROVIDER_COMMAND",
    ];

    public static AgentCli CliOf(CredentialMode mode) => mode switch
    {
        CredentialMode.ClaudeSubscription or CredentialMode.ClaudeApiKey or CredentialMode.ClaudeHostSignIn or CredentialMode.ClaudeGatewaySignIn => AgentCli.Claude,
        CredentialMode.GrokLogin or CredentialMode.GrokApiKey or CredentialMode.GrokHostSignIn or CredentialMode.GrokGatewaySignIn => AgentCli.Grok,
        CredentialMode.CodexHostSignIn or CredentialMode.CodexGatewaySignIn => AgentCli.Codex,
        CredentialMode.CopilotHostSignIn or CredentialMode.CopilotGatewaySignIn => AgentCli.Copilot,
        CredentialMode.AntigravityHostSignIn => AgentCli.Antigravity,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    /// <summary>Host sign-in: the CLI uses the host's own sign-in, and the gateway passes it through.</summary>
    public static bool IsHostSignIn(CredentialMode mode) => mode is CredentialMode.ClaudeHostSignIn or CredentialMode.GrokHostSignIn
        or CredentialMode.CodexHostSignIn or CredentialMode.CopilotHostSignIn or CredentialMode.AntigravityHostSignIn;

    /// <summary>
    /// The modes that need the person signed in to the CLI on this computer: the host sign-in ones (the sandbox reads the sign-in)
    /// and the gateway-held one (the core reads it). The launcher checks the sign-in is there before a run.
    /// </summary>
    public static bool NeedsHostSignIn(CredentialMode mode) => IsHostSignIn(mode) || IsGatewayHeld(mode);

    /// <summary>The person's own sign-in on this computer, held by the gateway: the sandbox gets a run token or placeholder only.</summary>
    public static bool IsGatewayHeld(CredentialMode mode) => mode is CredentialMode.ClaudeGatewaySignIn or CredentialMode.GrokGatewaySignIn
        or CredentialMode.CopilotGatewaySignIn or CredentialMode.CodexGatewaySignIn;

    /// <summary>The host sign-in mode for a CLI (what every run uses; most are held by the gateway).</summary>
    public static CredentialMode HostSignInMode(AgentCli cli) => cli switch
    {
        AgentCli.Claude => CredentialMode.ClaudeGatewaySignIn,
        AgentCli.Grok => CredentialMode.GrokGatewaySignIn,
        AgentCli.Codex => CredentialMode.CodexGatewaySignIn,
        AgentCli.Copilot => CredentialMode.CopilotGatewaySignIn,
        AgentCli.Antigravity => CredentialMode.AntigravityHostSignIn,
        _ => throw new ArgumentOutOfRangeException(nameof(cli)),
    };

    /// <summary>
    /// The CLIs whose model traffic does not go through a gateway vhost: they reach their vendor's own hosts by
    /// CONNECT (TLS end to end; the gateway sees host names only). These hosts are added by the preset for that CLI's
    /// runs only; no policy can add them (<see cref="FloorRules"/>), so no other agent reaches them.
    /// </summary>
    public static IReadOnlyList<string> PassthroughHosts(CredentialMode mode) => mode switch
    {
        // Copilot's model API goes through copilot.yawble.internal, and nothing else by CONNECT (a run answers without
        // api.github.com).
        CredentialMode.CopilotGatewaySignIn => [],
        // Codex: everything through codex.yawble.internal; nothing of OpenAI's by CONNECT (a refresh in the sandbox would
        // need auth.openai.com, and is refused there).
        CredentialMode.CodexGatewaySignIn => [],
        _ => PassthroughHosts(CliOf(mode)),
    };

    public static IReadOnlyList<string> PassthroughHosts(AgentCli cli) => cli switch
    {
        // Codex: chatgpt.com (the model), auth.openai.com (a sign-in refresh),
        // *.oaiusercontent.com (the model catalog's files), ab.chatgpt.com (feature flags).
        AgentCli.Codex => ["chatgpt.com", "ab.chatgpt.com", "auth.openai.com", "api.openai.com", "*.oaiusercontent.com"],
        // GitHub Copilot CLI: the token check (api.github.com) and the Copilot API (*.githubcopilot.com).
        AgentCli.Copilot => ["api.github.com", "api.githubcopilot.com", "*.githubcopilot.com"],
        // Antigravity CLI: Code Assist (cloudcode-pa), the Vertex AI model endpoints
        // (aiplatform.<region>.rep.googleapis.com), the account (www.googleapis.com) and the token refresh (oauth2).
        AgentCli.Antigravity => ["cloudcode-pa.googleapis.com", "*.rep.googleapis.com", "aiplatform.googleapis.com", "www.googleapis.com", "oauth2.googleapis.com"],
        _ => [],
    };

    public static string CodexHome(RunLayout l) => Join(l, l.Workspace, ".codex");

    /// <summary>Where Antigravity reads its sign-in when the OS store is unavailable (written by the launcher, never seeded).</summary>
    public static string AntigravityTokenFile(RunLayout l) => Join(l, l.Workspace, ".gemini", "antigravity-cli", "antigravity-oauth-token");

    public static string CopilotMcpConfigPath(RunLayout l) => Join(l, l.Workspace, ".yawble", "copilot-mcp.json");

    public static string BridgePath(RunLayout l) => Exe(l, l.AppBinDir, "yawble-bridge");

    public static string GrokAuthPath(RunLayout l) => Exe(l, l.AppBinDir, "yawble-grok-auth");

    public static string RunnerPath(RunLayout l) => Exe(l, l.AppBinDir, "yawble-run");

    /// <summary>
    /// The program the request starts: the CLI itself, or on Windows for Grok, yawble-run in front of it (it creates the
    /// container's temp folder, which Windows names per run, before the CLI starts; see bridge/src/Yawble.Run).
    /// </summary>
    public static IReadOnlyList<string> Launch(RunLayout l) =>
        l.Os == TargetOs.Windows && l.Cli == AgentCli.Grok && !l.CliInstall.Placeholder ? [RunnerPath(l), .. l.CliCommand] : l.CliCommand;

    public static string McpConfigPath(RunLayout l) => Join(l, l.Workspace, ".yawble", "mcp.json");

    public static string ClaudeConfigDir(RunLayout l) => Join(l, l.Workspace, ".claude");

    /// <summary>
    /// The key Claude Code uses for the clone in .claude.json "projects": the path, with forward slashes on Windows
    /// ("D:/Work/x"). A backslash key leaves the TUI on its trust dialog.
    /// </summary>
    public static string ClaudeProjectKey(RunLayout l) => l.Os == TargetOs.Windows ? l.ClonePath.Replace('\\', '/') : l.ClonePath;

    public static string GrokHome(RunLayout l) => Join(l, l.Workspace, ".grok");

    /// <summary>The placeholder a description shows for the per-run prompt.</summary>
    public const string PromptPlaceholder = "<prompt>";

    /// <summary>The placeholder a description shows for the per-run session id.</summary>
    public const string SessionPlaceholder = "<session id>";

    /// <summary>The placeholder a description shows for the member's workspace folder.</summary>
    public const string WorkspacePlaceholder = "<workspace>";

    /// <summary>The placeholder a description shows for the run's bearer token (never a real one).</summary>
    public const string RunTokenPlaceholder = "<run token>";

    /// <summary>
    /// The command line of one run: the ONE function that builds it. The launcher runs exactly this; <see cref="Describe"/>
    /// shows exactly this with placeholders. <paramref name="custom"/> is a custom agent's changes (null for a built-in).
    /// </summary>
    public static IReadOnlyList<string> Arguments(RunLayout l, CredentialMode mode, RunKind kind, string? prompt, string sessionId, string? model,
        ArgvCustomization? custom = null)
    {
        var groups = BuiltIn(l, mode, kind, prompt, sessionId, model);
        var argv = new List<string>(Launch(l));
        var cli = CliOf(mode);
        if (custom is { IsEmpty: false })
        {
            var problems = ValidateCustomization(cli, custom);
            if (problems.Count > 0) throw new ArgumentException(string.Join("; ", problems));
            argv.AddRange(custom.Before);
            foreach (var g in groups.Where(g => !g.Tail && !IsRemoved(g, custom))) argv.AddRange(g.Tokens);
            argv.AddRange(custom.After);
            foreach (var g in groups.Where(g => g.Tail)) argv.AddRange(g.Tokens);
            return argv;
        }
        foreach (var g in groups.Where(g => !g.Tail)) argv.AddRange(g.Tokens);
        foreach (var g in groups.Where(g => g.Tail)) argv.AddRange(g.Tokens);
        return argv;
    }

    /// <summary>
    /// What the person is shown: the same argv <see cref="Arguments"/> builds, with the per-run values as placeholders
    /// (&lt;prompt&gt;, &lt;session id&gt;, &lt;workspace&gt;/.yawble/mcp.json) and the NAMES of the environment variables set
    /// (never a value). One per run kind.
    /// </summary>
    public static CommandLineDescription Describe(RunLayout l, CredentialMode mode, RunKind kind, string? model, ArgvCustomization? custom = null)
    {
        var argv = Arguments(l, mode, kind, PromptPlaceholder, SessionPlaceholder, model, custom)
            .Select(a => ShowWorkspace(l, a)).ToList();
        var env = Environment(l, mode, RunTokenPlaceholder).Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        return new CommandLineDescription(kind, argv, env);
    }

    /// <summary>
    /// The same command line as <see cref="Describe"/>, as the person edits it: the launcher tokens, then each built-in
    /// group from <see cref="BuiltIn"/> (the one list the launch uses) with the same placeholders, whether the app depends
    /// on it (and why), whether it carries the prompt, and whether <paramref name="custom"/> removes it (the rule
    /// <see cref="Arguments"/> applies).
    /// </summary>
    public static CommandLineGroups DescribeGroups(RunLayout l, CredentialMode mode, RunKind kind, string? model, ArgvCustomization? custom = null)
    {
        var cli = CliOf(mode);
        var applied = custom is { IsEmpty: false } ? custom : null;
        var groups = BuiltIn(l, mode, kind, PromptPlaceholder, SessionPlaceholder, model)
            .Select(g => new ArgGroupView([.. g.Tokens.Select(t => ShowWorkspace(l, t))], g.Locked, g.Tail,
                applied is not null && !g.Tail && IsRemoved(g, applied), g.Locked ? LockedGroupReason(cli, g) : null))
            .ToList();
        return new CommandLineGroups(kind, [.. Launch(l).Select(t => ShowWorkspace(l, t))], groups);
    }

    /// <summary>Why a locked built-in group cannot be changed, in the words the validator uses.</summary>
    private static string LockedGroupReason(AgentCli cli, ArgGroup g)
    {
        if (g.Tail) return PromptWhy;
        var flag = g.Tokens[0].Split('=', 2)[0];
        if (LockedFlags(cli).TryGetValue(flag, out var why)) return why;
        return flag.StartsWith("--dangerously-", StringComparison.Ordinal) ? SandboxWhy : "the app depends on it";
    }

    private static string ShowWorkspace(RunLayout l, string arg)
    {
        var ws = l.Workspace;
        var at = arg.IndexOf(ws, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return arg;
        var rest = arg[(at + ws.Length)..].Replace('\\', '/');
        return arg[..at] + WorkspacePlaceholder + rest;
    }

    private static bool IsRemoved(ArgGroup g, ArgvCustomization c) =>
        !g.Locked && c.Removed.Any(r => string.Equals(r, g.Tokens[0], StringComparison.Ordinal) || string.Equals(r, string.Join(' ', g.Tokens), StringComparison.Ordinal));

    /// <summary>The built-in argument groups for a CLI's run, in order; the flags the app depends on are Locked.</summary>
    public static IReadOnlyList<ArgGroup> BuiltIn(RunLayout l, CredentialMode mode, RunKind kind, string? prompt, string sessionId, string? model)
    {
        var g = new List<ArgGroup>();
        var cli = CliOf(mode);
        string Need() => prompt ?? throw new ArgumentException("a headless run needs a prompt");
        bool Optional() => !string.IsNullOrWhiteSpace(prompt);
        if (cli == AgentCli.Codex)
        {
            // Headless Codex: exec, no approvals and no sandbox of its own (the app's sandbox is the sandbox), the bus
            // from CODEX_HOME/config.toml, no update check. Interactive: the TUI.
            if (kind == RunKind.Headless) g.Add(new(["exec"], true));
            g.Add(new(["--dangerously-bypass-approvals-and-sandbox"], true));
            g.Add(new(["-c", "check_for_update_on_startup=false"], false));
            if (kind == RunKind.Headless) g.Add(new(["--skip-git-repo-check"], true));
            if (!string.IsNullOrEmpty(model)) g.Add(new(["-m", model], true));
            if (kind == RunKind.Headless) g.Add(new([Need()], true, Tail: true));
            else if (Optional()) g.Add(new([prompt!], true, Tail: true));
        }
        else if (cli == AgentCli.Copilot)
        {
            // Not --disable-client-telemetry: Copilot CLI rejects it with --no-auto-update. -p stays last.
            g.Add(new(["--allow-all"], true));
            g.Add(new(["--disable-builtin-mcps"], true));
            g.Add(new(["--no-auto-update"], false));
            // List options take "=" so they cannot swallow the arguments after them.
            g.Add(new(["--secret-env-vars=" + CopilotTokenVariable], true));
            g.Add(new(["--additional-mcp-config", "@" + CopilotMcpConfigPath(l)], true));
            g.Add(new(["--session-id", sessionId], true));
            if (!string.IsNullOrEmpty(model)) g.Add(new(["--model", model], true));
            if (kind == RunKind.Headless) { g.Add(new(["--silent"], true)); g.Add(new(["-p", Need()], true, Tail: true)); }
            else if (Optional()) g.Add(new(["-i", prompt!], true, Tail: true));
        }
        else if (cli == AgentCli.Antigravity)
        {
            g.Add(new(["--dangerously-skip-permissions"], true));
            // Its command tool cannot run in an app container (exebox calls CreateDesktop); commands go to the shell MCP server.
            g.Add(new(["--settings", "{\"permissions\":{\"deny\":[\"command(*)\"]}}"], true));
            if (!string.IsNullOrEmpty(model)) g.Add(new(["--model", model], true));
            if (kind == RunKind.Headless) g.Add(new(["-p", Need()], true, Tail: true));
            else if (Optional()) g.Add(new(["-i", prompt!], true, Tail: true));
        }
        else if (cli == AgentCli.Claude)
        {
            if (kind == RunKind.Headless)
            {
                g.Add(new(["-p", Need()], true));
                g.Add(new(["--output-format", "stream-json"], true));
                g.Add(new(["--verbose"], true));
            }
            g.Add(new(["--strict-mcp-config"], true));
            g.Add(new(["--mcp-config", McpConfigPath(l)], true));
            g.Add(new(["--dangerously-skip-permissions"], true));
            g.Add(new(["--session-id", sessionId], true));
            if (!string.IsNullOrEmpty(model)) g.Add(new(["--model", model], true));
            // Interactive: the optional initial prompt is the last, positional argument.
            if (kind == RunKind.Interactive && Optional()) g.Add(new([prompt!], true, Tail: true));
        }
        else
        {
            // Grok.
            g.Add(new(["--no-auto-update"], false));
            g.Add(new(["--session-id", sessionId], true));
            if (kind == RunKind.Headless)
            {
                g.Add(new(["-p", Need()], true));
                g.Add(new(["--output-format", "json"], true));
                g.Add(new(["--always-approve"], true));
            }
            if (!string.IsNullOrEmpty(model)) g.Add(new(["--model", model], true));
            if (kind == RunKind.Interactive && Optional()) g.Add(new([prompt!], true, Tail: true));
        }
        return g;
    }

    /// <summary>
    /// Why a custom agent's changes are refused, one plain sentence each (empty = fine). See <see cref="CustomizationProblems"/>.
    /// </summary>
    public static IReadOnlyList<string> ValidateCustomization(AgentCli cli, ArgvCustomization custom) =>
        [.. CustomizationProblems(cli, custom).Select(p => p.Reason)];

    /// <summary>
    /// Every problem with a custom agent's changes, each with the argument it is about (empty = fine):
    /// <list type="bullet">
    /// <item>an item that is blank or holds a control character;</item>
    /// <item>an added argument in a locked flag FAMILY (every long and short alias of a flag the app depends on), in any
    /// spelling: <c>--flag</c>, <c>--flag=value</c>, <c>-x</c>, an attached value <c>-xVALUE</c>, or <c>-x</c> inside a short
    /// cluster (<c>-dp</c>); any <c>--dangerously-*</c> flag; <c>--</c> (it ends the options, so the app's own flags after it
    /// would be read as the prompt);</item>
    /// <item>Codex: <c>-c</c>/<c>--config</c> whose key sets the model, its provider, a profile, MCP servers, the sandbox,
    /// approvals or a base URL (other keys, e.g. model_reasoning_effort, stay free), or one with no key=value;</item>
    /// <item>a flag with no attached value as the LAST item of a list that the app follows with a value it depends on:
    /// After (the prompt may follow it) and, for Codex, Before (<c>exec</c> follows it). It could take that value as its own;</item>
    /// <item>a removal of a locked built-in.</item>
    /// </list>
    /// No shell string is ever built, so no metacharacter has a meaning. The model is set only through the model setting, so
    /// run records and future costs stay true.
    /// </summary>
    public static IReadOnlyList<(string Argument, string Reason)> CustomizationProblems(AgentCli cli, ArgvCustomization custom)
    {
        var problems = new List<(string, string)>();
        CheckList(custom.Before, cli == AgentCli.Codex ? "exec" : "the app's first built-in argument");
        CheckList(custom.After, "the prompt");
        foreach (var r in custom.Removed)
        {
            if (string.IsNullOrWhiteSpace(r)) { problems.Add((r ?? "", "an argument to remove is empty")); continue; }
            var flag = r.Trim().Split(' ', 2)[0];
            if (LockedUse(cli, flag, null, forRemoval: true) is { } locked) problems.Add((r, $"{locked.Flag} cannot be removed: {locked.Why}"));
        }
        return problems;

        void CheckList(IReadOnlyList<string> list, string? follows)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (string.IsNullOrWhiteSpace(a)) { problems.Add((a ?? "", "an argument is empty")); continue; }
                if (a.Any(char.IsControl)) { problems.Add((a, $"argument '{a}' holds a control character")); continue; }
                var next = i + 1 < list.Count ? list[i + 1] : null;
                if (LockedUse(cli, a, next, forRemoval: false) is { } locked)
                {
                    problems.Add((a, locked.Flag == a || a.StartsWith(locked.Flag + "=", StringComparison.Ordinal)
                        ? $"{locked.Flag} is locked: {locked.Why}"
                        : $"'{a}' is locked ({locked.Flag}): {locked.Why}"));
                    continue;
                }
                // A value-taking flag here would take the app's next value (the prompt, or Codex's exec) as its own.
                if (follows is not null && i == list.Count - 1 && IsBareFlag(a))
                    problems.Add((a, $"'{a}' is the last argument of the list and has no value, so it could take the app's next argument "
                        + $"({follows}) as its own: write it as {a}=value, add its value after it, or move it"));
            }
        }
    }

    private static bool IsBareFlag(string a) =>
        a.StartsWith("--", StringComparison.Ordinal) ? a.Length > 2 && !a.Contains('=') : a.Length == 2 && a[0] == '-' && char.IsAsciiLetter(a[1]);

    private sealed record Family(string Why, params string[] Names);

    private const string ToolsWhy = "the agent's tools (the bus) come through it";
    private const string SandboxWhy = "permissions are handled by the sandbox";
    private const string ParseWhy = "the app reads the run's output in this form";
    private const string SessionWhy = "the app tracks the run by its session id";
    private const string ModelWhy = "the model is set only by the model setting, so run records name the model really used";
    private const string PromptWhy = "the run's prompt";
    private const string EndOfOptionsWhy = "it ends the options, so the app's own arguments after it would be read as the prompt";

    /// <summary>The locked flag families per CLI: every alias the CLI accepts for a flag the app depends on.</summary>
    private static IReadOnlyList<Family> Families(AgentCli cli) => cli switch
    {
        AgentCli.Claude =>
        [
            new(PromptWhy, "-p", "--print"),
            new(ParseWhy, "--output-format", "--input-format", "--verbose", "--replay-user-messages", "--include-partial-messages"),
            new(ToolsWhy, "--mcp-config", "--strict-mcp-config"),
            new(SandboxWhy, "--dangerously-skip-permissions", "--allow-dangerously-skip-permissions", "--permission-mode", "--permission-prompt-tool"),
            new("settings could turn the app's permissions, hooks or tools back on", "--settings", "--setting-sources"),
            new(SessionWhy, "--session-id", "-r", "--resume", "-c", "--continue", "--fork-session"),
            new(ModelWhy, "--model", "--fallback-model"),
        ],
        AgentCli.Grok =>
        [
            new(SessionWhy, "--session-id", "-r", "--resume", "-c", "--continue"),
            new(PromptWhy, "-p", "--prompt"),
            new(ParseWhy, "--output-format"),
            new(SandboxWhy, "--always-approve"),
            new(ToolsWhy, "--mcp-config"),
            new(ModelWhy, "-m", "--model"),
        ],
        AgentCli.Codex =>
        [
            new(ParseWhy, "exec", "e", "resume", "--skip-git-repo-check", "--json", "--experimental-json", "-o", "--output-last-message", "--output-schema"),
            new(SandboxWhy, "--dangerously-bypass-approvals-and-sandbox", "--yolo", "--full-auto", "-s", "--sandbox", "-a", "--ask-for-approval"),
            new(ModelWhy, "-m", "--model", "--oss", "--local-provider"),
            new("a profile can set the model, the sandbox and approvals", "-p", "--profile"),
            new("the run's folder is the member's clone", "-C", "--cd"),
        ],
        AgentCli.Copilot =>
        [
            new(SandboxWhy, "--allow-all", "--allow-all-tools", "--allow-all-paths", "--yolo"),
            new(ToolsWhy, "--disable-builtin-mcps", "--additional-mcp-config"),
            new("keeps the run token out of output", "--secret-env-vars"),
            new(SessionWhy, "--session-id", "--resume", "--continue"),
            new(ParseWhy, "-s", "--silent"),
            new(PromptWhy, "-p", "--prompt", "-i", "--interactive"),
            new(ModelWhy, "-m", "--model"),
        ],
        AgentCli.Antigravity =>
        [
            new(SandboxWhy, "--dangerously-skip-permissions"),
            new("denies its own command tool, which cannot run in the sandbox", "--settings"),
            new(PromptWhy, "-p", "--prompt", "-i", "--interactive"),
            new(ModelWhy, "-m", "--model"),
            new("each run starts its own session", "-r", "--resume", "-c", "--continue", "--session-id"),
        ],
        _ => [],
    };

    /// <summary>
    /// The locked flag <paramref name="arg"/> uses (with its reason), or null. <paramref name="next"/> is the item after it
    /// in the same list (a separate value). Removals are matched on the flag only, and Codex's built-in -c group is removable.
    /// </summary>
    private static (string Flag, string Why)? LockedUse(AgentCli cli, string arg, string? next, bool forRemoval)
    {
        var a = arg.Trim();
        var locked = LockedFlags(cli);
        if (a == "--") return forRemoval ? null : ("--", EndOfOptionsWhy);
        if (a.StartsWith("--", StringComparison.Ordinal))
        {
            var name = a.Split('=', 2)[0];
            if (locked.TryGetValue(name, out var why)) return (name, why);
            if (name.StartsWith("--dangerously-", StringComparison.Ordinal)) return (name, SandboxWhy);
            if (!forRemoval && cli == AgentCli.Codex && name == "--config")
                return CodexConfig("--config", a.Length > name.Length ? a[(name.Length + 1)..] : next);
            return null;
        }
        if (a.Length >= 2 && a[0] == '-' && char.IsAsciiLetter(a[1]))
        {
            var first = a[..2];
            if (!forRemoval && cli == AgentCli.Codex && first == "-c")
                return CodexConfig("-c", a.Length > 2 ? a[2..].TrimStart('=') : next);
            if (locked.TryGetValue(first, out var why)) return (first, why);
            if (forRemoval) return null;
            // A short cluster (-dp) sets every letter in it, up to an attached '=value'.
            foreach (var c in a[2..].TakeWhile(c => c != '='))
            {
                if (!char.IsAsciiLetter(c)) break;
                if (locked.TryGetValue("-" + c, out var w)) return ("-" + c, w);
                if (cli == AgentCli.Codex && c == 'c') return ("-c", "a -c inside a cluster cannot be checked: give -c on its own");
            }
            return null;
        }
        return locked.TryGetValue(a, out var positional) ? (a, positional) : null;
    }

    /// <summary>Codex -c / --config key=value: refused for the keys the app sets or depends on.</summary>
    private static (string Flag, string Why)? CodexConfig(string flag, string? value)
    {
        var kv = value?.Trim();
        if (string.IsNullOrEmpty(kv) || !kv.Contains('=') || kv.StartsWith('-'))
            return (flag, "it needs its key=value (in the same item, or as the next item)");
        var key = kv.Split('=', 2)[0].Trim().Trim('"').ToLowerInvariant();
        string[] lockedKeys = ["model", "model_provider", "model_providers", "profile", "profiles", "mcp_servers", "sandbox", "sandbox_mode",
            "sandbox_workspace_write", "approval_policy", "approvals", "notify", "shell_environment_policy", "chatgpt_base_url", "openai_base_url"];
        var locked = lockedKeys.Any(k => key == k || key.StartsWith(k + ".", StringComparison.Ordinal) || key.StartsWith(k + "_", StringComparison.Ordinal) && k != "model")
            || key.StartsWith("sandbox", StringComparison.Ordinal) || key.StartsWith("approval", StringComparison.Ordinal) || key.EndsWith("base_url", StringComparison.Ordinal);
        if (!locked) return null;
        var why = key == "model" ? ModelWhy
            : key.StartsWith("mcp_servers", StringComparison.Ordinal) ? ToolsWhy
            : key.StartsWith("sandbox", StringComparison.Ordinal) || key.StartsWith("approval", StringComparison.Ordinal) ? SandboxWhy
            : "the app sets it for the sandbox, its gateway and the run records";
        return ($"{flag} {key}", why);
    }

    /// <summary>The flags the app depends on for a CLI (every alias of each locked family), with the reason (shown in the UI).</summary>
    public static IReadOnlyDictionary<string, string> LockedFlags(AgentCli cli)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var family in Families(cli))
            foreach (var name in family.Names) d.TryAdd(name, family.Why);
        return d;
    }

    /// <summary>The preset's variables: config folder, base URLs, the placeholder credential, isolation.</summary>
    public static IReadOnlyDictionary<string, string> Environment(RunLayout l, CredentialMode mode, string runToken)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (mode)
        {
            case CredentialMode.ClaudeSubscription:
            case CredentialMode.ClaudeApiKey:
            case CredentialMode.ClaudeHostSignIn:
            case CredentialMode.ClaudeGatewaySignIn:
                env["CLAUDE_CONFIG_DIR"] = ClaudeConfigDir(l);
                env["ANTHROPIC_BASE_URL"] = "http://" + ClaudeModelHost;
                // Subscription and gateway-held: the CLI presents the run token as its OAuth token (so it sends the OAuth
                // headers); the gateway swaps in the real one.
                if (mode is CredentialMode.ClaudeSubscription or CredentialMode.ClaudeGatewaySignIn) env["CLAUDE_CODE_OAUTH_TOKEN"] = runToken;
                else if (mode == CredentialMode.ClaudeApiKey) env["ANTHROPIC_AUTH_TOKEN"] = runToken;
                // Host sign-in: the credential store (only .credentials.json) is the host's folder; everything else of
                // CLAUDE_CONFIG_DIR stays the member's own.
                else foreach (var (k, v) in l.CliInstall.SignInEnvironment) env[k] = v;
                env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
                // Self-update off inside the sandbox (the install folders are read-only anyway; an update is the
                // person's, on the host). DISABLE_UPDATES also refuses `claude update`, and
                // DISABLE_INSTALLATION_CHECKS skips the install-method doctor (it would flag the sandbox's PATH).
                env["DISABLE_AUTOUPDATER"] = "1";
                env["DISABLE_UPDATES"] = "1";
                env["DISABLE_INSTALLATION_CHECKS"] = "1";
                // Without it the TUI CONNECTs to github.com,
                // raw.githubusercontent.com and downloads.claude.ai for the official plugin marketplace.
                env["CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL"] = "1";
                env["ENABLE_CLAUDEAI_MCP_SERVERS"] = "false";
                env["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1";
                // Windows: Claude Code's shell tool needs Git Bash or its PowerShell tool. Git Bash (MSYS2) cannot
                // start in a ProcessContainer (microsoft/mxc#1061), so the PowerShell tool is used.
                if (l.Os == TargetOs.Windows) env["CLAUDE_CODE_USE_POWERSHELL_TOOL"] = "1";
                break;
            case CredentialMode.GrokLogin:
            case CredentialMode.GrokGatewaySignIn:
            case CredentialMode.GrokApiKey:
            case CredentialMode.GrokHostSignIn:
                env["GROK_HOME"] = GrokHome(l);
                var baseUrl = "http://" + (mode == CredentialMode.GrokApiKey ? GrokApiKeyHost : GrokLoginHost) + "/v1";
                // All three, in both modes: with fewer, traffic leaves by CONNECT api.x.ai.
                env["GROK_CLI_CHAT_PROXY_BASE_URL"] = baseUrl;
                env["GROK_XAI_API_BASE_URL"] = baseUrl;
                env["GROK_MODELS_BASE_URL"] = baseUrl;
                if (mode is CredentialMode.GrokLogin or CredentialMode.GrokGatewaySignIn) env["GROK_AUTH_PROVIDER_COMMAND"] = GrokAuthPath(l);
                else if (mode == CredentialMode.GrokApiKey) env["XAI_API_KEY"] = runToken;
                // Host sign-in: GROK_AUTH_PATH is the host's auth.json (granted alone); GROK_HOME stays the member's own.
                else foreach (var (k, v) in l.CliInstall.SignInEnvironment) env[k] = v;
                env["GROK_DISABLE_AUTOUPDATER"] = "1";
                // With these, no CONNECTs to x.ai (update) or storage.googleapis.com (changelog). They work as a
                // set; which one each CONNECT needs is not known.
                env["GROK_AUTO_UPDATE"] = "0";
                env["GROK_CHANGELOG_OFFLINE"] = "1";
                env["DISABLE_TELEMETRY"] = "1";
                env["GROK_TELEMETRY_ENABLED"] = "false";
                env["DISABLE_ERROR_REPORTING"] = "1";
                env["GROK_CODEX_SKILLS_ENABLED"] = "false";
                env["GROK_CODEX_AGENTS_ENABLED"] = "false";
                env["GROK_CODEX_RULES_ENABLED"] = "false";
                env["GROK_CODEX_HOOKS_ENABLED"] = "false";
                env["GROK_CODEX_MCPS_ENABLED"] = "false";
                env["GROK_MEMORY"] = "0";
                // Diagnostics only (the core's own environment, never the person's choice in the UI): Grok's debug log
                // in the member's home.
                if (System.Environment.GetEnvironmentVariable("YAWBLE_GROK_DEBUG") == "1")
                {
                    env["GROK_DEBUG_LOG"] = "1";
                    env["GROK_LOG_FILE"] = Join(l, l.Workspace, "grok-debug.log");
                }
                break;
            case CredentialMode.CodexHostSignIn:
            case CredentialMode.CodexGatewaySignIn:
                // The member's own Codex home: config.toml (the bus), sessions, and the COPY of the host's auth.json
                // (host sign-in) or the placeholder (gateway-held).
                env["CODEX_HOME"] = CodexHome(l);
                break;
            case CredentialMode.CopilotHostSignIn:
                // The host's Copilot token (read from the OS credential store by the launcher), as Copilot's own
                // variable; Copilot's config, sessions and logs are the member's own (~/.copilot in HOME).
                foreach (var (k, v) in l.CliInstall.SecretEnvironment) env[k] = v;
                env["COPILOT_AUTO_UPDATE"] = "false";
                break;
            case CredentialMode.CopilotGatewaySignIn:
                // The placeholder only; the gateway holds the person's token. Copilot's config, sessions and logs are
                // the member's own (~/.copilot in HOME).
                env[CopilotTokenVariable] = CopilotPlaceholder(runToken);
                env["COPILOT_API_URL"] = "http://" + CopilotHost;
                env["COPILOT_AUTO_UPDATE"] = "false";
                break;
            case CredentialMode.AntigravityHostSignIn:
                env["AGY_CLI_DISABLE_AUTO_UPDATE"] = "1";
                break;
        }
        return env;
    }

    /// <summary>The variable that carries Copilot's host token into its run (the one credential a run's env holds).</summary>
    public const string CopilotTokenVariable = "COPILOT_GITHUB_TOKEN";

    /// <summary>The config the host seeds; never a credentials file (the credential invariants).</summary>
    public static IReadOnlyList<SeedFile> Seeds(RunLayout l, CredentialMode mode)
    {
        if (CliOf(mode) == AgentCli.Claude)
        {
            var mcp = new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["bus"] = new JsonObject { ["type"] = "stdio", ["command"] = BridgePath(l), ["args"] = new JsonArray() },
                },
            };
            // The minimal TUI seed: hasCompletedOnboarding alone is not
            // enough. Without the other keys the TUI stops on bypass-mode consent, folder trust and the auto-mode
            // nudge, and with no seed its onboarding check CONNECTs straight to api.anthropic.com.
            var settings = new JsonObject
            {
                ["permissions"] = new JsonObject { ["defaultMode"] = "bypassPermissions" },
                ["skipDangerousModePermissionPrompt"] = true,
            };
            var state = new JsonObject
            {
                ["hasCompletedOnboarding"] = true,
                ["hasSeenAutoDefaultNudge"] = true,
                ["projects"] = new JsonObject { [ClaudeProjectKey(l)] = new JsonObject { ["hasTrustDialogAccepted"] = true } },
            };
            return
            [
                new SeedFile(McpConfigPath(l), mcp.ToJsonString(SandboxJson.Indented)),
                new SeedFile(Join(l, ClaudeConfigDir(l), "settings.json"), settings.ToJsonString(SandboxJson.Indented)),
                new SeedFile(Join(l, ClaudeConfigDir(l), ".claude.json"), state.ToJsonString(SandboxJson.Indented)),
            ];
        }
        // TOML literal string: no escapes, so a Windows path is safe as long as it has no single quote.
        var bridge = BridgePath(l);
        if (bridge.Contains('\'')) throw new ArgumentException("the bridge path cannot contain a single quote");
        var cli = CliOf(mode);
        if (cli == AgentCli.Codex)
        {
            if (l.ClonePath.Contains('\'')) throw new ArgumentException("the clone path cannot contain a single quote");
            // Codex starts a stdio MCP server with only a few variables of its own; the bridge needs the proxy the
            // sandbox injects and the run token, so they are passed through by name (env_vars), never written here.
            var toml = "check_for_update_on_startup = false\n"
                + (mode == CredentialMode.CodexGatewaySignIn
                    // Every chatgpt.com call and the model itself through the gateway, plain HTTP, no WebSocket.
                    ? $"chatgpt_base_url = \"http://{CodexHost}/backend-api/\"\n"
                      + "model_provider = \"yawble\"\n"
                      + "[model_providers.yawble]\n"
                      + "name = \"OpenAI (through Yawble)\"\n"
                      + $"base_url = \"http://{CodexHost}/backend-api/codex\"\n"
                      + "wire_api = \"responses\"\n"
                      + "requires_openai_auth = true\n"
                      + "supports_websockets = false\n"
                    : "")
                + "[mcp_servers.bus]\n"
                + $"command = '{bridge}'\n"
                + "args = []\n"
                + "env_vars = [\"YAWBLE_RUN_TOKEN\", \"HTTP_PROXY\", \"HTTPS_PROXY\", \"http_proxy\", \"https_proxy\", \"ALL_PROXY\", \"NO_PROXY\", \"no_proxy\"]\n"
                + "startup_timeout_sec = 30\n"
                + "tool_timeout_sec = 180\n"
                + $"[projects.'{l.ClonePath}']\n"
                + "trust_level = \"trusted\"\n";
            return [new SeedFile(Join(l, CodexHome(l), "config.toml"), toml)];
        }
        if (cli == AgentCli.Copilot)
        {
            var mcp = new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["bus"] = new JsonObject
                    {
                        ["type"] = "local", ["command"] = bridge, ["args"] = new JsonArray(), ["tools"] = new JsonArray("*"),
                        // Copilot gives a local server only the variables named here (by reference, never a value).
                        ["env"] = new JsonObject
                        {
                            ["YAWBLE_RUN_TOKEN"] = "${YAWBLE_RUN_TOKEN}", ["HTTP_PROXY"] = "${HTTP_PROXY}", ["HTTPS_PROXY"] = "${HTTPS_PROXY}",
                        },
                    },
                },
            };
            var config = new JsonObject { ["trustedFolders"] = new JsonArray(l.ClonePath) };
            return
            [
                new SeedFile(CopilotMcpConfigPath(l), mcp.ToJsonString(SandboxJson.Indented)),
                new SeedFile(Join(l, l.Workspace, ".copilot", "config.json"), config.ToJsonString(SandboxJson.Indented)),
            ];
        }
        if (cli == AgentCli.Antigravity)
        {
            var mcp = new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["bus"] = new JsonObject { ["command"] = bridge, ["args"] = new JsonArray() },
                    // Its own command tool cannot run in an app container (exebox calls CreateDesktop); this one runs
                    // the shell as a plain child process inside the same sandbox. Built-in commands are denied (Arguments).
                    ["shell"] = new JsonObject { ["command"] = bridge, ["args"] = new JsonArray("--shell") },
                },
            };
            // A global rule (~/.gemini/config/, discovered on every run): with its own command tool denied, Antigravity
            // reports the denial instead of using run_shell, so it is told where commands go.
            // The trigger is required: without it agy logs "Invalid rule trigger: CORTEX_MEMORY_TRIGGER_UNSPECIFIED".
            const string shellRule = "---\ntrigger: always_on\n---\n\n# Running commands\n\n"
                + "Your built-in command tool (run_command) is disabled in this environment and every call to it is denied.\n"
                + "Run every command (build, test, git, listing files) with the `run_shell` tool of the `shell` MCP server\n"
                + "instead. It runs Windows PowerShell in your working directory and returns the output and the exit code.\n";
            return
            [
                new SeedFile(Join(l, l.Workspace, ".gemini", "config", "mcp_config.json"), mcp.ToJsonString(SandboxJson.Indented)),
                new SeedFile(Join(l, l.Workspace, ".gemini", "config", "rules", "yawble-commands.md"), shellRule),
            ];
        }
        return [new SeedFile(Join(l, GrokHome(l), "config.toml"), $"[mcp_servers.bus]\ncommand = '{bridge}'\nargs = []\n")];
    }

    public static IReadOnlyList<GatewayVhost> Vhosts(CredentialMode mode) => mode == CredentialMode.CodexGatewaySignIn
        ?
        [
            new GatewayVhost(BusHost, "bus", null, null, ["/mcp"]),
            new GatewayVhost(CodexHost, "model", "https://chatgpt.com", "host-sign-in-held-by-gateway",
                [.. Gateway.Vhosts.PathAllowlists[Gateway.Vhosts.Codex].Select(p => $"{p.Method} {p.Path}")]),
        ]
        : mode == CredentialMode.CopilotGatewaySignIn
        ?
        [
            new GatewayVhost(BusHost, "bus", null, null, ["/mcp"]),
            new GatewayVhost(CopilotHost, "model", "https://api.githubcopilot.com", "host-sign-in-held-by-gateway",
                [.. Gateway.Vhosts.PathAllowlists[Gateway.Vhosts.Copilot].Select(p => $"{p.Method} {p.Path}")]),
        ]
        : CliOf(mode) is AgentCli.Codex or AgentCli.Copilot or AgentCli.Antigravity
        ? [new GatewayVhost(BusHost, "bus", null, null, ["/mcp"])]
        :
    [
        new GatewayVhost(BusHost, "bus", null, null, ["/mcp"]),
        mode switch
        {
            CredentialMode.ClaudeSubscription => new GatewayVhost(ClaudeModelHost, "model", "https://api.anthropic.com", "claude-setup-token",
                ["POST /v1/messages", "POST /v1/messages/count_tokens", "GET /v1/models", "HEAD /api/hello"]),
            CredentialMode.ClaudeApiKey => new GatewayVhost(ClaudeModelHost, "model", "https://api.anthropic.com", "anthropic-api-key",
                ["POST /v1/messages", "POST /v1/messages/count_tokens", "GET /v1/models", "HEAD /api/hello"]),
            CredentialMode.ClaudeHostSignIn => new GatewayVhost(ClaudeModelHost, "model", "https://api.anthropic.com", "host-sign-in",
                ["POST /v1/messages", "POST /v1/messages/count_tokens", "GET /v1/models", "HEAD /api/hello"]),
            CredentialMode.ClaudeGatewaySignIn => new GatewayVhost(ClaudeModelHost, "model", "https://api.anthropic.com", "host-sign-in-held-by-gateway",
                ["POST /v1/messages", "POST /v1/messages/count_tokens", "GET /v1/models", "HEAD /api/hello"]),
            CredentialMode.GrokHostSignIn => new GatewayVhost(GrokLoginHost, "model", "https://cli-chat-proxy.grok.com", "host-sign-in",
                ["GET /v1/user", "GET /v1/settings", "GET /v1/models", "GET /v1/bundle/archive", "GET /"]),
            // The login-mode paths Grok uses.
            CredentialMode.GrokLogin => new GatewayVhost(GrokLoginHost, "model", "https://cli-chat-proxy.grok.com", "grok-login",
                ["GET /v1/user", "GET /v1/settings", "GET /v1/models", "GET /v1/bundle/archive", "GET /"]),
            CredentialMode.GrokGatewaySignIn => new GatewayVhost(GrokLoginHost, "model", "https://cli-chat-proxy.grok.com", "host-sign-in-held-by-gateway",
                ["GET /v1/user", "GET /v1/settings", "GET /v1/models", "GET /v1/bundle/archive", "GET /"]),
            _ => new GatewayVhost(GrokApiKeyHost, "model", "https://api.x.ai", "xai-api-key",
                ["GET /v1/models", "GET /v1/api-key", "POST /v1/responses", "POST /v1/chat/completions"]),
        },
    ];

    private static string Exe(RunLayout l, string dir, string name) =>
        Join(l, dir, l.Os == TargetOs.Windows ? name + ".exe" : name);

    internal static string Join(RunLayout l, string root, params string[] parts) =>
        new PolicyPaths(l.Os, l.Home).Combine(root, parts);

    internal static string Join(TargetOs os, string root, params string[] parts) =>
        new PolicyPaths(os, os == TargetOs.Windows ? @"C:\" : "/").Combine(root, parts);
}
