using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// A container's credential and the environment it is handed, in one place.
///
/// Those two are the same decision seen twice - what a container may cause, and what it is told -
/// so they live together. Split apart, a container ends up with a key it should not have, or a
/// permit it has no way to use, and neither shows up as a failure.
/// </summary>
public sealed class AgentEnvironment(
    IPrincipalStore principals,
    AgentCatalog agents,
    string baseAddress,
    TeamDocuments documents)
{
    /// <summary>
    /// Where this container's team keeps work that belongs to the TEAM rather than to one member.
    ///
    /// Named here rather than spelled as a literal in two files: `TeamRegistry.ComposePrompt` reads
    /// it back to resolve `{shared}`, and two spellings of one variable name drift apart.
    /// </summary>
    public const string SharedVariable = "HARNESS_SHARED";

    /// <summary>
    /// Cleared for every child, container and console alike. An inherited CLAUDE_* announces itself
    /// in the terminal; an inherited NO_COLOR does not, and presents as a rendering bug instead. A
    /// curated environment beats an unbounded denylist, and this is that curated list.
    ///
    /// It lives here rather than on ConciergeLaunchFactory because ProcessAgentRunner MERGES into
    /// the inherited environment - so a container would not be covered by a console-only copy, and
    /// two lists would drift the moment someone added a variable to one of them.
    ///
    /// Cleared means SET EMPTY, not removed, and that is load-bearing rather than incidental for
    /// the variables on this list: the CLI reads NO_COLOR as disabling colour only when it is
    /// non-empty, so an empty value is how a child is told "no inherited opinion" without being
    /// told "no colour".
    ///
    /// **ONE RULE DOES NOT FIT EVERY VARIABLE, AND FORCE_COLOR IS WHY IT IS NOT ON THIS LIST.** Node
    /// reads a SET FORCE_COLOR as "force colour on" whatever its value, and set beats NO_COLOR -
    /// *"The 'NO_COLOR' env is ignored due to the 'FORCE_COLOR' env being set"*. So clearing it
    /// would turn every node-based Agent's stdout into ANSI, inside the
    /// payload the ledger is built from, where escape codes are tokens that are re-sent on every
    /// later invocation and nothing strips them. Absent is the default and the only safe state for
    /// it, so it is simply not named here.
    ///
    /// **NOT NAMING IT IS AS FAR AS THIS CAN GO, AND THE REMAINING GAP IS REAL.** Both spawn paths
    /// MERGE into an inherited environment - `ProcessAgentRunner` into a pre-populated
    /// `ProcessStartInfo.Environment`, and Porta.Pty's own doc says omitting a variable inherits
    /// it - so an operator who has FORCE_COLOR set in their own shell still hands it to every
    /// child, and this file cannot express "unset" through a dictionary of strings. A `Removed`
    /// list here would be a NO-OP, because the dictionary it removed from starts EMPTY - and a
    /// control that looks like a boundary and is not is worse than an honest absence of one.
    /// Closing the gap means removing the name at the SPAWN site.
    ///
    /// **THE LIST FOR THAT IS <see cref="MustBeAbsent"/>.** Read it before
    /// adding anything here: a name that is dangerous when EMPTY belongs there and not on this
    /// list, and the two must never be tidied into one.
    /// </summary>
    public static IReadOnlyList<string> Cleared { get; } =
    [
        "NO_COLOR", "CI",
        "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_BRIDGE_SESSION_ID",
        "CLAUDE_CODE_MAX_OUTPUT_TOKENS", "CLAUDE_PID",
    ];

    /// <summary>
    /// Variables that must be ABSENT from a child's environment, not empty.
    /// <see cref="Cleared"/>'s siblings, and the distinction is the whole reason both lists exist.
    ///
    /// **EMPTY IS A VALUE, AND FOR THESE NAMES IT IS THE WRONG ONE.** Node reads a SET
    /// <c>FORCE_COLOR</c> as "force colour on" whatever its value - set beats <c>NO_COLOR</c> - so
    /// clearing it turns a node-based Agent's stdout into ANSI inside the payload the ledger is
    /// built from, where escape codes are tokens re-sent on every later invocation and nothing
    /// strips them. <see cref="Cleared"/> cannot express this: it is a dictionary of strings and no
    /// value means absent.
    ///
    /// **REMOVAL HAPPENS IN TWO PLACES AND THEY ARE NOT SYMMETRIC**, which is worth knowing before
    /// changing either:
    ///
    /// - <c>ProcessAgentRunner</c> populates a real <c>ProcessStartInfo.Environment</c> from the
    ///   parent, so <c>Remove</c> there does exactly what it says.
    /// - **THE PTY PATH CANNOT REMOVE ANYTHING.** Porta.Pty MERGES with the host environment and
    ///   <c>options.Environment</c> is an override dictionary - omitting a name inherits it, and
    ///   there is no value that deletes it. So the only thing that reaches a Concierge child is the
    ///   Host not having the variable in the first place, which is why <c>Program.cs</c> removes
    ///   these from its OWN process at startup.
    ///
    /// That startup removal is the mechanism; the spawn-site <c>Remove</c> is defence in depth for a
    /// Host started by some future path that skips it. Neither is decorative, and a fix on one path
    /// only would be the difference nobody could predict from reading either file.
    ///
    /// Safe for the Host itself because <c>FORCE_COLOR</c> is a NODE convention: nothing in this
    /// solution reads it, so removing it changes no .NET behaviour,
    /// including the Host's own console.
    /// </summary>
    public static IReadOnlyList<string> MustBeAbsent { get; } = ["FORCE_COLOR"];

    /// <summary>
    /// Every provider credential the Host may hold: each command's <c>credentialVariable</c> in
    /// <c>auth-probes.json</c>, plus <c>GITHUB_TOKEN</c>, which the container scripts accept in
    /// place of <c>GH_TOKEN</c>, and <c>GEMINI_API_KEY</c>, which no built-in CLI uses but an
    /// instance may still hold. Read once; the file ships with
    /// the Host.
    /// </summary>
    public static IReadOnlySet<string> ProviderVariables => Providers.Value.All;

    /// <summary>The provider variable <paramref name="command"/> authenticates with, or null.</summary>
    public static string? ProviderVariableFor(string command) =>
        Providers.Value.ByCommand.GetValueOrDefault(Path.GetFileName(command));

    /// <summary>
    /// Takes every provider key that is not <paramref name="command"/>'s own out of
    /// <paramref name="environment"/> - REMOVED, not set empty, because an empty key is still a
    /// variable a CLI may read and report on. A key the caller handed in explicitly
    /// (<paramref name="handedIn"/>: a catalog entry's own env, or <c>GH_TOKEN</c> for a team with
    /// a GitHub remote) is kept.
    ///
    /// Applied at the spawn site, because that is where the Host's whole environment is inherited:
    /// without this, every child would be handed every provider's key.
    /// </summary>
    public static void ScopeProviderKeys(
        IDictionary<string, string?> environment, string command, IReadOnlyDictionary<string, string> handedIn)
    {
        var own = ProviderVariableFor(command);

        foreach (var variable in ProviderVariables)
        {
            if (variable == own || handedIn.ContainsKey(variable)) continue;
            if (variable == GitHubAlias && (own == GitHubVariable || handedIn.ContainsKey(GitHubVariable))) continue;

            environment.Remove(variable);
        }
    }

    /// <summary>
    /// The GitHub token for a team with a GitHub remote, so git in the member's worktree can reach
    /// it through the container's credential helper. Null when the Host has none.
    /// </summary>
    public static string? GitHubTokenFor(IReadOnlyList<string> repos) =>
        repos.Any(IsGitHub)
            ? NonEmpty(Environment.GetEnvironmentVariable(GitHubVariable))
                ?? NonEmpty(Environment.GetEnvironmentVariable(GitHubAlias))
            : null;

    public const string GitHubVariable = "GH_TOKEN";
    private const string GitHubAlias = "GITHUB_TOKEN";

    /// <summary>A provider key with no command in the probe file. Still never inherited.</summary>
    private const string GeminiVariable = "GEMINI_API_KEY";

    private static bool IsGitHub(string url) =>
        url.Contains("github.com", StringComparison.OrdinalIgnoreCase);

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static readonly Lazy<(IReadOnlySet<string> All, IReadOnlyDictionary<string, string> ByCommand)> Providers =
        new(() =>
        {
            var byCommand = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (command, spec) in AgentAuthProbe.LoadSpecs())
            {
                if (spec.CredentialVariable is { Length: > 0 } variable) byCommand[command] = variable;
            }

            var all = new HashSet<string>(byCommand.Values, StringComparer.Ordinal) { GitHubAlias, GeminiVariable };
            return (all, byCommand);
        });

    /// <summary>
    /// The operator's OWN agent configuration, switched off for every child.
    ///
    /// The sibling of <see cref="Cleared"/>, one step further out, and the step that matters more.
    /// Cleared stops an inherited VARIABLE changing how a child behaves. This stops an inherited
    /// INSTRUCTION doing it - because an Agent CLI discovers skills, instruction files, hooks and
    /// MCP servers from the HOME directory it is launched under, and Harness launches it under
    /// the operator's. A member therefore inherits the personal agent setup of whoever started the
    /// Host, and those skills are written for somebody IMPLEMENTING.
    ///
    /// The failure this prevents: a Manager that inherits an implementation skill hires nobody,
    /// posts no progress, and writes the product code itself. The Harness manager prompt and the
    /// seeded `manager` skill both say "do not do the work yourself", but they lose a race to a
    /// skill that has already set the approach. **A prompt cannot win an argument with an
    /// instruction the Agent loaded before reading it**, which is why the switch is here and not in
    /// the prompt text.
    ///
    /// SET TO "false" RATHER THAN EMPTY, and that is the difference from Cleared: these are the
    /// vendors' own documented compatibility switches, which read a value. Clearing them would
    /// leave them unset, which is the default, which is ON.
    ///
    /// THIS IS HONESTLY A DENYLIST AND MUST BE READ AS ONE. It can only name the surfaces somebody
    /// has thought of, so a new Agent brand needs a line here or it inherits everything. Two
    /// limits are known and neither is closed:
    ///
    /// - **An Agent's OWN bundled skills are out of reach.** `grok inspect` still lists `implement`
    ///   and `execute-plan` after every switch below is set. They are hidden only by a
    ///   `[skills].disabled` entry in the vendor's config file, and the `GROK_CONFIG` overlay -
    ///   the documented way for a harness to inject settings - is fail-closed to an allowlist that
    ///   does not include it. So an Agent can still reach for a skill telling it to implement; what
    ///   it cannot reach is the OPERATOR's.
    /// - **Only grok is covered**, because grok is where the
    ///   switches are documented. `claude`, `copilot` and `codex` read their own homes and are not
    ///   isolated at all.
    ///
    /// HOME IS NOT RELOCATED. That is the one decision that would cover every
    /// vendor at once, and it breaks authentication: grok keeps its credential in `$GROK_HOME`, so
    /// a container pointed at its own home is a container that cannot log in - a failure that costs
    /// a whole run and reports as an agent fault. Isolating skills must not cost the ability to
    /// start.
    ///
    /// Merged in the same position as Cleared, BEFORE a definition's own env, so a hand-edited
    /// catalog entry can deliberately re-open this for an Agent that needs it - the same latitude
    /// the clearing gives, and for the same reason.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Isolated { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Grok reads the Claude and Cursor surfaces by default - `skills` is the obvious one,
            // and the other four are the same leak wearing different clothes: `agents` is the
            // operator's instruction files, `rules` their rule directories, `hooks` their
            // settings.json hooks, `mcps` their MCP servers. Switching off one of five would be a
            // fix that reads as complete and is not.
            ["GROK_CLAUDE_SKILLS_ENABLED"] = "false",
            ["GROK_CLAUDE_AGENTS_ENABLED"] = "false",
            ["GROK_CLAUDE_RULES_ENABLED"] = "false",
            ["GROK_CLAUDE_HOOKS_ENABLED"] = "false",
            ["GROK_CLAUDE_MCPS_ENABLED"] = "false",
            ["GROK_CURSOR_SKILLS_ENABLED"] = "false",
            ["GROK_CURSOR_AGENTS_ENABLED"] = "false",
            ["GROK_CURSOR_RULES_ENABLED"] = "false",
            ["GROK_CURSOR_HOOKS_ENABLED"] = "false",
            ["GROK_CURSOR_MCPS_ENABLED"] = "false",
        };

    /// <summary>
    /// Mints this container's credential and returns the environment its agent will run under.
    ///
    /// Called once per container, at creation. The credential is the container's own - no owner,
    /// scoped to its team - because a container is answerable to nobody: it is not a person acting
    /// and there is no person to resolve through.
    /// </summary>
    /// <param name="teamEnv">
    /// The team's own named values - its shared test credentials and anything else every member
    /// must agree on. REQUIRED and positional, never optional-with-a-default: a caller that could
    /// omit it would produce a container missing the very values its prompt names as
    /// <c>{env:NAME}</c>, and "a value a caller can omit is the same defect wearing a parameter
    /// list" is a rule that holds for floor sequences too. Pass an empty map to mean
    /// a team that has set none.
    /// </param>
    public async Task<IReadOnlyDictionary<string, string>> ForContainerAsync(
        ContainerId id,
        string agentName,
        IReadOnlySet<string> permits,
        IReadOnlyDictionary<string, string> teamEnv,
        IReadOnlyList<string> teamRepos,
        CancellationToken ct = default)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var variable in Cleared) environment[variable] = string.Empty;

        // ABOVE the no-permits return, deliberately, and pinned by its own spec. A permit-less
        // worker is told nothing about Harness, but it still runs a real Agent process that
        // still reads the operator's home - isolation is a leak being closed, not a capability
        // being handed out, so nothing about a permit should gate it.
        foreach (var (key, value) in Isolated) environment[key] = value;

        // The definition's own variables, merged BEFORE the platform's below - and BEFORE the
        // no-permits return that follows, deliberately. A definition's env is an outside Agent's
        // OWN credential (GROK_API_KEY and friends), not Harness's; a permit-less worker still
        // runs a real process that may need it just to function, so the permits gate below decides
        // whether this container gets an Harness platform credential, not whether its Agent
        // works at all.
        //
        // HARNESS_ keys are FILTERED OUT HERE rather than merely overwritten below, and that is
        // the difference between this merge site and ConciergeLaunchFactory's. A console
        // always reaches the platform's assignments, so there ordering alone is the guarantee.
        // HERE the no-permits return sits between this merge and those assignments, so on the
        // worker path - every worker, and the default - there is nothing later to win: without this
        // filter a hand-edited agents.json (an explicitly supported repair, since the catalog is a
        // file) could hand a permit-less container HARNESS_URL and HARNESS_KEY and give it a
        // working CLI authenticating as somebody else.
        //
        // Case-insensitive, mirroring AgentEndpoints' write-side refusal - which is the FIRST line
        // of defence and not the only one, because the file can be edited without going near it.
        if (agents.Definition(agentName)?.Env is { } configured)
        {
            foreach (var (key, value) in configured)
            {
                if (key.StartsWith("HARNESS_", StringComparison.OrdinalIgnoreCase)) continue;

                environment[key] = value;
            }
        }

        // GH_TOKEN for a team with a GitHub remote, whatever the Agent: its worktree's git reaches
        // the remote through the container's credential helper, which reads this variable. Before
        // the no-permits return because it is git's credential, not the platform's. Every other
        // provider key is taken out at the spawn site - see ScopeProviderKeys.
        if (GitHubTokenFor(teamRepos) is { } gitHubToken) environment[GitHubVariable] = gitHubToken;

        // No permits, no credential, and nothing naming the platform. Returning BEFORE the mint
        // rather than after it matters: a row with an empty permit set is still a row, and a row
        // is something a later change can widen without anyone deciding to.
        if (permits.Count == 0) return environment;

        // THE TEAM's own values, after the no-permits return and before the platform's assignments
        // below. Both halves of that placement are chosen.
        //
        // AFTER the return, for the reason HARNESS_SHARED is: a container whose permits somebody
        // deliberately stripped is one they decided should reach nothing, and handing it the team's
        // test credentials anyway would make that stripping cosmetic.
        //
        // BEFORE the assignments, so no entry can shadow HARNESS_KEY even if the filter below
        // were removed. The filter is the first line of defence and the ordering is the second, and
        // neither is relied on alone - exactly as the definition's env merge above is both filtered
        // and ordered.
        //
        // Filtered case-insensitively here as well as refused at the write in TeamEnv.Validate,
        // because a row can be edited by hand and repairing a database by hand is supported.
        foreach (var (key, value) in teamEnv)
        {
            if (key.StartsWith(TeamEnv.ReservedPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            environment[key] = value;
        }

        var credential = await principals.MintAsync(
            id.ToString(), PrincipalKind.Container, id.Team, permits, ct: ct);

        environment["HARNESS_URL"] = baseAddress;
        environment["HARNESS_TEAM"] = id.Team;
        // What this container is CALLED. The MCP tools build its
        // progress route from this and HARNESS_TEAM, and the route carries {team} so that
        // TeamGate covers it structurally rather than by anyone remembering. The server still
        // asserts the caller IS the container it names - this is convenience, never authority.
        environment["HARNESS_MEMBER"] = id.Name;
        environment["HARNESS_KEY"] = credential;

        // Where the TEAM's work goes, as opposed to this container's own scratch. The folder is
        // guarded by TeamDocuments.Resolve and listed by the Documents screen, and without this
        // nothing in a container's environment says where it is - a manager and its member would
        // each write "the workspace root" and mean different folders.
        //
        // AFTER the no-permits return rather than before it, and that placement is chosen against
        // the opposite argument. A permit-less container is told NOTHING about the platform - two
        // specs in AgentEnvironmentTests pin that as an absolute, under any spelling - and a
        // variable it can read is knowledge even when it confers no capability. The case this gives
        // up is a member somebody explicitly stripped, which is dead ground for members since every
        // one of them is created holding Progress; and it gives it up LOUDLY rather than silently,
        // because `{shared}` is resolved from this dictionary and stays verbatim in the prompt when
        // it is absent.
        //
        // AFTER the definition's env merge either way, which the merge's own HARNESS_ filter
        // already guarantees: a hand-edited agents.json must not be able to point one team's shared
        // folder at another team's.
        //
        // The agents learn the documents folder from this variable alone, rather than through
        // skill wording. `TeamDocuments` composes it and
        // this class takes `TeamDocuments` rather than a path, so where the folder lives (under the
        // tenant documents root) is decided in one place with NO PROMPT AND NO SKILL naming it.
        // RestoreAsync rebuilds this dictionary on every start.
        //
        // EnsureFor rather than RootFor: the folder is created when it is missing, so this also
        // repairs a team whose folder was removed by hand. RootFor never creates, because a folder
        // is reachable by the name of a team that no longer exists - see TeamDocuments.
        environment[SharedVariable] = documents.EnsureFor(id.Team);

        return environment;
    }

    /// <summary>
    /// Unconditional, and that is the point: a worker has no row, and deleting a row that is not
    /// there is not an error. A caller obliged to know which kind it was removing is a caller that
    /// eventually gets it wrong and leaves a live credential behind.
    /// </summary>
    public Task RevokeAsync(ContainerId id, CancellationToken ct = default) =>
        principals.RevokeAsync(id.ToString(), ct);
}
