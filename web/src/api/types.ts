/**
 * The Host's API contract, as the browser sees it.
 *
 * Declared here rather than generated, and kept honest the same way the CLI's
 * copies are: these shapes are exercised against the real API every time the
 * board renders, and a drift shows up immediately rather than silently.
 */

export type ContainerState = 'Idle' | 'Running'

export interface ContainerSnapshot {
  /** The team this container belongs to. Identity is the PAIR (team, id) — ids are not
   *  unique on their own, and matching on id alone lands one team's snapshot on another's card. */
  team: TeamId

  /** The IDENTIFIER. Half of the pair, and what every event type and path is built from.
   *  Branded: see {@link MemberId} for the defect that made it so. */
  id: MemberId

  /**
   * What a person calls this member - the LABEL. Equal to `id` for a member nobody gave a separate
   * name, which is what makes confusing the two easy to miss. Render this; address with `id`.
   *
   * Deliberately a plain `string`: it is not an identifier, and the point of branding `id` is
   * that this cannot be passed where one belongs.
   */
  name: string

  agent: string
  state: ContainerState
  queueDepth: number
  ceiling: number
  subscribes: string[]

  /** The workflow this container is currently working on. Null when idle. */
  currentCorrelation: number | null

  /**
   * The log's head when this container was created. Anything at or below it belongs to whatever
   * came before and is not this container's business — the feed filters by it, for the same reason
   * the ledger does.
   */
  sinceSeq: number

  /**
   * The Agent this container names, when the catalog has no such Agent — null when it resolves.
   * Reachable because the catalog is a file that is not restored with the database: an Agent can be
   * removed out from under a container that already exists.
   */
  missingAgent?: string | null

  /** This team's ROOT FOLDER, when the Host could not reach it at startup — `null` when it is
   *  there. A team can be placed on another volume or a share, and a machine that reboots with that
   *  drive unplugged has a team whose files are simply not present.
   *
   *  Unlike the two marks above there is nothing on a settings screen that fixes it: a team's root
   *  has no setter, so the recovery is reconnecting the folder and restarting the Host. The member
   *  REFUSES to run meanwhile — an agent with no workspace would otherwise run in whatever
   *  directory the Host itself was started from. */
  unreachableRoot?: string | null

  /**
   * Why this container stopped without finishing — `null` when it did not.
   *
   * Set when the container publishes `container.blocked` and cleared by its next wake, so it
   * OUTLIVES the run it belongs to. That is the point: a member that gave up and one that
   * delivered are both idle with exit code 0, and without this the only way to tell them apart
   * would be to read the message log.
   *
   * Not a `ContainerState`, and not derived from the feed either — a card holds a SLICE of
   * messages, so a member that narrated its run would scroll its own blocked row out of the window
   * and the mark would come and go.
   */
  blocked?: string | null

  /**
   * Why the PLATFORM did not complete this member's last run — `null` when it did.
   *
   * Shaped exactly like `blocked` above and set the same way: written where `container.failed` is
   * published, cleared by the next wake on the line beside it, pushed rather than merely written,
   * and never derived client-side from the feed. Read that field's documentation before changing
   * either.
   *
   * DIFFERENT FROM `blocked` IN WHO IS SPEAKING. `blocked` is the AGENT saying its run finished
   * and it is giving up on the work; this is the PLATFORM saying the run did not finish at all — a
   * launch error, a non-zero exit, a host restart mid-flight.
   *
   * Without it, a run that failed would leave a snapshot indistinguishable from a run that
   * succeeded - both end `Idle` - and a team whose every run failed would render as healthy idle
   * cards with nothing to do.
   *
   * WHEN THIS AND `blocked` ARE BOTH SET THE CARD SAYS FAILED — a run can leave both, and the
   * ranking is `failed` › `blocked` › `needsDecision`, the same order the tab chip uses. The two
   * surfaces must agree or a chip and the card beneath it disagree about one member.
   */
  failed?: string | null

  /**
   * What this container stopped to ASK — `null` when it is not waiting on anybody.
   *
   * Shaped exactly like `blocked` above and set the same way: written where `container.needs-decision`
   * is published, cleared by the next wake on the line beside it, pushed rather than merely written.
   * Read that field's documentation before changing either.
   *
   * THE POINT OF IT IS THAT IT IS NOT A FAILURE. `blocked` says the agent gave up and `failed` says
   * the platform did not finish a run; this says a run ended WELL and the work cannot continue
   * without a person. A card carrying it is not a card to go and investigate — it is a card with a
   * question on it, addressed to whoever is reading the board.
   *
   * Without it, a manager that stopped on purpose to ask questions could only publish
   * `container.completed` with exit code 0 — and `completed` is the worst of the three to be wrong
   * about, since the seeded `recovery` skill reads it as "that step is DONE, dispatch the step
   * AFTER it".
   */
  needsDecision?: string | null

  /**
   * WHAT THIS MEMBER LAST HANDED BACK — the words on its most recent `agentContainer.handback`, or
   * `null` when it has never handed anything back.
   *
   * NOT SHAPED LIKE THE THREE ABOVE, and that is the decision rather than an oversight. `blocked`,
   * `failed` and `needsDecision` are MARKS OF TROUBLE, each cleared at the member's next wake
   * because a warning that outlives the job it warned about shows a team in trouble that is not.
   * A DELIVERY DOES NOT GO STALE: this member really did hand that back, and it stays true after it
   * is given something else to do. Cleared on the next wake it would vanish exactly when it matters
   * — a hand-back's whole purpose is to wake somebody who then sends more work, so the reply would
   * take the record of what it was replying to with it.
   *
   * So it is a LAST-X FIELD, replaced by the next hand-back and never cleared, which is why every
   * reader words it `last handback` rather than as a present-tense condition: it can appear beside
   * a member that is Running something else entirely.
   *
   * IT IS NOT A {@link ContainerMark}. Nobody has to do anything about a card that was handed back,
   * and the three-way ranking `failed` › `blocked` › `needsDecision` is a ranking of things that
   * need attention. See `containerMark` for the rest of that argument.
   */
  handedBack?: string | null

  /** The tag this member was hired under, or null when none was named. */
  hiredFor?: string | null

  /**
   * WHETHER THIS MEMBER CAN BE WATCHED: its agent's preset has a `liveView` the Host can
   * read. True whether the member is running or idle - an idle member's earlier runs are still
   * there to read - and the card's eye follows it. False, or absent, shows no
   * eye rather than one that opens onto "no live view".
   */
  watchable?: boolean

  /**
   * WHAT KIND OF MEMBER THIS IS: `agent` for a coding-agent CLI, `plugin` for an installed plugin
   * executable (its `agent` is then `plugin:<id>`). Additive; absent from an older Host, which
   * only ever hosted agents.
   */
  kind?: 'agent' | 'plugin'

  /** Presence of a chosen Agent when this snapshot was created or patched: present on the write
   *  response if a chosen Agent did not resolve at the time. This is a WARNING - the write SUCCEEDED. */
  unresolvedAgents?: UnresolvedAgent[]

  /**
   * WHAT KIND of failure `failed` was — `quota`, `rate`, `transport`, `agent-fault`, `launch-missing`,
   * `out-of-memory`, `crashed`, `timeout`, `interrupted` or `unknown` — and `null` when this member's last run did not fail.
   *
   * A SECOND VALUE ON ONE MARK, not a second mark. It is set and cleared with `failed` on the same
   * lines, because a card wearing a class from one run beside a reason from another would be worse
   * than a card with no class at all. Read `failed` before changing either.
   *
   * MAY BE ABSENT, and then the card renders the reason alone.
   */
  failureClass?: string | null

  /**
   * WHEN THE PLATFORM WILL RESUME THIS MEMBER'S WORKFLOW BY ITSELF, as an ISO-8601 instant — `null`
   * when it will not, which is the ordinary case and every class but `quota` and `rate`.
   *
   * VISIBLE IS A REQUIREMENT rather than polish: a silent
   * automatic retry is how a quota failure becomes a spend failure, and this is the field a person
   * reads to see one coming. The server WITHDRAWS it — sets it back to null — the moment the bound
   * is spent or the member moves on, so a card never promises a resume nothing is going to fire.
   */
  resumeAt?: string | null

  /**
   * TRUE WHILE THIS MEMBER'S CLAIM ON A WIP SLOT IS WAITING - it has work to start and the
   * instance-wide limit is full. Waiting, never failed: nothing is refused, the
   * run starts when a slot is released. Absent is not held.
   */
  held?: boolean
}

/**
 * The STORED row behind a member, as `GET /api/teams/{team}/containers/{name}` answers it - not
 * `ContainerSnapshot`, deliberately: that travels on every SignalR push. What a member is TOLD is
 * not part of it: the prompt is chosen by role from the build.
 */
export interface MemberDetail {
  team: TeamId
  id: string
  name: string

  /** The member's OWN INSTRUCTIONS, added after its built-in role prompt and before the team
   *  instructions. Null when it has none. */
  systemPrompt?: string | null

  /** WHO LAST SET `systemPrompt`: the hiring Manager's member id, or a person's identity. Null for
   *  a member from before this was recorded, until it is next edited. */
  systemPromptSetBy?: string | null

  /** Whether `systemPromptSetBy` is the Manager that hired it or a person. */
  systemPromptSetByKind?: 'manager' | 'person' | null

  /** When `systemPrompt` was last set, ISO-8601 UTC. */
  systemPromptSetAt?: string | null
}

/**
 * A team's IDENTIFIER, distinct from its name at the type level.
 *
 * Both are strings, and that is exactly the problem this solves: passing a team's NAME where the
 * identifier belongs works for every team whose name EQUALS its id - which is every team until
 * somebody renames it - and then 404s, or selects nothing for a name with a space in it. A fixture
 * whose name defaults to its id cannot catch that.
 *
 * The brand is compile-time only — it does not exist at runtime and costs nothing on the wire. A
 * plain string cannot be passed where a `TeamId` is wanted, so the mistake becomes a type error at
 * the call site instead of a 404 in front of somebody.
 *
 * Use {@link asTeamId} at the edges where a raw string genuinely IS an identifier: a value read back
 * from storage, or one that arrived in a route. Nowhere else.
 */
export type TeamId = string & { readonly __brand: 'TeamId' }

/** Asserts that a raw string is a team identifier. Only for values arriving from outside the app —
 *  storage, a route, a URL. Never to quiet a type error at an ordinary call site: there, the error is
 *  telling you a name reached something that wanted an id. */
export const asTeamId = (value: string): TeamId => value as TeamId

/**
 * THE KEY A DOCUMENTS ROUTE IS ADDRESSED WITH, which is NOT a team identifier.
 *
 * See {@link DocumentsFolder.folder}. Branded for {@link TeamId}'s reason and after the same shape
 * of defect: for the live folder of a live team the two strings are equal, so passing the wrong one
 * works on every folder anybody would reach for first and fails only on a retired one - where it
 * does not 404, it opens the successor team's documents.
 */
export type DocumentsFolderKey = string & { readonly __brand: 'DocumentsFolderKey' }

/** Asserts that a raw string is a documents folder key. Only for a value that arrived from the
 *  server already being one - never to quiet a type error where a `TeamId` reached it, which is
 *  the error doing its job. */
export const asDocumentsFolderKey = (value: string): DocumentsFolderKey =>
  value as DocumentsFolderKey

/**
 * A MEMBER'S IDENTIFIER, branded for the reason {@link TeamId} is, one level down.
 *
 * A container has two names too: `ContainerSnapshot.id` is the identifier every route and event
 * type is built from, and `ContainerSnapshot.name` is the LABEL a person reads. Passing the label
 * to a route - `Researcher Rhea` producing `/containers/Researcher%20Rhea/stop` - is refused by
 * `ContainerId.IsLegalName` for the space.
 *
 * THE MISTAKE HIDES THE WAY THE TEAM ONE DOES. A member's label EQUALS its id until somebody gives
 * it a different one, and the seeded `Manager` never gets one - so a label-addressed call works on
 * the member anyone would try first, and fails for every hired member, whose label carries a
 * space by the naming rule. Most fixtures build members whose label is their id.
 *
 * It is `MemberId` and not `ContainerId` on purpose: C#'s `ContainerId` is the QUALIFIED pair
 * `Team/Name`, and this is only ever the bare second half. The endpoints and the UI both say
 * member - `getMember`, `deleteMember`, the Members tab - so the word that travels is the word
 * used here.
 *
 * Compile-time only. It does not exist at runtime and costs nothing on the wire; what it buys is
 * that passing a label where an identifier belongs is a type error at the call site, and CI runs
 * `npm run typecheck`, so the gate is real rather than a local courtesy.
 */
export type MemberId = string & { readonly __brand: 'MemberId' }

/** Asserts that a raw string is a member identifier. Only for values arriving from outside the app
 *  - the API, storage, a route. Never to quiet a type error at an ordinary call site: there, the
 *  error is telling you a LABEL reached something that wanted an identifier. */
export const asMemberId = (value: string): MemberId => value as MemberId

/**
 * One of your own API keys. There is no `credential` field and there never can be: only a SHA-256
 * hash is stored, so the value existed once, in the response to the mint that created it.
 */
export interface ApiKey {
  id: string
  label: string | null
  prefix: string
  createdAt: string
  lastUsedAt: string | null
}

/** The mint response, and the ONLY shape that carries a credential. */
export interface MintedKey {
  id: string
  label: string
  prefix: string
  createdAt: string
  credential: string
}

/**
 * A roster row. There is no team list on it: every key reaches every team its owner does, and
 * every person reaches every team, so a list here would be right today and wrong the moment
 * somebody creates a team. There is no tier on the owner either; every person is an administrator.
 */
export interface TenantApiKey extends ApiKey {
  owner: { id: string; email: string | null }
}

/** What a team deletion actually removed. `failures` is the half worth rendering: a directory that
 *  could not be deleted usually means a file still held by a child that has not finished exiting,
 *  and the team is gone either way. */
export interface TeamDeleted {
  team: string
  containers: number
  pendingDeliveries: number
  schedules?: number
  directories: string[]
  failures: string[]

  /**
   * Every path under the team's root that could not be removed, one by one. Empty when the root
   * went. When it is not, the root keeps its `.harness-team` marker, is recorded as a removal
   * unfinished, and is retried at the Host's next start or by `retryRemoval`.
   */
  remaining?: string[]

  /** The root recorded as a removal unfinished — the path `retryRemoval` names — or null. */
  removalUnfinished?: string | null

  /**
   * The team's documents, WHICH THIS DELETION KEPT. Said out loud rather than left to be noticed,
   * because a team is deleted as soon as its work is merged - exactly when its reports become the
   * only record of how that work was checked.
   *
   * A path, not a boolean, so the sentence shown can name the folder a person can go and open.
   * Null only when the deletion could not establish where they are, which is the same condition
   * that stops it removing the root at all.
   */
  documentsKept: string | null

  /** The `local:<name>` repositories the team had, which were KEPT; delete them in Admin -> Repositories. */
  localRepositoriesKept?: string[]

  /** The `local:<name>` repositories the person ticked, deleted after the team. */
  localRepositoriesDeleted?: string[]

  /**
   * A ticked local repository that could not be deleted, and why. The team is deleted either way;
   * the repository is also in `localRepositoriesKept`, and is finished in Admin -> Repositories.
   */
  localRepositoryFailures?: { reference: string; reason: string }[]

  /** How many agent CLI session folders keyed to the team's workspaces were removed. */
  sessionFolders?: number

  /** Every path of those session folders still on disk; each is also in `failures`. */
  sessionFoldersRemaining?: string[]
}

/**
 * One team documents folder that exists on disk, from `GET /api/documents`.
 *
 * **THE FOLDERS, NOT THE TEAMS.** A document outlives the team that wrote it, so this list is
 * strictly larger than the list of teams and the difference is the work that would otherwise have
 * been lost with them.
 */
export interface DocumentsFolder {
  /**
   * ADDRESS WITH THIS. Its name on disk, and the value to put in `/api/teams/{team}/documents` to
   * browse it. For a live team this IS the team identifier; for a retired folder it is not, and
   * never can be.
   *
   * BRANDED for the reason {@link TeamId} is branded, one level out: the two strings are EQUAL for every folder anybody would try first, so getting
   * it wrong works until the day it silently does not. Addressing a retired folder by `team`
   * reaches the SUCCESSOR's documents - a 200, the wrong files, and nothing saying so.
   */
  folder: DocumentsFolderKey

  /** RENDER THIS, never address with it. The team whose documents these are - it survives the
   * retirement suffix, so a folder is never shown anonymously. */
  team: TeamId

  /** What that team is called, for a team the registry may never have heard of. */
  label: string

  /**
   * Whether this is the LIVE folder of a team that still exists. False for a retired folder
   * whatever the team list says: a folder is retired BECAUSE a later team took its identifier, so
   * a team of that name exists by construction and is a different team.
   */
  exists: boolean

  /** Whether a later team of the same id has claimed the name, moving this folder aside. A retired
   * folder is reachable from the Documents screen by any signed-in person. */
  retired: boolean

  /** Immediate children, excluding the platform's own marker. -1 when the Host could not read the
   * folder, which is said rather than hidden. */
  entries: number

  modifiedAt: string
}

/** What a clone carried, and what it could not. The team exists either way. */
/**
 * One row inside a documents folder. Here rather than in `api/documents.ts` since the answers of
 * the change routes carry it too; that file re-exports it.
 */
export interface DocumentEntry {
  name: string
  path: string
  isFolder: boolean
  size: number
  modifiedAt: string
  /** How many things are in a folder. What makes it removable, so the UI can say so up front. */
  children: number
}

/**
 * A NAME CLASH ON MOVE, COPY OR UPLOAD, answered per item. Absent means "ask": the server answers
 * 409 with `clashes` and does nothing, and the person chooses.
 */
export type OnClash = 'keep-both' | 'replace' | 'skip'

export interface DocumentsRenameItem { path: string; name: string }

export interface DocumentsTransferItem { path: string; onClash?: OnClash }

/** A move or copy: every item from the folder the route names, into `to`. */
export interface DocumentsTransfer {
  to: { folder: DocumentsFolderKey; path: string }
  items: DocumentsTransferItem[]
}

export interface DocumentLeft { path: string; reason: string }

/** One item of a rename, move or copy, as the server did it. `to` is relative to the destination
 *  folder, with any keep-both name already applied. */
export interface DocumentsChangeResult {
  from: string
  to: string
  outcome: 'done' | 'skipped' | 'failed'
  reason?: string | null
  notCopied?: DocumentLeft[] | null
}

export interface DocumentsClash { from: string; to: string; isFolder: boolean }

/**
 * WHAT A RENAME, MOVE OR COPY ANSWERED, as data rather than a throw: every item done or skipped,
 * a clash nobody chose for (nothing done), or a batch that partly failed (the sentence and every
 * item). A plain refusal is still thrown with the server's sentence.
 */
export type DocumentsChangeAnswer =
  | { kind: 'ok'; results: DocumentsChangeResult[] }
  | { kind: 'clash'; error: string; clashes: DocumentsClash[] }
  | { kind: 'partial'; error: string; results: DocumentsChangeResult[] }

/** What an upload that named `onClash` answered. */
export type DocumentsUploadAnswer =
  | { kind: 'saved'; entry: DocumentEntry }
  | { kind: 'skipped'; path: string }
  | { kind: 'clash'; error: string; clashes: DocumentsClash[] }

/** Where an uploaded folder or unpacked .zip landed: the folder made (its keep-both name shows
 *  here) and every file written. */
export interface DocumentsPackageSaved {
  path: string
  name: string
  isFolder: true
  files: string[]
  size: number
}

/** What a folder or .zip upload answered. A refusal (an unsafe zip, a bad path) is thrown with
 *  the server's sentence. */
export type DocumentsPackageAnswer =
  | { kind: 'saved'; saved: DocumentsPackageSaved }
  | { kind: 'skipped'; path: string }
  | { kind: 'clash'; error: string; clashes: DocumentsClash[] }

export interface TeamCloned {
  team: Team
  repos: number
  envKeys: number
  members: number
  failures: string[]
}

/**
 * What a reset is being asked to do.
 *
 * Every flag is OPTIONAL, and absent means false for all of them EXCEPT `forgetHistory`, whose
 * server-side default is TRUE — naming members and saying nothing else is a reset. So the reset
 * nobody thought carefully about is the one that only moves floors, which is the reversible half;
 * `purge` is the one that destroys something an audit could have used.
 */
export interface TeamResetRequest {
  members: string[]
  forgetHistory?: boolean
  purge?: boolean
  clearWorkspaces?: boolean
  clearTranscripts?: boolean
  clearSharedDocuments?: boolean
  /** Remove the named members' worktrees and merged or empty branches, and with EVERY member
   *  named reset `team/<id>` to the stored default branch. Never forced; what would lose work is
   *  kept and named in `repositories`. */
  resetRepositories?: boolean
}

/** A worktree (its path) or a branch (its name) a repository reset names, with its member (null for
 *  the team branch) and why it was kept or what was done to it. */
export interface RepoResetItem {
  repo: string
  name: string
  member?: string | null
  reason?: string | null
}

/** What Reset repositories did: each tree and branch removed, each kept with the reason. */
export interface RepositoriesReset {
  worktreesRemoved: RepoResetItem[]
  worktreesKept: RepoResetItem[]
  branchesDeleted: RepoResetItem[]
  branchesKept: RepoResetItem[]
  teamBranchReset: RepoResetItem[]
  teamBranchKept: RepoResetItem[]
}

/**
 * What Reset repositories would remove, for EVERY member of the team (`GET .../reset/repositories`).
 * The dialog shows the ticked members' lines, and `teamBranch` when every member is ticked.
 * `defaultBranchNotKnown` names each repository for which that team-branch reset is refused.
 */
export interface RepositoryResetPreview {
  worktrees: RepoResetItem[]
  branches: RepoResetItem[]
  teamBranch: string
  defaultBranchNotKnown: string[]
}

/**
 * What a reset actually did.
 *
 * `retained` is not a failure: a message another team's surviving row cites cannot be purged
 * without destroying that row's causation. `failures` names a directory that could not be emptied —
 * usually a file still held by a child that has not finished exiting — and the team is reset either
 * way, so both are worth rendering and neither is an error.
 */
export interface TeamWasReset {
  team: string
  floor: number
  floored: string[]
  purged: number
  retained: number
  cleared: string[]
  failures: string[]

  /** Every path a clear could not remove. Recorded and retried; a retry removes only these. */
  remaining?: string[]

  /** What Reset repositories removed and kept; absent or null when it was not asked. */
  repositories?: RepositoriesReset | null
}

/** What an unfinished removal was removing: a deleted team's root, a deleted member's workspace,
 *  or a folder a Reset emptied and kept (`RemovalKinds` on the Host). */
export type RemovalKind = 'team-root' | 'workspace' | 'emptied'

/**
 * One folder whose removal did not finish, as `GET /api/removals` lists it: every path still on
 * disk at its last attempt, when it was first recorded, and how many attempts it has had. `member`
 * is set only for a workspace.
 */
export interface UnfinishedRemoval {
  path: string
  kind: RemovalKind
  team: string
  member: string | null
  remaining: string[]
  recordedAt: string
  attempts: number
}

/** One unfinished removal retried: whether it `finished`, what is still `remaining`, and why it
 *  was set aside when it was (`note`). */
export interface RemovalRetried {
  path: string
  kind: RemovalKind
  team: string
  member: string | null
  finished: boolean
  remaining: string[]
  note: string | null
}

export type TriggerKindWire = 'cron' | 'every' | 'once' | 'event' | 'folderChange'

/**
 * What `POST /api/teams/{team}/triggers/{id}/run` did: the fire the schedule makes, `fired`, or
 * `skipped` (paused team, busy idle-only member), `capped` (its daily cap), `member-missing`.
 * `trigger` is the row after it, as the list routes return it.
 */
export interface TriggerRunNowResult {
  outcome: 'fired' | 'skipped' | 'capped' | 'member-missing'
  reason: string | null
  seq: number | null
  trigger: TeamTrigger
}

export interface TeamTrigger {
  id: string
  team: TeamId
  container: string
  name: string
  instruction: string
  kind: TriggerKindWire
  expression: string | null
  timezone: string | null
  intervalSeconds: number | null
  fireAt: string | null
  idleOnly: boolean
  enabled: boolean
  nextDueAt: string | null
  lastFiredAt: string | null
  lastOutcome: string | null
  lastSeq: number | null
  missedCount: number
  createdAt: string
  createdBy: string

  /** Which event type fires this trigger. Null for a clock-driven one. */
  eventType: string | null

  /** `field op value`, evaluated before the wake is enqueued. Null means every message of that
   *  type. */
  filter: string | null

  /**
   * The folder-change fields. Null for every other kind. OPTIONAL ON THE CLIENT ONLY, so a row
   * that omits them still reads.
   *
   * `watchRoot` is `documents` or `root:<file-browser root name>`; `watchPath` is relative to it,
   * and `""` is the root itself. A folder trigger's `eventType` is always `file.changed`, set by the
   * server; `lastFiredAt` is when that event last WOKE the member, `lastChangeAt` when the trigger
   * last PUBLISHED it.
   */
  watchRoot?: string | null
  watchPath?: string | null
  watchGlob?: string | null
  pollSeconds?: number | null
  quietSeconds?: number | null
  minIntervalSeconds?: number | null

  /** When the folder was last listed, how long that took in milliseconds, how many entries it saw,
   *  and the sentence when it could not list. A network share is slow to list; the row shows this so
   *  a person can see it. */
  lastPollAt?: string | null
  lastPollMs?: number | null
  lastPollEntries?: number | null
  lastPollError?: string | null
  lastChangeAt?: string | null

  /** What a run this trigger started does to the Manager when it ends. OPTIONAL ON THE CLIENT ONLY,
   *  so a row from a Host that predates it still reads; absent is read as `always`, today's
   *  behaviour, which is what every trigger made before the setting keeps. */
  wakeManager?: TriggerWakeManager

  /** The most billable tokens this trigger's runs (and the Manager runs they woke) may spend in one
   *  day, in the trigger's timezone. Null is no cap. */
  dailyTokenCap?: number | null

  /** The outcome this trigger's fires serve, by id, or null. Absent from an older server. */
  outcomeId?: string | null

  /** What this trigger's runs spent today, measured only. Read-only. */
  spentToday?: TriggerSpentToday | null

  /** The cap was reached today, so fires are skipped until the next day. Read-only. */
  capReachedToday?: boolean

  /** A schedule asleep on its daily cap: the instant it resumes (its first occurrence of the next
   *  day in its timezone). Null or absent when the cap is not holding it. Read-only. */
  cappedUntil?: string | null

  /** How many fires the daily cap skipped today. Only the first was logged. Read-only. */
  skippedToday?: number
}

/**
 * Whether a run a trigger started wakes the Manager when it ends: `onHandbackOrFailure` (the default
 * for a new trigger) wakes it only on a hand-back or a failure, `always` on every completion, and
 * `never` not even on a failure - the failure still shows on the card and in the feed.
 */
export type TriggerWakeManager = 'always' | 'onHandbackOrFailure' | 'never'

/**
 * A trigger's spend today. `billableTokens` sums only the MEASURED runs; `unmeasuredRuns` are runs
 * that reported no usage, and they are counted as such - never as zero.
 */
export interface TriggerSpentToday {
  billableTokens: number
  measuredRuns: number
  unmeasuredRuns: number
}

/**
 * What a member's recent runs actually cost, from its last `lastRuns` finished runs. The median is
 * over the measured ones only, and null when none was measured - nothing is estimated.
 */
export interface MemberMeasuredCost {
  lastRuns: number
  measuredRuns: number
  unmeasuredRuns: number
  medianBillableTokens: number | null
  kind: 'agent' | 'plugin'
}

export interface CreateTriggerRequest {
  name?: string | null
  container?: string | null
  instruction?: string | null
  kind?: TriggerKindWire | null
  expression?: string | null
  timezone?: string | null
  intervalSeconds?: number | null
  fireAt?: string | null
  idleOnly?: boolean | null
  enabled?: boolean | null
  nextDueAt?: string | null
  eventType?: string | null
  filter?: string | null
  watchRoot?: string | null
  watchPath?: string | null
  watchGlob?: string | null
  pollSeconds?: number | null
  quietSeconds?: number | null
  minIntervalSeconds?: number | null
  wakeManager?: TriggerWakeManager | null
  dailyTokenCap?: number | null
  /** The outcome its fires serve: an active or proposed outcome's id or exact name. */
  outcomeId?: string | null
}

export interface UpdateTriggerRequest {
  name?: string | null
  container?: string | null
  instruction?: string | null
  kind?: TriggerKindWire | null
  expression?: string | null
  timezone?: string | null
  intervalSeconds?: number | null
  fireAt?: string | null
  idleOnly?: boolean | null
  enabled?: boolean | null
  nextDueAt?: string | null
  eventType?: string | null
  filter?: string | null
  watchRoot?: string | null
  watchPath?: string | null
  watchGlob?: string | null
  pollSeconds?: number | null
  quietSeconds?: number | null
  minIntervalSeconds?: number | null
  wakeManager?: TriggerWakeManager | null
  dailyTokenCap?: number | null
  /** The outcome its fires serve: an active or proposed outcome's id, or '' to clear it. */
  outcomeId?: string | null
}

/**
 * One field an event type's payload carries, from `GET /api/events`. `List` is a JSON array of
 * strings (`file.changed`'s `changed`); a filter or an `{event.*}` token reads it as its raw JSON
 * text, so only `contains` is a useful filter on one.
 */
export interface EventFieldDefinition {
  name: string
  kind: 'String' | 'Integer' | 'Boolean' | 'List'
  summary: string
}

/**
 * One event type this platform can publish, from `GET /api/events` - the catalog a trigger picker
 * is built from. `summary` is the ONE source of human wording for what this event means; a picker
 * or a rendered sentence reads it from here rather than carrying a second, hand-written copy.
 */
export interface EventDefinition {
  type: string
  publisher: 'Platform' | 'Agent' | 'Person'
  highVolume: boolean
  inLedger: boolean
  fields: EventFieldDefinition[]
  summary: string
}

/**
 * What deleting ONE member removed. Its team survives, so this names the member as well as the team
 * — and carries the member's LABEL beside its identifier, because the identifier is the half nobody
 * is ever shown.
 */
export interface MemberDeleted {
  team: string
  member: string
  label: string
  pendingDeliveries: number
  schedules?: number
  directories: string[]
  failures: string[]

  /** Every path in the workspace that could not be removed; recorded and retried. */
  remaining?: string[]
}

/** One page of the tenant log, with the total beside it so a grid can say "page 3 of 47". */
export interface TenantLogPage {
  events: TenantEvent[]
  total: number
}

/** One administrative act. `actorEmail` and `subjectName` are stored ON the row rather than joined,
 *  so an entry still reads after the account or team it names has been deleted — which is most of
 *  the point of the log. */
export interface TenantEvent {
  seq: number
  occurredAt: string
  actorId: string | null
  actorEmail: string | null
  action: string
  subject: string | null
  subjectName: string | null
  detail: string | null
}

/**
 * THE THIRD STORE, AS THE BROWSER SEES IT.
 *
 * `messages` is the causal stream and `tenant_events` is administrative acts; a diagnostic row is
 * neither. Nobody DID these, which is why no row names an actor, and nothing reacts to them, which
 * is why nothing here subscribes. The question it answers is "what was this instance doing when it
 * went wrong", and the reader is a person looking at Admin › Diagnostics after the fact.
 */

/** How loud one row is. THREE VALUES AND NO MORE — a scale nobody can hold in their head is one
 *  every writer picks from at random, and this store is read by filtering on exactly this column.
 *  Sent as the NAME, never the number: the host serialises enums as names on both transports. */
export type DiagnosticSeverity = 'Error' | 'Warning' | 'Info'

/**
 * One diagnostic row.
 *
 * FOUR FIELDS ARE FIRST-CLASS RATHER THAN BURIED IN `detail` — `kind`, `severity`, `route` and
 * `exceptionType` — because they are what the screen FILTERS on, and a filter that has to parse
 * JSON is one no screen will offer.
 *
 * `route` is the TEMPLATE (`/api/teams/{team}/pause`), never the raw URL: a template carries no
 * query string and no path value, which is where a credential pasted into a URL would be.
 */
export interface DiagnosticEvent {
  seq: number
  occurredAt: string
  severity: DiagnosticSeverity
  kind: string
  source: string | null
  route: string | null
  status: number | null
  exceptionType: string | null
  message: string | null

  /** Whatever else the kind carries, as JSON, redacted at the write. */
  detail: string | null
}

/** One page of the diagnostics log, and how many rows MATCH THE FILTER — not how many rows there
 *  are. A grid paging a filtered read needs the filtered count. */
export interface DiagnosticsPage {
  events: DiagnosticEvent[]
  total: number
}

/**
 * What the screen is asking for. Every field is optional and absent means "do not narrow on this".
 *
 * `search` is matched case-insensitively against `message`, `detail`, `route` and `exceptionType`,
 * with `%` and `_` taken literally. It deliberately does NOT reach `kind` or `severity`: those have
 * filters of their own, and a box that also matched them would make the typed filter and the typed
 * search disagree about the same word.
 */
export interface DiagnosticsFilter {
  /** From the CLOSED vocabulary the screen offers as a list. A kind outside it is refused by the
   *  server rather than matching nothing, because an empty page is this screen's most misleading
   *  answer. */
  kinds?: string[]
  severities?: DiagnosticSeverity[]
  from?: string
  to?: string
  search?: string
}

/**
 * A page of the log with the answer to "why is this empty" beside it.
 *
 * THE TWO TRAVEL TOGETHER BECAUSE THE SCREEN CANNOT RENDER EITHER ALONE. An empty page has three
 * meanings and `hasAny` is the single fact that tells them apart:
 *
 * - `hasAny === false` — nothing was ever captured. On a host that writes its startup facts at
 *   every boot, that is itself a fault.
 * - `hasAny === true` with no rows — the store is working and this filter matched nothing. Nothing
 *   went wrong.
 * - `hasAny === null` — the store could not be read. `(unknown)` is a real state in this product,
 *   and rendering it as "empty" would tell the reader a broken instance is a healthy one.
 */
export interface DiagnosticsView {
  page: DiagnosticsPage
  hasAny: boolean | null

  /** The closed kind vocabulary, SERVED rather than copied — the screen's kind filter is built from
   *  this and never from a list maintained here. A second hand-kept copy drifts from the first;
   *  `/api/events` serves the trigger picker's vocabulary the same way. */
  kinds: string[]
}

/**
 * One container start and the version each agent CLI reported at it. `null` is a CLI
 * that was not installed at that start. Recorded onto the data volume by the start script, so the
 * history follows the volume; a host started outside the container records nothing.
 */
export interface CliVersionsAtStart {
  at: string
  versions: Record<string, string | null>
}

export interface Team {
  /**
   * The IDENTIFIER. Keys the documents folder on disk and half of every container's identity, so
   * it is what every call, path and event type is built from — and it never changes.
   */
  id: TeamId

  /**
   * What a person reads. Equal to `id` until the team is relabelled, and only ever the name moves.
   * Render this; address with `id`. Getting it backwards means a relabelled team's Concierge
   * binds to a team that does not exist.
   */
  name: string

  /**
   * Which Agent preset the tenant-wide Concierge launches. Changed through
   * `PUT /api/concierge`, and only ever an INTERACTIVE preset — a headless one has no
   * command that can be typed at, so it would open a terminal that dies immediately.
   *
   * `null` when nobody has chosen one: the launcher then picks an installed, signed-in preset,
   * which `GET /api/concierge` names under `effective`. Never render this raw.
   */
  concierge: string | null

  /**
   * Which Agent presets this team's NEW members may run, in ORDER.
   *
   * The order is load-bearing: when multiple entries match a requested tag, earlier entries win the
   * tie. A team with no entries has chosen nothing and cannot hire.
   */
  memberAgents: string[] | null

  /**
   * Words a person wrote for this team, APPENDED after the built-in role prompt for its Manager and
   * members under a heading of their own. Never a replacement for it: the role prompt comes from
   * the build and no person chooses it. `null` or empty means nothing is appended.
   */
  additionalInstructions: string | null

  /**
   * WHERE THIS TEAM'S FILES ARE — the resolved absolute team folder, whether the team was placed or
   * left in the default location. Read-only: there is deliberately no route that moves an existing
   * team's root, because that means relocating the whole tree and repairing git
   * worktrees.
   *
   * `null` FOR A MACHINE PRINCIPAL, and only `GET /api/overview` fills it at all — an absolute
   * path on the Host's own filesystem is not an agent's business. Every person reads it. Render it
   * only when it is there; its absence is "you may not see this" and never "this team has no
   * folder".
   */
  root: string | null

  /** The ordered Git repository URLs configured for this team. First entry is primary. */
  repos?: string[] | null

  /**
   * Tokens this team may spend on ONE workflow, input and output together - the team's STORED
   * CHOICE, and never what is in force. For that, read {@link effectiveWorkflowBudget}.
   *
   * THREE STATES, AND THE DIALOGS ARE THE ONLY READERS OF THEM. `null` means this team has chosen
   * NOTHING and runs on the instance's own `WorkflowSpendLimit`; `0` means it explicitly chose
   * UNLIMITED; anything above 0 is the figure a person typed. 0 and null are NOT the same answer
   * here, which is the one thing easiest to get wrong: storing 0 as null would, under the rule
   * below, silently turn "unlimited" into the instance figure.
   *
   * Not the same as the instance-wide `WorkflowSpendLimit` backstop, which has no screen on
   * purpose: exactly ONE of the two applies. A team that has chosen a figure runs on that figure,
   * ABOVE the instance's own if that is what was typed - a bound a team could only lower is not
   * offered, because a field that silently refuses what a person typed is a worse lie than a
   * number they can change. The instance figure applies only to a team that has chosen nothing.
   *
   * OPTIONAL ON THE CLIENT ONLY: absent means "the Host did not say", which is not the same as
   * `null`.
   */
  budgetTokens?: number | null

  /**
   * WHAT IS ACTUALLY IN FORCE for one workflow on this team, resolved by the SERVER from
   * {@link budgetTokens} and the instance figure. `null` MEANS UNLIMITED and 0 never appears.
   *
   * THE BROWSER MUST NOT RE-RESOLVE THIS, and `lib/teamBudget.ts` reads only this field. A second
   * resolver in the browser can drift from the pump's, and then a team is stopped by a ceiling
   * that appears on no screen while its tile displays a percentage of a bound that is not
   * operating. A bar that reassures is worse than no bar.
   *
   * OPTIONAL ON THE CLIENT ONLY, for {@link budgetTokens}'s reason. Absent is a Host with
   * nothing to say, where `null` is the Host saying UNLIMITED.
   */
  effectiveWorkflowBudget?: number | null
  /**
   * Each configured repository's default branch, in list order. `branch` is what the host
   * uses (`setByPerson`, else `fromRemote`), null when not known.
   */
  defaultBranches?: TeamRepoDefaultBranch[] | null
  /**
   * Each configured repository's contributor settings, in list order. A repository with an
   * `upstreamUrl` is in contributor mode: its own URL is the fork.
   */
  contributors?: TeamRepoContributor[] | null

  /**
   * This team is paused: new work stays queued and the next queued batch does not start until
   * somebody resumes it.
   *
   * Optional: an absent field reads as unpaused rather than faulting.
   */
  paused?: boolean

  /** Agents that could not be resolved when this team was created or updated: present on the
   *  write response if a chosen Agent did not resolve at the time. This is a WARNING - the write SUCCEEDED. */
  unresolvedAgents?: UnresolvedAgent[]

  /**
   * The solution package this team was installed from, or null/absent for a team made by hand. The
   * delete dialog reads it: deleting the team never removes the package's plugins.
   */
  solution?: TeamSolution | null

  containers: ContainerSnapshot[]
}

/** The package a team was installed from: its id, name, version and the ids of its plugins. */
export interface TeamSolution {
  id: string
  name: string
  version: string
  plugins: string[]
}

/** Which agents a skill is offered to. `any` is every role. */
export type SkillRole = 'concierge' | 'manager' | 'member' | 'any'

/** Built-in skills come from the build and are read-only; custom ones are a person's. */
export type SkillKind = 'builtin' | 'custom'

/** Which kinds `GET /api/skills` lists. The route's default is `custom`. */
export type SkillKindFilter = SkillKind | 'all'

export interface SkillRecord {
  /** The keyset cursor `?before=` pages by - see `listSkillsPage`. Never shown. */
  id: number
  name: string
  description: string
  roles: SkillRole[]
  kind: SkillKind
  body: string

  /** ISO-8601, and who by. Both null for a built-in, which only a build changes. */
  updatedAt: string | null
  updatedBy: string | null

  /**
   * The team a TEAM SKILL belongs to - offered only to that team's members of its roles - or null
   * for an instance-wide skill. Written through `/api/teams/{team}/skills`.
   */
  team?: TeamId | null
}

/** What a create or an edit of a custom skill sends. A built-in is never sent. */
export interface SkillDraft {
  name: string
  description: string
  roles: SkillRole[]
  body: string
}







/**
 * One entry in a `DirectoryListing`, a directory or a file. Folder mode renders files disabled
 * rather than hidden, so a person can see a folder is not empty even when nothing in it is
 * choosable.
 */
export interface HostEntry {
  name: string
  type: 'dir' | 'file'
}

/** The write flags of the root CONTAINING a listed path — whether New Folder or Upload will do
 *  anything here, known before a person tries either. */
export interface HostPermissions {
  allowCreate: boolean
  allowUpdate: boolean
  allowDelete: boolean
}

/**
 * Answers `GET /api/fs/browse` — the Host's own filesystem, files and directories, bounded to the
 * configured allowlist (`GET /api/fs/roots`). Backs the folder picker for `POST /api/teams`'s
 * `root` field and `HostPathPicker.vue` generally; a browser has no other way to name a path on the
 * machine the Host runs on.
 */
export interface DirectoryListing {
  path: string

  /** `null` when `path` is itself the top of its root — an "up" affordance built on this must hide
   *  rather than offer a click the server will refuse. A non-null value here is guaranteed itself
   *  browsable; the filesystem's own parent is suppressed to `null` when it sits outside every
   *  configured root. */
  parent: string | null

  permissions: HostPermissions
  entries: HostEntry[]

  /** Whether the server's cap bit. A shortened listing that looks complete is the same defect as a
   *  200 with `entries: []` for a folder that could not be read, so it is SAID rather than left to
   *  be inferred from a count against a constant the client does not have. */
  truncated?: boolean

  /** How many subdirectories there really are, cap or no cap. */
  total?: number
}

/** One entry in `GET /api/fs/roots` — a folder the picker may open, and what it may do inside it. */
export interface FileSystemRoot {
  name: string
  path: string

  /** Whether this root IS the instance's own data folder — what a blank "Place team in" box
   *  actually resolves to. Answered by the SERVER, once, because the client's only other handle was
   *  `roots[0]`, and that is the instance root only while it survives normalisation:
   *  `--DataRoot=\\?\C:\foo` drops it, index nought becomes the operator's first configured root,
   *  and New Team then previews a folder the team will not go in. */
  isInstance: boolean

  allowCreate: boolean
  allowUpdate: boolean
  allowDelete: boolean
}

/** What `POST .../triggers/test-folder` asks: the folder a folder-change trigger would watch. */
export interface FolderTestRequest {
  watchRoot: string
  watchPath: string
  watchGlob: string | null
}

/** One file the folder test saw. Names and sizes only - never contents. */
export interface FolderTestEntry {
  path: string
  size: number
  modifiedAt: string
}

/**
 * What the folder test saw, and how long the listing took. ALWAYS a 200 for a team that exists: a
 * folder the server will not watch or cannot list is `ok: false` with the sentence in `refusal`
 * and no entries - not a thrown error.
 */
export interface FolderTestResult {
  ok: boolean
  refusal: string | null

  /** What was listed, for display: `documents/<path>` or `<root name>/<path>`. Null when the folder
   *  was refused before it could be resolved (a root or path the server will not watch). */
  folder: string | null

  /** Every file the watch sees, after the ignore list and the glob. */
  count: number

  /** The first 200, sorted by path, each relative to the ROOT; `truncated` says there were more. */
  entries: FolderTestEntry[]
  truncated: boolean

  elapsedMs: number
}

/** One root a folder trigger may watch, from `GET .../triggers/watch-roots`: `documents` first,
 *  then every file-browser root carrying `allowWatch`. */
export interface WatchRootOption {
  value: string
  label: string
}

/** Answers `GET /api/fs/roots`. Empty only when every root was dropped at startup — a device path,
 *  or one that trims to nothing once normalised. **Not** a root that has merely gone away: nothing
 *  probes the filesystem when the roots are built, and a root that is unreachable is WARNED ABOUT
 *  AND KEPT (a share that comes back must not need a Host restart), so it is still listed here and
 *  refuses at the moment it is opened — 404 for absent, 423 for present-but-unreadable. This
 *  comment said such a root was dropped, which it never was.
 *
 *  The picker must say so rather than render a blank list, the same rule the server follows in
 *  refusing to answer 200 with `entries: []` for a folder it could not read. */
export interface RootListing {
  roots: FileSystemRoot[]
}

export interface Overview {
  teams: Team[]
  managerName: MemberId

  /**
   * The ceiling no team can raise, in tokens, for ONE workflow - the instance's own
   * `WorkflowSpendLimit`.
   *
   * INSTANCE-WIDE, so it rides this payload once rather than being repeated per team. It is not
   * settable from the product and must not become so; it is here so it is not INVISIBLE, which is
   * a different thing.
   *
   * OPTIONAL ON THE CLIENT ONLY: absent means "the Host did not say", which reads the same as no
   * ceiling.
   */
  workflowSpendLimit?: number | null

  /**
   * The sweep findings that are LIVE right now, one row per (team, kind).
   *
   * The board's view of the detector's verdict. This field provides visibility into teams that
   * may be in stuck states, and is used by the board surface to inform its displays.
   *
   * Empty array when nothing is found, never null. OPTIONAL ON THE CLIENT ONLY: absent and empty
   * alike mean "no finding".
   */
  activeFindings?: ActiveFinding[]
}

/**
 * One live sweep finding, flattened to the two facts the board needs: whose it is, and what it is.
 *
 * `kind` is one of `RunningWithoutProgress`, `PendingAcceptedWithoutTerminal`, `QuietTeam` or
 * `WrapUpNotPushed`. Typed as a plain string rather than a union on purpose: a Host that grows a
 * new finding must not make this client fail to parse the ones it already understands, and the one
 * consumer asks only whether the list is empty.
 */
export interface ActiveFinding {
  team: string
  kind: string
}

/**
 * Team token in/out totals from the message log, not from the board's twenty-message feed.
 *
 * `available` is false when every completed/failed run on the team predates capture (no
 * tokensIn/tokensOut keys). A true zero — no runs at all — is available with tokensIn/tokensOut 0.
 * `partial` means some runs on this team have no usage.
 */
export interface TeamTokenTotals {
  available: boolean
  /** Uncached input only. Cache reads are {@link tokensCachedIn} and are not in here. */
  tokensIn: number
  tokensOut: number
  tokensCachedIn: number
  tokensCacheCreation: number
  /** Cache reads at 1/10, cache writes at 5/4, a combined total as reported. Summed on the server. */
  tokensBillable: number
  partial: boolean
  runsWithUsage: number
  runsWithoutUsage: number
  missing: string | null
  members: MemberTokenTotals[]
}

/** Budget information for one workflow. */
export interface WorkflowSpend {
  /** Sum of all measured tokens on the workflow. */
  tokensSpent: number
  /** Completed or failed runs whose usage was measured. */
  runsWithMeasuredUsage: number
  /** Completed or failed runs that had no measured usage. */
  runsWithoutUsage: number
}

/**
 * How long this team's current — or last — workflow has been going, from `GET
 * /api/teams/{team}/workflow`.
 *
 * A LOG PROJECTION, sibling to `TeamTokenTotals` and fetched the same way. It crosses HTTP only:
 * `ContainerSnapshot` is what rides every SignalR frame, and this must never be added to it.
 *
 * ELAPSED IS NOT ON THIS PAYLOAD, deliberately. `startedAt` and `serverNow` are, and the client
 * subtracts them and ticks the result every second with no further traffic — a server-computed
 * integer would need polling to move.
 */
export interface TeamWorkflowTiming {
  /** False for a team that has never run. Render an em dash, never `0m`. */
  available: boolean

  /** The seq of the message that began this workflow. */
  correlation: number | null

  /**
   * What the LOG says: `Failed`, `Blocked`, `Awaiting`, `Completed`, `Closed`, `Paused`,
   * `Undeclared` or `Running`.
   *
   * `Paused` IS DERIVED FROM {@link pausedAt}, ON THE SERVER, IN ONE PLACE - the same pairing
   * `blockedBy`/`Blocked` and `awaitingFrom`/`Awaiting` already have. The client maps the word and
   * never re-derives it, or two surfaces end up answering the same question separately.
   *
   * A STRING and not a union of the tile's own words. `Running` here does NOT mean a member is
   * running — the log cannot see a roster. It means the workflow is open and no member's last
   * terminal fact says anything louder. `workflowTiming` in `lib/teamKpis.ts` adds the half that
   * needs live containers, and RUNNING ranks first there.
   */
  state: string | null

  /** When the workflow's ROOT was published. Elapsed is measured from here, not from its last row. */
  startedAt: string | null

  /** When its last row was published, or null while it is open — which is the ordinary case. */
  endedAt: string | null

  /** The server's clock at the moment this was read. See `workflowClockOffset` in the store. */
  serverNow: string

  /** Per-member run time SUMMED. It legitimately EXCEEDS elapsed; never add or divide the two. */
  executionSeconds: number

  /** Some runs here could not be measured — `runsUnfinished` above zero. */
  partial: boolean

  runsCounted: number

  /** Runs that started with no terminal partner. Unknown, never zero and never "still running". */
  runsUnfinished: number

  /** The member that gave up, when a member's last terminal fact here is `container.blocked`. */
  blockedBy: string | null

  /** How many `container.failed` rows this workflow holds. A count of RUNS, not of members. */
  runsFailed: number

  /** Members whose LAST terminal fact here is `container.failed`, in publication order. */
  failedMembers: string[]

  /** What the log does not carry, in words. Null when it carries enough. */
  missing: string | null

  members: MemberExecution[]

  /**
   * The newest row in this workflow, however published — present even while it is open, which
   * `endedAt` deliberately is not. It is what `StalledBadgeGrace` is measured against, so the
   * grace period stays a single constant living here rather than a second one on the server.
   */
  lastActivityAt: string | null

  /** The member waiting on a person, when its last terminal fact here is `container.needs-decision`. */
  awaitingFrom: string | null

  /** The subject of the instruction that began this workflow — read from the root message, never
   *  re-derived client-side. Null when the root is not an addressed instruction or carries no
   *  subject; `workflowsTile` falls back to a `Workflow #<correlation>` label only then. */
  subject: string | null

  /** Total measured workflow spend, or null when this workflow has no completed or failed runs. */
  spend?: WorkflowSpend | null

  /**
   * WHEN THIS WORKFLOW WAS PAUSED, and NULL MEANS NOT PAUSED. The single source of truth for the
   * state: present-means-paused rather than a `bool` beside a timestamp, so the two cannot
   * disagree.
   *
   * IT IS A WORKFLOW PAUSE, NOT A TEAM ONE. `Team.paused` stops a whole team; this stops ONE
   * correlation and leaves that team's other open workflows running. A team with three workflows
   * must not lose all three because one spent its budget.
   *
   * `endedAt` STAYS NULL while this is set. A paused workflow is OPEN - `workflowOpen` tests
   * `endedAt === null`, and a paused workflow that dropped out of the open list would be one
   * nobody could find.
   */
  pausedAt: string | null

  /** Why, in the sentence the server wrote. Null when not paused. */
  pausedReason: string | null

  /** The figure that was in force when it paused, so a reader can be told what stopped it without
   *  the browser re-resolving which of the team's number and the instance's applied. Null when not
   *  paused, and null when a pause did not say. */
  pausedLimit: number | null

  /**
   * THE SAME MEASUREMENT AS {@link spend}, COUNTED SINCE THE LAST NUDGE — and **the figure the
   * budget bar measures**, because it is the figure the server's own guard decides on.
   *
   * TWO QUESTIONS, TWO FIELDS. `spend` is `GetWorkflowSpendAsync`, the WHOLE workflow, which never
   * comes down; the pump's over-budget check is `GetSpendSinceNudgeAsync`, whose window resets at
   * every nudge. Drawing the bar from the cumulative one would leave a workflow that had been nudged —
   * or resumed — reading `> limit` while the server was letting it run, which is a display
   * contradicting the decision it is about. `spend` cannot simply BECOME the window:
   * `BacklogExecutionRecord` reads it for what a backlog item cost, which is genuinely a
   * whole-workflow question.
   *
   * NULL UNDER EXACTLY THE CONDITION `spend` IS — no completed or failed runs — so the two appear
   * and disappear together. **Absent is a different thing again**: the Host said nothing, and `spendAgainstBudget` falls back to `spend` so it goes on drawing a bar.
   */
  spendSinceNudge?: WorkflowSpend | null

  /**
   * The outcome this workflow serves now: its newest link, followed through `mergedInto`, or null
   * for none. `name` is text. Absent from an older server.
   */
  outcome?: { id: string; name: string; status: 'proposed' | 'active' | 'retired' | 'merged' } | null
}

export interface MemberExecution {
  member: string
  runs: number

  /** Null when none of this member's runs has a partner — never zero. */
  executionSeconds: number | null
  unfinished: number
}

/**
 * One team's workflow projection inside the rollup. `team` is the IDENTIFIER.
 *
 * `openWorkflows` is the plural sibling of `workflow` - named apart, never `workflows`, so the two
 * cannot be mistaken for one another at a call site (`Harness.Contracts/Workflow.cs`'s
 * `TeamRollupRow` documents the same reasoning server-side). It is what lets the Teams table and
 * the tab strip answer "what is this team doing" for every team the rollup names, not only the one
 * that happens to be active - see `pullRollup` in `stores/console.ts`.
 */
export interface TeamRollupRow {
  team: string
  workflow: TeamWorkflowTiming
  openWorkflows: TeamWorkflows
}

/** One state a member's lane can be in. "No data" is never a state: it is the absence of a span. */
/** `held` is the part of a wait an admission hold covered: "waiting for a slot". */
export type ActivityState = 'running' | 'waiting' | 'held' | 'blocked' | 'failed' | 'idle'

/** One stretch of one state. `to` is null while it is still open at the answer's `serverNow`. */
export interface ActivitySpan {
  state: ActivityState
  from: string
  to: string | null
  /** The workflow a run or its block belongs to, when it names one. */
  workflow?: number
  /**
   * A block's or failure's words while the log still holds them, or a hold's sentence as admission
   * worded it ("waiting for memory: 11.2 of 12.9 GB in use"). Text, never markup.
   */
  reason?: string
  /** A held span's reason in one word: `slot`, `memory`, `pressure` or `worker`. */
  reasonKind?: string
}

/** One member's lane. `current` is false for a member since removed. */
export interface ActivityMember {
  member: string
  kind: string | null
  isManager: boolean
  current: boolean
  spans: ActivitySpan[]
}

/**
 * WHAT EACH MEMBER WAS DOING, AND WHEN, from `GET /api/teams/{team}/activity`. Mirrors
 * `TeamActivityAnswer` in `Harness.Host/TeamActivity.cs`. `window` is `workflows` (from the root of
 * the team's earliest workflow, open or closed, to the latest activity of any of them - never the
 * clock), `requested`, or `none` for a team that has never run - which carries no members and null
 * `from` / `to`.
 */
export interface TeamActivity {
  from: string | null
  to: string | null
  serverNow: string
  window: ActivityWindow
  members: ActivityMember[]
}

/**
 * A TEAM'S RUNS AND THE TOKENS EACH USED, from `GET /api/teams/{team}/tokens/runs`. Mirrors
 * `TeamTokenRunsAnswer` in `Harness.Host/TeamTokenRuns.cs`. With no period asked for, `runs` is
 * every run since the team's creation and `window` is `workflows` (`from`/`to` the activity read's
 * own window: earliest workflow root to latest workflow activity) or `none` (no workflow, no
 * `from`/`to`); with one, `requested`.
 */
export interface TeamTokenRuns {
  from: string | null
  to: string | null
  serverNow: string
  window: 'workflows' | 'requested' | 'none'
  runs: TeamTokenRun[]
}

/**
 * ONE FINISHED RUN, at the instant it ended - when its tokens were measured. A figure the run did not
 * report is ABSENT, never 0: an unmeasured run (`measured: false`) carries none, and a combined total
 * carries `combined` and `billable` with no in/out split.
 */
export interface TeamTokenRun {
  member: string
  /** False for a member since removed. */
  current: boolean
  endedAt: string
  measured: boolean
  billable?: number
  tokensIn?: number
  tokensCachedIn?: number
  tokensCacheCreation?: number
  tokensOut?: number
  combined?: number
}

/** Which period an `/activity` answer covers; see {@link TeamActivity}. */
export type ActivityWindow = 'workflows' | 'requested' | 'none'

/**
 * EVERY WORKFLOW A TEAM HAS RUN, OPEN AND CLOSED ALIKE AND NEWEST FIRST, and one span over the open
 * ones, from `GET /api/teams/{team}/workflows`. Mirrors `TeamWorkflows` in
 * `Harness.Contracts/Workflow.cs`.
 *
 * `openCount`, `totalCount` and `earliestStartedAt` are UNCAPPED; `workflows` is capped at fifty
 * entries so the tile stays cheap to fetch.
 *
 * THE TRUNCATION SIGNAL IS `workflows.length < totalCount`, NEVER `openCount`. A team with nothing
 * open reads `openCount: 0` while the list still carries rows, and since the list carries closed
 * workflows too there is no reading of `openCount` that can answer "is this list complete" at all.
 *
 * `earliestStartedAt` IS THE SPAN'S START AND NEVER A SUM: three workflows covering the same ten
 * minutes summed reads as thirty, a number larger than the time that has actually passed.
 */
export interface TeamWorkflows {
  /** False for a team that has never run. Render an em dash, never `0`. */
  available: boolean

  /**
   * How many workflows this team holds OPEN, uncapped — not the length of `workflows`, which now
   * carries closed ones too. Allowed to grow: an undeclared workflow stays open, and this number is
   * what makes that visible. STILL MEANS OPEN: {@link totalCount} was added beside it rather than
   * redefining it, so the tile's own count line is unaffected.
   */
  openCount: number

  /**
   * How many workflows this team has run IN TOTAL since its floor, open and closed alike, uncapped.
   * THE FIELD THE TRUNCATION LINE COUNTS AGAINST — see this interface's own documentation.
   */
  totalCount: number

  /** When the OLDEST open workflow began — the start of the span. Null when none are open. */
  earliestStartedAt: string | null

  /** The server's clock when this was read. */
  serverNow: string

  /** One entry per workflow, newest first — OPEN AND CLOSED ALIKE. Capped at fifty: compare its
   *  length against `totalCount`, never against `openCount`, to tell truncation. */
  workflows: TeamWorkflowTiming[]

  /** What the log does not carry, in words a person can act on. Null when it carries enough. */
  missing: string | null
}

/**
 * Every reachable team's workflow projection.
 *
 * It carries NOTHING but timings: names and containers come from `/api/overview`, which the
 * store already holds. A second source for a team's name is a second answer waiting to disagree.
 */
export interface TeamRollup {
  teams: TeamRollupRow[]
}

export interface MemberTokenTotals {
  member: string
  /** Current Agent for this member, empty when the member is no longer on the roster. */
  brand: string
  tokensIn: number | null
  tokensOut: number | null

  /** Whether this member's current Agent carries a usage format at all. False means it reports
   *  none and never will; null means the Agent could not be resolved, which is not the same claim. */
  brandReportsUsage?: boolean | null

  /** Completed and failed rows this member has on the log above the team's floor. 0 means it has
   *  never finished a run, which is why it has no numbers - a different state from having none. */
  runs?: number

  /** What this member's runs cost where its Agent reports ONE figure and no in/out split (codex).
   *  Null for a brand that splits, and null for one that reports nothing at all. */
  tokensTotal?: number | null

  /** Null when no run of this member reported an in/out split. */
  tokensCachedIn?: number | null
  tokensCacheCreation?: number | null

  /** Null when no run of this member reported a split or a combined total. */
  tokensBillable?: number | null
}

export interface Message {
  seq: number
  type: string

  /** JSON, opaque to the transport. See `summarise` for how it is read. */
  payload: string

  /**
   * Who published it — a QUALIFIED container id (`Team/Name`), a Concierge, or the
   * host. A container's identity is the pair, so a bare name is not one: two teams
   * can each hold a `Manager`. This is what Console Cards bucket a card's activity on.
   */
  source: string

  correlationId: number
  causationSeq: number | null
  depth: number
  occurredAt: string
}

/** How one Agent preset is launched. A preset carries exactly one — a Team Member is always
 *  Headless and the Concierge is always Interactive, so there is no command/interactive pair to
 *  choose between. */
export interface AgentLaunch {
  fileName: string
  arguments: string[]
  systemPromptArguments?: string[] | null

  /**
   * A conventional instructions file the Agent reads from its working directory — `AGENTS.md` for
   * codex, copilot and grok, none of which accepts a system prompt on the command line.
   *
   * DECLARED HERE BECAUSE OMITTING IT DESTROYS DATA. A TypeScript interface is compile-time only,
   * so an undeclared field survives every GET at runtime and is dropped only when `AgentEditDialog`
   * REBUILDS the launch object from its own fields — and `PUT /api/agents` replaces the catalog
   * wholesale, so the loss is permanent. Losing this field removes the ONLY way such a preset
   * receives a system prompt, and the member then runs knowing neither its name nor its team.
   *
   * EXACTLY ONE MECHANISM PER PRESET: a `claude` preset uses `systemPromptArguments` above and must
   * NOT also carry this, or the same text is handed over twice and billed twice on every run.
   */
  instructionsFile?: string | null

  /** The usage payload this launch reports, or null where usage is unknown for this preset. */
  usageFormat?: string | null

  /**
   * Whether this launch runs a LANGUAGE MODEL. False marks an ordinary program — a build script, a
   * CI tool — which an Agent Container can host and wake exactly like any other: it is then not
   * billed, not probed for CLI use, and may subscribe to high-volume events a model preset must not.
   *
   * OPTIONAL AND `undefined` READS AS `true`, matching `Harness.Host.AgentLaunch`'s constructor
   * default — a catalog entry without the key is a model. THE SAME RULE AS `instructionsFile`
   * APPLIES: if `AgentEditDialog` saved a launch with the key absent, `System.Text.Json` would fill
   * in the constructor default of `true` and silently turn a `languageModel: false` program into a
   * billed, probed, firehose-eligible model. `launchFromDialogState` always writes an explicit
   * value; this stays optional only for a caller that does not set the field.
   */
  languageModel?: boolean
}

/** Which of the two worlds a preset belongs to — Headless for a member woken by a message,
 *  Interactive for a team's Concierge session. Matches `Harness.Host.AgentMode`, serialised as a
 *  name rather than a number (`JsonStringEnumConverter`, `Program.cs`). */
export type AgentMode = 'Headless' | 'Interactive'

/**
 * One Agent preset, as `GET /api/agents` answers it. ONE shape, because every person is an
 * administrator: `launch`, `systemPrompt` and `env` are populated for every human reader, and a
 * machine principal is refused rather than handed a redacted copy. The optional fields below are
 * optional because a catalog written by hand may omit them, not because a reader may be denied
 * them. A preset serves exactly one `mode`.
 */
export interface Agent {
  name: string
  mode: AgentMode
  launch?: AgentLaunch | null
  env?: Record<string, string> | null
  tags?: string[] | null

  /** How long one run of this Agent may take, in seconds, before the platform stops it and reports
   *  the member as failed. `null` means UNBOUNDED, which is the default — a number invented by the
   *  platform would cut off real work on an instance
   *  whose owner never asked for one. Ignored for an Interactive preset: a terminal has no run to
   *  bound, and the idle reaper ends unattended sessions. */
  timeoutSeconds?: number | null

  /**
   * Kept out of every screen a person chooses from — `echo` and `shell`, the two test fixtures the
   * suites launch and no operator should ever be offered.
   *
   * ABSENT MEANS VISIBLE. A catalog written by hand may carry no key, so read it
   * as `!agent.hidden` and never `agent.hidden === false`.
   *
   * NOT a permission and not a redaction. `GET /api/agents` returns a hidden preset flagged, and it must:
   * `PUT /api/agents` replaces the catalog wholesale, so the editor cannot send back an entry it
   * never received. Filter with `visibleAgents` at RENDER; never filter the list you submit.
   */
  hidden?: boolean

  /**
   * Where a person goes to install this Agent's CLI, when the probe reports its command is not on
   * this machine's PATH. Optional, and ABSENCE IS AN ORDINARY STATE — the guidance degrades to the
   * same sentence with no link, and nothing is ever constructed to fill the gap.
   *
   * DECLARED HERE FOR THE REASON `instructionsFile` IS. `AgentEditDialog`
   * REBUILDS the definition it saves (`rebuildAgentDefinition`) and `PUT /api/agents` replaces the
   * catalog wholesale, so a field the form does not carry is a field the next edit silently
   * deletes. That already happened once and cost three presets their only means of receiving a
   * system prompt. It matters more here than it looks: `LoadOrSeed` never merges, so the Agents
   * screen is the ONLY way this field ever reaches an existing instance — a dialog that dropped it
   * would make the feature unreachable on precisely the tenants that need it.
   */
  install?: AgentInstall | null

  /**
   * TRUE FOR A PRESET THE BUILD SHIPS (claude, codex, copilot, grok and their headless forms). It is
   * listed read-only: every route refuses to edit or delete it. A person's own presets are false.
   * Optional because a preset a person is still writing has not been answered for yet.
   */
  builtIn?: boolean

  /**
   * TRUE WHEN A BUILT-IN'S `tags` ARE THE OPERATOR'S, from the tenant setting `agents.tags`,
   * rather than the build's. Not a field of the preset: a save that sends it back writes nothing.
   */
  tagsFromOperator?: boolean

  /** The tags a built-in carries in the build, shown beside the operator's. Null for a custom preset. */
  buildTags?: string[] | null

  /**
   * How a member's launch of this preset is kept to the platform's tools: the arguments and
   * environment that switch off the account's connectors and the shared home's configuration, and
   * the CLI's own tools it may use. A headless preset without one is NOT VERIFIED.
   *
   * CARRIED THROUGH AN EDIT for the reason `install` is: the dialog rebuilds what it saves and
   * `PUT /api/agents` replaces the catalog, so a field it drops is a declaration the save deletes.
   */
  isolation?: AgentIsolation | null

  /**
   * What turns this CLI's own automatic update off on every launch, and the command the platform
   * runs to update it when a person asks. Carried through an edit for `isolation`'s reason.
   */
  updates?: AgentUpdates | null
}

/** A preset's update declaration. See `AgentUpdates` in the Host. */
export interface AgentUpdates {
  env?: Record<string, string> | null
  arguments?: string[] | null
  update?: string[] | null
}

/** A run holding, or waiting for, a share of a CLI's install, named as `/api/wip` names a slot's holder. */
export interface AgentRunHolder {
  team: string
  member: string
}

/** `none` until one is asked for; `waiting` (cancellable) and `updating` while it is in the gate; then
 *  the last one's outcome. */
export type AgentUpdatePhase = 'none' | 'waiting' | 'updating' | 'done' | 'cancelled' | 'failed'

/**
 * What the Host's update gate says about one command's update, read from the gate and never
 * estimated: `POST`, `GET` and `DELETE /api/agents/{name}/update`, and `GET /api/agents/updates`.
 */
export interface AgentUpdateState {
  command: string
  phase: AgentUpdatePhase
  agent: string | null
  requestedBy: string | null
  requestedAt: string | null
  /** Runs of the command in flight now. */
  running: number
  /** The team and member of each of those runs. */
  inFlight: AgentRunHolder[]
  /** The team and member of each launch held behind the update. */
  held: AgentRunHolder[]
  startedAt: string | null
  finishedAt: string | null
  /** What the update measured, once `done`. */
  result: AgentUpdateResult | null
  /** Why it did not finish, when `failed`. */
  error: string | null
  cancelledBy: string | null
}

/** What a finished update measured: an `AgentUpdateState`'s `result` once it is `done`. */
export interface AgentUpdateResult {
  agent: string
  command: string
  updated: boolean
  /** Null when no update command ran: the preset declares none. */
  exitCode: number | null
  versionBefore: string | null
  versionAfter: string | null
  at: string
  detail: string
  /** The CLI's entry as `cliVersions` gives it, read back after this update's line was written.
   *  Null when the Host keeps no version record. */
  cliVersion?: CliVersion | null
}

/**
 * One CLI's installed version and when it last changed, from the Host's CLI version record
 * (`cli-versions.jsonl`) - the record the operator CLI's `doctor` and `agents` read. `version` is null
 * when the record has none, which is shown as not known and never guessed.
 */
export interface CliVersion {
  cli: string
  version: string | null
  /** When this version first appeared after a different one; null when the kept record never saw
   *  it change, and `since` is then how far back the record reaches. Null, with `updatedBy`, when
   *  the version is not known: a version nobody could read has no update time. */
  updatedAt: string | null
  since: string | null
  /** `start` for a container start, `person` for a person's update through the platform, `measured`
   *  when control measured it on a worker as the workers changed. A plain
   *  string rather than a union: every quoted literal in the app is cut into the icon font subset. */
  updatedBy: string | null
  /** That person's email, when the record has it. */
  person: string | null
}

/** A preset's isolation declaration. See `AgentIsolation` in the Host. */
export interface AgentIsolation {
  arguments: string[]
  env?: Record<string, string> | null
  allowedTools?: string[] | null
  allowedServers?: string[] | null
  gaps?: string[] | null
}

/** Where to get the CLI a preset launches. Data in the catalog, never a table in code. */
export interface AgentInstall {
  url: string
  hint?: string | null
}

/**
 * What the probe answers for one preset, as `GET /api/agents` returns it in `installations` —
 * beside the catalog rather than inside it.
 *
 * THAT SEPARATION IS NOT PRESENTATION. `PUT /api/agents` replaces the catalog wholesale from a body
 * `AgentsDialog` composes out of what `GET` handed it, so anything that reads like part of a
 * definition is something a save tries to write back into `agents.json`. This is a MEASUREMENT of
 * the machine, not configuration.
 *
 * HUMAN-ONLY, like the route. It names commands and says where they resolved on the Host's disk,
 * which is not an agent's business.
 */
export interface AgentInstallation {
  /** The preset this describes, matched against `Agent.name` without regard to case. */
  agent: string

  /** The command it launches. Several presets share one, and the probe answers about the COMMAND. */
  command: string

  /**
   * NULL when the command resolves, `AgentNotInstalled` when it does not, and `AgentUpdating` while
   * the platform's update holds the command (then never `AgentNotInstalled`).
   *
   * A STRING, never an enum: the enum crosses two serialisers as a name and this side compares the
   * string. It is a DIFFERENT fact from a container's `missingAgent`, which means the catalog has
   * no such entry — that one is fixed on the Agents screen, this one in a terminal.
   */
  state: string | null

  /** Where the command resolved to, or null. */
  resolvedPath: string | null

  /** Whether any team is on this preset. The screen lists every preset; the badge counts these. */
  referenced: boolean

  /** What a person is told — that a command of this name does or does not resolve on this
   *  machine's PATH, and NOT that it runs, is current, or is authenticated. */
  message: string

  /** The preset's own install guidance, or null. Never composed. */
  install?: AgentInstall | null

  /**
   * Sent only by a server whose agent CLIs are on workers: each worker's answer for this command,
   * where `state` and `message` come from. EMPTY is NOT MEASURED - no worker that runs agents has
   * answered - and never "installed". Absent: the server answered from its own PATH.
   */
  measuredOn?: InstallMeasurement[]

  /** Sent only while the platform's update holds this preset's command: absent otherwise. */
  updating?: AgentUpdatingOn | null
}

/**
 * The update holding a preset's command: `waiting` for its runs, or `updating`; `worker` is the one it
 * runs on, null until one is picked (and always in a server that runs its runs itself).
 */
export interface AgentUpdatingOn {
  phase: 'waiting' | 'updating'
  worker: string | null
  since: string
}

/** One worker's answer to whether a command is on its PATH, and when it gave it. */
export interface InstallMeasurement {
  worker: string
  installed: boolean
  at: string
}

/**
 * Whether one preset's CLI is installed AND signed in, as `GET /api/agents/auth` measured it.
 *
 * A MEASUREMENT, like `AgentInstallation`, and held beside the catalog for the same reason: nothing
 * here may be folded into an `Agent` and written back by the wholesale save.
 *
 * `authenticated` IS A TRI-STATE. `true` and `false` are answers; `null` means the probe could not
 * ask - a CLI with no way to report, or a probe that did not run - and must render as NOT MEASURED,
 * never as a failure. Collapsing it to a boolean turns every unmeasured preset into a false alarm,
 * which teaches people to ignore the one that is real.
 */
export interface AgentAuthReport {
  /** The preset this describes, matched against `Agent.name` without regard to case. */
  agent: string

  /** The command it launches. */
  command: string

  /**
   * Whether the command resolves at all on the worker that was asked. An uninstalled CLI cannot be
   * signed in. `null` is NOT MEASURED: no worker was connected to ask, or the one asked did not
   * answer - `detail` says which. Never "not installed": nothing looked.
   */
  installed: boolean | null

  /** Signed in, signed out, or not measured. */
  authenticated: boolean | null

  /** The probe's own sentence - what it ran and what it saw. Shown verbatim when `false`. */
  detail: string

  /**
   * Whether a team's member, a team's hiring allowlist or the Concierge uses this preset. Only a
   * referenced preset is worth a sign-in warning: an unused, never-signed-in CLI is information on
   * the Agents screen, not a problem on every page.
   */
  referenced: boolean

  /**
   * How the preset signs in: `home` through the shared home, `issued` through the credential stored
   * for its command. Absent from an older server, which reads as not known rather than as home.
   */
  source?: AgentCredentialSource

  /**
   * While the platform's update holds the preset's command: the server's sentence. The CLI was not
   * asked, so `installed` and `authenticated` are null - never "not installed" or signed out.
   */
  updating?: string | null
}

/** How a preset signs in. Chosen per preset in the tenant setting `agents.credentialSource`. */
export type AgentCredentialSource = 'home' | 'issued'

/** One kind of credential a preset declares, and the variable its CLI reads it from. */
export interface IssuedCredentialKind {
  kind: 'apiKey' | 'token'
  variable: string
  /** Beginnings of a value this CLI refuses to start with. The Host answers 400 for one, with a
   *  sentence the screen shows as it is. Absent or null for none. */
  refusedPrefixes?: string[] | null
}

/**
 * A preset's issued-credential declaration. `loginPrecedence` is which credential its CLI uses when a
 * login also exists in the shared home - what the Concierge, which keeps that home, actually runs on.
 */
export interface IssuedCredentialDeclaration {
  kinds: IssuedCredentialKind[]
  displaces: string[]
  loginPrecedence: 'credential' | 'login' | 'unmeasured'
  measuredWith: string
  /** The CLI's own config-directory variables, removed from an issued run. Display only. */
  homeVariables?: string[] | null
}

/**
 * One model preset as `GET /api/agents/credentials` answers it.
 *
 * TWO SCOPES ON ONE ROW. `source` is this preset's own choice; `set`, `setBy` and `setAt` are its
 * COMMAND'S, since one credential is stored per command and shared by every preset that runs it -
 * `sharedWith` names the others. No answer ever carries the value or any part of it.
 */
export interface AgentCredential {
  agent: string
  command: string
  sharedWith: string[]
  source: AgentCredentialSource
  issuedCredential: IssuedCredentialDeclaration | null
  set: boolean
  setBy: string | null
  setAt: string | null
}

/** What `PUT` and `DELETE /api/agents/{name}/credential` answer: the command's state after it. */
export interface AgentCredentialStatus {
  command: string
  set: boolean
  setBy: string | null
  setAt: string | null
}

/** What `GET /api/agents` answers. Launch definitions only: the prompt an agent is told is chosen by
 *  its role from the build, so the catalog carries no Prompts. */
export interface Catalog {
  agents: Agent[]

  /**
   * Whether each preset's command is on this machine at all. OPTIONAL because a server that does
   * not probe may omit it, and a client that treated its absence as "everything is fine" would be
   * making a claim nothing checked; the library reads an absent entry as `unknown` instead.
   */
  installations?: AgentInstallation[]

  /** Each preset's CLI version, one entry per preset that launches a command. A person's arm only;
   *  absent reads as not known. */
  cliVersions?: (CliVersion & { agent: string })[]
}

/**
 * Presets a person may be OFFERED for a given mode: a member is always Headless, a team's
 * Concierge always Interactive. Works on either shape `GET /api/agents` answers, because
 * `mode` rides both.
 *
 * HIDDEN PRESETS ARE DROPPED HERE, which is what makes hiding whole rather than half-applied. Every
 * caller of this is a picker building options - `AddMemberDialog`, `CreateTeamDialog`,
 * `TenantSettingsDialog`, `MemberSettingsDialog`, `TeamSettingsDialog` - and none of them uses it
 * to build a body, so filtering here reaches all of them and reaches nothing that is submitted.
 *
 * The list a screen SUBMITS must never be filtered: `PUT /api/agents` replaces the catalog
 * wholesale, so a dialog that submits what it rendered deletes every hidden preset. `AgentsDialog`
 * keeps its own complete ref for exactly that reason and filters into a separate computed.
 */
export const agentsForMode = (agents: Agent[], mode: AgentMode) =>
  agents.filter((a) => a.mode === mode && !a.hidden)

/**
 * Whether a container is its team's manager — matching `TeamRegistry.IsManager`'s own rule:
 * case-insensitively against the container's IDENTIFIER, never its editable label, so renaming a
 * manager's display name does not stop this from applying (`ContainerId` equality is itself
 * case-insensitive, for the same reason). `managerName` is `Overview`'s own `managerName` field —
 * the one seeded manager identifier for the whole tenant — read from the caller rather than
 * hard-coded here a second time. A manager's prompt cannot be overridden
 * (`ManagerPromptCannotBeOverriddenException`), which is what `MemberSettingsDialog` uses this to
 * decide whether to show a control for at all.
 */
export const isManagerContainer = (id: MemberId, managerName: MemberId) =>
  id.toLowerCase() === managerName.toLowerCase()

/** Prerequisites for the Repos section — git and gh. */
export interface Prerequisite {
  /** The executable that was resolved: "git" or "gh". */
  command: string
  /** PathSearch.Find answered non-null. */
  resolves: boolean
  /** Wording like "git was not found on this machine's PATH." */
  message: string
  /** Closed set: "platform" | "agents". git → "platform". gh → "agents". */
  usedBy: 'platform' | 'agents'
}

/** An Agent that could not be resolved. */
export interface UnresolvedAgent {
  /** The preset name (catalog spelling). */
  agent: string
  /** AgentLaunch.FileName that did not resolve. */
  command: string
  /** AgentInstallProbe message. */
  message: string
}

/** Status of a worktree in a repo clone. */
export interface WorktreeStatus {
  /** Absolute path git reported. */
  path: string
  /** Branch name without refs/heads/, or null if detached. */
  branch: string | null
  /** 40-character HEAD sha, or null if git did not report one. */
  sha: string | null
  /** Inferred member name if path matches expected pattern, null otherwise. */
  member: string | null
  /** Commits on this worktree not in local main, or null if unknown. */
  aheadMain: number | null
  /** Commits on local main not in this worktree, or null if unknown. */
  behindMain: number | null
  /**
   * Commits on this worktree's branch whose CHANGE is not on origin/main, counted by patch-id
   * (`git cherry`), or null when it could not be measured — a detached HEAD, or a cherry that
   * failed. `aheadMain` is sha ancestry, so a rebased worktree reads ahead while every one
   * of its changes is already upstream. Conservative by construction: a commit whose patch was
   * modified during integration still counts as outstanding.
   */
  commitsNotOnMain: number | null
  /**
   * The card this tree is for — the key in `wt_<Member>_<key>`: a card id, or
   * `w<correlation>` for an instruction that named no card. Null for any other tree.
   */
  card?: string | null
  /**
   * Whether the tree's card (or workflow) is still open, so clean-up leaves it. Null or absent
   * when the Host did not say.
   */
  open?: boolean | null
  /** Bytes the tree takes on disk, or null when not measured. */
  sizeBytes?: number | null
}

/** One repository's default branch, as Team settings shows and edits it. */
export interface TeamRepoDefaultBranch {
  repo: string
  /** The branch the host uses, or null when not known. */
  branch: string | null
  /** What origin's HEAD named on the last clone or successful Fetch. */
  fromRemote: string | null
  /** A person's choice; kept across Fetches until cleared. */
  setByPerson: string | null
}

/** One repository's contributor settings, as Team settings shows and edits them. */
export interface TeamRepoContributor {
  repo: string
  /** The original project. Null is an owned repository; set, the repository's URL is the fork. */
  upstreamUrl: string | null
  /** The account the fork belongs to. */
  forkOwner: string | null
  /** Sign off commits (DCO): the clone's commit-msg hook adds Signed-off-by. */
  dcoSignOff: boolean
  /** A person's note that the upstream project's CLA is signed. A record, never a signature. */
  claSignedNote: string | null
}

/** Status of a repository clone and its team branch. */
export interface RepoStatus {
  /** Repository name. */
  name: string
  /** Absolute path of repos/{name}/main, null for a machine principal. */
  clonePath: string | null
  /** 40-character sha of local main, or null if clone/ref is missing. */
  mainSha: string | null
  /** Commits on local main not in origin/main, or null if unknown. */
  mainAhead: number | null
  /** Commits on origin/main not in local main, or null if unknown. */
  mainBehind: number | null
  /** Working tree of the main clone has uncommitted changes. */
  dirty: boolean
  /** Checked-out branch of the clone named main, or 'detached'. Null if unknown. */
  headCheckout: string | null
  /** Team branch name. */
  teamBranch: string
  /** 40-character sha of local team/{id}, or null if ref does not exist. */
  teamSha: string | null
  /** true = on origin, false = not on origin, null = unknown. */
  teamPushed: boolean | null
  /** The ref that answered teamPushed: team/{id}, origin/team/{id}, or null when unanswered. */
  teamPushedFrom: string | null
  /**
   * true = the team's work is on main; false = ANCESTRY SAID NO, which is not by itself a settled
   * "not merged"; null = unknown.
   *
   * FALSE IS NOT A VERDICT ON ITS OWN. Ancestry alone answers false for a branch that was rebased, squashed
   * or cherry-picked reads false here while every one of its changes is already upstream. It
   * becomes a settled "not merged" only once CONTENT has also been measured - that is
   * `teamCommitsNotOnMain` answering a non-zero count. Until then `mergeState` in
   * `lib/repoStatus.ts` answers `unknown` and the card says so, which is the BEHAVIOUR and not an
   * oversight: reading false as "definitely not merged" is the defect this field's readers exist
   * to stop.
   *
   * TRUE FOR BOTH MECHANISMS, WHICH IS WHY IT CANNOT BE THE ONLY FIELD. It covers ancestry and
   * also work whose shas were rewritten and whose changes are all upstream.
   * `teamMergedToMainBy` is what tells the two apart, and `mergeState` is the only place either
   * is read.
   */
  teamMergedToMain: boolean | null
  /**
   * HOW merged-ness was established, and the field every sentence about integration is derived
   * from.
   *
   * 'ancestry' - the team commits are reachable from main.
   * 'content'  - the team shas were rewritten, but every change is already on main. THE WORK IS
   *              MERGED, and the wording must say so plainly rather than implying a failure.
   * null/absent - NOT MERGED **OR** NOT MEASURED. Never read as "definitely not merged": that is
   *              the mistake this whole card exists to stop, and it is the same rule
   *              `teamCommitsNotOnMain` and `cloneMainOnTeamBranch` already carry.
   *
   * OPTIONAL ON PURPOSE. A status read that omits the field is `undefined` here, which must behave exactly like null rather than throwing or convicting.
   */
  teamMergedToMainBy?: 'ancestry' | 'content' | null
  /**
   * true = the clone's local main is reachable from the team ref, so commits on it are already
   * on the team branch; false = they are not; null = not measured (no team ref, or origin never
   * checked). Null is NOT false: false accuses, and the banner says so out loud.
   */
  cloneMainOnTeamBranch: boolean | null
  /**
   * How many commits on the team branch carry changes `origin/main` does not have, by PATCH-ID
   * rather than by sha - so work integrated with a cherry-pick counts as present. `0` means every
   * change is already upstream, possibly under different commits. `null` is NOT MEASURED and must
   * never be read as `0`.
   */
  teamCommitsNotOnMain: number | null
  /** ISO-8601 UTC of last fetch, or null if FETCH_HEAD is absent. */
  originCheckedAt: string | null
  /** Result of git ls-remote --exit-code, or null if not checked. */
  originReachable: boolean | null
  /** Git's error message if originReachable is false. */
  originUnreachableReason: string | null
  /** Worktrees in this clone (excluding main clone). */
  worktrees: WorktreeStatus[]
  /**
   * The repository's default branch — what every `main` / `origin/main` on this record
   * means. Null when it is NOT KNOWN: every main-relative field is null then too, and the actions
   * refuse. Never read a missing value as `main`.
   */
  defaultBranch?: string | null
  /**
   * Where `defaultBranch` came from: `person` (Team settings), `remote` (origin's HEAD), or null
   * (not known). A plain string: the icon-name scan reads every quoted lowercase literal.
   */
  defaultBranchSource?: string | null
  /**The clone's origin remote, credential removed. The fork, in contributor mode. */
  originUrl?: string | null
  /**
   *The clone's upstream remote in contributor mode, credential removed; null when owned.
   * When set, `mainAhead` / `mainBehind` are against `upstream/<defaultBranch>`.
   */
  upstreamUrl?: string | null
  /**The fork's account in contributor mode: the head owner of a pull request. Null when owned. */
  forkOwner?: string | null
  /**"Sign off commits (DCO)": Open pull request refuses a commit without Signed-off-by. */
  dcoSignOff?: boolean
  /**A person's note that the upstream's CLA is signed, shown beside Open pull request. */
  claSignedNote?: string | null
  /**The pull request recorded for the team branch, as GitHub last described it. */
  pullRequest?: RepoPullRequestStatus | null
  /**
   * Commits on `origin/<defaultBranch>` the team ref lacks: above 0 is what Bring current and
   * merge is for. Null when not measured (no team ref, never fetched, contributor mode).
   */
  teamBranchBehindDefault?: number | null
  /**
   * With the team branch behind, the files changed on both sides since they parted (at most 20).
   * Bring current and merge runs no tests, so a non-empty list is when the dialog says to run the
   * suites first. Null when not measured.
   */
  filesChangedOnBothSides?: string[] | null
  /**
   * A local `team/{id}` that origin lacks or holds an older commit of: "team/{id} is not pushed",
   * and Push publishes it. Null when there is no local team branch or origin was never fetched.
   */
  teamBranchUnpushed?: boolean | null
  /**Set when the clone's default branch holds commits origin's lacks - a Manager moved it: "moved
   * <default> in the clone; the work is on <commit>; the team branch is team/<id>". Measured on
   * every read; the platform resets nothing. Null when unmoved or not measured. */
  defaultBranchMoved?: string | null
  /** False when there is no working clone: none was made, or what is there is an empty clone (an
   * interrupted clone's `.git` and nothing else). The card reads it "Not ready" and offers Fetch,
   * which makes the clone. Absent from an older Host, which means ready. */
  cloneReady?: boolean
}

/**
 *The recorded pull request: GitHub's last answer and when it was read, and what it means
 * now. `landing` is `unknown` when GitHub could not be asked this time; `unknownReason` says why.
 */
export interface RepoPullRequestStatus {
  url: string
  number: number
  /** GitHub's last answer: open, closed (without merging) or merged. A plain string on purpose. */
  state: string
  /** ISO-8601 UTC of that answer. */
  readAt: string
  /** in-review, declined, landed or unknown. */
  landing: string
  unknownReason: string | null
}

/**What Open pull request starts with: GET .../pull-request-draft. */
export interface PullRequestDraft {
  title: string
  body: string
  citation: string | null
  workflow: number | null
}

/**What Fork it for me answers. */
export interface ForkResult {
  forkUrl: string
  forkOwner: string
  upstreamUrl: string
}

/** Response to GET /api/teams/{team}/repo-status. */
export interface TeamRepoStatus {
  /**
   * TWO NAMED FIELDS, BECAUSE THAT IS WHAT THE SERVER SENDS.
   *
   * `Contracts/RepoStatus.cs` sends `Git` and `Gh` as named properties, not an array. `json<T>()`
   * CASTS rather than checks, so a declared shape that does not mirror the record is `undefined`
   * at runtime and nothing anywhere says so: the whole Repos panel then renders "Not found on this
   * machine" with an empty message after it.
   *
   * A `find` over an array is a string lookup for something that already has a name. The shape
   * here must mirror the record, and `repoPanel` in `lib/repoStatus.ts` is the single place that
   * reads it.
   */
  git: Prerequisite
  gh: Prerequisite
  /** Repos and their status, one per team.repos entry. */
  repos: RepoStatus[]
}

/**
 * What every one of the seven repo actions answers: fetch, bring-current, rebase, push,
 * merge-to-main, delete-remote-branch and cleanup-worktrees.
 *
 * `status` IS WHY THIS EXISTS. The server composes it AFTER the git work, so a card repaints from
 * the response rather than firing a second request to ask what the first one just changed - and the
 * two answers cannot disagree in the window between them. This interface was declared here and in
 * `Contracts/RepoStatus.cs` and CONSTRUCTED BY NOBODY: every handler answered its own anonymous
 * object, so `status` was `undefined` at runtime on all seven while `client.ts` typed them all as
 * this. A record identical on both sides of the wire and populated by nobody passes a parity test
 * cleanly, which is why `RepoEndpointsTests.Every_repo_action_returns_the_status_it_produced`
 * asserts the property set off a LIVE response, in both directions.
 *
 * `success` IS NOT REDUNDANT WITH THE HTTP STATUS. The fetch route answers 200 when origin is
 * unreachable - a fact about the network, reported rather than refused - so this flag is the only
 * thing separating "fetched" from "could not reach origin". The reason travels in
 * `status.originReachable` and `status.originUnreachableReason`, not in fields of its own.
 */
export interface RepoActionResult {
  /** Repository name, derived from the URL rather than echoed from the caller's spelling. */
  repo: string
  /** Post-action status, composed by the server after the git work. */
  status: RepoStatus
  /** One sentence a person can read. */
  message: string
  /** Whether the action did what it set out to do. False on a reported unreachable origin. */
  success: boolean
}

/**
 * An image stored in the Concierge's own folder. `path` is absolute on the Host, and is what the
 * panel types into the prompt; `type` is what the Host found in the content, not the upload's name.
 */
export interface ConciergeAttachment {
  path: string
  size: number
  type: string
}

/**
 * The tenant-wide Concierge: which Agent runs it. What it is told is the built-in Concierge prompt,
 * chosen by role and never by a person.
 */
export interface ConciergeSettings {
  /** `null` when nobody has chosen one: the launcher picks an installed, signed-in preset - `effective.agent` names it. */
  agent: string | null
  /** What the launcher will actually run after defaults, and whether it can. OPTIONAL: absent
   *  means "not known", never "fine". */
  effective?: ConciergeEffective
  /** Every running Concierge session, any person's, oldest first. Absent from a server that does not list them. */
  sessions?: ConciergeSessionView[]
}

/**
 * One running Concierge session as `GET /api/concierge` lists it. A session is ended only when nobody
 * has had it open AND it has done nothing for `concierge.idleTimeout`; `wouldEndAt` is when that
 * would be, null while someone has it open. `memory` null is NOT MEASURED, never 0.
 */
export interface ConciergeSessionView {
  user: string
  email: string | null
  worker: string | null
  startedAt: string
  viewer: boolean
  lastViewerAt: string | null
  lastActivityAt: string
  /** What it last did: started, output, typed, attached or call. A plain string here, because a
   *  quoted word that is also an icon's name would be taken for one by the icon-font subset. */
  lastActivity: string
  callsInFlight: number
  /** `source` `default` is the platform's floor, never a measurement. */
  outputFloor: { bytesPerMinute: number; source: 'declared' | 'default'; measuredWith: string | null }
  /** Bytes printed in each of its last minutes, oldest first, the current minute last. */
  outputPerMinute: number[]
  wouldEndAt: string | null
  memory: { residentBytes: number; processes: number; sampledAt: string } | null
}

/**
 * The Concierge the NEXT launch will start: the stored choice where there is one, the server's
 * default where there is not, and the auth probe's verdict on that agent: the pre-flight.
 *
 * `agent` NULL means nothing installed can run it, and the panel does not open a socket.
 * `agentSource` says where it came from, so a default can be SAID rather than passed off as a choice.
 */
export interface ConciergeEffective {
  agent: string | null
  agentSource: 'chosen' | 'default'
  auth: ConciergeAgentAuth
}

/** The probe's verdict on the effective agent. `detail` is the probe's own sentence, if any. */
export interface ConciergeAgentAuth {
  installed: boolean
  signedIn: boolean
  detail: string | null
}

/**
 * One backlog item AS THE WIRE CARRIES IT, which is deliberately not the row the server stores.
 *
 * NAMED `...View` BECAUSE THE TWO SHAPES REALLY DIFFER, and `AgentLaunchWireParityTests` is what
 * made that explicit: a same-name C#/TypeScript pair must have IDENTICAL properties, which is the
 * protection that caught `AgentLaunch.LanguageModel` being silently dropped by a dialog. Sharing the
 * name here would have bought a false pair - `position` is deliberately never sent, and `teamName`
 * and `teamGone` are computed at render and exist in no row. Two different things wearing one name
 * is what the test refuses, and correctly.
 *
 * `id` IS RENDERED `B000H` (Crockford base 32, padded - see `itemLabel`), and the `#` COLUMN IS
 * SOMETHING ELSE ENTIRELY. The id never changes and is
 * never reused; the position index renumbers as things move and is computed here, client-side, from
 * the returned order. The server never sends it - two bare integers on one row, one of which
 * renumbers under you, is the confusion the prefix exists to prevent.
 */
export interface BacklogItemView {
  id: number

  /** The team this item is VISIBLE to, or null for the tenant's own. Not where it runs. */
  team: string | null

  /** What that team is called, or null when it is gone or there is no team. */
  teamName: string | null

  /** The linked team no longer exists. The screen says so. */
  teamGone: boolean

  title: string

  /** The spec itself, as markdown. Harness is the store of record for it. */
  body: string

  /**
   * `pending`, `ready`, `declared` or `implemented` - see `BacklogStates` in `lib/backlog.ts`.
   *
   * WHAT SOMEBODY SAID, AND NOTHING ELSE. A person is the authority on `pending`, `ready` and
   * `implemented`; the platform writes `declared` and only `declared`, from
   * `OnWorkflowCompletedAsync` when a Manager declares a dispatched workflow complete.
   *
   * A DECLARATION STOPS AT `declared`. Writing `implemented` would make one word carry two
   * claims - "an agent says it is done" and "the work is in the product" - with people reading it
   * as the second, while the only copy of the work might be unpushed in one team's clone.
   *
   * WHERE THE WORK ACTUALLY IS IS {@link BacklogItemView.landed}, WHICH IS A DIFFERENT FIELD ON
   * PURPOSE. Two facts, two fields: what was SAID and what the repository SHOWS. They compose -
   * "declared, pushed" and "declared, local" are different situations and a reader needs both
   * words to tell them apart - so nothing here folds the second into the first.
   */
  state: string

  /** The SECOND axis, independent of state. Null means it is in the backlog. */
  archivedAt: string | null

  /** The outcome this item serves, by id, or null; a dispatch links its workflow to it. Absent from an older server. */
  outcomeId?: string | null

  createdAt: string
  updatedAt: string
  createdBy: string

  /**
   * The item's CURRENT dispatch while its workflow is still OPEN, or null.
   *
   * DERIVED BY THE SERVER, from what the platform already holds: the last dispatch record and
   * whether its correlation has a terminal row nothing has woken since - the same predicate the
   * Teams table's open-workflow count uses. Nothing new is stored, the team link is NOT rewritten
   * and there is no third item state; this names who is WORKING the item, which is a different
   * fact from `team`, which names who may SEE it.
   *
   * Null means "not in flight" on the list and the detail, which compute it. A create or edit
   * response answers null without asking, and the screen reloads the list after every write.
   */
  inFlight: BacklogInFlight | null

  /**
   * The team the CURRENT dispatch went to, WHETHER OR NOT ITS WORKFLOW IS STILL OPEN - what the
   * Team column shows. Null, or absent, when the item was never dispatched.
   *
   * OPTIONAL BECAUSE THE SERVER DOES NOT SEND IT YET, and the client is written to take it the
   * moment it does. `BacklogEndpoints.Render` already has the latest dispatch in hand on the list
   * path; `inFlight` is derived from the same record but is DROPPED once the correlation closes,
   * so it answers this question only while somebody is still working. Until the field lands, an
   * item dispatched and finished renders an EMPTY Team cell rather than a wrong one - see
   * `dispatchedTeamId`, which is the one place that decides.
   */
  dispatchedTeam?: string | null

  /** That team's name. The dispatch record keeps it even after the team is deleted. */
  dispatchedTeamName?: string | null

  /**
   * WHERE THE WORK ACTUALLY IS, as opposed to what anybody said about it. Null when there is no
   * landing to report.
   *
   * DERIVED BY THE SERVER AND STORED NOWHERE - the same discipline `inFlight` follows, and the
   * whole point of the field. It is computed from the dispatch record plus the repository status
   * the platform already knows how to compute, so nothing new is written and nothing can go stale.
   *
   * IT IS NOT A STATE. {@link BacklogItemView.state} is what a person or a Manager SAID; this is
   * what the repository SHOWS, and folding the two into one word would make one word carry
   * two claims. An item can be `declared` and `local`, `declared` and `landed`, or `implemented` and
   * `unknown`, and every one of those is a real, different situation.
   *
   * NULL IS NOT `unknown`. Null means there is no landing question to answer - an item no dispatch
   * has ever touched - and renders nothing at all, exactly as `inFlight: null` does.
   * `landed.state === 'unknown'` means the question WAS asked and could not be answered, and that
   * renders as its own visible reading. *Nobody has said* and *it is not done* are different
   * facts and the screen must not substitute one for the other.
   *
   * OPTIONAL BECAUSE THE SERVER MAY NOT SEND IT. Until `BacklogEndpoints.Render` does, every row
   * answers `undefined` and the screen draws no mark - an absence,
   * not a wrong answer.
   */
  landed?: BacklogLanded | null

  /**
   * THE DISPATCH'S WORKFLOW WAS LEFT BEHIND: still open, Blocked or Failed, while a later workflow
   * on the same team took one of its cards to Done. The row says where the work continued and
   * offers a person the existing close route. An offer, never an act - nothing is closed until a
   * person presses it. Null or absent otherwise.
   */
  stranded?: BacklogStranded | null

  /**
   * WHETHER WHERE THE CURRENT DISPATCH STARTED WAS RECORDED. Only a dispatch with a recorded start
   * has `landed` kept after its team is gone. `false` carries `startDetail`, the sentence a person
   * reads; null or absent for an item never dispatched, or a dispatch where nothing was tried.
   */
  startRecorded?: boolean | null

  /** The sentence saying why the start was not recorded and what that costs. Null when it was. */
  startDetail?: string | null

  /**
   * A person may still press Record where it started now: the team is here and has not committed
   * on its branch since the dispatch.
   */
  startRecordable?: boolean
}

/** See `BacklogItemView.stranded`. Derived by `BacklogStrandedState` on the server. */
export interface BacklogStranded {
  teamId: string

  /** The dispatch's own workflow - the one a person may close. */
  workflow: number

  /** `Blocked` or `Failed`, the log's word for it. */
  state: string

  /** The latest later workflow one of its cards was done in. */
  continuedIn: number

  /** The original workflow's cards that were done later. */
  cards: string[]

  /** "The work continued in workflow N." - what the row says. */
  notice: string

  /** The person-only close route for `workflow`, and a suggested reason. */
  close: { route: string; by: 'person'; reason: string }
}

/**
 * WHAT THE REPOSITORY SHOWS ABOUT ONE ITEM'S WORK. See `BacklogItemView.landed`.
 *
 * FOUR READINGS AND THE LAST ONE IS LOAD-BEARING. `landed`, `pushed` and `local` are answers
 * somebody computed; `unknown` is the absence of one, and it is a value here rather than a null so
 * that "we looked and cannot tell" stays on the screen instead of vanishing into the same blank as
 * "there was nothing to look at".
 */
export interface BacklogLanded {
  /**
   * `landed`, `pushed`, `local` or `unknown` - see `LandedStates` in `lib/backlog.ts`, which
   * carries the client's copy of this vocabulary and the words each one renders as.
   *
   * A STRING RATHER THAN A UNION, for the reason `BacklogExecutionStats.outcome` is one: the
   * server may grow a fifth reading before this client does, and `landedMark` reads anything it
   * does not recognise as `unknown` - never as a negative, which would have an old client assert
   * something nobody measured.
   */
  state: string

  /**
   * The team whose clone was ASKED - the team the current dispatch went to, which is the same team
   * {@link dispatchedTeamId} resolves and the Team column already shows.
   *
   * IT PAIRS WITH {@link BacklogInFlight.teamId} AND DIFFERS FROM IT IN ONE WAY THAT MATTERS: that
   * one names a team that necessarily still exists, because an open workflow is running in it.
   * This one may name a team that has been DELETED - and that is not an error, it is the first
   * `unknown` the server derives, because the clone went with the team and nobody on that machine
   * can answer the question any more.
   *
   * NOT DRAWN ON THE SCREEN, AND THAT IS A DECISION RATHER THAN AN OVERSIGHT. The row already
   * carries this team in its Team column, under the NAME rather than the id, and the server writes
   * `detail` naming the repository and branch it actually looked at. A second, less readable copy
   * beside the mark would say nothing the row does not already say. It is on the wire because the
   * server sends it and parity is the whole point of these types; the screen is free not to use it.
   */
  teamId: string

  /**
   * ONE SHORT SENTENCE A PERSON CAN READ - which branch, which remote, how far behind. Composed by
   * the server, rendered in the mark's long form, and never parsed here.
   */
  detail: string

  /**
   *In contributor mode the answer is GitHub's, about the recorded pull request, and this is
   * when GitHub last gave it (ISO-8601). Null or absent for an answer read from the clone.
   */
  readAt?: string | null

  /**
   * WHEN `landed` WAS FIRST PROVEN AND STORED on the dispatch (ISO-8601). A stored landed is kept
   * after the branch, the clone and the team are gone, and never downgraded. Null or absent for
   * every other answer.
   */
  landedAt?: string | null
}

/** Where a backlog item is being worked right now. See `BacklogItemView.inFlight`. */
export interface BacklogInFlight {
  teamId: string

  /** The team's CURRENT label. The team exists, or this would be null on the item. */
  teamName: string

  /** The workflow - the `#1402` a dispatch notification names and `harness` takes. */
  correlation: number

  /**
   * Whether a member of that team is Running under this workflow AT THIS MOMENT. An open workflow
   * with nobody running is an ordinary shape - a Manager that has not declared yet - and the two
   * are shown differently because a person deciding whether to intervene wants to know which.
   */
  running: boolean
}

/** What one dispatch of an item did, derived from the log or frozen at archive. */
export interface BacklogExecutionStats {
  correlation: number
  members: string[]
  instructions: number

  /** `completed`, `failed`, or `unknown` - which is a real state, not a guess. */
  outcome: string

  tokens: number
  runsWithUsage: number

  /** THE NUMBER THAT STOPS THE TOTAL LYING BY OMISSION. Rendered beside it, never dropped. */
  runsWithoutUsage: number

  /** Null when the workflow has not finished. NULL IS "NOT MEASURED" AND IS NOT ZERO. */
  elapsedSeconds: number | null

  /**
   * The platform's check of each solution package this workflow wrote, from its `solution.checked`
   * rows. Null in stats frozen before the notice existed.
   */
  notices?: BacklogSolutionNotice[] | null
}

/** One package check, as the backlog item shows it: ready with its link, or the problems. */
export interface BacklogSolutionNotice {
  ok: boolean
  text: string
  folder: string
  name: string | null
  version: string | null
  link: string | null
  problems: string[]
}

export interface BacklogDispatchView {
  id: number
  teamId: string
  teamName: string
  correlation: number
  dispatchedAt: string
  dispatchedBy: string
  frozenAt: string | null
  teamGone: boolean
}

/**
 * One slot holder or waiter on the instance-wide WIP ledger, as `GET /api/wip` names it. A waiter's
 * `reason` is the ledger's own sentence for what holds it - "waiting for a slot", or "waiting for
 * memory: 11.2 of 12.9 GB in use" - and absent on a holder or from a Host that predates it.
 */
export interface WipHold {
  team: string
  member: string
  since: string
  reason?: string | null
}

/**
 * `GET /api/wip`: the one instance-wide count of running agent processes. `max` of 0 is no limit.
 * `waiting` is in the order the ledger admits it.
 */
export interface WipView {
  max: number
  running: WipHold[]
  waiting: WipHold[]
}

/** Where a tenant setting's value came from: a row somebody saved, or the deployment's file. */
export type TenantSettingSource = 'row' | 'appsettings'

/**
 * Where a setting's `default` comes from - what it would take with no row: the deployment's file
 * (`appsettings`) or the platform's own built-in default (`builtIn`).
 */
export type TenantSettingDefaultSource = 'appsettings' | 'builtIn'

/**
 * One instance-wide setting, `GET /api/tenant/settings`.
 *
 * `value` and `default` are whatever the setting holds - a number, a duration string, a lane map -
 * so they are `unknown` here and `lib/tenantSettings.ts` is the one place that reads them.
 */
export interface TenantSetting {
  name: string
  value: unknown
  default: unknown
  /** Which of the two `default` is. Null when the server does not say. */
  defaultSource: TenantSettingDefaultSource | null
  source: TenantSettingSource
  /** Null for a value read from appsettings: nobody changed it. */
  updatedAt: string | null
  updatedBy: string | null
  description: string
}

/**
 * One file-browser root. A DEPLOYMENT setting, never settable from the product: it rides the
 * settings list read-only as `fileBrowser.roots.<name>`, and `note` says the container must also
 * mount the path.
 */
export interface TenantRoot {
  name: string
  path: string
  note: string
}

/** The whole dialog's read: every setting, and the file-browser roots, which are not settable. */
export interface TenantSettings {
  settings: TenantSetting[]
  roots: TenantRoot[]
}

/** How a finished run ended, as `GET .../runs` words it. */
export type RunOutcome = 'completed' | 'handedBack' | 'blocked' | 'failed'

/** What became of one item of a run that carried several, as `GET .../runs` words it. */
export type RunItemOutcome = 'answered' | 'failed' | 'blocked' | 'deferred'

/** One item of a run that carried several: its number in the prompt, its seq, and its outcome. */
export interface MemberRunItem {
  item: number
  seq: number
  outcome: RunItemOutcome
  /** Why the agent deferred it; null for every other outcome. */
  reason: string | null
}

/** One finished run of one member, from the terminal row that closed it. */
export interface MemberRun {
  /** The terminal row's seq: the run's address for `runs/{seq}/transcript`. */
  seq: number
  /** The workflow the run worked on, or null when it had none. */
  workflow: number | null
  /** Null when the run's start row is not in the log; so then is `durationMs`. */
  startedAt: string | null
  endedAt: string
  durationMs: number | null
  outcome: RunOutcome
  /**
   * What the run reported, for a PLUGIN member's run: its output, or a failure's launch error. A
   * plugin run has no transcript to open, so this is its record. Null for an agent member's runs.
   */
  output: string | null
  /**
   * Why the run was blocked, for a plugin run that blocked every item it was given: such a run
   * wrote no completed or failed row, so it is listed from its last `blocked` row, with `output`
   * null. Null on every other run.
   */
  reason: string | null
  /** A run that finished quietly (`quiet: true` on its `completed` row), which woke nobody. */
  quiet: boolean
  /**
   * The run's workflow was declared complete by the time it ended (`workflowDeclared: true` on its
   * `completed` row) - by the platform for an owner that cannot declare, or by the member itself:
   * the Manager was not woken by it.
   */
  workflowDeclared: boolean
  /**
   * What became of each item, for a run that carried more than one - answered, failed, blocked or
   * deferred - in prompt order. A deferred item was not closed by this run: it is delivered again
   * as its own run. Null on a run of one item.
   */
  items: MemberRunItem[] | null
  /** For a deferred item's own run: the `started` seq of the run it was deferred from. Null otherwise. */
  deferredFromRun: number | null
}

/** One page of `GET .../runs`: newest first, and the cursor for the next older page or null. */
export interface MemberRunsPage {
  runs: MemberRun[]
  nextBefore: number | null
}

/**
 * One configuration field a plugin's manifest declares, as `GET /api/plugins` and the settings
 * route list it (`PluginEndpoints.Fields`). Every key is always sent.
 */
export interface PluginConfigField {
  /** `list` is a list of strings, default `[]`, its values limited by `enum` when one is given. */
  type: 'string' | 'number' | 'bool' | 'list'
  description: string
  required: boolean
  /** The manifest's default, as JSON. A list with none is sent as `[]`. */
  default: PluginSettingValue | null
  enum: string[] | null
  /** `person`: only a person may set it - a Manager hiring on the plugin cannot. */
  setBy: 'person' | 'anyone'
  /** A `number` field's bounds, when the manifest declares them. Absent from a Host older than bounds. */
  min?: number | null
  max?: number | null
  /** A `number` field that takes whole numbers only. */
  integer?: boolean
}

/** One secret a plugin's manifest names. Its NAME only: no route ever carries a value. */
export interface PluginSecretField {
  description: string
  required: boolean
}

/** A member hired on a plugin, as the plugins list names it. */
export interface PluginMemberRef {
  team: string
  member: string
}

/** An event a plugin publishes: its full type (`plugin.<id>.<suffix>`), and the manifest's one line on it. */
export interface PluginEvent {
  type: string
  summary: string
}

/** An installed plugin a person can hire, as `GET /api/plugins` lists it (`PluginEndpoints.Listing`). */
export interface InstalledPlugin {
  id: string
  /** `plugin:<id>`: what a member hired on it names as its Agent. */
  reference: string
  name: string
  description: string
  version: string
  /** Always true: the catalog loads only the version `active` points at. */
  active: boolean
  verdict: 'installed'
  protocol: string
  timeoutSeconds: number
  config: Record<string, PluginConfigField>
  secrets: Record<string, PluginSecretField>
  /** The events it publishes. */
  publishes: PluginEvent[]
  /** The manifest's skill files. */
  skills: string[]
  /** Its skill's name (`plugin-<id>` for its main one), or null when it has none. */
  skill: string | null
  /** Runtimes it needs from the image: `dotnet`, `node`, `python3`. */
  requires: string[]
  /** The members hired on it, team by team. Empty to a machine principal. */
  members: PluginMemberRef[]
  /** Its connection slots, by slot name; `{}` when the manifest declares none. Absent from a Host older than connections. */
  connections?: Record<string, ConnectionSlot>
  /** Manifest keys reserved for a later Host, and unknown keys it ignored. */
  reserved: string[]
  ignored: string[]
}

/** A plugin directory the Host did NOT load, with its sentence saying why. */
export interface RefusedPlugin {
  id: string
  reason: string
}

/**
 * One version folder under the plugins directory and the Host's verdict on it: `installed` for the
 * active version it loaded, `refused` (with `reason`) for an active version it would not load,
 * `inactive` for a version kept on disk and not in use. A refused plugin with no version folder is
 * one row with `version` null. A folder whose name starts with `.` is never listed here.
 */
export interface PluginVersion {
  id: string
  version: string | null
  name: string | null
  active: boolean
  verdict: 'installed' | 'refused' | 'inactive'
  reason: string | null
}

/** `GET /api/plugins`: the installed plugins, the directories that were not loaded, and every version folder. */
export interface PluginList {
  plugins: InstalledPlugin[]
  refused: RefusedPlugin[]
  versions: PluginVersion[]
}

/** `POST /api/plugins/install`: what the Host made of the folder. */
export interface PluginInstallResult {
  id: string | null
  version: string | null
  installed: boolean
  /** Whether it went over a version already installed (Replace ticked). */
  replaced: boolean
  reason: string | null
}

/** `DELETE /api/plugins/{id}`: what went - the whole plugin, or one version. */
export interface PluginRemoveResult {
  id: string
  /** The version removed; null when the whole plugin went. */
  version: string | null
  whole: boolean
  /** Every version folder removed. */
  versions: string[]
}

/** One stored setting value: a string, number or bool, or a `list` field's strings. */
export type PluginSettingValue = string | number | boolean | string[]

/**
 * `GET /api/teams/{team}/members/{member}/plugin-settings`: the member's stored settings (only
 * those that differ from default) and each secret's LOGICAL KEY - never a value - beside the
 * manifest's own declarations, so the editor is shaped by the version the member runs.
 */
export interface PluginMemberSettings {
  team: string
  member: string
  plugin: string
  version: string
  config: Record<string, PluginSettingValue>
  secrets: Record<string, string>
  fields: Record<string, PluginConfigField>
  secretFields: Record<string, PluginSecretField>
  /** Each slot's bound connection id. Never a token. */
  connections?: Record<string, string>
  /** The manifest's connection slots, as `GET /api/plugins` lists them. */
  connectionFields?: Record<string, ConnectionSlot>
  /**
   * Each STORED number outside its manifest bounds (bounds added after it was saved), with the
   * Host's sentence. Always present on the read and the PUT answer; `{}` when none is.
   */
  outOfRange?: Record<string, string>
}

/** A plugin member's settings on hire: config values, and each secret bound to a LOGICAL KEY. */
export interface PluginHire {
  config: Record<string, PluginSettingValue>
  secrets: Record<string, string>
  /** Slot -> connection id. Sent only for a plugin that declares slots; `{}` unbinds every slot. */
  connections?: Record<string, string>
}

/**
 * One connection slot a plugin's manifest declares, as `GET /api/plugins` lists it: which providers'
 * connections it takes and the scopes it needs from each, normalised to the object form.
 */
export interface ConnectionSlot {
  description: string | null
  /** `google`, `microsoft`, `custom` (any custom provider) or one `custom-<id>`. */
  providers: string[]
  /** Provider (or `custom`) -> the scopes the slot needs from a connection of it. */
  scopes: Record<string, string[]>
  required: boolean
  /** The Host's own words: "needs a Google or Microsoft connection". */
  summary: string
}

/** `google`, `microsoft`, or `custom` for any provider a person defined. */
export type ConnectionProviderKind = 'google' | 'microsoft' | 'custom'

/**
 * A provider and its OAuth client, as `GET /api/connections/providers` lists it. The client SECRET
 * is never sent: `clientSecretSet` says only whether there is one.
 */
export interface ConnectionProvider {
  /** `google`, `microsoft` or `custom-<id>`. */
  id: string
  kind: ConnectionProviderKind
  name: string
  clientId: string | null
  clientSecretSet: boolean
  /** Its client is set up (and, for a custom one, its URLs): Connect is possible. */
  configured: boolean
  authorizeUrl: string | null
  tokenUrl: string | null
  userinfoUrl: string | null
  /** Disconnect revokes the grant at the provider. */
  revokes: boolean
  defaultScopes: string[]
  /** One line: which client type to create and which redirect URI to register. */
  help: string
  /** Microsoft's tenant, when one is set. */
  tenant?: string | null
  /** How to set its client up, step by step. A built-in provider has one; no step carries a secret. */
  guide?: ConnectionGuide | null
}

/** A provider's setup guide: its steps in the order a person takes them. */
export interface ConnectionGuide {
  steps: ConnectionGuideStep[]
}

export interface ConnectionGuideStep {
  /** Google's, in order: `project`, `apis`, `branding`, `data-access`, `client`, `credentials`. */
  id: string
  title: string
  /** One or two sentences. */
  text: string
  /** A deep link, which may hold `{projectId}`. */
  link: string | null
  /** Values to copy at the provider: the redirect URI, a scope, an API. */
  copy: { label: string; value: string }[]
}

/**
 * `GET /api/connections/needs`: what the installed plugins' connection slots ask of a provider -
 * each slot with its scopes, the scopes merged with the plugins that want each, and the APIs to
 * turn on for them.
 */
export interface ConnectionNeeds {
  provider: string
  needs: { plugin: string; slot: string; description: string | null; scopes: string[] }[]
  /** `words` null: the Host has no wording, and the scope is shown as itself. */
  scopes: { scope: string; words: string | null; plugins: string[] }[]
  apis: { api: string; link: string }[]
}

/**
 * `PUT /api/connections/providers/{id}`. `clientSecret` omitted keeps the stored one; `""` clears it.
 * The URLs, name and default scopes are a custom provider's.
 */
export interface ConnectionProviderSave {
  clientId: string
  clientSecret?: string
  tenant?: string
  /**
   * Microsoft's guided setup: who can sign in. `common` is personal and any work account,
   * `organizations` work accounts only, `tenant` only the organisation `tenantId` names. Saved with
   * no secret: a public client.
   */
  audience?: MicrosoftAudience
  /** With `audience: 'tenant'`: the Directory (tenant) ID, a GUID. */
  tenantId?: string
  name?: string
  authorizeUrl?: string
  tokenUrl?: string
  userinfoUrl?: string
  defaultScopes?: string[]
}

/** A member using a connection, and in which slot. */
export interface ConnectionUse {
  team: string
  member: string
  label: string
  slot: string
}

/** One connected account, as `GET /api/connections` lists it. No token is ever on it. */
export interface Connection {
  id: string
  name: string
  provider: string
  providerKind: ConnectionProviderKind
  account: string
  scopes: string[]
  connectedAt: string
  refreshedAt: string | null
  status: 'ok' | 'needs-reconnect'
  statusReason: string | null
  usedBy: ConnectionUse[]
}

/** `POST /api/connections/start`. */
/** Who can sign in through a Microsoft app the guided setup saved. */
export type MicrosoftAudience = 'common' | 'organizations' | 'tenant'

export interface ConnectionStartRequest {
  provider?: string
  scopes: string[]
  name?: string | null
  reconnectId?: string | null
  redirectUri?: string | null
}

/** Its answer: where to send the browser. The PKCE verifier stays on the Host. */
export interface ConnectionStart {
  authorizationUrl: string
  state: string
  redirectUri: string
  expiresAt: string
}

/** `POST /api/connections/start` for sign-in with a code, at a provider with a device endpoint. */
export interface ConnectionDeviceStartRequest extends Omit<ConnectionStartRequest, 'redirectUri'> {
  flow: 'device'
}

/**
 * Its answer: the code the person enters at `verificationUri`, until `expiresAt`. The provider's
 * device code stays on the Host, which waits for the sign-in itself.
 */
export interface ConnectionDeviceStart {
  flowId: string
  userCode: string
  verificationUri: string
  expiresAt: string
}

/**
 * One of `GET /api/connections/flows/open`: a sign-in with a code the caller started that the Host
 * still holds - the code the person types, never the provider's device code.
 */
export interface ConnectionOpenFlow {
  flowId: string
  provider: string
  userCode: string
  verificationUri: string
  expiresAt: string
  state: ConnectionFlow['state']
}

/** `GET /api/connections/flows/{flowId}`: where a sign-in with a code is, in a sentence. */
export interface ConnectionFlow {
  state: 'waiting' | 'done' | 'refused' | 'expired'
  sentence: string
  /** When `done`: the connection it stored. */
  connection?: Connection | null
}

/**
 * One of the instance's local repositories, as `GET /api/local-repos` lists it: a bare repository
 * on the volume that a team names as `reference` (`local:<name>`) in its repository list.
 */
export interface LocalRepo {
  name: string
  reference: string
  sizeBytes: number
  /** Its HEAD. Null when HEAD names no branch. */
  defaultBranch: string | null
  lastCommit: { sha: string; subject: string; committedAt: string | null } | null
  /** Every team whose repositories name it, by id. */
  teams: string[]
  /**
   * No team's list names it (`teams` empty). A team's local repository is kept when the team is
   * deleted, so this is how one left behind is found and deleted. Optional only for an older Host.
   */
  unused?: boolean
  /** Its branches, by short name: what deleting it loses. Optional only for an older Host. */
  branches?: string[]
  /** How many commits its branches hold together; null when the Host could not count them. */
  commitCount?: number | null
}

/**
 * What a person may answer a refused repository check with, per URL, in `repoChoices` (B001F).
 * `create-on-github` and `attach-anyway` are a person's; an agent is only ever offered `use-local`.
 */
export type RepoChoice = 'create-on-github' | 'use-local' | 'attach-anyway'

/** One URL `git ls-remote` could not read, as the 422 names it. */
export interface RepoCheckFailure {
  url: string
  /** `not-found` (missing or not readable) or `unreachable` (a network failure). */
  failure: string
  /** Git's own words. */
  reason: string
  /** The choices THIS caller may send for this URL, in display order. */
  choices: string[]
}

/** The 422 `code: "repo-check-failed"` body: nothing was created, and each URL needs a choice. */
export interface RepoCheckRefusal {
  /** The server's sentence, naming the URL and git's reason. Shown as it came. */
  error: string
  code: 'repo-check-failed'
  repos: RepoCheckFailure[]
}

/** `POST /api/teams`'s answer: the team, and what was made for its repositories along the way. */
export type TeamCreated = Team & {
  /** The local repository attached (the default, or `use-local`). Absent when none was. */
  localRepository?: TeamLocalRepository
  /** The github.com URLs a `create-on-github` choice created. Absent when none. */
  createdOnGitHub?: string[]
}

/** The team's local repository, as a create or `POST /api/teams/{team}/local-repo` attached it. */
export interface TeamLocalRepository {
  name: string
  reference: string
  /** False when an unused one of that name was reused. */
  created: boolean
}

// --- Solution packages: check, preview, install, update ------------------------------------------
//
// The wire shapes of `/api/solutions/*` and `GET /api/teams/{team}/solution`, key for key as the
// Host writes them (camelCase). Kept together at the end of this file so another card appending
// here does not collide with this block.

/** One thing wrong with a package: the file, the field in it, and a sentence saying what to change. */
export interface SolutionRefusal {
  file: string
  field: string
  reason: string
}

export interface SolutionPlanPackage {
  id: string
  name: string
  version: string
  description: string
  folder: string
  readme: boolean
}

export interface SolutionPlanMember {
  name: string
  kind: 'agent' | 'plugin'
  role: 'manager' | 'member'
  preset: string | null
  instructions: string
  pluginId: string | null
  pluginVersion: string | null
  settings: Record<string, unknown>
}

export interface SolutionPlanPlugin {
  id: string
  name: string
  version: string
  description: string
  folder: string
  events: string[]
}

export type SolutionWakeManager = 'always' | 'onHandbackOrFailure' | 'never'

export interface SolutionPlanTrigger {
  name: string
  kind: 'schedule' | 'event' | 'folder'
  platformKind: string
  member: string
  instruction: string
  wakeManager: SolutionWakeManager
  dailyTokenCap: number | null
  idleOnly: boolean
  /** The schedule in words, when the Host gave one. */
  schedule: string | null
  cron: string | null
  timezone: string | null
  everySeconds: number | null
  eventType: string | null
  filter: string | null
  folderPath: string | null
  folderGlob: string | null
  /** Fired once right after the install's last step, then on its clock; `schedule` then says so. */
  runAtInstall?: boolean
}

export interface SolutionPlanSkill {
  name: string
  description: string
  roles: string[]
  file: string
  body: string
}

export interface SolutionPlanSite {
  name: string
  folder: string
  files: string[]
}

export interface SolutionPlanTools {
  folder: string
  installedAs: string
  files: string[]
}

export interface SolutionSettingInput {
  member: string
  setting: string
  description: string
  required: boolean
}

export interface SolutionConnectionInput {
  member: string
  slot: string
  description: string
  required: boolean
}

export interface SolutionDocumentInput {
  folder: string
  description: string
  required: boolean
}

/** A person-only setting the install asks for, with its manifest type, default and choices. */
export interface SolutionPersonSetting {
  member: string
  setting: string
  description: string
  required: boolean
  type: string | null
  default: unknown
  choices: string[] | null
  /** A number setting's bounds, from its plugin's manifest. Absent when it declares none. */
  min?: number | null
  max?: number | null
  integer?: boolean
}

/** What installing the package would create. */
export interface SolutionPlan {
  package: SolutionPlanPackage
  team: { name: string; instructions: string }
  members: SolutionPlanMember[]
  plugins: SolutionPlanPlugin[]
  triggers: SolutionPlanTrigger[]
  skills: SolutionPlanSkill[]
  sites: SolutionPlanSite[]
  tools: SolutionPlanTools | null
  inputs: {
    settings: SolutionSettingInput[]
    connections: SolutionConnectionInput[]
    documents: SolutionDocumentInput[]
  }
  personSettings: SolutionPersonSetting[]
  /**
   * Each connection input with its slot as the package's plugin declares it - known before the
   * plugin is installed, which the installed plugins and the needs read are not.
   */
  personConnections: SolutionPersonConnection[]
  ignored: string[]
}

/** One connection input of a plan: its member's plugin, and the providers and scopes the slot takes. */
export interface SolutionPersonConnection {
  member: string
  slot: string
  description: string
  required: boolean
  plugin: string | null
  providers: string[]
  /** Provider -> the scopes the slot asks of a connection of it. */
  scopes: Record<string, string[]>
}

/** `POST /api/solutions/check`. A 400 (a refused folder) is thrown with the Host's sentence. */
export type SolutionCheck =
  | { ok: true; folder: string; plan: SolutionPlan; refusals: SolutionRefusal[] }
  | { ok: false; folder: string; plan: null; refusals: SolutionRefusal[] }

/** One row of `GET /api/solutions/installed`: a team installed from a package. */
export interface InstalledSolution {
  team: string
  teamName: string
  id: string
  name: string
  version: string
  installedAt: string
  installedBy: string
  plugins: string[]
  /**
   * THE LAUNCHER'S FIELDS. Optional on the client only, so a row from a Host that predates
   * them still reads: the tile then shows no Open, no status line and no badge.
   */
  updatedAt?: string | null
  /** The source folder of the installed version. */
  folder?: string | null
  /** The package's `panel.primarySite`; null when it declares none, and then there is no Open. */
  primarySite?: SolutionPrimarySite | null
  /** The filled status line. Package text: rendered as text, never HTML. */
  status?: string
  state?: SolutionState
  paused?: boolean
}

/** A package's primary site. `url` is relative to the Host; unpublished means Open would 404. */
export interface SolutionPrimarySite {
  name: string
  url: string
  published: boolean
}

/** Which one holds, first that does: paused, blocked, running, capped, idle. */
export type SolutionStateKind = 'running' | 'idle' | 'blocked' | 'paused' | 'capped'

/** A solution's state and, for blocked and capped, why ("Upload a file to Resume/"). */
export interface SolutionState {
  kind: SolutionStateKind
  reason: string | null
}

/** A connection a picker offers, as the preview lists it. */
export interface SolutionConnectionOption {
  id: string
  name: string
  provider: string
  account: string
  status: string
}

export interface SolutionDiffSection {
  added: string[]
  changed: string[]
  removed: string[]
}

export interface SolutionDiff {
  members: SolutionDiffSection
  triggers: SolutionDiffSection
  skills: SolutionDiffSection
  sites: SolutionDiffSection
  tools: SolutionDiffSection
  plugins: SolutionDiffSection
}

/**
 * A secret a package's plugin member binds, by KEY NAME - never a value. `set` is whether the Host
 * has the key set (by name only). `needed` is false for a secret whose setting the person left off
 * (a source not ticked), true when needed, and null in a preview where it waits on the person's
 * answer to `when`'s setting. `setWith` is the exact way to set it. Unset is not a refusal: the
 * secret's source fails until it is set.
 */
export interface SolutionSecret {
  member: string
  field: string
  key: string
  description: string
  required: boolean
  when: { setting: string; value: string } | null
  set: boolean
  needed: boolean | null
  setWith: string
}

/** `GET /api/secrets/{key}`: whether the Host has a key set, by name. `refusal` names a key no
 *  plugin may be bound to, which is never `set`. */
export interface SecretKeyState {
  key: string
  set: boolean
  refusal: string | null
  setWith: string
}

/** `POST /api/solutions/preview`: what installing or updating would do. Writes nothing. */
export type SolutionPreview =
  | {
      ok: true
      mode: 'install'
      teamName: string
      nameRefusal: string | null
      plan: SolutionPlan
      connections: SolutionConnectionOption[]
      /** The package's secrets and whether the Host has each set (a Host before it answers without). */
      secrets?: SolutionSecret[]
    }
  | {
      ok: true
      mode: 'update'
      team: string
      teamName: string
      from: string
      to: string
      plan: SolutionPlan
      diff: SolutionDiff
      connections: SolutionConnectionOption[]
      /** The person's part the update keeps (a Host before it answers without). */
      kept?: SolutionKept
      /** The secrets as the update leaves them: a binding the person changed is kept. */
      secrets?: SolutionSecret[]
    }
  | { ok: false; error?: string; refusals?: SolutionRefusal[] }

/**
 * What an update keeps of the person's part: each kept member's person-only settings (null when
 * unset) and connection slots (the bound connection's id, null when unbound), and the files already
 * in each document folder the package asks for. An update never changes a kept member's settings
 * or bindings.
 */
export interface SolutionKept {
  settings: { member: string; setting: string; value: unknown }[]
  connections: { member: string; slot: string; connection: string | null }[]
  documents: { folder: string; files: string[] }[]
}

/** The install's steps, in order: `plugins`, `team`, `members`, `skills`, `tools`, `sites`, `triggers`, `record`. */
export interface SolutionStep {
  step: string
  number: number
  title: string
  done: boolean
}

/** An input still missing: while any is, the team shows as blocked. */
export interface SolutionMissing {
  kind: 'document' | 'connection' | 'setting'
  name: string
  member: string | null
  description: string
}

/** Setting values and connection bindings, by the package's member name. */
export interface SolutionInstallInputs {
  settings?: Record<string, Record<string, unknown>>
  connections?: Record<string, Record<string, string>>
}

export interface SolutionInstallRequest extends SolutionInstallInputs {
  folder: string
  teamName?: string
  agent?: string
  localRepository?: boolean
}

export interface SolutionUpdateRequest extends SolutionInstallInputs {
  folder: string
  team: string
}

/**
 * One schedule's first run after an install. `outcome` is `fired` when the install ran it
 * (`ranNow`), `scheduled` when it waits for its first due time, and for a first run at install that
 * did not happen the fire's own word (`skipped`, `capped`, `member-missing`) or `failed` - the run's
 * outcome, never the install's. `at` is when it ran, or when it first runs.
 */
export interface SolutionFirstRun {
  trigger: string
  member: string
  runAtInstall: boolean
  ranNow: boolean
  outcome: string
  at: string | null
  next: string | null
}

/** `POST /api/solutions/install` and `/update`. A 409 or 400 is thrown with the Host's sentence. */
export type SolutionInstallResult =
  | {
      ok: true
      team: string
      teamName: string
      version: string
      missing: SolutionMissing[]
      steps: SolutionStep[]
      diff?: SolutionDiff
      from?: string
      to?: string
      /** Every secret the team's plugin members bind, set or not, needed or not. */
      secrets?: SolutionSecret[]
      /** The keys still to set: bound, needed and not set on the Host. */
      unset?: string[]
      /** Each schedule's first run: ran now at install, or when it first comes due. */
      firstRuns?: SolutionFirstRun[]
    }
  | { ok: false; step: string; stepNumber: number; reason: string; steps: SolutionStep[] }
  | { ok: false; refusals: SolutionRefusal[] }

/** `GET /api/teams/{team}/solution`: the package a team came from and what it still waits for. */
export interface TeamSolution {
  team: string
  id: string
  name: string
  version: string
  installedAt: string
  installedBy: string
  plugins: string[]
  missing: SolutionMissing[]
}

/**
 * One thing an agent CLI's own listing says it would load. `off` is null when it would load,
 * otherwise why it does not (`disabled`, or the launch switch that turns it off).
 */
export interface ListedToolItem {
  kind: 'server' | 'connector' | 'plugin' | 'skill' | 'hook'
  name: string
  source: string | null
  off: string | null
}

/**
 * The pre-flight's word for a preset. `notMeasured` is never isolated, and `concierge` is
 * information, never a warning.
 */
export type ToolVerdict =
  | 'isolated'
  | 'foreignFound'
  | 'notVerified'
  | 'notMeasured'
  | 'concierge'
  | 'notAModel'

/** One preset's pre-flight, from `GET /api/agents/tools`. A measurement: never folded into an `Agent`. */
export interface PresetToolReport {
  preset: string
  mode: 'headless' | 'interactive'
  command: string
  verdict: ToolVerdict
  /** What a member would be offered that it may not be. Always empty for the Concierge. */
  foreign: ListedToolItem[]
  loaded: ListedToolItem[]
  switchedOff: ListedToolItem[]
  /** What no launch switch reaches, as the preset records it. */
  gaps: string[]
  ran: string[]
  detail: string | null
}

/** `GET /api/agents/tools`: the Host's last pre-flight. `at` is null before the first one ends. */
export interface AgentToolsReport {
  at: string | null
  running: boolean
  presets: PresetToolReport[]
}

// --- One solution's control panel, `GET /api/teams/{team}/solution/panel` -----------------
//
// Every string that comes from a package or from site data - names, descriptions, the status line,
// file names, run output - is PLAIN TEXT, rendered with `{{ }}` and never `v-html`.

export interface SolutionPanelMember {
  /** The package's own name for the member. */
  packageName: string
  /** The stored member name, for every `/members/{member}` and `/containers/{name}` route. */
  member: string
  kind: 'agent' | 'plugin'
  role: 'manager' | 'member'
  /** `missing`: the member is no longer on the team. */
  state: 'running' | 'idle' | 'missing'
  /** The container snapshot's own strings, null when there is nothing to say. */
  blocked?: string | null
  failed?: string | null
  needsDecision?: string | null
  queueDepth?: number
  lastRun: { seq: number; at: string; outcome: string } | null
}

/**
 * One of the package's triggers: EXACTLY the Triggers dialog's view of it (spend measured only,
 * unmeasured runs counted), plus the package's name and kind for it and whether Run now applies.
 */
export interface SolutionPanelTrigger extends TeamTrigger {
  packageName: string
  packageKind: 'schedule' | 'event' | 'folder'
  /** A schedule: Run now is offered. */
  runNow: boolean
}

/** What the team waits for, with how to fix it where it is shown. */
export interface SolutionPanelBlocked {
  kind: 'document' | 'connection' | 'setting'
  name: string
  member: string | null
  packageMember?: string | null
  description: string
  /** What to do, in a sentence: "Upload a file to Resume/". */
  reason?: string | null
  fix?: {
    upload?: { folder: string } | null
    connection?: { member: string; slot: string } | null
  } | null
}

/** One of `panel.settings`, in the package's order: shown before "All settings". */
export interface SolutionPanelSetting {
  member: string
  packageMember: string
  setting: string
  personOnly: boolean
}

export interface SolutionPanelFile {
  path: string
  name: string
  size: number
  modifiedAt: string
  /** The download route, relative to the Host. */
  download: string
}

/** One of `panel.outputs`: its files newest first, at most 50, `more` when there were more. */
export interface SolutionPanelOutput {
  folder: string
  exists: boolean
  files: SolutionPanelFile[]
  more: boolean
}

export interface SolutionPanelRun {
  member: string
  packageMember: string
  seq: number
  startedAt: string | null
  endedAt: string
  outcome: string
  /** A plugin member's output; null for an agent member's run. */
  output: string | null
  /** A blocked run's reason. */
  reason: string | null
  /** An agent member's run: its transcript can be read. */
  transcript?: boolean
}

export interface SolutionPanel {
  team: string
  teamName: string
  id: string
  name: string
  version: string
  description: string
  installedAt: string
  updatedAt: string | null
  installedBy: string
  folder: string
  paused: boolean
  state: SolutionState
  status: string
  primarySite: SolutionPrimarySite | null
  /**
   * Each site the package publishes, in its order, with whether it is published now. Optional on
   * the client only, so a panel from a Host that predates it still reads: then no Sites are shown.
   */
  sites?: SolutionPrimarySite[]
  members: SolutionPanelMember[]
  triggers: SolutionPanelTrigger[]
  blocked: SolutionPanelBlocked[]
  settings: SolutionPanelSetting[]
  outputs: SolutionPanelOutput[]
  recentRuns: SolutionPanelRun[]
}

/** `POST /api/teams/{team}/solution/uninstall`. The team and its documents stay. */
export interface SolutionUninstallResult {
  ok: boolean
  team: string
  id: string
  version: string
  removed: { triggers: string[]; members: string[]; skills: string[]; sites: string[]; tools: boolean }
  plugins: { removed: string[]; kept: { id: string; usedBy: string[] }[] }
  teamName?: string
  /** The team's documents folder, kept. */
  documentsKept: string | null
  /** Anything that could not be removed, one sentence each. */
  failures: string[]
}

/** Pressure stall figures for one resource: percent of wall time some (or all) work waited. */
export interface PressureLine {
  avg10: number
  avg60: number
  avg300: number
  totalUsec: number
}

export interface Pressure {
  some: PressureLine
  full: PressureLine | null
}

/** One run's process group, summed: who it is and what it uses. `cpuPercent` is of ONE CPU. */
export interface RunFigures {
  team: string
  member: string
  processes: number
  residentBytes: number
  cpuPercent: number | null
  /** The worker the run is on; absent from a server that does not say. */
  worker?: string | null
}

/**
 * ONE WORKER, as `GET /api/workers` lists it and each capacity sample's `workers` carries it: its
 * build, when it connected, `connected` or `dropped` (in its grace to come back, since `droppedAt`),
 * what it measured of its own container - every null figure NOT MEASURED, never 0 - its own `bound`
 * under the default run limit (null when the limit is set), what a run asking it now would wait for,
 * and the runs placed on it.
 */
export interface WorkerSample {
  id: string
  version: string | null
  connectedSince: string
  state: 'connected' | 'dropped'
  droppedAt: string | null
  capacity: {
    cpus: number | null
    memoryLimitBytes: number | null
    bound: number | null
    sampledAt: string | null
    memoryInUseBytes: number | null
    memoryPercent: number | null
    notMeasured: string[]
  }
  holding: string | null
  runs: { team: string; member: string; since: string }[]
  /** The people's Concierge terminals on it, with their process group's memory - null when not measured, never 0. Absent from a server that does not say. */
  terminals?: { user: string; since: string; residentBytes: number | null; processes: number | null; sampledAt: string | null }[]
}

/** A holder of, or waiter for, the heavy lease. `team` is null for the Concierge. */
export interface LeaseParty {
  team: string | null
  member: string
  since: string
}

/**
 * ONE CAPACITY SAMPLE, as `GET /api/capacity` serves it and `capacityChanged` pushes it. EVERY NULL
 * FIGURE IS NOT MEASURED - shown as "not measured", never as 0. A null limit with `unlimited` true is
 * "no limit". `heavyLease` null is "not available", not "nobody holds it".
 */
export interface CapacitySample {
  at: string
  cgroup: 'v2' | 'v1' | null
  cpu: {
    limitCpus: number | null
    unlimited: boolean
    usageUsec: number | null
    cpusInUse: number | null
    percentOfLimit: number | null
    throttledPeriods: number | null
    throttledUsec: number | null
    pressure: Pressure | null
  }
  memory: {
    limitBytes: number | null
    unlimited: boolean
    currentBytes: number | null
    anonBytes: number | null
    fileBytes: number | null
    shmemBytes: number | null
    inUseBytes: number | null
    percentOfLimit: number | null
    pressure: Pressure | null
  }
  pids: { current: number | null; limit: number | null; unlimited: boolean }
  notMeasured: string[]
  runs: {
    limit: number
    managerReserved: number
    runningCount: number
    waitingCount: number
    running: WipHold[]
    waiting: WipHold[]
  }
  admission: { memoryPercent: number; memoryPressurePercent: number; holding: string | null }
  topByMemory: RunFigures[]
  topByCpu: RunFigures[]
  heavyLease: { holders: number; holding: LeaseParty[]; queued: LeaseParty[] } | null
  /** Each worker's own figures and runs; absent from a server that does not say. */
  workers?: WorkerSample[] | null
}

/** `GET /api/capacity`: the sampler's interval, its latest sample and about ten minutes of history. */
export interface CapacityView {
  intervalSeconds: number
  latest: CapacitySample | null
  history: CapacitySample[]
}
