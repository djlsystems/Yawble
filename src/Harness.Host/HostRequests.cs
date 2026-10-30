using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using ModelContextProtocol.AspNetCore;
using Harness.Containers;
using Harness.Backlog;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Host.Capacity;
using Harness.Host.Solutions;
using Harness.Identity;
using Harness.Kanban;
using Harness.Messaging;
using Harness.Skills;

namespace Harness.Host;

internal readonly record struct SchedulePatch(
    bool HasName, string? Name,
    bool HasContainer, string? Container,
    bool HasInstruction, string? Instruction,
    bool HasKind, string? Kind,
    bool HasExpression, string? Expression,
    bool HasTimezone, string? Timezone,
    bool HasIntervalSeconds, int? IntervalSeconds,
    bool HasFireAt, DateTimeOffset? FireAt,
    bool HasIdleOnly, bool? IdleOnly,
    bool HasEnabled, bool? Enabled,
    bool HasNextDueAt, DateTimeOffset? NextDueAt,
    bool HasEventType, string? EventType,
    bool HasFilter, string? Filter,
    bool HasWatchRoot = false, string? WatchRoot = null,
    bool HasWatchPath = false, string? WatchPath = null,
    bool HasWatchGlob = false, string? WatchGlob = null,
    bool HasPollSeconds = false, int? PollSeconds = null,
    bool HasQuietSeconds = false, int? QuietSeconds = null,
    bool HasMinIntervalSeconds = false, int? MinIntervalSeconds = null,
    bool HasWakeManager = false, string? WakeManager = null,
    bool HasDailyTokenCap = false, long? DailyTokenCap = null,
    bool HasOutcomeId = false, string? OutcomeId = null);

/// <summary>
/// One store of the Concierge or non-Manager Container refusal, so duplicating the sentence is
/// impossible.
/// </summary>
/// <summary>
/// Who may `tell`: a person; any machine principal holding Tell; and a member (a container, which
/// <c>TeamGate</c> binds to its own team) telling a plugin member, Tell or not. A principal that
/// cannot be read is refused.
/// </summary>
internal static class TellPermit
{
    public static bool Allows(Principal? caller, bool targetIsPlugin) => caller switch
    {
        null => false,
        { Kind: PrincipalKind.User } => true,
        _ when caller.May(Permits.Tell) => true,
        { Kind: PrincipalKind.Container } => targetIsPlugin,
        _ => false,
    };
}

internal static class TellRefusals
{
    /// <summary>
    /// Refused when a Concierge or Container tells a non-Manager member on a team that has a Manager.
    /// Names the Manager so the caller knows what to do instead.
    ///
    /// COMPOSED FROM <see cref="TeamRegistry.DefaultManagerName"/> RATHER THAN SPELLING IT. The
    /// check three lines away reads that constant; a literal here is a second store of the same
    /// fact, and the two are adjacent enough that a rename would move one and leave the message
    /// naming a container the reader cannot find. `static readonly` rather than `const` for the
    /// same reason - a const cannot be composed, and that is not a good enough argument for
    /// hard-coding a name.
    /// </summary>
    public static readonly string SkipsManager =
        $"This team is coordinated by '{TeamRegistry.DefaultManagerName}'. "
        + "Send the work there and let it dispatch its own members.";
}

/// <summary>
/// Parameter prose that repeats across routes, written once.
///
/// A constant rather than a copy per route because the identifier-versus-label distinction is the
/// single easiest thing to get backwards in this API, and a reader who meets two slightly different
/// explanations of `{team}` reasonably concludes the two routes want different things.
/// </summary>
internal static class Describe
{
    /// <summary>What rename, move and copy share: the batch, the row first, the partial answer.</summary>
    public const string DocumentsChange =
        "A batch of at most 1000. 200 with `results`: one per item, each `{ from, to, outcome }` "
        + "with `outcome` `done`, `skipped` or `failed` and a `reason` when it was not done; `to` is "
        + "the final path in the destination folder.\n\n"
        + "Appends a `documents.renamed`, `documents.moved` or `documents.copied` tenant event naming "
        + "every source, destination and replaced path BEFORE acting, and does nothing (500) when "
        + "that row cannot be written. A batch that partly fails answers 409 with a sentence in "
        + "`error` saying path by path what was not done and why (permission denied, in use, on "
        + "another drive), the full `results`, and a `-incomplete` tenant event after the first. "
        + "What changed in a live team's folder is announced to its folder triggers, once per folder.";

    /// <summary>What move and copy share: where to, and a name already taken there.</summary>
    public const string DocumentsTransfer =
        "A name already taken where an item goes is a CLASH. With no `onClash` on that item the "
        + "request answers 409 with `clashes` (`{ from, to, isFolder }` each) and nothing is "
        + "recorded or done; send it again choosing per item. `keep-both` takes the first free "
        + "`name (copy).ext`, `name (copy 2).ext`; `replace` removes what is there first, through "
        + "the same removal a delete uses; `skip` leaves both. A folder holding a subfolder the Host "
        + "cannot list is not copied, or moved to another team, unchecked: that item fails with "
        + "permission denied.\n\n";

    public const string Team =
        "The team's IDENTIFIER - the `name` field from `GET /api/overview`, never the `label` a "
        + "person reads. Matched case-insensitively. Addressing a team by its label reaches a team "
        + "that does not exist, and the failure is quiet.";

    /// <summary>
    /// Appended to every documents route rather than set once on the group, and that is not a
    /// stylistic choice: a route's own <c>WithDescription</c> REPLACES the group's, so the three
    /// routes here that have something of their own to say would have silently dropped this - the
    /// half of the explanation covering where a path may point and what happens when it does not.
    /// </summary>
    public const string Documents =
        "\n\nPaths are relative to the team's documents folder at `<dataRoot>/documents/<team>`, "
        + "claimed with the team and NOT removed with it. Every path arriving from outside is "
        + "RESOLVED and then compared against that folder rather than searched for `..`, so one "
        + "that climbs out is refused with 400 whatever spelling it arrives in.\n\n"
        + "**A team's documents outlive the team.** Deleting a team keeps them - its result says "
        + "where - so the READ routes here (`GET` and `GET /content`) also serve a folder whose "
        + "team no longer exists, named by the folder on disk. `GET /api/documents` lists those "
        + "folders. The WRITE routes answer 404 for a team that is gone: a dead team's documents "
        + "are a record, and a record is not edited.\n\n"
        + "Who may reach a folder is decided by the team in the path exactly as it is everywhere "
        + "else, so a document a team wrote stays readable to that team - and a folder whose team "
        + "no longer exists is absent from everybody's set, which leaves it reachable by a tenant "
        + "admin alone. 404 for a missing file.";
}

/// <summary>
/// What a team may spend on ONE workflow, in tokens, IN and OUT together.
///
/// THREE STATES: null and 0 are different answers. Null means the team has CHOSEN NOTHING and inherits the instance `WorkflowSpendLimit` - which must still
/// apply to such a team - leaving 0 as the only spelling of an explicit "unlimited". A negative
/// value is refused with 400 at the route.
/// </summary>
internal sealed record TeamBudget(
    [property: Description(
        "Tokens this team may spend on ONE workflow, input and output together. **Null means the "
        + "team has chosen nothing** and the instance figure applies; **0 means unlimited**; a "
        + "positive value is stored exactly as typed, including above the instance figure.")]
    long? BudgetTokens);

/// <summary>The object form of <c>PUT /api/teams/{team}/repos</c>: the list and the choices for URLs a
/// check refused.</summary>
internal sealed record SetTeamRepos(
    IReadOnlyList<string>? Repos,
    IReadOnlyDictionary<string, string>? RepoChoices);

internal sealed record CreateTeam(
    [property: Description(
        "The team's name as a PERSON would write it - free text, spaces and accents included. The "
        + "identifier every other route addresses is derived from this and is never asked for.")]
    string? Name,
    [property: Description(
        "The agent this team's Manager runs. REQUIRED - there is no default: nothing in code "
        + "names an Agent, because a name in code is a reference no rename can move, and a "
        + "create that does not name all three Agents is refused.")]
    string? Agent,
    [property: Description(
        "Optional instructions from the person for this team. Appended after the built-in role "
        + "prompt for the team's Manager and members, under the heading \"Instructions from the "
        + "person for this team\"; they never replace it. Omitted or blank means nothing is "
        + "appended. The prompt itself is chosen by role and cannot be set.")]
    string? AdditionalInstructions = null,
    [property: Description(
        "Single-entry shape of `memberAgents`, kept for callers still sending one string. "
        + "When `memberAgents` is present, this is ignored.")]
    string? MemberAgent = null,
    [property: Description(
        "Which Agents this team's new members may run, in tie-break order. Required for new teams: "
        + "at least one HEADLESS Agent. Empty is refused.")]
    IReadOnlyList<string>? MemberAgents = null,
    [property: Description(
        "The folder that will CONTAIN this team's `teams/<id>/` directory. Omitted, the instance "
        + "root is used and nothing is stored, so the team follows the data root if it ever moves. "
        + "Checked in this order: it must be a location this host allows - one of "
        + "`FileBrowser:Roots` in `appsettings.json`, or inside the instance's own folder, which is "
        + "always permitted - then it must already exist, then it must be writable, and it must not "
        + "already contain a folder for this team. The allowlist is checked FIRST and deliberately: "
        + "it is the only one of the four whose recovery a caller cannot guess, and checking it "
        + "later would mean probing a path the host was about to refuse.")]
    string? Root = null,
    [property: Description(
        "The ordered Git repository URLs for this team. Each must be an absolute http or https "
        + "URL whose final path segment is safe and unique as a folder name, or `local:<name>` for one "
        + "of the instance's local repositories. Omitted means none.")]
    IReadOnlyList<string>? Repos = null,

    [property: Description(
        "Tokens this team may spend on ONE workflow, input and output together - a budget for one "
        + "workflow, not a total the team may spend across everything it runs. Null means the team "
        + "has chosen nothing and the instance figure applies; 0 means unlimited; a positive value "
        + "is stored exactly as typed, including above the instance figure. A negative value is "
        + "refused (400) BEFORE anything is created. Changeable later on the team's settings "
        + "screen.")]
    long? BudgetTokens = null,

    [property: Description(
        "True to give a team left with no repository a local repository named after it now, attached "
        + "as `local:<team id>`. Omitted or false means none: the team's local repository is made when "
        + "its work first needs it. Ignored when `repos` names any repository.")]
    bool? LocalRepository = null,

    [property: Description(
        "What to do with a URL in `repos` that `git ls-remote` could not read, keyed by that URL: "
        + "`use-local`, `create-on-github` (a person only; github.com only) or `attach-anyway` (a person "
        + "only; a network failure only). A URL with no choice that cannot be read refuses the create.")]
    IReadOnlyDictionary<string, string>? RepoChoices = null,

    [property: Description(
        "Each contributed repository's upstream, keyed by its URL in `repos`: that repository "
        + "is in contributor mode, its URL is the fork (origin), and its clone is made with an "
        + "`upstream` remote. A blank value, or a repository not named, is owned. Checked BEFORE "
        + "anything is created (400). Changeable later on the team's settings screen.")]
    IReadOnlyDictionary<string, string>? Upstreams = null);

/// <summary>
/// What a person typed as the clone's name, and NOTHING ELSE. The identifier is derived from it
/// here, on the server, where <c>DeriveName</c> lives.
///
/// There is deliberately no field for anything else a team carries. Every one of them comes from
/// the SOURCE - a body that could override one would make this two operations wearing one name, and
/// the first field somebody added would be `root`, which is the one value a browser is never sent.
/// </summary>
internal sealed record CloneTeam(
    [property: Description(
        "The new team's name as a PERSON would write it. The identifier is derived from it, and a "
        + "name deriving to one that is taken is refused with 409 naming the existing team.")]
    string? Name);

/// <summary>A team's additional instructions.</summary>
internal sealed record SetAdditionalInstructions(
    [property: Description(
        "The instructions, as free text. Null or blank clears them.")]
    string? AdditionalInstructions);

internal sealed record SetMemberAgent(
    [property: Description(
        "The ordered allowlist of Agents this team's new members may run. Empty is refused.")]
    IReadOnlyList<string>? Agents,
    [property: Description(
        "Single-entry shape of `agents` for callers still sending one Agent.")]
    string? Agent);

internal sealed record CreateSchedule(
    [property: Description("A human-readable name for this schedule. Required.")]
    string? Name,
    [property: Description(
        "Which member receives the scheduled instruction. Omit for the team's Manager.")]
    string? Container,
    [property: Description("The instruction text to append when this schedule fires. Required.")]
    string? Instruction,
    [property: Description("The kind: `cron`, `every`, `once`, `event` or `folderChange`. Required.")]
    string? Kind,
    [property: Description("Cron expression when `kind` is `cron`.")]
    string? Expression = null,
    [property: Description("IANA timezone when `kind` is `cron`.")]
    string? Timezone = null,
    [property: Description("Interval in seconds when `kind` is `every`. Minimum 1.")]
    int? IntervalSeconds = null,
    [property: Description("The one-shot instant when `kind` is `once`.")]
    DateTimeOffset? FireAt = null,
    [property: Description(
        "When true, this schedule is skipped while the target member is busy. Defaults to true.")]
    bool? IdleOnly = null,
    [property: Description("Whether this schedule is currently enabled. Defaults to true.")]
    bool? Enabled = null,
    [property: Description(
        "The next due instant this row should be swept at. Null means not currently due.")]
    DateTimeOffset? NextDueAt = null,
    [property: Description(
        "Which event type fires this trigger, required when `kind` is `event` and checked against "
        + "GET /api/events - naming one this platform does not publish is refused.")]
    string? EventType = null,
    [property: Description(
        "A filter evaluated before the wake is enqueued. Null means every message of that type. "
        + "`field op value`, where op is `eq` or `contains`. Naming a field the event type does not "
        + "declare is refused.")]
    string? Filter = null,
    [property: Description(WatchDescriptions.Root)]
    string? WatchRoot = null,
    [property: Description(WatchDescriptions.Path)]
    string? WatchPath = null,
    [property: Description(WatchDescriptions.Glob)]
    string? WatchGlob = null,
    [property: Description(WatchDescriptions.Poll)]
    int? PollSeconds = null,
    [property: Description(WatchDescriptions.Quiet)]
    int? QuietSeconds = null,
    [property: Description(WatchDescriptions.MinInterval)]
    int? MinIntervalSeconds = null,
    [property: Description(WatchDescriptions.WakeManager + " Defaults to `onHandbackOrFailure`.")]
    string? WakeManager = null,
    [property: Description(WatchDescriptions.DailyTokenCap + " Omit or null for no cap.")]
    long? DailyTokenCap = null,
    [property: Description(WatchDescriptions.Outcome + " Omit or null for none.")]
    string? OutcomeId = null);

/// <summary>The folder-trigger field descriptions, shared by the create, update and test-folder bodies.</summary>
internal static class WatchDescriptions
{
    public const string Outcome =
        "The outcome this trigger's fires serve: an active or proposed outcome's id or exact name, "
        + "stored as its id. A fire that roots a workflow links it to this outcome, attributed to the "
        + "person who configured the trigger.";

    public const string Root =
        "`kind: folderChange` only. `documents` for the team's documents, or `root:<name>` for a "
        + "file-browser root configured with allowWatch.";

    public const string Path =
        "`kind: folderChange` only. The folder to watch, relative to `watchRoot`. Empty is the root.";

    public const string Glob =
        "`kind: folderChange` only. Optional wildcard: `*.pdf` matches file names, a pattern with `/` "
        + "matches the path inside the folder, `**` crosses folders.";

    public const string Poll = "`kind: folderChange` only. Seconds between listings. Default 60, minimum 15.";

    public const string Quiet =
        "`kind: folderChange` only. Seconds a change must hold still before it fires. Default 30.";

    public const string MinInterval =
        "`kind: folderChange` only. The shortest gap in seconds between two fires. Default 60.";

    public const string WakeManager =
        "What a run this trigger started does to the Manager when it ends: `always` (every finished "
        + "run wakes it), `onHandbackOrFailure` (only a hand-back or a failure), or `never` (not even a "
        + "failure; the run is still recorded and shown). Work the Manager sent, and a person's tell, "
        + "are not affected.";

    public const string DailyTokenCap =
        "Billable tokens this trigger's runs may spend per day in its timezone (UTC when it has "
        + "none), the Manager runs they woke included; measured usage only. A fire once it is reached "
        + "is skipped: the first skip of the day writes `schedule.skipped` (reason `daily token cap "
        + "reached`, and for a schedule `; resumes at <time>`) and a tenant row, and later ones are "
        + "only counted. A schedule sleeps until its first occurrence of the next day; raising the "
        + "cap above today's spend or clearing it wakes it. A whole number of at least 1.";
}

/// <summary>"Test this folder".</summary>
internal sealed record TestFolder(
    [property: Description(WatchDescriptions.Root)] string? WatchRoot,
    [property: Description(WatchDescriptions.Path)] string? WatchPath,
    [property: Description(WatchDescriptions.Glob)] string? WatchGlob = null);

/// <summary>The cron builder's and the start field's preview: a schedule shape, nothing stored.</summary>
internal sealed record PreviewSchedule(
    [property: Description("`cron`, `every` or `once`.")] string? Kind,
    [property: Description("A cron expression in the seconds format, for `cron`.")] string? Expression = null,
    [property: Description("An IANA timezone for `cron`; UTC when absent.")] string? Timezone = null,
    [property: Description("Seconds between firings, for `every`.")] int? IntervalSeconds = null,
    [property: Description("For `cron` and `every`, the start before which it never fires; for `once`, when it fires.")] DateTimeOffset? FireAt = null,
    [property: Description("How many firings to list, 1 to 20; 5 when absent.")] int? Count = null);

internal sealed record UpdateSchedule(
    [property: Description("A new human-readable name for this schedule.")]
    string? Name = null,
    [property: Description("A new target member. Use null or blank to leave unchanged.")]
    string? Container = null,
    [property: Description("New instruction text.")]
    string? Instruction = null,
    [property: Description("A new kind: `cron`, `every`, `once`, `event` or `folderChange`.")]
    string? Kind = null,
    [property: Description("New cron expression. Null or blank clears it.")]
    string? Expression = null,
    [property: Description("New timezone. Null or blank clears it.")]
    string? Timezone = null,
    [property: Description("New interval seconds. Null clears it.")]
    int? IntervalSeconds = null,
    [property: Description("New one-shot instant. Null clears it.")]
    DateTimeOffset? FireAt = null,
    [property: Description("When supplied, sets idle-only behavior.")]
    bool? IdleOnly = null,
    [property: Description("When supplied, sets enabled/disabled.")]
    bool? Enabled = null,
    [property: Description("When supplied, sets next-due. Null clears it.")]
    DateTimeOffset? NextDueAt = null,
    [property: Description(
        "New event type. Null or blank clears it. Checked against GET /api/events when the "
        + "resulting kind is `event`.")]
    string? EventType = null,
    [property: Description("New filter. Null or blank clears it.")]
    string? Filter = null,
    [property: Description(WatchDescriptions.Root)]
    string? WatchRoot = null,
    [property: Description(WatchDescriptions.Path)]
    string? WatchPath = null,
    [property: Description(WatchDescriptions.Glob + " Null or blank clears it.")]
    string? WatchGlob = null,
    [property: Description(WatchDescriptions.Poll)]
    int? PollSeconds = null,
    [property: Description(WatchDescriptions.Quiet)]
    int? QuietSeconds = null,
    [property: Description(WatchDescriptions.MinInterval)]
    int? MinIntervalSeconds = null,
    [property: Description(WatchDescriptions.WakeManager + " May not be null.")]
    string? WakeManager = null,
    [property: Description(WatchDescriptions.DailyTokenCap + " Null clears the cap.")]
    long? DailyTokenCap = null,
    [property: Description(WatchDescriptions.Outcome + " Null clears it.")]
    string? OutcomeId = null);

internal sealed record CreateContainer(
    [property: Description(
        "The member's name as a PERSON would write it. Its identifier is derived from this and "
        + "never asked for, the same as a team's.")]
    string? Name,
    [property: Description(
        "The Agent this member runs. Omit to have the team resolve one from its allowlist.")]
    string? Agent,
    [property: Description(
        "The work tag to hire this member for. When omitted, the team's first allowed Agent is used.")]
    string? For,
    [property: Description(
        "The member's own role line - who it is, not what it is doing now. Appended after the "
        + "built-in Member prompt, never in place of it.")]
    string? SystemPrompt,
    [property: Description(
        "The message types that WAKE this member. The container holds the subscription, never the "
        + "agent - a headless agent runs and exits and cannot hold one. Beware global types: two "
        + "managers subscribed to `agentContainer.completed` wake each other without end.")]
    string[]? Subscribes,
    [property: Description(
        "A PLUGIN member's configuration: field name to value, checked against the plugin's "
        + "manifest (`GET /api/plugins`). Refused for an Agent.")]
    Dictionary<string, JsonElement>? Config = null,
    [property: Description(
        "A PLUGIN member's secret bindings: each secret its manifest names, to a LOGICAL KEY set on "
        + "the Host with `secret set` - never a value. A required secret whose key is not set is "
        + "refused. Refused for an Agent.")]
    Dictionary<string, string>? Secrets = null,
    [property: Description(
        "A PLUGIN member's connection bindings: each connection slot its manifest declares, to a "
        + "CONNECTION ID (Admin > Connections) - never a token. The connection must suit the slot's "
        + "providers and hold its scopes. A Manager or a Concierge may name only a connection a person "
        + "has already bound on a member of this team. Refused for an Agent.")]
    Dictionary<string, string>? Connections = null,
    [property: Description(
        "A PLUGIN member's OWN secret values, typed by a person: each secret field its manifest names, to "
        + "the value. Kept for this member only, as ciphertext, with the member's settings and a "
        + "`member.secret-set` row each, in one transaction; a hire whose values cannot be kept makes no "
        + "member. Refused for an agent's hire (a Manager's, a Concierge's): a person sets them in the "
        + "member's settings. Refused for an Agent.")]
    Dictionary<string, string>? SecretValues = null,
    [property: Description(
        "A PLUGIN member's NAMED secrets, typed by a person, for a plugin whose manifest declares a "
        + "`namedSecrets` block: each name a person chose, to the value. Kept for this member only, as "
        + "ciphertext, with the member's settings and a `member.named-secret-set` row each, in one "
        + "transaction. Refused for an agent's hire, as `secretValues` is, and for an Agent.")]
    Dictionary<string, string>? NamedSecretValues = null);

/// <summary>A repository's contributor settings. See the route.</summary>
internal sealed record SetRepoContributor(
    [property: Description("The original project's URL. Set puts the repository in contributor mode; null or blank makes it owned.")]
    string? UpstreamUrl,
    [property: Description("The account the fork belongs to. Null or blank reads it from the repository URL.")]
    string? ForkOwner,
    [property: Description("Sign off commits (DCO): install the commit-msg hook that adds Signed-off-by with the instance's git identity.")]
    bool DcoSignOff,
    [property: Description("A person's note that the upstream project's CLA is signed. A record only; the platform signs nothing.")]
    string? ClaSignedNote);

/// <summary>A person's choice of a repository's default branch.</summary>
internal sealed record SetRepoDefaultBranch(
    [property: Description(
        "The branch this repository's Git actions treat as main. Null or blank clears the person's "
        + "choice, so the branch origin's HEAD named on the last clone or Fetch is used, or not known.")]
    string? Branch);

/// <summary>
/// `Agent` repoints the member. Headless presets only, and it takes effect on the next wake: the
/// runner resolves a preset by name at launch rather than from anything captured at create.
/// </summary>
internal sealed record SetConcierge(
    [property: Description(
        "The Agent preset this team's Concierge launches. Must be an INTERACTIVE preset. Blank "
        + "clears the choice, so the Concierge runs its default agent; OMIT it to leave it as it is.")]
    string? Agent);

/// <summary>
/// A person's current team, or nothing. NULL and empty mean the SAME thing here - "no team is
/// active" - which is the one place in this codebase where collapsing them is right: there is no
/// third state to tell apart, unlike a stored system prompt where absent leaves and empty clears.
/// </summary>
internal sealed record SetCurrentTeam(
    [property: Description(
        "The team identifier to work on, or null/empty for none. Null is a real state rather than a "
        + "misconfiguration: it is what the Teams tab sets, and a tool's refusal for it names how "
        + "to choose rather than reporting a fault.")]
    string? Team);

/// <summary>
/// Who, if anyone, is the PERSON behind a principal - the one question both `/api/me/current-team`
/// routes have to answer before they can do anything.
/// </summary>
internal static class CurrentTeam
{
    /// <summary>
    /// The user id whose current team this principal shares, or null when the principal is not a
    /// person's door at all.
    ///
    /// A CONTAINER FALLS OUT AS NULL rather than being refused by name, and that is deliberate: its
    /// team is half of its `(team, name)` identity and arrives in HARNESS_TEAM, so it has no
    /// person to share a current team with. A future PrincipalKind that gains an owner joins the
    /// second arm automatically, which is the safe direction here - the value is per-person state,
    /// not authority, and every route the caller then uses is still gated on its own terms.
    /// </summary>
    public static string? PersonBehind(Principal principal) =>
        principal.Kind == PrincipalKind.User ? principal.Id : principal.OwnerUserId;

    /// <summary>
    /// Tells every connection this PERSON holds, on every device, that their current team moved.
    ///
    /// Without it the browser follows a tab click and not a change made anywhere else, which leaves
    /// two stores of one fact again - the shape this whole design exists to remove.
    ///
    /// A PERSON GROUP, never a team group. The message is "your current team is now X", and the
    /// teams it moves between are exactly the groups a connection may not be in.
    ///
    /// ContainerSnapshot is NOT widened to carry this. That record rides the hub to every browser
    /// holding a team, and a per-person fact on it would be pushed to people it is not about.
    /// </summary>
    public static Task AnnounceAsync(
        IHubContext<ContainerHub> hub,
        string person,
        string? team,
        string? name,
        CancellationToken ct) =>
        hub.Clients.Group(ContainerHub.PersonGroup(person))
            .SendAsync("currentTeamChanged", new { team, name }, ct);

    /// <summary>
    /// 403 rather than 400: the request is well formed and it is the CALLER that is wrong for it.
    /// </summary>
    public static IResult NotAPersonsDoor { get; } = Results.Content(
        """{"error":"This principal has no current team. A member's team is fixed at its identity."}""",
        "application/json",
        statusCode: StatusCodes.Status403Forbidden);
}

internal sealed record UpdateContainer(
    [property: Description(
        "The member's new display name, as a person would write it. Omit or send blank to leave "
        + "it unchanged.")]
    string? Name,
    [property: Description(
        "The member's own role line, appended after the built-in role prompt. Omit to leave it "
        + "unchanged; blank clears it.")]
    string? SystemPrompt,
    [property: Description(
        "The Agent preset this member runs. Must be a HEADLESS preset - a member is never "
        + "interactive. Omit or send blank to leave it unchanged. Changing it takes effect on the "
        + "member's next wake, with no restart: the catalog entry the runner resolves is updated in "
        + "the same operation.")]
    string? Agent = null);

/// <remarks>
/// There is no `From`. A caller-supplied sender is never validated, so it could name any container
/// on any team and would be evidence of nothing; the source is derived from the authenticated
/// principal. Do not add one - a body field that names a sender is indisputably
/// forgeable, and the activity feed renders it.
/// </remarks>
/// <summary>What a member says about itself, mid-run.</summary>
internal sealed record ReportProgress(
    [property: Description(
        "One short line saying what you are doing right now - \"gathering sources - 3 of 8\". It "
        + "REPLACES whatever you last said rather than adding to it, so write the current state "
        + "rather than a running commentary. Blank is refused: an empty line on a card cannot be "
        + "told apart from having said nothing.")]
    string? Status);

/// <summary>Why a member stopped without finishing.</summary>
internal sealed record ReportBlocked(
    [property: Description(
        "Why you stopped, in one or two sentences - \"the contract files were never written, so "
        + "there is nothing to build against\". It stays on your card until something wakes you "
        + "again, so write what the person reading the board needs in order to unblock you. Blank "
        + "is refused: a card saying only that you stopped sends its reader to the log, which is "
        + "the trip this exists to remove.")]
    string? Reason,
    [property: Description(
        "Optional 1-based item number from this run's batched prompt. When present, marks only "
        + "that item as abandoned; omit it to mark the whole run.")]
    int? Item = null,
    [property: Description(
        "With `item`: defer that item instead of abandoning it. It is delivered again as its own "
        + "next run; `reason` says why it waits. Refused for the only item of a run.")]
    bool? Defer = null);

/// <summary>What question this member needs answered before it can continue.</summary>
internal sealed record ReportNeedsDecision(
    [property: Description(
        "The question that needs an answer before you can continue this step. It stays on your "
        + "card until something wakes you again, so write the actual decision and not a status "
        + "line. Blank is refused.")]
    string? Question);

/// <summary>A member asking to be woken once, later.</summary>
internal sealed record AskWake(
    [property: Description("How long from now, as whole hours, minutes or seconds: `90s`, `10m`, `2h`, `1h30m`.")]
    string? In,
    [property: Description("One line, at most 300 characters: what to look at when woken.")]
    string? Note);

/// <summary>
/// What a worker finished and is handing back.
///
/// `Delivered`, the same member name `ReportWorkflowCompleted` uses below, because it answers the
/// same question and writes the same payload field - see the catalog entry for
/// `agentContainer.handback`. The two records stay separate because the ROUTES are: one is a
/// worker's hand-back and the other a Manager's declaration, and sharing a body type between them
/// would put one OpenAPI description on two different claims.
/// </summary>
internal sealed record ReportHandback(
    [property: Description(
        "What you finished, in one short sentence. Blank is refused: this wakes your Manager, and "
        + "a wake that does not say what it is about sends the reader to the log.")]
    string? Delivered);

/// <summary>What this workflow delivered.</summary>
internal sealed record ReportWorkflowCompleted(
    [property: Description(
        "What the finished workflow delivered, in one short sentence. Blank is refused: a "
        + "completion with no result says that work ended and not what was delivered.")]
    string? Delivered,
    [property: Description(
        "Required only when cards of this workflow are unfinished (still to do, running, "
        + "interrupted, failed or blocked): why they are being left undone. Recorded on the "
        + "declaration.")]
    string? Dropped = null);

/// <summary>A person's reason for closing a workflow. Optional - see the route's own comment for
/// why a blank one is accepted rather than refused.</summary>
internal sealed record CloseWorkflow(
    [property: Description(
        "Why this workflow is being closed, in one short sentence. Optional: the row still records "
        + "who closed it and when regardless.")]
    string? Reason);

internal sealed record WorkflowWaitReply(
    long Workflow,
    string Outcome,
    string? MessageType,
    long? Seq,
    int TimeoutSeconds,
    string? TimeoutVerdict,
    DateTimeOffset? IdleSince)
{
    public static WorkflowWaitReply From(long workflow, Message terminal, int timeoutSeconds) =>
        terminal.Type switch
        {
            MessageTypes.WorkflowCompleted => new(
                workflow, WaitOutcome.Completed, terminal.Type, terminal.Seq, timeoutSeconds, null, null),
            MessageTypes.WorkflowClosed => new(
                workflow, WaitOutcome.Closed, terminal.Type, terminal.Seq, timeoutSeconds, null, null),
            MessageTypes.Blocked => new(
                workflow, WaitOutcome.Blocked, terminal.Type, terminal.Seq, timeoutSeconds, null, null),
            MessageTypes.NeedsDecision => new(
                workflow, WaitOutcome.NeedsDecision, terminal.Type, terminal.Seq, timeoutSeconds, null, null),
            MessageTypes.Failed => new(
                workflow, WaitOutcome.Failed, terminal.Type, terminal.Seq, timeoutSeconds, null, null),
            _ => throw new InvalidOperationException(
                $"Message type '{terminal.Type}' is not a wait terminal."),
        };

    public static WorkflowWaitReply Timeout(
        long workflow,
        int timeoutSeconds,
        string timeoutVerdict,
        DateTimeOffset? idleSince = null) =>
        new(workflow, WaitOutcome.Timeout, null, null, timeoutSeconds, timeoutVerdict, idleSince);
}

internal static class WaitOutcome
{
    public const string Completed = "completed";

    /// <summary>A person ended the workflow rather than a manager declaring it delivered - see
    /// the close route's own comment for why this is a distinct outcome from `Completed`.</summary>
    public const string Closed = "closed";
    public const string Blocked = "blocked";
    public const string NeedsDecision = "needs-decision";
    public const string Failed = "failed";
    public const string Timeout = "timeout";
}

internal static class TimeoutVerdict
{
    public const string Working = "working";
    public const string Quiet = "quiet";
}

internal sealed record Tell(
    [property: Description("What to tell the member. Empty or whitespace is refused.")]
    string? Instruction,

    [property: Description(
        "A short line to head the card this instruction becomes, at most 80 characters. Omitted, "
        + "the subject is taken from the instruction's own first line - so this is for when that "
        + "line is not a title. It is a DISPLAY convenience: the member is handed the whole "
        + "instruction either way.")]
    string? Subject = null,

    [property: Description(
        "The seq of the message this instruction is ANSWERING, so it joins that workflow instead "
        + "of starting a new one. The `tell` tool sends it from `HARNESS_CAUSATION`, which the platform "
        + "sets per invocation; a caller with no waking message - a person, a console - omits it. "
        + "A string rather than a number so a malformed value is refused with a message naming it, "
        + "rather than rejected by the JSON binder before any of this code runs.")]
    string? Causation = null,

    [property: Description(
        "The planned card this instruction CLAIMS. The card gains this member and moves out of "
        + "Todo; no second card is minted. Absent - which is every ordinary instruction - behaves "
        + "exactly as it always has, which is what keeps a team that never touches the backlog "
        + "unchanged and stops conversational instructions each becoming a card of their own.")]
    string? Card = null,

    [property: Description(
        "The outcome the NEW workflow this instruction roots serves: an active or proposed "
        + "outcome's id, or its exact name. Only on an instruction with no causation; one that "
        + "joins a workflow is refused, and that workflow's outcome is set with the `outcome` tool.")]
    string? Outcome = null);

internal sealed record DocumentsRename(
    [property: Description("The documents to rename, each with its new leaf name. At most 1000.")]
    IReadOnlyList<DocumentsRenameItem>? Items);

internal sealed record DocumentsRenameItem(
    [property: Description("The file or folder, relative to the team's documents root.")]
    string? Path,
    [property: Description("Its new name: a leaf, never a path. It stays in the same folder.")]
    string? Name);

internal sealed record DocumentsTransfer(
    [property: Description("Where the items go.")]
    DocumentsTransferTarget? To,
    [property: Description("The documents to move or copy, all in this folder. At most 1000.")]
    IReadOnlyList<DocumentsTransferItem>? Items);

internal sealed record DocumentsTransferTarget(
    [property: Description(
        "A documents folder, as `folder` in `GET /api/documents` names it: this team's or another's.")]
    string? Folder,
    [property: Description("A folder in it, relative to its root. Omit it for the root itself.")]
    string? Path);

internal sealed record DocumentsTransferItem(
    [property: Description("The file or folder, relative to this team's documents root.")]
    string? Path,
    [property: Description(
        "What to do when its name is already taken where it goes: `keep-both`, `replace` or "
        + "`skip`. Omitted, a clash answers 409 naming it and nothing is done.")]
    string? OnClash);

internal sealed record NewFolder(
    [property: Description(
        "The folder to create, relative to the team's documents root. Resolved against that root "
        + "before anything touches disk, so a path climbing out of it is refused.")]
    string? Path);

/// <summary>A path a documents delete left, relative to the documents folder, and why.</summary>
internal sealed record DocumentLeft(string Path, string Reason);
