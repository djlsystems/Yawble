using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;

namespace Harness.Host;

/// <summary>
/// The Agent catalog on disk, at &lt;dataRoot&gt;/agents.json.
///
/// A FILE rather than a table, chosen deliberately: an operator can repair a
/// catastrophic catalog by hand, with no SQL and no tooling, when the Host will not start. The cost
/// is that it sits outside the database's backup story - the catalog is CONFIGURATION, not tenant
/// data, so `scripts/backup.ps1` and `--restore` leave it alone and a restored database
/// can legitimately reference an Agent this file does not have.
/// </summary>
public static class AgentCatalogFile
{
    /// <summary>
    /// The exact options this file is read and written through. Public rather than internal: there
    /// is no <c>InternalsVisibleTo</c> from <c>Harness.Host</c> to <c>Harness.Host.Tests</c>,
    /// and a test deserialising an <see cref="AgentLaunch"/> with different options than the product
    /// uses would prove nothing about a real catalog file.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // `AgentDefinition.BuiltIn` is computed from the name and belongs on the wire, never in
        // the file: a file that said it would be a second store of a fact the build decides.
        IgnoreReadOnlyProperties = true,

        // So `mode` reads as `"Headless"` / `"Interactive"` on disk rather than 0 / 1 - an operator
        // repairing this file by hand should never need to remember which number means which world.
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathIn(string dataRoot) => Path.Combine(dataRoot, "agents.json");

    /// <summary>
    /// The instructions file every CLI here reads except Claude Code, which takes a path on the
    /// command line instead. One constant rather than six string literals: it is the same
    /// convention in every case, and six copies is six chances for one of them to be a typo that
    /// presents as a member with no system prompt.
    /// </summary>
    private const string AgentsFile = "AGENTS.md";

    /// <summary>
    /// The built-in presets, compiled into the host. Listed read-only: a person may add
    /// custom presets and edit or delete only those. None of these is written to agents.json, so a
    /// fix to one reaches every instance on its next start.
    /// </summary>
    public static IReadOnlyList<AgentDefinition> BuiltIns() => Seed();

    public static bool IsBuiltIn(string name) =>
        Seed().Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<AgentDefinition> Seed()
    {
        var claudeArguments = new[] { "--append-system-prompt-file", "{systemPromptFile}" };

        // THE MODEL IS NAMED, AND IT IS NAMED AS AN ALIAS RATHER THAN AS A VERSION. `opus` resolves
        // to the newest Opus the installed CLI knows about, so a preset written today does not pin
        // an instance to a model that is superseded next month - which is the failure a full model
        // id would guarantee and nothing would report, since a stale-but-valid id runs perfectly.
        //
        // WITHOUT IT THE CLI CHOOSES, and what it chooses is not written down anywhere: two members
        // of one team could run different models and every card would look identical. A run's cost
        // and its quality both depend on this, so the catalog states it.
        //
        // NOT `LanguageModel`, which is a BOOL - whether this preset launches a language model at
        // all, as opposed to a build script. There is no model-NAME field on `AgentLaunch`, and
        // adding one would be a second store of something the arguments already carry.
        var claudeModel = new[] { "--model", "opus" };

        // CODEX REACHES THE PLATFORM BY FLAG, NOT BY FILE. `-c` overrides one `config.toml` key for
        // this launch alone; the file would be `~/.codex/config.toml`, and every team shares one
        // agent home, so one launch's entry would be every member's. `{mcpUrl}` is this host's
        // `/mcp`, and `env_http_headers` names the VARIABLE the key is read from - the child holds
        // it as HARNESS_KEY, set by both launch paths - so no key is on the command line or on disk.
        //
        // Measured against codex-cli 0.156.0, not signed in: `codex exec` with these two flags
        // POSTs MCP `initialize` to the URL carrying `X-Api-Key: <$HARNESS_KEY>` before it ever
        // reaches the model. A misspelt key is IGNORED SILENTLY (`codex mcp get` shows it
        // absent) while a wrong type is refused, so the spelling is what the tests pin.
        var codexMcp = new[]
        {
            "-c", "mcp_servers.harness.url=\"{mcpUrl}\"",
            "-c", "mcp_servers.harness.env_http_headers={\"X-Api-Key\"=\"HARNESS_KEY\"}",
        };

        // WHERE TO GET EACH CLI, as SEED DATA - which is the whole of why this is expressed here
        // and not as a lookup in the Host mapping a preset's name to a vendor's page. Such a table
        // would work everywhere immediately, reach every existing instance, and be exactly the
        // thing this repository refuses: nothing in code may name an Agent, because a name in code
        // is a reference nothing wrote down, so no check sees it and no rename moves it. Here the
        // URL travels with the preset, so renaming, editing or deleting the preset takes it along.
        //
        // ONE LOCAL PER BRAND rather than a literal per entry, for the reason `AgentsFile` above is
        // one constant rather than six: each brand's interactive and headless presets install the
        // same way, and two copies of one URL is one chance for them to drift into disagreeing
        // about where a person should go.
        //
        // THESE ARE AN OPINION THE SEED CARRIES, like `Tags` beside them - an operator is expected
        // to correct one on the Agents screen when a vendor moves its documentation, and that is
        // the recovery this shape exists to make possible. A built-in preset comes from
        // the build, so a corrected URL here reaches every instance on its next start.
        var claudeInstall = new AgentInstall(
            "https://docs.claude.com/en/docs/claude-code/setup",
            "npm install -g @anthropic-ai/claude-code");

        var codexInstall = new AgentInstall(
            "https://developers.openai.com/codex/cli/",
            "npm install -g @openai/codex");

        var copilotInstall = new AgentInstall(
            "https://github.com/github/copilot-cli",
            "npm install -g @github/copilot");

        var grokInstall = new AgentInstall(
            "https://docs.x.ai/build/overview",
            "The container installs grok on startup. Set XAI_API_KEY to sign it in.");

        return
        [
            new AgentDefinition(
                "claude",
                AgentMode.Interactive,
                new AgentLaunch(
                    "claude",
                    ["--dangerously-skip-permissions", "--mcp-config", "{mcpConfig}", .. claudeModel],
                    claudeArguments),
                Install: claudeInstall),

            new AgentDefinition(
                "claude-headless",
                AgentMode.Headless,
                new AgentLaunch(
                    "claude",
                    // `--session-id {sessionId}` names the transcript before the run starts, so the
                    // live view below knows its file. Measured with `-p` on the container:
                    // the transcript is named by that id. The output format is unchanged.
                    ["-p", "--dangerously-skip-permissions", "--mcp-config", "{mcpConfig}", "--session-id", "{sessionId}", "--output-format", "json", .. claudeModel],
                    claudeArguments,
                    UsageFormat: "claude-json"),
                // Thirty minutes, a platform default: an invocation whose agent leaves a long-lived
                // child holding stdio never completes at all unless some clock rescues it. NULL
                // means unbounded, so an operator who wants that clears the field.
                //
                // NOT SHORTER, and the reason is what this clock actually measures: it is a
                // SILENCE watchdog, not a cap on how long a run may take. `RunHeartbeat.Touch`
                // resets it on every `container.progress`, so a member that narrates runs
                // indefinitely and a member that goes quiet dies - however much real work it is
                // doing. A member running the test suite produces no progress lines for minutes at
                // a time, and two suites competing for one machine stretch that further, so a
                // shorter clock kills members mid-verification while they are working correctly.
                // The failure presents as an agent fault rather than as contention, which is what
                // makes it expensive to diagnose.
                //
                // Thirty minutes is a silence budget rather than a work budget: it bounds how long
                // a run may say NOTHING before the platform stops paying for it. A member that
                // reports progress is never bounded by it at all.
                TimeoutSeconds: 1800,

                // TAGS MUST DIFFER BETWEEN PRESETS OR THEY DO NOTHING. A manager hires by asking
                // for a CAPABILITY, and the platform picks the least-used allowlist entry carrying
                // that tag, ties broken by allowlist position. Tag every preset with every tag and
                // every tag matches every preset, so the rotation degrades to plain round-robin and
                // the feature is inert while looking configured.
                //
                // Which model is good at what is a JUDGEMENT rather than something to derive:
                // the seed carries an opinion, and an operator is expected to disagree with it on
                // the Agents screen. What the seed must not carry is an opinion that says nothing.
                Tags: ["developer", "tester"],
                Install: claudeInstall,

                // Claude writes its session to this file as it works, named by the
                // `--session-id` above; the cwd is the folder name with every `/` and `.` as `-`.
                LiveView: new AgentLiveView(
                    "~/.claude/projects/{workspaceDashed}/{sessionId}.jsonl", LiveView.ClaudeJsonl)),

            // The three other coding CLIs, verified against their own --help rather than from
            // documentation, which disagreed with the binaries in several places.
            //
            // NONE of them takes a system prompt as a file on the command line, so all six carry
            // `AGENTS.md` as their instructions file instead - the one convention all three read.
            // codex has `-c model_instructions_file=`, but its own schema calls it a REPLACEMENT
            // for the built-in instructions rather than an addition, which is a different thing
            // from what a member's prompt is.
            //
            // Their headless halves also take the prompt as an ARGUMENT rather than on stdin, which
            // is what `{userPrompt}` and `{userPromptFile}` exist for. `codex exec` is the exception - it
            // reads stdin when given no positional prompt - and is left on stdin for that reason.

            new AgentDefinition(
                "codex",
                AgentMode.Interactive,
                new AgentLaunch(
                    "codex",
                    ["--dangerously-bypass-approvals-and-sandbox", .. codexMcp],
                    InstructionsFile: AgentsFile),
                Install: codexInstall),

            new AgentDefinition(
                "codex-headless",
                AgentMode.Headless,
                new AgentLaunch(
                    "codex",
                    // --skip-git-repo-check because a container's workspace is a scratch directory
                    // rather than a checkout, and codex otherwise refuses to run outside a repo.
                    ["exec", "--dangerously-bypass-approvals-and-sandbox", "--skip-git-repo-check", .. codexMcp],
                    InstructionsFile: AgentsFile,
                    // ONE COMBINED FIGURE, WHICH BEATS SILENCE. codex emits no JSON envelope and no
                    // in/out split - it prints `tokens used N` as its last line - so without this
                    // every run would be `(unknown)` while the number sat in the transcript. The
                    // split stays null rather than being invented from the total.
                    UsageFormat: "codex-total"),
                // Thirty minutes, a platform default: an invocation whose agent leaves a long-lived
                // child holding stdio never completes at all unless some clock rescues it. NULL
                // means unbounded, so an operator who wants that clears the field.
                //
                // NOT SHORTER, and the reason is what this clock actually measures: it is a
                // SILENCE watchdog, not a cap on how long a run may take. `RunHeartbeat.Touch`
                // resets it on every `container.progress`, so a member that narrates runs
                // indefinitely and a member that goes quiet dies - however much real work it is
                // doing. A member running the test suite produces no progress lines for minutes at
                // a time, and two suites competing for one machine stretch that further, so a
                // shorter clock kills members mid-verification while they are working correctly.
                // The failure presents as an agent fault rather than as contention, which is what
                // makes it expensive to diagnose.
                //
                // Thirty minutes is a silence budget rather than a work budget: it bounds how long
                // a run may say NOTHING before the platform stops paying for it. A member that
                // reports progress is never bounded by it at all.
                TimeoutSeconds: 1800,
                Tags: ["tester"],
                Install: codexInstall,

                // `codex exec` takes no session id (measured, codex-cli 0.157.0), so its
                // rollout is FOUND: the newest under `~/.codex/sessions/YYYY/MM/DD/` written after
                // launch whose first line (`session_meta`) names this workspace as its `cwd`.
                LiveView: new AgentLiveView(
                    null, LiveView.CodexRollout,
                    new AgentLiveViewFind("~/.codex/sessions", "*/*/*/rollout-*.jsonl", LiveView.CwdFromFirstLine))),

            new AgentDefinition(
                "copilot",
                AgentMode.Interactive,
                // --allow-all is the documented shortcut for --allow-all-tools --allow-all-paths
                // --allow-all-urls. It is a permission model rather than a sandbox: there is no
                // sandbox flag here to turn off.
                //
                // THE MODEL IS PINNED BECAUSE COPILOT HAS NO PERSISTENT DEFAULT, and an unpinned
                // one is a silent capability change. Neither `~/.copilot/config.json` nor its
                // `settings.json` carries a model key - `/model` inside a session writes SESSION
                // state - so an unpinned Concierge comes up on whatever the CLI chooses that day.
                // A weaker model can read its role skill, which says in as many words *"tell
                // Manager ... do not go around it to the members it manages"*, then pick a MEMBER
                // anyway, announce the `tell`, and end its turn without issuing it. Nothing reaches
                // the log and the panel looks like it is thinking.
                //
                // THAT FAILURE IS INVISIBLE FROM INSIDE THE PRODUCT. There is no wrong answer to
                // read back - no instruction row, no workflow, no `blocked` - so it presents as a
                // hung terminal rather than as a model that could not follow its own skill.
                //
                // PINNED TO `auto`. A named model is refused where the account does not offer it
                // (Copilot prints "not available. Using "auto" instead"). `auto` lets Copilot choose
                // among the account's own models, which is not the unpinned day-to-day default the
                // failure above comes from. `auto` REFUSES `--reasoning-effort` - measured with
                // 1.0.88, a headless `-p` run answers only "Model "auto" does not support reasoning
                // effort configuration" - so neither preset passes that flag.
                new AgentLaunch(
                    "copilot",
                    //
                    // `--additional-mcp-config @{mcpConfig}` is this launch's MCP server: see
                    // copilot-headless below for why it is the flag and not a file in the home.
                    [
                        "--allow-all", "--model", "auto",
                        "--additional-mcp-config", "@{mcpConfig}",
                    ],
                    InstructionsFile: AgentsFile),
                Install: copilotInstall),

            new AgentDefinition(
                "copilot-headless",
                AgentMode.Headless,
                new AgentLaunch(
                    "copilot",
                    // --allow-all is REQUIRED for non-interactive use: without it `-p` still stops
                    // at the first tool call waiting for an approval nobody can give, which presents
                    // as a member that hangs rather than one that refuses.
                    //
                    // A NOTE ON A THEORY THAT WAS WRONG, so nobody rebuilds it. Copilot logs
                    // "bypass-permissions mode DISABLED by enterprise policy (fail-closed)" at every
                    // startup, and it is TEMPTING and INCORRECT to read that as the cause. Read the
                    // `applied:` lines IN ORDER: every run - including every failing one - ends at
                    // "no bypass restriction in force". The cap is applied and released during
                    // startup on a machine with no policy at all. `--yolo` and `--allow-all-tools`
                    // are also not a fix and not needed: `--yolo` and `--allow-all` carry identical
                    // help text, and --allow-all-tools is a subset of the same expansion.
                    //
                    // `-p {userPrompt}` IS LAST, AND THAT ORDER IS THE WHOLE FIX. Anything placed
                    // after it is LOST once the prompt is long, and a member's prompt - its role
                    // Prompt plus its context plus the instruction - is always long.
                    //
                    // MEASURED, twice, identical but for the order. Same CLI, same directory, same
                    // flags, one 7,560-character prompt:
                    //
                    //   -p <prompt> --allow-all --silent   ->  nothing ran, `Changes +0 -0`
                    //   --allow-all --silent -p <prompt>   ->  SUCCEEDED
                    //
                    // The member behaved EXACTLY like a run with no permission flag at all - reads
                    // and `cd`/`ls` allowed, every write and every unrecognised command refused with
                    // "Permission denied and could not request permission from user" - because that
                    // is what it was. `--allow-all` never reached the CLI.
                    //
                    // A LONGER-TERM FIX IS TO STOP PUTTING THE PROMPT ON THE COMMAND LINE AT ALL.
                    // Order is a real fix and a fragile one: it holds only while nothing is appended
                    // after `-p`, and the next person to add a flag will naturally add it at the end.
                    // `--add-dir .` is the member's WORKSPACE - a relative path resolves against the
                    // session working directory, and ProcessAgentRunner spawns the child there.
                    // Without it Copilot STAGES its file writes into
                    // `~/.copilot/session-state/<id>/files/` and reports them as succeeded, so a
                    // member appears to have written a file that is nowhere on the team's disk.
                    //
                    // It must come before `-p`: after it, it is discarded with everything else
                    // and looks ineffective.
                    [
                        "--allow-all",
                        "--add-dir", ".",
                        "--silent",
                        // PINNED FOR THE REASON THE INTERACTIVE PRESET ABOVE RECORDS, and placed
                        // HERE rather than at the end on purpose: anything after `-p` is what was
                        // dropped. `AgentPresetLaunchTests.The_copilot_headless_prompt_stays_
                        // the_last_argument` enforces that.
                        "--model", "auto",
                        "--usage-output-file", "{usageFile}",
                        // THE PLATFORM'S MCP SERVER, PER LAUNCH. `--additional-mcp-config` takes
                        // JSON or `@<file>` and adds to `~/.copilot/mcp-config.json` for this
                        // session only; that file is shared by every team (one agent home) and is
                        // not touched. The file is McpLaunchConfig's `mcp.json`, the same one Claude
                        // reads, and it holds no key: Copilot expands `${HARNESS_KEY}` in the header
                        // from the child's environment (measured with 1.0.88: a local listener
                        // receives the value). The directory is removed when the run
                        // ends, whether it succeeded or not.
                        "--additional-mcp-config", "@{mcpConfig}",
                        // The session's id, so its transcript is named before launch:
                        // `--session-id <uuid>` sets a NEW session's id (measured with 1.0.88: the
                        // folder under `session-state/` is the id passed). Before
                        // `-p`, for the reason above.
                        "--session-id", "{sessionId}",
                        "-p", "{userPrompt}",
                    ],
                    InstructionsFile: AgentsFile,
                    UsageFormat: "copilot-usage-file"),

                // Thirty minutes, a platform default: an invocation whose agent leaves a long-lived
                // child holding stdio never completes at all unless some clock rescues it. NULL
                // means unbounded, so an operator who wants that clears the field.
                //
                // NOT SHORTER, and the reason is what this clock actually measures: it is a
                // SILENCE watchdog, not a cap on how long a run may take. `RunHeartbeat.Touch`
                // resets it on every `container.progress`, so a member that narrates runs
                // indefinitely and a member that goes quiet dies - however much real work it is
                // doing. A member running the test suite produces no progress lines for minutes at
                // a time, and two suites competing for one machine stretch that further, so a
                // shorter clock kills members mid-verification while they are working correctly.
                // The failure presents as an agent fault rather than as contention, which is what
                // makes it expensive to diagnose.
                //
                // Thirty minutes is a silence budget rather than a work budget: it bounds how long
                // a run may say NOTHING before the platform stops paying for it. A member that
                // reports progress is never bounded by it at all.
                TimeoutSeconds: 1800,
                Tags: ["developer", "tester"],
                Install: copilotInstall,

                // Copilot writes its session's events here, in the folder named by the
                // `--session-id` above.
                LiveView: new AgentLiveView(
                    "~/.copilot/session-state/{sessionId}/events.jsonl", LiveView.CopilotEvents)),

            new AgentDefinition(
                "grok",
                AgentMode.Interactive,
                new AgentLaunch(
                    "grok",
                    [],
                    InstructionsFile: AgentsFile),
                Install: grokInstall),

            new AgentDefinition(
                "grok-headless",
                AgentMode.Headless,
                new AgentLaunch(
                    "grok",
                    // The current grok CLI takes the task with -p and prints one JSON object
                    // when asked. --always-approve lets a headless member use its tools
                    // without a person sitting at the terminal. --no-auto-update keeps a
                    // run from stopping to upgrade itself.
                    // `--session-id` names a NEW session (measured with 1.0.41: the folder
                    // under `~/.grok/sessions/<workspace>/` is the id passed), so its
                    // transcript is known before launch.
                    [
                        "--no-auto-update",
                        "--session-id", "{sessionId}",
                        "-p", "{userPrompt}",
                        "--output-format", "json",
                        "--always-approve",
                    ],
                    InstructionsFile: AgentsFile,
                    UsageFormat: "grok-json"),
                // Thirty minutes, a platform default: an invocation whose agent leaves a long-lived
                // child holding stdio never completes at all unless some clock rescues it. NULL
                // means unbounded, so an operator who wants that clears the field.
                //
                // NOT SHORTER, and the reason is what this clock actually measures: it is a
                // SILENCE watchdog, not a cap on how long a run may take. `RunHeartbeat.Touch`
                // resets it on every `container.progress`, so a member that narrates runs
                // indefinitely and a member that goes quiet dies - however much real work it is
                // doing. A member running the test suite produces no progress lines for minutes at
                // a time, and two suites competing for one machine stretch that further, so a
                // shorter clock kills members mid-verification while they are working correctly.
                // The failure presents as an agent fault rather than as contention, which is what
                // makes it expensive to diagnose.
                //
                // Thirty minutes is a silence budget rather than a work budget: it bounds how long
                // a run may say NOTHING before the platform stops paying for it. A member that
                // reports progress is never bounded by it at all.
                TimeoutSeconds: 1800,
                Tags: ["researcher"],
                Install: grokInstall,

                // `updates.jsonl` rather than `events.jsonl` (no tool input) or
                // `chat_history.jsonl` (no time): it carries a tool's input, its result and when.
                // The workspace folder is the working directory percent-encoded, `/` as `%2F`.
                LiveView: new AgentLiveView(
                    "~/.grok/sessions/{workspaceEncoded}/{sessionId}/updates.jsonl", LiveView.GrokUpdates)),

            new AgentDefinition(
                "echo",
                AgentMode.Headless,
                new AgentLaunch(
                    "cat",
                    [],

                    // NOT A LANGUAGE MODEL, and the flag is cleared here rather than left to the
                    // default because the default is the STRICT arm and right for anything unknown.
                    // This runs `cat`. It calls no model, authenticates nothing, and has no
                    // usage to report.
                    //
                    // WHAT THE DEFAULT COST WAS A REAL WARNING. `echo` is Headless with no
                    // `UsageFormat`, so every boot printed "No usage format for Headless presets
                    // echo" about a preset that has none because it is not a model - and the check
                    // that prints it warns in its own comment that "a report that always fires is
                    // one people stop reading, which costs the real entries their only reader".
                    // The seed was undermining the mechanism the seed ships.
                    LanguageModel: false),

                // HIDDEN. These two are test fixtures - the suites launch them, and an operator
                // choosing one gets a member that echoes its prompt back or a terminal with no
                // agent in it. Kept in the catalog rather than removed, because the suites reach
                // them by name; kept out of every screen, because nobody should be offered them.
                Hidden: true),

            new AgentDefinition(
                "shell",
                AgentMode.Interactive,
                // `bash` with no agent in it - see `echo` above for why the flag is
                // cleared explicitly. Interactive, so no startup line named it, but it was being
                // probed for a CLI call it has no reason to make.
                new AgentLaunch(
                    "bash", [], LanguageModel: false),
                Hidden: true),
        ];
    }

    /// <summary>
    /// The person's custom presets from agents.json, or none when the file is absent. Nothing is
    /// written when it is absent: built-ins are never stored.
    ///
    /// A file that carries built-in presets or a `prompts` list has both dropped - the build's
    /// built-ins win their names and prompts are chosen by role - and is rewritten with the custom
    /// presets alone, after a copy of the original is kept beside it.
    /// A file that cannot be read is left exactly as it is, and the host runs on the built-ins.
    /// </summary>
    public static IReadOnlyList<AgentDefinition> LoadCustom(string dataRoot, TextWriter log)
    {
        var path = PathIn(dataRoot);
        if (!File.Exists(path)) return [];

        string text;
        IReadOnlyList<AgentDefinition> read;
        bool hadPrompts;

        try
        {
            text = File.ReadAllText(path);

            using var document = JsonDocument.Parse(text);

            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                read = JsonSerializer.Deserialize<List<AgentDefinition>>(text, JsonOptions) ?? [];
                hadPrompts = true;
            }
            else
            {
                var contents = JsonSerializer.Deserialize<CatalogFileContents>(text, JsonOptions);
                read = contents?.Agents ?? [];
                hadPrompts = document.RootElement.TryGetProperty("prompts", out _);
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or NotSupportedException)
        {
            log.WriteLine(
                $"{path} could not be read ({exception.Message}). Running on the built-in presets; "
                + "the file has been left exactly as it is.");

            return [];
        }

        var unusable = read.Where(d => d.Launch is null || string.IsNullOrWhiteSpace(d.Launch.FileName)).ToList();
        var custom = read
            .Except(unusable)
            .Where(d => !IsBuiltIn(d.Name))
            .ToList();

        foreach (var offender in unusable)
        {
            log.WriteLine($"{path} holds '{offender.Name}' with no usable launch; it was skipped.");
        }

        if (hadPrompts || custom.Count != read.Count - unusable.Count)
        {
            var copy = $"{path}.before-builtins";
            if (!File.Exists(copy)) File.Copy(path, copy);

            Save(dataRoot, custom);

            log.WriteLine(
                $"{path} held built-in presets or prompts, which now come from the build. It was "
                + $"rewritten with the {custom.Count} custom preset{(custom.Count == 1 ? "" : "s")} it "
                + $"carried; the old file is at {Path.GetFileName(copy)}.");
        }

        return custom;
    }

    /// <summary>The object root on disk. `prompts` is read only to notice a file that still carries prompts.</summary>
    private sealed record CatalogFileContents(IReadOnlyList<AgentDefinition>? Agents);

    /// <summary>Writes the custom presets. A built-in handed in here is a bug, and is dropped.</summary>
    public static void Save(string dataRoot, IReadOnlyList<AgentDefinition> custom)
    {
        Directory.CreateDirectory(dataRoot);

        File.WriteAllText(
            PathIn(dataRoot),
            JsonSerializer.Serialize(
                new CatalogFileContents([.. custom.Where(d => !IsBuiltIn(d.Name))]), JsonOptions));
    }
}
