using System.Text.Json.Nodes;
using Yawble.Core.Agents;
using Yawble.Core.Policy;

namespace Yawble.Core.Sandboxing;

/// <summary>
/// The compiler's invariants (things the app must never do, and the credential invariants). They
/// are checked on every compiled run, independently of how the compiler built it, so a future change to the
/// compiler that emits a forbidden option fails loudly instead of launching. Each rule is unit-tested by
/// mutating a valid request.
/// </summary>
public static class SandboxRequestInvariants
{
    private static readonly string[] ProxyVariables =
        ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy"];

    private static readonly string[] TopLevel =
        ["version", "containment", "containerId", "process", "filesystem", "network", "runtimeConfig", "ui", "processContainer", "fallback", "seatbelt", "telemetry"];

    public static IReadOnlyList<string> Check(CompiledRun compiled, RunSpec run, Func<string, string>? canonicalize = null)
    {
        var problems = new List<string>(CheckRequest(compiled.Request, run, canonicalize));

        // Credential invariants outside the request.
        // The preset's own vendor hosts (Codex, Copilot) are exempt: only that CLI's runs get them, and no policy can.
        var passthrough = AgentPresets.PassthroughHosts(run.Credential);
        foreach (var h in compiled.Gateway.AllowHosts.Where(h => !passthrough.Contains(h)))
            if (FloorRules.HostProblem(h) is { } p) problems.Add($"gateway allowHosts: {p}");
        foreach (var seed in compiled.Seeds)
        {
            var name = seed.Path.Replace('\\', '/').Split('/').Last();
            if (name is ".credentials.json" or "auth.json")
                problems.Add($"seed {seed.Path}: a credentials file is never seeded");
            if (seed.Content.Contains("apiKeyHelper", StringComparison.Ordinal))
                problems.Add($"seed {seed.Path}: apiKeyHelper is never set");
            if (seed.Content.Contains(run.RunToken, StringComparison.Ordinal))
                problems.Add($"seed {seed.Path}: the run token is never written to disk");
        }
        if (run.Layout.Cli == AgentCli.Claude) problems.AddRange(ClaudeSeedProblems(compiled.Seeds, run.Layout));
        return problems;
    }

    /// <summary>The 4-key TUI seed: without any one of them the Claude TUI stops on a screen.</summary>
    public static IEnumerable<string> ClaudeSeedProblems(IReadOnlyList<SeedFile> seeds, RunLayout layout)
    {
        JsonObject? Load(string file)
        {
            var seed = seeds.FirstOrDefault(s => s.Path == AgentPresets.Join(layout, AgentPresets.ClaudeConfigDir(layout), file));
            try { return seed is null ? null : JsonNode.Parse(seed.Content) as JsonObject; }
            catch (System.Text.Json.JsonException) { return null; }
        }
        var state = Load(".claude.json");
        var settings = Load("settings.json");
        if (state?["hasCompletedOnboarding"]?.GetValue<bool>() != true) yield return "Claude seed: hasCompletedOnboarding must be true";
        if (state?["hasSeenAutoDefaultNudge"]?.GetValue<bool>() != true) yield return "Claude seed: hasSeenAutoDefaultNudge must be true";
        if (state?["projects"]?[AgentPresets.ClaudeProjectKey(layout)]?["hasTrustDialogAccepted"]?.GetValue<bool>() != true)
            yield return "Claude seed: the clone must be a trusted project";
        if (settings?["skipDangerousModePermissionPrompt"]?.GetValue<bool>() != true)
            yield return "Claude seed: skipDangerousModePermissionPrompt must be true";
    }

    public static IReadOnlyList<string> CheckRequest(JsonObject request, RunSpec run, Func<string, string>? canonicalize = null)
    {
        var problems = new List<string>();
        var l = run.Layout;
        var os = l.Os;
        void Fail(string s) => problems.Add(s);
        string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

        foreach (var key in request.Select(p => p.Key))
            if (!TopLevel.Contains(key)) Fail($"unexpected top-level field '{key}' (lifecycle, lxc, wslc, processContainer, phase and others are never emitted)");

        if (Str(request["version"]) != SandboxPin.RequestContract) Fail("version must be the pinned 1.0.0 contract");
        if (Bool(request["telemetry"]?["enabled"]) != false) Fail("telemetry.enabled must be false");
        if (Str(request["containment"]) != os.Containment()) Fail($"containment must be {os.Containment()} on {os}");

        // ProcessContainer: off Windows never; on Windows only ui (the strict limits) and filesystem.enumeratePaths.
        // captureDenials, learningMode, capabilities, leastPrivilege and network are never emitted (the first two
        // turn enforcement off).
        if (request["processContainer"] is JsonObject pc)
        {
            if (os != TargetOs.Windows) Fail("processContainer is Windows-only");
            foreach (var key in pc.Select(p => p.Key))
                if (key is not ("ui" or "filesystem")) Fail($"processContainer.{key} is never emitted");
            var limits = SandboxRequestCompiler.WindowsUiLimits();
            var pcUi = pc["ui"] as JsonObject;
            foreach (var (key, want) in limits)
                if (!JsonNode.DeepEquals(pcUi?[key], want)) Fail($"processContainer.ui.{key} must be {want!.ToJsonString()}");
            foreach (var key in pcUi?.Select(p => p.Key) ?? [])
                if (!limits.ContainsKey(key)) Fail($"processContainer.ui.{key} is never emitted");
            if (pc["filesystem"] is JsonObject pfs)
            {
                foreach (var key in pfs.Select(p => p.Key))
                    if (key != "enumeratePaths") Fail($"processContainer.filesystem.{key} is never emitted");
                var fsec = request["filesystem"];
                List<string> L(JsonNode? n) => (n as JsonArray)?.Select(x => Str(x) ?? "").ToList() ?? [];
                var grants = L(fsec?["readonlyPaths"]).Concat(L(fsec?["readwritePaths"])).ToList();
                var deny = L(fsec?["deniedPaths"]);
                var pp = new PolicyPaths(os, l.Home, canonicalize);
                var allowed = SandboxRequestCompiler.WindowsEnumeratePaths(grants, deny, pp);
                foreach (var e in L(pfs["enumeratePaths"]))
                    if (!allowed.Contains(e, pp.Comparer))
                        Fail($"processContainer.filesystem.enumeratePaths: {e} is not the root of a drive holding a grant");
            }
        }
        else if (os == TargetOs.Windows) Fail("processContainer.ui (the strict UI limits) must be explicit on Windows");
        if (request["seatbelt"] is JsonObject sb)
        {
            if (os != TargetOs.MacOS) Fail("seatbelt section on a non-macOS request");
            if (sb.ContainsKey("profileOverride")) Fail("seatbelt.profileOverride is never emitted (it drops all policy)");
            if (sb.ContainsKey("guiAccess")) Fail("seatbelt.guiAccess is never emitted");
            if (Bool(sb["keychainAccess"]) != false) Fail("seatbelt.keychainAccess must be false");
            foreach (var key in sb.Select(p => p.Key))
                if (key != "keychainAccess") Fail($"seatbelt.{key} is never emitted");
        }
        else if (os == TargetOs.MacOS) Fail("seatbelt.keychainAccess: false must be explicit on macOS");
        if (os == TargetOs.Windows)
        {
            if (Bool(request["fallback"]?["allowDaclMutation"]) != false) Fail("fallback.allowDaclMutation must be false (host ACLs are never changed)");
        }
        else if (request["fallback"] is not null) Fail("fallback is Windows-only");

        // Network: egress deny, no direct rules, the run's gateway as the only proxy.
        var net = request["network"] as JsonObject;
        if (Str(net?["egress"]?["default"]) != "deny") Fail("network.egress.default must be deny");
        if (net?["egress"]?["allow"] is not null || net?["egress"]?["deny"] is not null) Fail("network.egress rules are never emitted (the gateway filters by host)");
        var ingress = Str(net?["ingress"]?["default"]);
        var loopback = Str(net?["ingress"]?["hostLoopback"]);
        if (os == TargetOs.Windows)
        {
            if (ingress != "allow" || loopback != "allow") Fail("Windows (unpackaged) needs ingress.default and hostLoopback allow with the proxy");
        }
        else
        {
            if (loopback != "deny") Fail($"network.ingress.hostLoopback: allow is never emitted on {os}");
            if (ingress != "deny") Fail($"network.ingress.default must be deny on {os}");
        }
        if (Str(request["runtimeConfig"]?["networkProxy"]) != $"http://127.0.0.1:{run.GatewayPort}")
            Fail("runtimeConfig.networkProxy must be this run's gateway on 127.0.0.1");

        // UI.
        var ui = request["ui"];
        // Windows: false (Win32k kept for USER32 programs; the strict limits are checked under processContainer).
        if (Bool(ui?["disable"]) != (os != TargetOs.Windows)) Fail($"ui.disable must be {(os != TargetOs.Windows ? "true" : "false")} on {os}");
        if (Str(ui?["clipboard"]) != "none") Fail("ui.clipboard must be none");
        if (Bool(ui?["injection"]) != false) Fail("ui.injection must be false");

        // Process: explicit env, cwd = clone, a wall-clock timeout.
        var process = request["process"] as JsonObject;
        if (process is null) { Fail("process is missing"); return problems; }
        if (process.ContainsKey("inheritDefaultEnv")) Fail("process.inheritDefaultEnv is never emitted");
        var timeout = process["timeout"] is JsonValue tv && tv.TryGetValue<long>(out var t) ? t : 0;
        if (timeout <= 0) Fail("process.timeout must carry the wall-clock limit");
        var paths = new PolicyPaths(os, l.Home, canonicalize);
        string R(string p) => paths.Resolve(p) ?? p;
        if (!paths.Same(Str(process["cwd"]) ?? "", R(l.ClonePath))) Fail("process.cwd must be the member's clone");
        var commandLine = Str(process["commandLine"]) ?? "";
        // The detected CLI, or (Windows, Grok) the bundled yawble-run directly in front of it.
        if (!commandLine.StartsWith(CommandLine.Join(os, AgentPresets.Launch(l)), StringComparison.Ordinal)) Fail("process.commandLine must start with the detected CLI's absolute path");
        // The CLI's program must be inside a read-only grant (exactly its install folders), never a writable one.
        if (!l.CliInstall.Placeholder)
        {
            var roGrants = (request["filesystem"]?["readonlyPaths"] as JsonArray)?.Select(n => Str(n) ?? "").ToList() ?? [];
            foreach (var part in l.CliCommand)
                if (!roGrants.Any(g => paths.IsWithin(R(part), g))) Fail($"the CLI's program {part} is not inside a read-only grant");
        }

        if (process["env"] is not JsonArray envArray) { Fail("process.env must be explicit (an omitted env leaks the user profile on Windows)"); return problems; }
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in envArray)
        {
            var s = Str(item) ?? "";
            var eq = s.IndexOf('=');
            if (eq <= 0) { Fail($"env entry '{s}' is not KEY=VALUE"); continue; }
            if (!env.TryAdd(s[..eq], s[(eq + 1)..])) Fail($"env {s[..eq]} is set twice");
        }
        if (os == TargetOs.Windows)
            foreach (var required in new[] { "SYSTEMROOT", "LOCALAPPDATA" })
                if (!env.ContainsKey(required)) Fail($"env {required} is required by ProcessContainer");
        foreach (var proxy in ProxyVariables)
            if (env.Keys.Any(k => string.Equals(k, proxy, StringComparison.OrdinalIgnoreCase))) Fail($"env {proxy} is never set (the sandbox injects the proxy)");
        if (env.GetValueOrDefault("YAWBLE_RUN_TOKEN") != run.RunToken) Fail("env YAWBLE_RUN_TOKEN must be the run token");
        if (!paths.Same(env.GetValueOrDefault("HOME") ?? "", R(l.Workspace))) Fail("env HOME must be the member's workspace");
        if (env.GetValueOrDefault("IS_SANDBOX") != "1") Fail("env IS_SANDBOX must be 1");
        problems.AddRange(CheckCredentials(env, run));

        // Filesystem: never grant the app data root, the DB, the host Grok home, credentials, other members.
        var fs = request["filesystem"] as JsonObject;
        List<string> List(string key) => (fs?[key] as JsonArray)?.Select(n => Str(n) ?? "").ToList() ?? [];
        var ro = List("readonlyPaths");
        var rw = List("readwritePaths");
        var denied = List("deniedPaths");
        var appRoot = R(l.AppDataRoot);
        foreach (var implicitDeny in l.ImplicitDenied.Select(R))
        {
            if (!denied.Any(d => paths.Same(d, implicitDeny))) Fail($"deniedPaths must contain the implicit denial '{implicitDeny}'");
            foreach (var g in ro.Concat(rw))
                if (paths.Overlaps(g, implicitDeny)) Fail($"grant '{g}' overlaps the implicitly denied '{implicitDeny}'");
        }
        var own = l.ImplicitReadwrite.Select(R).ToList();
        // The team's documents may be granted, and only the member's own team's.
        if (l.TeamDocuments is { } docs && !paths.Same(R(docs), R(l.TeamDocumentsCandidate)))
            Fail($"the team documents '{docs}' are not this member's team's");
        // The team's shared view may be granted, read-only, and only the member's own team's.
        // So may the team's extra repositories' shared views and reference checkouts (0022): read-only, inside the
        // member's own team folder, one plain folder name each.
        foreach (var n in l.ExtraRepoNames.Concat(l.ReferenceRepoNames))
            if (!Teams.TeamRepos.IsRepoName(n)) Fail($"'{n}' is not a repository name");
        var teamReadonly = l.ExtraSharedPaths.Concat(l.ReferencePaths).Select(R).ToList();
        bool IsSharedView(string g) => (l.SharedRepo is { } shared && paths.Same(g, R(shared)) && paths.Same(R(shared), R(l.SharedRepoCandidate))
                || teamReadonly.Any(t => paths.Same(g, t)))
            && ro.Any(r => paths.Same(r, g)) && !rw.Any(w => paths.Overlaps(w, g));
        foreach (var g in ro.Concat(rw))
        {
            if (paths.IsWithin(appRoot, g)) Fail($"grant '{g}' contains the app data root");
            else if (paths.IsWithin(g, appRoot) && !own.Any(o => paths.Same(o, g)) && !IsSharedView(g))
                Fail($"grant '{g}' is inside the app data root and is not one of the member's own folders");
            if (paths.Same(g, R(l.Home))) Fail("the person's home folder is never granted");
        }
        foreach (var g in own)
            if (!rw.Any(r => paths.Same(r, g))) Fail($"readwritePaths must contain the member's own folder '{g}'");
        // The CLI's install folders are read-only, always: no writable grant may reach into them or cover them, so a
        // self-update from inside the sandbox can only fail.
        foreach (var install in l.CliInstall.ReadOnlyFolders.Select(R))
        {
            if (!ro.Any(g => paths.Same(g, install))) Fail($"readonlyPaths must contain the CLI's install folder '{install}'");
            foreach (var g in rw)
                if (paths.Overlaps(g, install)) Fail($"writable grant '{g}' overlaps the CLI's install folder '{install}'");
        }
        // Host sign-in: the host's sign-in files are granted exactly, as the catalog says, and nothing else of the CLI's
        // config folder is: a read-only sign-in file is never reachable by a writable grant.
        foreach (var file in l.CliInstall.SignInReadOnly.Select(R))
        {
            if (!ro.Any(g => paths.Same(g, file))) Fail($"readonlyPaths must contain the read-only sign-in file '{file}'");
            foreach (var g in rw)
                if (paths.Overlaps(g, file)) Fail($"writable grant '{g}' overlaps the read-only sign-in file '{file}'");
        }
        foreach (var file in l.CliInstall.SignInReadWrite.Select(R))
            if (!rw.Any(g => paths.Same(g, file))) Fail($"readwritePaths must contain the read-write sign-in file '{file}'");
        foreach (var config in l.CliInstall.ConfigFolders.Select(R))
            foreach (var g in ro.Concat(rw))
                if (paths.IsWithin(g, config) || paths.IsWithin(config, g))
                {
                    var allowed = l.CliInstall.ReadOnlyFolders.Concat(l.CliInstall.SignInReadOnly).Concat(l.CliInstall.SignInReadWrite).Select(R);
                    if (!allowed.Any(a => paths.Same(a, g))) Fail($"grant '{g}' reaches into the CLI's config folder '{config}' and is not one of its install folders or sign-in files");
                }
        return problems;
    }

    /// <summary>No credential variable except the placeholder; never two Claude tiers; Grok's rules.</summary>
    public static IReadOnlyList<string> CheckCredentials(IReadOnlyDictionary<string, string> env, RunSpec run)
    {
        var problems = new List<string>();
        var mode = run.Credential;
        bool Has(string k) => env.ContainsKey(k);

        foreach (var (key, value) in env)
        {
            if (key == "GROK_AUTH_PROVIDER_COMMAND")
            {
                if (value != AgentPresets.GrokAuthPath(run.Layout)) problems.Add("GROK_AUTH_PROVIDER_COMMAND must be the bundled yawble-grok-auth");
                continue;
            }
            var credentialLike = AgentPresets.CredentialVariables.Contains(key)
                || key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) || key.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
                || key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) || key.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("GROK_OIDC_", StringComparison.OrdinalIgnoreCase);
            // The one exception (host sign-in for Copilot): its own token from the host's credential store, in its own
            // variable, on a Copilot run only. It must be the host's sign-in, never the run token or an empty value.
            // Gateway-held Copilot: the variable carries the run token, GitHub-shaped, and nothing else.
            if (mode == CredentialMode.CopilotGatewaySignIn && key == AgentPresets.CopilotTokenVariable)
            {
                if (value != AgentPresets.CopilotPlaceholder(run.RunToken)) problems.Add($"env {key} must be the run token placeholder (gho_<run token>)");
                continue;
            }
            if (mode == CredentialMode.CopilotHostSignIn && key == AgentPresets.CopilotTokenVariable)
            {
                if (value.Length == 0 || value == run.RunToken) problems.Add($"env {key} must be Copilot's own sign-in from the host");
                continue;
            }
            if (credentialLike && value != run.RunToken)
                problems.Add($"env {key}: a credential variable may carry only the run token placeholder");
            if (!credentialLike && value.Contains(run.RunToken, StringComparison.Ordinal))
                problems.Add($"env {key}: the run token appears outside a credential variable");
        }

        // Never in any mode.
        foreach (var never in new[] { "ANTHROPIC_API_KEY", "GROK_CODE_XAI_API_KEY", "CLAUDE_CODE_OAUTH_REFRESH_TOKEN", "CLAUDE_CODE_HOST_CREDS_FILE" })
            if (Has(never)) problems.Add($"env {never} is never set");
        if (env.Keys.Any(k => k.StartsWith("GROK_OIDC_", StringComparison.OrdinalIgnoreCase))) problems.Add("env GROK_OIDC_* is never set");
        if (Has("CLAUDE_CODE_OAUTH_TOKEN") && (Has("ANTHROPIC_API_KEY") || Has("ANTHROPIC_AUTH_TOKEN")))
            problems.Add("never two Claude credential tiers at once (CLAUDE_CODE_OAUTH_TOKEN with an API key or auth token)");

        switch (mode)
        {
            case CredentialMode.ClaudeSubscription:
            case CredentialMode.ClaudeGatewaySignIn:
                if (!Has("CLAUDE_CODE_OAUTH_TOKEN")) problems.Add("Claude subscription mode needs CLAUDE_CODE_OAUTH_TOKEN=<run token>");
                if (Has("ANTHROPIC_AUTH_TOKEN")) problems.Add("Claude subscription mode never sets ANTHROPIC_AUTH_TOKEN");
                // The gateway holds the sign-in: nothing points the CLI at the host's sign-in file.
                if (mode == CredentialMode.ClaudeGatewaySignIn && Has(SignInCatalog.ClaudeStoreVariable))
                    problems.Add($"gateway-held Claude sign-in never sets {SignInCatalog.ClaudeStoreVariable}");
                break;
            case CredentialMode.ClaudeApiKey:
                if (!Has("ANTHROPIC_AUTH_TOKEN")) problems.Add("Claude API-key mode needs ANTHROPIC_AUTH_TOKEN=<run token>");
                if (Has("CLAUDE_CODE_OAUTH_TOKEN")) problems.Add("Claude API-key mode never sets CLAUDE_CODE_OAUTH_TOKEN");
                break;
        }
        // Host sign-in: the CLI's own sign-in, read from the host file the request grants; no credential variable at all,
        // and the variable that points at the file names exactly the catalog's file (or its folder).
        if (AgentPresets.IsHostSignIn(mode))
        {
            foreach (var k in new[] { "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_AUTH_TOKEN", "XAI_API_KEY", "GROK_AUTH_PROVIDER_COMMAND", "GROK_AUTH" })
                if (Has(k)) problems.Add($"host sign-in mode never sets {k}");
            foreach (var (k, v) in run.Layout.CliInstall.SignInEnvironment)
                if (env.GetValueOrDefault(k) != v) problems.Add($"host sign-in mode needs {k}={v}");
            var expected = mode switch
            {
                CredentialMode.ClaudeHostSignIn => "CLAUDE_SECURESTORAGE_CONFIG_DIR",
                CredentialMode.GrokHostSignIn => "GROK_AUTH_PATH",
                CredentialMode.CopilotHostSignIn => AgentPresets.CopilotTokenVariable,
                _ => null,
            };
            if (expected is not null && run.Layout.CliInstall.SignIn is { Status: Agents.SignInStatus.SignedIn } && !Has(expected))
                problems.Add($"host sign-in mode needs {expected}");
        }
        else
        {
            foreach (var k in new[] { "CLAUDE_SECURESTORAGE_CONFIG_DIR", "GROK_AUTH_PATH" })
                if (Has(k)) problems.Add($"{k} is set only in host sign-in mode");
        }
        var cli = AgentPresets.CliOf(mode);
        if (cli is AgentCli.Codex or AgentCli.Copilot or AgentCli.Antigravity)
        {
            foreach (var k in env.Keys.Where(k => k.StartsWith("CLAUDE_", StringComparison.Ordinal) || k.StartsWith("ANTHROPIC_", StringComparison.Ordinal)
                         || k.StartsWith("GROK_", StringComparison.Ordinal) || k == "XAI_API_KEY"))
                problems.Add($"env {k} on a {cli.DisplayName()} run");
            if (cli == AgentCli.Codex)
            {
                foreach (var k in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN" })
                    if (Has(k)) problems.Add($"env {k} is never set: Codex uses the person's own sign-in");
                if (env.GetValueOrDefault("CODEX_HOME") != AgentPresets.CodexHome(run.Layout)) problems.Add("CODEX_HOME must be the member's own Codex home");
            }
            if (cli == AgentCli.Copilot)
            {
                foreach (var k in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
                    if (Has(k)) problems.Add($"env {k} is never set: Copilot gets only its own sign-in");
                if (env.GetValueOrDefault("COPILOT_AUTO_UPDATE") != "false") problems.Add("COPILOT_AUTO_UPDATE must be false");
            }
            return problems;
        }
        if (cli == AgentCli.Claude)
        {
            if (env.GetValueOrDefault("ANTHROPIC_BASE_URL") != "http://" + AgentPresets.ClaudeModelHost) problems.Add("ANTHROPIC_BASE_URL must be the gateway's model host");
            if (env.GetValueOrDefault("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC") != "1") problems.Add("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC must be 1");
            if (env.GetValueOrDefault("DISABLE_AUTOUPDATER") != "1") problems.Add("DISABLE_AUTOUPDATER must be 1");
            if (env.GetValueOrDefault("CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL") != "1") problems.Add("CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL must be 1");
            foreach (var k in env.Keys.Where(k => k.StartsWith("GROK_", StringComparison.Ordinal) || k == "XAI_API_KEY")) problems.Add($"env {k} on a Claude run");
        }
        else
        {
            foreach (var k in new[] { "GROK_CLI_CHAT_PROXY_BASE_URL", "GROK_XAI_API_BASE_URL", "GROK_MODELS_BASE_URL" })
            {
                var v = env.GetValueOrDefault(k);
                if (v is null || !Uri.TryCreate(v, UriKind.Absolute, out var u) || u.Scheme != "http" || !u.Host.EndsWith("." + FloorRules.ReservedSuffix, StringComparison.Ordinal))
                    problems.Add($"Grok always gets all three base URLs on the gateway: {k}");
            }
            if (mode == CredentialMode.GrokHostSignIn)
            {
                // checked above
            }
            else if (mode is CredentialMode.GrokLogin or CredentialMode.GrokGatewaySignIn)
            {
                if (!Has("GROK_AUTH_PROVIDER_COMMAND")) problems.Add("Grok login mode needs GROK_AUTH_PROVIDER_COMMAND=yawble-grok-auth");
                if (Has("XAI_API_KEY")) problems.Add("Grok login mode never sets XAI_API_KEY");
            }
            else
            {
                if (!Has("XAI_API_KEY")) problems.Add("Grok API-key mode needs XAI_API_KEY=<run token>");
                if (Has("GROK_AUTH_PROVIDER_COMMAND")) problems.Add("Grok API-key mode never sets GROK_AUTH_PROVIDER_COMMAND");
            }
            if (env.GetValueOrDefault("GROK_DISABLE_AUTOUPDATER") != "1") problems.Add("GROK_DISABLE_AUTOUPDATER must be 1");
            if (env.GetValueOrDefault("GROK_AUTO_UPDATE") != "0") problems.Add("GROK_AUTO_UPDATE must be 0");
            if (env.GetValueOrDefault("GROK_CHANGELOG_OFFLINE") != "1") problems.Add("GROK_CHANGELOG_OFFLINE must be 1");
            foreach (var k in env.Keys.Where(k => k.StartsWith("CLAUDE_", StringComparison.Ordinal) || k.StartsWith("ANTHROPIC_", StringComparison.Ordinal))) problems.Add($"env {k} on a Grok run");
        }
        return problems;
    }
}

/// <summary>The Windows executor's arguments (wxc-exec.exe; interactive runs put it under ConPTY).</summary>
public static class WxcExec
{
    /// <summary>Executor flags that turn enforcement off. Never passed.</summary>
    public static IReadOnlyList<string> NeverPass { get; } = ["--audit"];

    /// <summary>
    /// The request goes in a file only the core can read (<c>--config</c>), never <c>--config-base64</c>:
    /// a command line is visible to the user's other processes and the request carries the run token.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string requestFile) => ["--config", requestFile];
}
