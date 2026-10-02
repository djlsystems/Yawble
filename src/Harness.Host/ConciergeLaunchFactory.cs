using Harness.Contracts;
using Harness.Pty;

namespace Harness.Host;

/// <summary>
/// Everything a console child needs to exist: where it runs, what it runs, and what it inherits.
/// </summary>
public sealed class ConciergeLaunchFactory(
    TeamPaths paths, string baseAddress, IPrincipalStore principals, AgentCatalog agents,
    SkillDirectory? skills = null, AgentLaunchUser? runAs = null, AgentUpdateGate? updates = null,
    IRunCredentials? credentials = null)
{
    /// <summary>
    /// What a console's agent may cause. It is a human's door into a team, so it reads and
    /// dispatches. It may create a team only when it acts as a user who could create one;
    /// authority is inherited, never held. It does not open or end consoles, which would let one
    /// console reach into another's session.
    /// </summary>
    public static IReadOnlySet<string> ConciergePermits { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Permits.Read, Permits.Tell, Permits.CreateContainer,
            Permits.CreateTeam,
            Permits.Skills,
            Permits.Sites,
            Permits.Outcomes,
        };

    /// <summary>
    /// The id of ONE PERSON'S tenant-wide Concierge credential.
    ///
    /// Both halves. Keyed on the team alone, a console credential would carry the team's authority
    /// rather than the driver's - which is why it also carries an owner. MintAsync upserts on this id, so re-opening rotates that person's
    /// key and leaves everyone else's on the same team untouched.
    /// </summary>
    public static string PrincipalId(string user) => $"concierge-{user}";

    /// <summary>
    /// The browser's address reduced to scheme, host, port and base path, or null when it is not an
    /// absolute http(s) address. Nothing after the base path survives: links are built on it.
    /// </summary>
    public static string? PublicUrl(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)
            || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/');
    }

    /// <summary>
    /// Under a tenant-level folder outside every team root. This
    /// is not team-owned state: one Concierge serves one person across teams.
    /// </summary>
    /// <param name="user">The user ID. STILL the identity, and the marker inside the workspace
    /// records it - the directory is only named after the login.</param>
    /// <param name="login">What the person signs in as, which is what the directory is
    /// NAMED after. Passed in rather than looked up, for the reason <c>teamLabel</c> is: this
    /// factory has no IUserStore, and the delegate in <c>Program.cs</c> that composes a launch
    /// already holds one. Nothing parses this back - see <see cref="ConciergeWorkspaceName"/>.
    /// </param>
    /// <returns>
    /// The directory AND whether it was already this person's before the call. Nothing in the
    /// launch reads the second half; it is kept because the resolver settles it in the same act
    /// that creates the directory.
    ///
    /// IT IS RETURNED FROM HERE RATHER THAN ASKED SEPARATELY. The directory is named after the
    /// LOGIN, so a probe composed from any other key - the user id, say - answers false
    /// forever and every launch reads as a first launch.
    ///
    /// A SECOND QUESTION ASKED AFTERWARDS COULD NOT BE MADE CORRECT, whatever it probed - resolution
    /// CREATES the directory, so anything asked from the line below is asking about our own
    /// footprint and says "relaunch" to a Concierge that has never been opened. The answer has to
    /// come from the act that settles it. See <see cref="ConciergeWorkspace.Existed"/>.
    /// </returns>
    public ConciergeWorkspace WorkspaceFor(string user, string login) =>
        ConciergeWorkspaces.Resolve(paths, user, login);

    /// <param name="team">The team's STORED IDENTIFIER - what HARNESS_TEAM carries, what the
    /// workspace and the principal id are keyed on, and what `{teamId}` resolves to. Callers
    /// already have this before they have anything else: it is half of the session key.</param>
    /// <param name="teamLabel">What a person calls this team - what `{team}` resolves to. Taken as
    /// a second argument rather than looked up, because this factory has no TeamRegistry of its
    /// own to look one up in; `Program.cs`'s socket route already resolves the team's stored
    /// spelling before building a session key, and it holds the registry that can answer
    /// `LabelFor` right there. One token meaning two different things depending on which path
    /// composed it - the identifier here, the label on a member's - is exactly the drift a shared
    /// token vocabulary exists to prevent.</param>
    public async Task<PtySpec> ForAsync(
        string team, string teamLabel, string user, string login, string agent,
        IReadOnlyDictionary<string, string> teamEnv,
        string? steeringCausation = null,
        string? publicUrl = null,
        CancellationToken ct = default) =>
        // THE ONE-PROCESS COMPOSITION of the two halves: what control resolves, made into a terminal
        // as a worker makes it. Production resolves here and hands the launch to a worker
        // (WorkerPtyEngine); this composition is kept so the launch's tests read one finished spec.
        ConciergeTerminal.Materialize(
            await ResolveAsync(team, teamLabel, user, login, agent, teamEnv, steeringCausation, publicUrl, ct), runAs).Spec;

    /// <summary>
    /// Everything about one person's Concierge launch that control decides: the refusal when agents
    /// cannot be run as their user, the CLI, the wait for its update, the minted credential, the
    /// environment in its order, the system prompt, and the workspace with its files. What is written
    /// to a machine's temp folder - the MCP config and the system-prompt file - and the wrapping that
    /// runs the CLI as the agent are the worker's (<see cref="ConciergeTerminal.Materialize"/>), on the
    /// worker that runs it.
    /// </summary>
    public async Task<TerminalLaunch> ResolveAsync(
        // NO DEFAULT for the Agent. A default would be a catalog entry a person may rename or
        // remove - and the one production caller reads the stored value and passes it, so a
        // default would only ever cover a caller that had not looked.
        // NO DEFAULT for the login either, and for the Agent's reason one line up: the caller that
        // has not looked is the one whose person gets a workspace named after a 32-hex id.
        string team, string teamLabel, string user, string login, string agent,
        IReadOnlyDictionary<string, string> teamEnv,
        string? steeringCausation = null,
        string? publicUrl = null,
        CancellationToken ct = default)
    {
        // FAIL CLOSED, before a credential is minted: see AgentLaunchUser.Refuses.
        if (runAs is { Refuses: true })
        {
            throw new InvalidOperationException(runAs.Refusal("The Concierge"));
        }

        var command = agents.Interactive(agent)
            ?? throw new InvalidOperationException(
                $"'{agent}' is not an Agent this tenant has, or has no interactive command.");

        // HELD WHILE THE PLATFORM UPDATES THIS CLI, the way a member's launch is, without holding
        // an update back in turn: a terminal can stay open for days.
        if (updates is not null) await updates.WaitUntilNotUpdatingAsync(command.FileName, ct);

        // Minted per call, and MintAsync upserts on the id - so re-opening a console ROTATES the
        // key rather than accumulating a row per attach, and the previous one stops working.
        //
        // Tenant-wide Concierge credentials inherit the driver's authority. Team access is
        // still enforced per request by TeamGate against the route's team value.
        var credential = await principals.MintAsync(
            PrincipalId(user), PrincipalKind.TenantConcierge, null, ConciergePermits,
            ownerUserId: user, ct: ct);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        // The shared list, not a second copy, so containers and the Concierge clear the same names.
        foreach (var variable in AgentEnvironment.Cleared) environment[variable] = string.Empty;

        // The same shared list, for the same reason. A Concierge is an Agent too: it reads the
        // operator's skills, and a skill that says "starting feature work - make a worktree and
        // implement" turns the front door into an implementer exactly as it would a Manager.
        // The person's own configuration reaches their own terminal, not the agent they open here.
        foreach (var (key, value) in AgentEnvironment.Isolated) environment[key] = value;

        // The definition's own variables, merged BEFORE the platform's - same position as
        // AgentEnvironment.ForContainerAsync, and no filter needed here where that one has one.
        // A console ALWAYS reaches the four assignments below (there is no early return on this
        // path), so ordering alone is the guarantee: HARNESS_* overwrite anything a catalog
        // entry set, never the other way round. The container path returns before its assignments
        // when a container has no permits, which is why it drops HARNESS_ keys at the merge
        // instead. The two must not drift apart, which is why each comment names the other.
        if (agents.Definition(agent)?.Env is { } configured)
        {
            foreach (var (key, value) in configured) environment[key] = value;
        }

        // The TEAM's own values, before the platform's assignments below. A Concierge is a
        // person's door into a team rather than a member of it, but "what does this team sign in to
        // its own test instance with" is a question about the TEAM, so both doors answer it the same
        // way - the identical argument that already gives this path HARNESS_SHARED.
        //
        // Passed in rather than looked up, for teamLabel's reason: this factory has no TeamRegistry,
        // and the caller that resolves the label already holds one.
        //
        // Unfiltered here where the container path filters, and for that path's stated reason: a
        // console ALWAYS reaches the four assignments below, so ordering alone is the guarantee.
        // TeamEnv.Validate has refused an HARNESS_ key at the write in any case.
        foreach (var (key, value) in teamEnv) environment[key] = value;

        // THE CLI'S OWN UPDATER OFF, after the preset's and the team's env so neither can turn it
        // back on: this terminal launches from the install every member shares.
        foreach (var (key, value) in command.UpdateEnvironment ?? new Dictionary<string, string>()) environment[key] = value;

        // AN ISSUED CREDENTIAL, beside update-off and for its reason. The Concierge KEEPS ITS HOME and
        // every tool: it is the person's own session, so only the variables are changed. The PTY
        // cannot remove a variable (it merges with the Host's environment), so each one the
        // preset's declaration displaces is set EMPTY instead - measured, an empty key reads as
        // absent - and its config-directory variables are left alone. Which credential the CLI then
        // uses, when the person's login is in the home as well, is the declaration's measured
        // loginPrecedence. One whose credential is not set opens on the person's login, as before.
        var issued = credentials is null ? RunCredential.Home : await credentials.ResolveAsync(agent, null, ct);
        if (issued is { Source: CredentialSource.Issued, Missing: null }
            && agents.Definition(agent)?.IssuedCredential is { } declaration)
        {
            foreach (var name in issued.Displace.Intersect(declaration.Displaces, StringComparer.Ordinal))
            {
                environment[name] = string.Empty;
            }

            foreach (var (key, value) in issued.Environment) environment[key] = value;
        }

        // NO HARNESS_TEAM, AND ITS ABSENCE IS THE MECHANISM RATHER THAN AN OMISSION.
        //
        // Written at spawn, it could never be revised - a process's environment cannot be changed
        // from outside it. The session outlives the team surface it was opened from (it is keyed on
        // the person, not the team), so a person who switched teams in the browser would find the
        // agent still addressing the previous one. So the Concierge names the team on every
        // team-scoped tool call, and `team_current` is the live answer to which team the person is
        // on. There is no HARNESS_SHARED for the identical reason: it is derived from the team.
        environment["HARNESS_URL"] = baseAddress;
        environment["HARNESS_KEY"] = credential;

        // THE ADDRESS THE PERSON USES, for the links the Concierge hands them - a deep link into the
        // install wizard, a site. HARNESS_URL is where the Host listens from inside this machine, and
        // a person on a VM address or a tunnel cannot open it. Taken from the browser request that
        // opened this session; the internal address when there was none. Members never get it: they
        // hand nothing to a person's browser, and the container path does not set it.
        environment["HARNESS_PUBLIC_URL"] = PublicUrl(publicUrl) ?? baseAddress;

        if (!string.IsNullOrWhiteSpace(steeringCausation))
        {
            environment["HARNESS_CAUSATION"] = steeringCausation;
        }

        // THE MCP CONFIG IS THE WORKER'S to write, in its own temp folder, so its paths are not known
        // here: a prompt token naming HARNESS_MCP_CONFIG is left as written.
        // Tenant-wide Concierge prompts do not resolve team-scoped tokens. Leaving unresolved
        // tokens verbatim is the intentional and visible failure mode.
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        // THE BUILT-IN CONCIERGE PROMPT, always. The prompt is chosen by role, never by a
        // person or by the CLI that runs it, and a Concierge has no team to add instructions for -
        // what follows it is the skills its role is offered.
        var template = BuiltInPrompts.Compose(
            SkillRoles.Concierge, null, null,
            (skills ?? new SkillDirectory()).For(SkillRoles.Concierge));
        var systemPrompt = PromptTokens.Resolve(template, values, environment);

        // THE ONE RESOLUTION, and the line that creates the directory. It answers both halves at
        // once - where the workspace is, and whether it was already there - because the second is
        // not knowable from either side of this line: not before, when the folder's NAME is not yet
        // settled and a relabelled login's workspace is under a name nothing here could compose; and
        // not after, when this call has just made it.
        var resolved = WorkspaceFor(user, login);
        var workspace = resolved.Path;

        // NO RESUME FLAG, FOR ANY AGENT. The Concierge's continuity is CONTEXT.md in its
        // workspace, which every agent can read. A vendor resume such as `claude --continue` fails
        // whenever the workspace exists but that vendor has no conversation for it - the first
        // Claude launch after a grok one, or after Claude's own session store moved - ending the
        // Concierge with "No conversation found to continue".
        List<string> argv = [command.FileName, .. command.Arguments];

        try
        {
            File.WriteAllText(
                Path.Combine(workspace, "STEERING.md"),
                SteeringFile.Note(steeringCausation));
        }
        catch (IOException)
        {
            // The terminal still opens. The person can say which workflow they mean.
        }

        // BEFORE the instructions file and outside its `if`, because this one is not conditional on
        // anything in the catalog. An agent with a `--system-prompt` argument and no
        // `InstructionsFile` - claude, the default - must still get a context file, or continuity
        // would be a property of which CLI a tenant happened to pick.
        EnsureContextFile(workspace);

        // codex, copilot and grok have no system-prompt argument, interactively either, and read a
        // conventional file in the working directory instead. Without this a Concierge on
        // any of the three opens a terminal that has never been told which team it is a door into.
        // Directory.CreateDirectory first: unlike the headless path, this workspace may not exist
        // yet, and PtySpec would create it moments later anyway.
        if (command.InstructionsFile is { Length: > 0 } instructions
            && !string.IsNullOrWhiteSpace(systemPrompt))
        {
            try
            {
                Directory.CreateDirectory(workspace);

                var instructionsPath = Path.Combine(workspace, instructions);

                // The line above creates the WORKSPACE, never this file's own PARENT - the same gap
                // as `ProcessAgentRunner`'s headless write. Fine for every seeded preset, which names
                // a flat "AGENTS.md" and so needs nothing beyond the workspace itself; a preset
                // naming a NESTED path (say "sub/agent.md") throws `DirectoryNotFoundException` on
                // the write below, which derives from `IOException` and would be swallowed by the
                // catch, silently - a person opens a terminal never told which team it is a door
                // into. `Directory.CreateDirectory` on a directory that already exists is a no-op,
                // so a flat name still lands directly in the workspace.
                //
                // Path containment is a SEPARATE gap, deliberately not fixed here: a preset naming
                // "../../elsewhere/agent.md" is not run through `TeamDocuments.Resolve`, so this
                // write is not checked against the team's own root.
                var instructionsDirectory = Path.GetDirectoryName(instructionsPath);
                if (!string.IsNullOrEmpty(instructionsDirectory))
                {
                    Directory.CreateDirectory(instructionsDirectory);
                }

                File.WriteAllText(instructionsPath, systemPrompt);
            }
            catch (IOException)
            {
                // A workspace that cannot be written is a terminal that will fail visibly when it
                // opens, which is more use than an exception out of a WebSocket upgrade.
            }
        }

        return new TerminalLaunch(
            argv,
            workspace,
            environment,
            steeringCausation is null
                ? ["HARNESS_TEAM", "HARNESS_SHARED", "HARNESS_MEMBER", "HARNESS_CAUSATION"]
                : ["HARNESS_TEAM", "HARNESS_SHARED", "HARNESS_MEMBER"],
            command.SystemPromptArguments is { Count: > 0 } ? systemPrompt : null,
            command.SystemPromptArguments is { Count: > 0 } systemArguments ? [.. systemArguments] : null,
            new TerminalMcp(baseAddress, "concierge-" + user));
    }

    /// <summary>
    /// Ends the credential, not the session. Called when the session it belonged to is ended - a
    /// key for a terminal that no longer exists is one nothing would ever notice was still live.
    /// </summary>
    public Task RevokeAsync(string user, CancellationToken ct = default) =>
        principals.RevokeAsync(PrincipalId(user), ct);

    /// <summary>
    /// The Concierge's continuity file, in the workspace root beside the instructions file.
    ///
    /// PINNED, and not a catalog property. `InstructionsFile` is per-preset because each CLI reads
    /// a different conventional name; this one is read by the `concierge` SKILL, which is one text
    /// shared by every preset, so a per-agent name would mean the skill could not say where to
    /// look. It is also the file a PERSON opens by hand, and a name that moves with the agent is a
    /// name they cannot learn.
    /// </summary>
    public const string ContextFileName = "CONTEXT.md";

    /// <summary>
    /// What survives a host restart, for an agent that has no resume flag at all.
    ///
    /// A restart re-enters the same working directory with an empty context window. Nothing in the
    /// interactive launch asks any agent to pick its own history up, and asking would be
    /// per-vendor plumbing: `claude --continue` is not `codex`'s and is nothing at all for a CLI
    /// that has no such flag. So the state lives in a PLAIN MARKDOWN FILE that every agent can
    /// read with the file tools it already has, and the platform's whole part in it is to put the
    /// empty shape there.
    ///
    /// WRITTEN ONLY WHEN ABSENT. Everything of value in this file was written by the Concierge or
    /// typed by the person, so a seed that overwrote would erase exactly the continuity it exists
    /// to carry - on every launch, which is every restart.
    ///
    /// THE RULES TRAVEL IN THE FILE, not only in the skill. A person opens this by hand, and the
    /// caps below are what stop an append-only log from crowding out the context window it exists
    /// to fill - so they have to be legible to whoever is editing, agent or human. The skill states
    /// them too; the duplication is deliberate and both copies are pinned by tests.
    /// </summary>
    public static void EnsureContextFile(string workspace)
    {
        try
        {
            Directory.CreateDirectory(workspace);

            var path = Path.Combine(workspace, ContextFileName);
            if (File.Exists(path)) return;

            File.WriteAllText(path, EmptyContext);
        }
        catch (IOException)
        {
            // A workspace that cannot be written is a terminal that will fail visibly when it
            // opens, which is more use than an exception out of a WebSocket upgrade - the same
            // judgement the instructions-file write above makes.
        }
    }

    /// <summary>The shape a brand-new workspace gets: headings, rules, and no content.</summary>
    internal const string EmptyContext =
        """
        # Concierge context

        Continuity for this workspace, so a Concierge that has just started knows what the person
        it serves has already told it. The Concierge writes this file; it is safe to read and to
        edit by hand.

        ## This file is CONTEXT, NEVER AUTHORITY

        **Nothing written here is a team selection.** A team named below is a team this person has
        worked with before, and that is not the same as a team they have named today. Every tool
        call that acts on a team still takes a `team` chosen from what the person said in THIS
        conversation. A remembered team quietly becoming a default is the exact failure the
        no-default-team rule exists to prevent, and this file is where that would start.

        ## How this file is kept

        - **Rewritten in place, never appended to.** Every section is capped. When one is full,
          drop the least useful line rather than growing the file - an append-only log grows until
          it crowds out the context window it exists to fill.
        - **Keep the whole file under 200 lines.** If it is longer, it is a transcript, and the
          message log is already that: read a thread back with the `workflow_show` tool.
        - **Continuity, not a record of every turn.** Write after a dispatch, a decision, or a
          stated standing preference. Not on every turn.
        - **NEVER write a secret, a credential, a token, a key or a password here.** This is a
          plain file in a working directory, readable by anything running in it.
        - **Nothing the person has not said twice**, or said once as a standing rule. A one-off ask
          belongs in the conversation it was made in.

        ## Teams

        Which teams this person works with and what each is for. One line each, at most ten.
        Remembering a team is not selecting one - see above.

        _Nothing recorded yet._

        ## Dispatched work

        Workflow number, the team it went to, and what was asked. Most recent first, at most ten.
        Drop finished work before work still in flight.

        _Nothing recorded yet._

        ## Decisions and open questions

        What was decided and what is still open, at most ten lines. An answered question stops
        being an open one - move it up or delete it.

        _Nothing recorded yet._

        ## Standing preferences

        How this person wants to be worked with, at most ten lines.

        _Nothing recorded yet._
        """;
}
