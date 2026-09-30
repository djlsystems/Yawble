namespace Harness.Contracts;

/// <summary>One administrative act. <paramref name="ActorEmail"/> and <paramref name="SubjectName"/>
/// are DENORMALISED copies, not joins - the whole point of this record is to survive the account or
/// the team it names.</summary>
public sealed record TenantEvent(
    long Seq,
    DateTimeOffset OccurredAt,
    string? ActorId,
    string? ActorEmail,
    string Action,
    string? Subject,
    string? SubjectName,
    string? Detail);

/// <summary>One page of the tenant log, and how many rows there are in total.</summary>
public sealed record TenantLogPage(IReadOnlyList<TenantEvent> Events, long Total);

/// <summary>
/// The verbs. Constants rather than free strings, because a log filtered or read by a human is a log
/// whose vocabulary has to be stable - a stray <c>team.delete</c> beside <c>team.deleted</c> is two
/// answers to one question, and nothing would ever fail.
/// </summary>
public static class TenantActions
{
    public const string SignedIn = "user.signed-in";
    public const string SignedOut = "user.signed-out";
    public const string UserCreated = "user.created";
    public const string UserChanged = "user.changed";
    public const string UserDeleted = "user.deleted";
    public const string PasswordReset = "user.password-reset";

    /// <summary>A person changed an instance-wide setting. Subject is the setting's name;
    /// detail carries <c>setting</c>, <c>old</c> and <c>new</c>.</summary>
    public const string TenantSettingChanged = "tenant.settingChanged";

    public const string TeamCreated = "team.created";
    public const string TeamRelabelled = "team.renamed";
    public const string TeamDeleted = "team.deleted";

    /// <summary>Unfinished removals were retried - at the Host's start (no actor) or by a person.
    /// Detail names every folder retried, whether it finished, and each path still remaining.</summary>
    public const string RemovalRetried = "removal.retried";
    public const string TeamPaused = "team.paused";
    public const string TeamResumed = "team.resumed";

    /// <summary>A person changed what this team may spend on ONE workflow.</summary>
    public const string TeamBudgetChanged = "team.budgetChanged";

    /// <summary>A team's substrate was reset under its Agent Containers. Its own verb rather than a
    /// flavour of a change, because it is the one act short of deletion that can permanently remove
    /// message rows - and an audit asking "who wiped this team's history" has to find it by name.
    /// </summary>
    public const string TeamReset = "team.reset";

    /// <summary>A person asked a reset to reset the team's repositories. Written BEFORE anything
    /// moves, naming every tree and branch it may remove and whether the team branch is reset; when
    /// it cannot be written nothing is reset. What was removed and kept is on the
    /// <see cref="TeamReset"/> row after it.</summary>
    public const string TeamResetRepositories = "team.reset-repositories";

    /// <summary>
    /// A team was created carrying another team's configuration. Its own verb rather than a
    /// <c>team.created</c> with a detail field, because the question an audit asks is "where did
    /// this team's settings come from" - and a row that only says "created" cannot answer it. The
    /// detail names the SOURCE, which outlives the source's own deletion for the reason every
    /// subject on this table is denormalised.
    /// </summary>
    public const string TeamCloned = "team.cloned";
    public const string ConciergeChanged = "team.concierge-changed";

    /// <summary>Which Agent a team's NEW members run. Its own verb rather than folded into a rename,
    /// because it is its own act and the log is read to answer "who changed what".</summary>
    public const string TeamMemberAgentChanged = "team.member-agent-changed";

    public const string TeamMemberPromptChanged = "team.member-prompt-changed";

    /// <summary>
    /// The backlog's own acts. THE ITEM IS THE SPEC, so creating, editing and deleting one are
    /// administrative acts on the tenant's work rather than work themselves - which is what puts
    /// them here rather than on the message log. Nothing subscribes to a tenant event and nothing
    /// wakes from one.
    ///
    /// A REORDER IS DELIBERATELY NOT AUDITED. It is not a change to what the work IS, it happens
    /// many times in a sitting, and a table every person reads is not improved by a hundred
    /// rows saying somebody dragged something. The same call switching team is already recorded as
    /// not being a server action.
    /// </summary>
    public const string BacklogItemCreated = "backlog.item-created";

    public const string BacklogItemEdited = "backlog.item-edited";
    /// <summary>An item was marked implemented: who confirmed the work is in the product. Its own verb
    /// rather than read out of <c>BacklogItemEdited</c>, because the item's detail answers "who said
    /// so" from it, and that includes a person's word when landed could not be proven. Subject is the
    /// citation; detail names the state it moved from and whether a Concierge wrote it for them.</summary>
    public const string BacklogItemImplemented = "backlog.item-implemented";

    public const string BacklogItemArchived = "backlog.item-archived";
    public const string BacklogItemRestored = "backlog.item-restored";

    /// <summary>Its own verb rather than a flavour of an edit, for the reason <c>TeamReset</c> is:
    /// it is the one backlog act that permanently removes something, and an audit asking "where did
    /// B000H go" has to find it by name.</summary>
    public const string BacklogItemDeleted = "backlog.item-deleted";

    /// <summary>An item was handed to a team. The SEQ of the row it wrote on the message log is the
    /// workflow's correlation root, so this is the administrative half of an act whose causal half
    /// lives on the other log entirely.</summary>
    public const string BacklogItemDispatched = "backlog.item-dispatched";

    public const string MemberAdded = "member.added";
    public const string MemberChanged = "member.changed";
    public const string MemberDeleted = "member.deleted";

    /// <summary>A member's own instructions changed, a clear included. Detail names who set them
    /// and whether they were cleared - never the words, which are somebody's and can be long.</summary>
    public const string MemberInstructionsChanged = "member.instructions-changed";

    public const string AgentsSaved = "agents.saved";

    /// <summary>A person had the platform update an agent CLI. Subject is the preset; detail names the
    /// command, the versions before and after, and whether it succeeded.</summary>
    public const string AgentUpdated = "agent.updated";

    /// <summary>A person cancelled an agent CLI update while it waited for runs in flight. Subject is
    /// the preset; detail names the command and how many launches it released.</summary>
    public const string AgentUpdateCancelled = "agent.update-cancelled";

    /// <summary>A person re-read the installed plugins, so a plugin installed or upgraded on disk is
    /// registered without a restart.</summary>
    public const string PluginsRescanned = "plugins.rescanned";

    /// <summary>A person installed a plugin version from a folder inside the instance (the web app's
    /// route, or the operator CLI's <c>--from-instance</c>). Subject is the plugin id; detail names the
    /// version, the source folder, whether it replaced one, and the Host's verdict.</summary>
    public const string PluginInstalled = "plugins.installed";

    /// <summary>A person removed a plugin, or one kept version of it, from the instance (the web app's
    /// Remove, the same rules as <c>plugin remove</c>). Subject is the plugin id; detail names
    /// whether it was the whole plugin and the versions removed. Written in the removal's transaction.</summary>
    public const string PluginRemoved = "plugins.removed";

    /// <summary>A person changed a plugin member's settings after hire. Detail names the plugin and
    /// the field and secret NAMES that changed - never a value.</summary>
    public const string MemberPluginSettingsChanged = "member.plugin-settings-changed";

    /// <summary>A plugin member's connection bindings were set or changed (at hire, in its settings,
    /// or carried by a clone). Detail names the slots and connection ids - never a token.</summary>
    public const string MemberConnectionsChanged = "member.connections-changed";

    /// <summary>A person set an OAuth provider's client. Detail names the fields changed, never the
    /// client secret.</summary>
    public const string ConnectionProviderSaved = "connections.provider-saved";

    /// <summary>A person removed a custom OAuth provider.</summary>
    public const string ConnectionProviderRemoved = "connections.provider-removed";

    /// <summary>A person connected an account. Detail: provider, account, scopes - never a token.</summary>
    public const string ConnectionConnected = "connections.connected";

    /// <summary>A person reconnected an account, clearing <c>needs-reconnect</c>.</summary>
    public const string ConnectionReconnected = "connections.reconnected";

    /// <summary>A person renamed a connection.</summary>
    public const string ConnectionRenamed = "connections.renamed";

    /// <summary>A person disconnected an account: its tokens deleted. The revoke follows it.</summary>
    public const string ConnectionDisconnected = "connections.disconnected";

    /// <summary>After a disconnect, the provider's revocation of its token, best effort: the result.</summary>
    public const string ConnectionRevoked = "connections.revoked";

    /// <summary>The provider refused a refresh; the connection needs a person to reconnect it.</summary>
    public const string ConnectionNeedsReconnect = "connections.needs-reconnect";

    /// <summary>A custom skill was created, changed or deleted. Built-ins are never written.</summary>
    public const string SkillCreated = "skill.created";
    public const string SkillChanged = "skill.changed";
    public const string SkillDeleted = "skill.deleted";

    /// <summary>A person deleted a document, a folder, or a gone team's whole documents folder
    /// Written BEFORE the delete, which does not happen when this row cannot be.</summary>
    public const string DocumentsDeleted = "documents.deleted";

    /// <summary>A documents delete that left something: written after it, naming each path left
    /// and why, so the log never claims a deletion that did not happen.</summary>
    public const string DocumentsDeleteIncomplete = "documents.delete-incomplete";

    /// <summary>A person uploaded a document into a team's documents. Detail carries the team, the
    /// path and the size - never the file's contents.</summary>
    public const string DocumentUploaded = "document.uploaded";

    /// <summary>A team's additional instructions changed.</summary>
    public const string TeamInstructionsChanged = "team.instructions-changed";

    /// <summary>A person put the Agent catalog back to this build's built-in seed. Carries
    /// counts and nothing else - what moved, what broke, how many - because a Prompt is words
    /// somebody wrote and this table is readable by every person and kept forever.</summary>
    public const string AgentsResetToSeed = "agents.reset-to-seed";

    public const string ScheduleCreated = "schedule.created";
    public const string ScheduleChanged = "schedule.changed";
    public const string ScheduleDeleted = "schedule.deleted";
    public const string ScheduleFired = "schedule.fired";
    public const string ScheduleSkipped = "schedule.skipped";
    public const string ScheduleMissed = "schedule.missed";
    public const string ScheduleMemberMissing = "schedule.member-missing";

    /// <summary>A person pressed Run now on a schedule. Detail carries the fire's `outcome`
    /// (`fired`, `skipped`, `capped`, `member-missing`), its `reason` and the `seq` it appended.</summary>
    public const string ScheduleRunNow = "schedule.run-now";

    /// <summary>A solution install ran a `runAtInstall` schedule once, by the person who installed it.
    /// Detail carries the same `outcome`, `reason` and `seq` as <see cref="ScheduleRunNow"/>.</summary>
    public const string ScheduleRunAtInstall = "schedule.run-at-install";

    /// <summary>A person minted a credential for themselves. The row carries the key's id, its
    /// label and its prefix - never the credential, which is the one thing this log must never
    /// hold.</summary>
    public const string KeyMinted = "key.minted";

    public const string KeyRevoked = "key.revoked";

    public const string SweepRunningWithoutProgress = "sweep.running-no-progress";
    public const string SweepPendingNeverTerminal = "sweep.pending-never-terminal";
    public const string SweepQuietTeam = "sweep.quiet-team";
    public const string SweepRunningWithoutProgressCleared = "sweep.running-no-progress-cleared";
    public const string SweepPendingNeverTerminalCleared = "sweep.pending-never-terminal-cleared";
    public const string SweepQuietTeamCleared = "sweep.quiet-team-cleared";

    /// <summary>
    /// A team declared its workflow complete while its clone still holds commits that are not on
    /// its own `origin/main` tracking ref. The LOCAL half only: the tracking ref is updated BY a
    /// successful push, so being ahead of it is already conclusive, and asking the network instead
    /// would put `git ls-remote` and its failure modes inside a background sweep -- a detector that
    /// reports a finding because GitHub was briefly unreachable is one nobody trusts.
    /// </summary>
    /// <summary>
    /// A member's run was offered, or called, a tool outside <c>harness</c> and its preset's allowed
    /// list: the same finding as the team log's <c>agent.foreignTools</c> row, kept here because it
    /// is about what reached a person's accounts, and outlives the team. Subject is the member,
    /// detail the tools called and offered.
    /// </summary>
    public const string AgentForeignTools = "agent.foreign-tools";

    public const string SweepWrapUpNotPushed = "sweep.wrap-up-not-pushed";
    public const string SweepWrapUpNotPushedCleared = "sweep.wrap-up-not-pushed-cleared";

    /// <summary>
    /// Repository maintenance actions: bring clone current, merge to main, and cleanup worktrees.
    /// Detail JSON: { "repo": "...", "sha": "...", "refused": bool, "reason": string | null }
    /// </summary>
    public const string RepoBringCurrent = "repo.bring-current";
    public const string RepoMergeToMain = "repo.merge-to-main";

    /// <summary>A person pressed Bring current and merge: origin's default branch merged into the
    /// team branch, which was pushed; or refused, with the conflicting files named. The row that
    /// says it was pushed is written after the push. Merge to main then writes its own row.</summary>
    public const string RepoBringCurrentAndMerge = "repo.bring-current-and-merge";
    public const string RepoCleanupWorktrees = "repo.cleanup-worktrees";
    public const string RepoFetch = "repo.fetch";
    public const string RepoRebase = "repo.rebase";
    public const string RepoPush = "repo.push";

    /// <summary>A person set or cleared a repository's default branch in Team settings.</summary>
    public const string RepoDefaultBranchSet = "repo.default-branch-set";

    /// <summary>A person set a repository's contributor settings in Team settings.</summary>
    public const string RepoContributorSet = "repo.contributor-set";

    /// <summary>A person pressed Open pull request in the Git dialog: opened, linked or refused.</summary>
    public const string RepoPullRequestOpen = "repo.pull-request-open";

    /// <summary>A person created one of the instance's local repositories (<c>local:&lt;name&gt;</c>).
    /// Subject is the name.</summary>
    public const string LocalRepoCreated = "local-repo.created";

    /// <summary>A person deleted a local repository. Written BEFORE the delete, which does not
    /// happen when this row cannot be.</summary>
    public const string LocalRepoDeleted = "local-repo.deleted";

    /// <summary>A person asked GitHub to fork an upstream for a team repository.</summary>
    public const string RepoFork = "repo.fork";

    /// <summary>A person asked the team's Manager to bring main current after a rebase would conflict.</summary>
    public const string RepoAskTeam = "repo.ask-team";

    /// <summary>
    /// The team branch was deleted from origin - refused unless it was already an ancestor of
    /// origin/main, since that is the only thing standing between this action and losing work. The
    /// only destructive repository action in this file; see <c>RepoEndpoints.DeleteRemoteBranchAsync</c>.
    /// </summary>
    public const string RepoDeleteRemoteBranch = "repo.delete-remote-branch";

    /// <summary>
    /// A `tenant_agents` row became an ordinary team, in the schema step that converts them. Written by that step's SQL as the literal `tenant-agent.converted` rather than through
    /// <see cref="ITenantLog"/> - a migration has no <c>ITenantLog</c> and no actor - so this
    /// constant is what keeps the verb in the vocabulary and is what the tests read it back by.
    ///
    /// IT IS THE ONLY RECORD OF A NARROWING. A row with `all_teams = 0` carries a per-team
    /// allowlist, which is not supported: it becomes an ordinary team that reaches only itself, and
    /// this row's detail names the teams the allowlist reached. Silently widening it instead would
    /// be the worst outcome, and a narrowing nobody is told about is the second worst.
    /// </summary>
    public const string TenantAgentConverted = "tenant-agent.converted";

    /// <summary>A team's site was created. Subject is <c>&lt;team&gt;/&lt;site&gt;</c>, as on every site row.</summary>
    public const string SiteCreated = "site.created";

    /// <summary>A new version of a site was copied and made live. Detail carries the version, the
    /// source folder, its file count and bytes, and the version it replaced.</summary>
    public const string SitePublished = "site.published";

    /// <summary>An earlier kept version of a site was made live again.</summary>
    public const string SiteRolledBack = "site.rolled-back";

    /// <summary>A site stopped being served. Its versions and data are kept.</summary>
    public const string SiteUnpublished = "site.unpublished";

    /// <summary>A site, its versions and its data were deleted - by a person who confirmed, or by
    /// its team's deletion (no actor, detail <c>reason: team deleted</c>).</summary>
    public const string SiteDeleted = "site.deleted";

    /// <summary>A person installed a solution package as a new team. Subject is the team; detail
    /// names the package id and version, the members, triggers, skills, sites and plugins it made.
    /// Written in the same transaction as the team's <c>team_solutions</c> row.</summary>
    public const string SolutionInstalled = "solution.installed";

    /// <summary>A person updated a team to a newer version of the package it came from. Detail names
    /// both versions and what was added, changed and removed. Written with the <c>team_solutions</c>
    /// row it rewrites.</summary>
    public const string SolutionUpdated = "solution.updated";

    /// <summary>An install or update failed at a step and what it had made was undone. Detail names
    /// the step, the reason and what was undone.</summary>
    public const string SolutionFailed = "solution.failed";

    /// <summary>A person uninstalled the solution a team was installed from. Subject is the team;
    /// detail names the package id and version, the triggers, members, skills and sites removed,
    /// whether the tools folder went, the plugins removed and kept (with the teams still using
    /// them), the documents folder kept, and anything that could not be removed. Written with the
    /// deletion of the team's <c>team_solutions</c> row.</summary>
    public const string SolutionUninstalled = "solution.uninstalled";

    // ONLY ACTIONS SOMETHING WRITES ARE LISTED HERE. A tenant-log action nobody writes is a row type
    // that can never appear. Rows carrying a verb not listed here still read: this table is
    // append-only and the verbs in it can outlive the code that wrote them, which is exactly why
    // they are strings rather than an enum.
}

/// <summary>
/// The tenant log: who did what, administratively.
///
/// NOT <see cref="IMessageLog"/>, and the separation is deliberate. That one is the causal stream
/// Agent Containers publish to and subscribe from; every type in it is <c>container.*</c>, every row
/// carries a correlation and a cause, and a manager can be woken by one. Nothing subscribes to this,
/// nothing wakes on it, and it has no correlation - an administrative act is not work.
///
/// APPEND-ONLY, and it outlives its subjects: the row saying a team was deleted has to survive the
/// team, and the row saying an account was removed has to survive the account. There is no delete on
/// this interface for the same reason there is none on the message log.
///
/// <b>Never record a secret here.</b> Every person can read it and it is kept forever, which
/// makes it the worst place in the system for a credential, a password hash, or an Agent
/// definition's <c>env</c>.
/// </summary>
public interface ITenantLog
{
    /// <summary>
    /// Records one act. Takes the actor's EMAIL as well as their id so the row still reads after the
    /// account is gone.
    /// </summary>
    Task WriteAsync(
        string? actorId,
        string? actorEmail,
        string action,
        string? subject = null,
        string? subjectName = null,
        string? detail = null,
        CancellationToken ct = default);

    /// <summary>
    /// The most recent row for one (action, subject) pair, or null when there is none.
    ///
    /// DELIBERATELY GENERAL rather than a feature-shaped lookup. This log records administrative acts
    /// of many kinds, and one feature's query living on everyone's interface would invite the next
    /// feature that wants "the latest X about Y" to add a second.
    ///
    /// NULL IS AN ANSWER, not an error: nothing has been recorded for that pair.
    ///
    /// MOST RECENT WINS. A repeated act writes a new row and the newer one is the one read back.
    /// Rows are never updated or removed - this log exists to answer questions about things that
    /// are gone.
    /// </summary>
    Task<TenantEvent?> FindLatestAsync(
        string action,
        string subject,
        CancellationToken ct = default);

    /// <summary>
    /// The most recent row for each (action, subject) pair in <paramref name="actions"/>.
    ///
    /// DELIBERATELY GENERAL for the same reason as <see cref="FindLatestAsync"/>: this is "latest
    /// by pair" as a log operation, not a feature-shaped read. Taking a set keeps callers from
    /// looping the singular and repeating one query shape N times.
    ///
    /// Subject MUST be non-null. Rows without a subject answer a different question ("something
    /// happened"), and mixing them here would collapse distinct acts onto one null key.
    ///
    /// Empty actions returns empty and does not hit storage. "No actions asked for" is a complete
    /// answer, not an error.
    /// </summary>
    Task<IReadOnlyList<TenantEvent>> FindLatestBySubjectAsync(
        IReadOnlyCollection<string> actions,
        CancellationToken ct = default);

    /// <summary>
    /// One page, most recent first: rows with seq below <paramref name="before"/>, or from the
    /// newest when it is null, with the TOTAL beside it as a caption.
    /// </summary>
    /// <remarks>
    /// A SEQ CURSOR, not an offset. Rows are only ever APPENDED and this reads newest-first,
    /// so under an offset anything written while somebody is scrolling shifts every older row
    /// one place back and the seam between two pages shows a row twice. A cursor is stable
    /// against that, and an infinite list never needed "how many pages". The next page's cursor is
    /// the last row's seq. <paramref name="take"/> is clamped to 1..<see cref="MaxTake"/>.
    /// </remarks>
    Task<TenantLogPage> ReadAsync(long? before = null, int take = 50, CancellationToken ct = default);

    /// <summary>The most rows one <see cref="ReadAsync"/> answers.</summary>
    public const int MaxTake = 200;
}
