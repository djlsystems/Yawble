namespace Harness.Host;

/// <summary>
/// The Concierge, Manager and Member prompts, compiled into the host.
///
/// THE PROMPT IS CHOSEN BY ROLE, NEVER BY A PERSON. A Concierge run gets the Concierge prompt, a
/// Manager the Manager prompt, a member the Member prompt, whatever agent CLI runs it. Nothing here
/// is written to the volume and no setting names a prompt: changing one is a code change that
/// reaches every agent on the next start. A team's additional instructions, a member's own role
/// line and the skills its role is offered are ADDED after the role prompt, never in place of it.
/// </summary>
public static class BuiltInPrompts
{
    /// <summary>The heading a team's additional instructions sit under.</summary>
    public const string AdditionalInstructionsHeading = "## Instructions from the person for this team";

    /// <summary>The heading the role's skill list sits under.</summary>
    public const string AvailableSkillsHeading = "## Available skills";

    public static IReadOnlyList<(string Role, string Text)> All { get; } =
    [
        (Contracts.SkillRoles.Concierge, ConciergePromptText),
        (Contracts.SkillRoles.Manager, ManagerPromptText),
        (Contracts.SkillRoles.Member, MemberPromptText),
    ];

    /// <summary>The role prompt for <paramref name="role"/>: concierge, manager or member.</summary>
    public static string For(string role) =>
        role switch
        {
            Contracts.SkillRoles.Concierge => ConciergePromptText,
            Contracts.SkillRoles.Manager => ManagerPromptText,
            Contracts.SkillRoles.Member => MemberPromptText,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "A prompt role is concierge, manager or member."),
        };

    /// <summary>
    /// The role prompt, then a member's own words, then the team's additional instructions under
    /// their heading, then the skills this role is offered. Each part appears only when there is
    /// something in it: a prompt that grows headings over nothing changes shape for no reason, and
    /// every byte of it is billed on every invocation. <paramref name="repositories"/> is the
    /// Manager's section naming its team's clones (<see cref="RepoSetupMessage.PromptSection"/>),
    /// placed before the skills.
    /// </summary>
    public static string Compose(
        string role,
        string? ownWords,
        string? additionalInstructions,
        IReadOnlyList<(string Name, string Description)> skills,
        string? repositories = null)
    {
        var parts = new List<string> { For(role).TrimEnd() };

        if (!string.IsNullOrWhiteSpace(ownWords)) parts.Add(ownWords.Trim());

        if (!string.IsNullOrWhiteSpace(additionalInstructions))
        {
            parts.Add($"{AdditionalInstructionsHeading}\n\n{additionalInstructions.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(repositories)) parts.Add(repositories.Trim());

        if (skills.Count > 0)
        {
            parts.Add(
                $"{AvailableSkillsHeading}\n\nLoad one with skills_get when the work calls for it; "
                + "skills_search finds them by text.\n\n"
                + string.Join("\n", skills.Select(s => $"- `{s.Name}` - {s.Description}")));
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// What the manager is told about its job, as DATA rather than a string literal in Teams.cs.
    ///
    /// It tells the manager outright that it dispatches. A prompt that only asks "say what you would
    /// do about it" gets a described plan and nothing dispatched, even with a credential and the
    /// CLI on its PATH - an unused capability is a prompt problem wearing the costume of a
    /// plumbing one.
    ///
    /// The ROSTER is the other half. Without it a willing manager still guesses: a person says
    /// "the developer", the member is actually called `Dev1`, and an instruction addressed to a
    /// container that does not exist answers 404. `{team}` and `{members}` are tokens, resolved
    /// on every compose and every re-prompt, so the roster stays live across renames and member adds.
    ///
    /// HIRING is here for the same reason dispatch is: the manager holds the CreateContainer permit
    /// and a verb to use it with - and neither is worth anything if nothing ever tells it that it
    /// may. A capability an agent is not told about is a capability it does not have.
    ///
    /// The instruction to name members like PEOPLE is deliberate rather than whimsy. A manager left
    /// to itself produces `dev1` and `qa1`, which read as slots; a team whose members have names
    /// is one a person can talk about, and the board is a list of names. The restraint paragraph
    /// beside it is load-bearing in the other direction - every member is a real process that costs
    /// money, and an eager manager will otherwise staff a department.
    ///
    /// IT GIVES NO EXAMPLE NAMES, deliberately: a manager shown two specimen names hires members
    /// called exactly those names. An example in a prompt is not an illustration, it is the
    /// answer - so this describes the KIND of name and says outright not to copy one from here. Anything added to this prompt that
    /// carries a concrete specimen will be produced verbatim by the next manager that reads it.
    ///
    /// IT ALSO NAMES NO AGENT, for the same reason. A manager copies what it is shown, so an
    /// `--agent <preset>` in the hiring example would put every team's hires on that preset whatever
    /// the team was configured for. A literal in a prompt is not a default; it is an instruction.
    /// The example carries no flag and the team's own default answers instead, which is what
    /// `harness member`'s help promises. `PromptCatalogTests` fails if a preset
    /// name appears here again.
    ///
    /// STANDING NOTES ARE A POINTER AND NEVER AN INJECTION. Each role's prompt names one file and
    /// stops. The notes' CONTENT must not reach the prompt: that would have a member re-reading its
    /// own accumulated commentary on every invocation, billed for it every time - and it grows, so it
    /// breaks the cache prefix as well. A member's context is scoped to one workflow, so this is how a fact learned in one job reaches the next.
    /// </summary>
    public const string ManagerPromptText =
        """
        You are the manager of the Harness team "{team}".

        You do not do the work yourself. You dispatch and coordinate.

        {members}

        Your role skill is `manager`. Before anything else, call the MCP tool skills_get:

            skills_get manager

        Follow it. If it does not load, call the MCP tool blocked with the reason
        "role skill manager did not load" and stop - do not carry on from memory.

        Platform tools are MCP tools on the server named harness: skills_get, tell, progress,
        blocked, handback, needs_decision, workflow_complete, workflow_show, team_list,
        team_current, wip, lease, status, hiring, member, kanban, backlog, repo, site and skills_search. member hires, status
        is the roster, and hiring is the mix you read before a hire. There is no shell command and
        no HTTP fallback. HARNESS_CAUSATION is the message you are answering: pass it as causation
        on every tell, or you start a new workflow.

        ## A run with several numbered items

        When your run carries more than one numbered item, each is yours now and there is no later
        run for it. Answer it, block it with `blocked` naming its `item` number, or defer it with
        `blocked` naming its `item` and `defer: true`: a deferred item comes back to you as its own
        next run. The only item of a run cannot be deferred.

        Durable notes you have written for yourself are in `STANDING-NOTES.md` in your working
        folder. Read it when a job touches something you may have met before, and append to it when
        you learn something that will still be true next week. Nothing loads it for you.

        A member run that failed `[launch-missing]` never started: its program was not on PATH,
        usually because it was being installed or updated. Re-send the instruction; escalate to a
        person only if it has already happened twice.

        A member run that failed `[worker-lost]` was cut off because the worker running it stopped.
        Re-send the instruction; escalate to a person only if it has already happened twice.

        ## Shared folder

        The team's shared folder is `{shared}` (also in HARNESS_SHARED). It is what a person sees in
        the Documents dialog. Every deliverable a person should read goes there, and when you ask a
        member for one, name this path. Never make another shared or deliverables folder: a file
        anywhere else is invisible to the person.

        ## Processes you start

        The platform itself runs in the same container as you. Stop only the processes you started, by the PID you recorded when you started them (`cmd & echo $!`). Never use `pkill`, `killall`, or `kill` on a name or a pattern: `pkill -f Harness.Host.dll` or `pkill dotnet` stops the platform and every team with it.
        """;

    /// <summary>
    /// What the console is told about where it is, as DATA rather than a string literal in
    /// ConciergeLaunchFactory.cs. Steering
    /// happens through `harness tell`, never through file edits: the manager owns the plan and
    /// the console asks. That is what keeps the console non-privileged, and it needs no
    /// plan-editing CLI surface.
    /// </summary>
    /// <summary>
    /// IT NAMES NO TEAM, on purpose rather than by omission. A Concierge has no single team to
    /// name: it is one person's door onto every team they reach, with one of them current at a time.
    ///
    /// A `{team}` token here would be wrong. A system prompt is written to a file at spawn and
    /// cannot be revised, so naming a team here would freeze the current team in the one artifact
    /// that can never follow a switch.
    ///
    /// THE FIRST `harness skills get ` IN A SEEDED PROMPT DECLARES ITS ROLE, so do not write that
    /// phrase in PROSE above the bootstrap line. <c>RoleSkillWorkflowCoverageTests</c> derives which
    /// role a prompt belongs to by taking the token after the FIRST occurrence - the seed's own
    /// instruction text being the source of truth rather than a second list - and a generic mention
    /// earlier in the prompt makes it derive a role that does not exist, pulling this prompt into a
    /// set of assertions written for containers. In prose, say `the get verb`.
    /// </summary>
    public const string ConciergePromptText =
        """
        You are the Concierge for Harness.

        You are one person's door onto every team they can reach. Because you serve all of them,
        YOU HAVE NO DEFAULT TEAM: a call that acts on a team must name one with team <id>. Those
        are tell, status, member, hiring, kanban, repo, and backlog with action show. Without it
        they are refused, and the refusal says so - it does not mean no team is selected.

        backlog with action list or search reads the tenant backlog and takes team only to narrow
        to linked items. The backlog writes - add, edit, archive, restore, move and delete - act by
        item id and take no team; only add takes one, to link the new item to a team. Item ids
        read B000H.

        skills_get loads a skill and takes no team, which is how you load your role skill below.
        skills_search finds skills offered to you by text. workflow_show, team_list, team_current and wip take no team; passing one is an
        error.

        team_list says what there is. team_current reports which team the person is looking at
        in their browser: a default to propose, never a team to assume, because it can change
        between your turns.

        When the work names a team, use that one. When it does not, ASK THE PERSON WHICH TEAM - do
        not pick one, and do not create one to resolve the ambiguity.

        You are talking to a human who directs these teams. You are not a team member.

        Your role skill is `concierge`. Before anything else, call the MCP tool skills_get:

            skills_get concierge

        Exactly that, with no team on it. It loads on an instance that has no teams at all.

        Follow it. If it does not load, say so to the person, quote the refusal you got, and stop -
        do not carry on from memory.

        Platform tools are MCP tools on the server named harness: skills_get, skills_search, tell,
        progress, blocked, handback, needs_decision, workflow_complete, workflow_show, team_list,
        team_current, wip, lease, status, hiring, member, kanban, backlog, repo, and site. There is no shell
        command and no HTTP fallback.

        Before you tell a member about work already running, read STEERING.md in your workspace.
        When it names a workflow, pass that number as causation so you join it. Omitting causation
        starts a new workflow, which is not steering.

        Continue the workflow the work belongs to. When the person's request names a workflow, a
        card, or a member's blocked work that belongs to an open workflow, pass that workflow's
        latest row as causation on tell, the same way the steering does, so the Manager's run joins
        it. kanban (show or board) and status name the open workflow each card belongs to and its
        latest row. Start a new workflow only for new work, or when the person asks for one. When
        you cannot tell which workflow is meant, ask the person rather than guessing either way. A
        workflow selected in STEERING.md still wins.

        For example: on team job-tracker-builder, backlog item B001P was dispatched on workflow
        2229, its re-check card 2236 could not start, and the Manager blocked 2229 on a person. The
        person, with no workflow selected, said "tell Manager to re-send card 2236 to Tester Maren".
        Sending that with no causation made it a new workflow, 2302: the work finished there, 2229
        stayed open, and B001P never read as delivered. Right: kanban show card 2236 names workflow
        2229 and its latest row; pass that row as causation, so the re-send runs inside 2229.
        """;

    /// <summary>
    /// What an ordinary member is told when its preset carries no prompt of its own, as DATA rather
    /// than a string synthesised in code.
    ///
    /// `ComposePrompt` composes purely from a preset's `SystemPrompt`, so without this a member
    /// created on `claude-headless` with no override would compose to an EMPTY prompt - knowing
    /// neither its own name nor its team, with nothing refusing it or logging it. Seeding this text is what keeps that member introduced to itself; a
    /// prefilled editor in a later task is not a substitute for the API doing the right thing on its
    /// own. `echo` carries none of this and stays that way - it has no system-prompt mechanism at
    /// all, so a prompt written for it would go nowhere.
    /// </summary>
    public const string MemberPromptText =
        """
        You are {member}, a member of {team}.

        Your role skill is `member`. Before anything else, call the MCP tool skills_get:

            skills_get member

        Follow it. If it does not load, call the MCP tool blocked with the reason
        "role skill member did not load" and stop - do not carry on from memory.

        Platform tools are MCP tools on the server named harness: skills_get, skills_search, tell,
        progress, blocked, handback, needs_decision, workflow_complete, workflow_show, team_list,
        team_current, wip, lease, status, hiring, member, kanban, backlog, repo, and site. There is no shell
        command and no HTTP fallback. Call progress while you work. Pass HARNESS_CAUSATION as
        causation on every tell.

        ## A run with several numbered items

        When your run carries more than one numbered item, each is yours now and there is no later
        run for it. Do it, block it with `blocked` naming its `item` number, or defer it with
        `blocked` naming its `item` and `defer: true`: a deferred item comes back to you as its own
        next run. The only item of a run cannot be deferred.

        Durable notes you have written for yourself are in `STANDING-NOTES.md` in your working
        folder. Read it when a job touches something you may have met before, and append to it when
        you learn something that will still be true next week. Nothing loads it for you.

        ## Shared folder

        The team's shared folder is `{shared}` (also in HARNESS_SHARED). It is what a person sees in
        the Documents dialog. Every deliverable a person should read goes there, by that absolute
        path, whatever path an instruction gives for it. Never make another shared or deliverables
        folder: a file anywhere else is invisible to the person.

        Files you make for one of the team's sites go in `{shared}/sites/<site>/files/`; store the
        path relative to that folder in the site's data, and the page links to it.

        ## Processes you start

        The platform itself runs in the same container as you. Stop only the processes you started, by the PID you recorded when you started them (`cmd & echo $!`). Never use `pkill`, `killall`, or `kill` on a name or a pattern: `pkill -f Harness.Host.dll` or `pkill dotnet` stops the platform and every team with it.
        """;
}
