// What the renderer may ask for, through the preload bridge (window.yawble): two read-only calls (getInfo,
// getAvailableBackends), the screens' calls (core methods under UI_METHOD_PREFIXES), core pushes, and two window helpers.
export interface SandboxInfo {
  sdkVersion: string;
  nativeVersion: string;
  requestContract: string;
  telemetryEnabled: boolean;
}

/** The core's startup probe: what this host still needs before an agent can start (Linux: AppArmor, bwrap). */
export interface HostCheck {
  id: string;
  ok: boolean;
  message: string;
  fix: string | null;
}

export interface CoreInfo {
  product: string;
  version: string;
  platform: string;
  os: string;
  dotnet: string;
  pid: number;
  sandbox: SandboxInfo;
  schema: string[];
  hostChecks?: HostCheck[];
  /** A remote view's access, over the Cloud Link: "read" for a workgroup Visitor. Missing means "full". */
  remoteAccess?: RemoteAccess;
}

/** What a remote viewer may do: everything ("full": an Owner or a workgroup Member) or only look ("read": a Visitor). */
export type RemoteAccess = 'full' | 'read';

/** This build's version (core app.version; scripts/build-version.mjs). */
export interface AppVersion {
  /** yyyy.mm.dd.N for a release; <last release>+dev.<short sha>[.dirty] between releases; 0.0.0+dev.unknown without git. */
  version: string;
  /** The full commit, or 'unknown'. */
  commit: string;
  dirty: boolean;
  /** The release tag's own commit, built clean. */
  released: boolean;
  /** The cloud site's version while the Cloud Link is connected and the site said it; null otherwise. */
  cloudVersion?: string | null;
}

export interface AvailableBackend {
  backend: string;
  tier: string | null;
  capabilities: string[];
  warnings: string[];
}

// The in-app guardrail check (core: Guardrail/GuardrailService.cs). The core answers one IPC request at a
// time per connection and a check takes seconds, so the window starts it and polls:
//   guardrail.start {team?, member?} -> GuardrailStarted     (omit both: the global policy alone)
//   guardrail.get   {team?, member?} -> GuardrailState       (the last report survives a restart)
//   guardrail.stop  {team?, member?} -> GuardrailStopped     (withdraws a queued check, or stops its sandbox)
// The check is a run of the member through the core's sandbox launcher: a member's check waits for a global run slot
// (state 'running' while queued too), shows in runs.list and can be stopped with runs.stop; guardrail.start fails
// with "busy" while the member is running or queued. The window's panel is GuardrailPanel.vue (an agent card's
// shield button, the agent's Guardrails settings tab, and the global policy dialog for the global policy alone).
export type GuardrailVerdict = 'BLOCKED' | 'ALLOWED' | 'SKIP' | 'INFO' | 'MISSING';

export interface GuardrailSubjectParams {
  team?: string;
  member?: string;
}

export interface GuardrailResult {
  id: string;            // e.g. "app.grok-home", "write.host-home", "gateway.connect-denied-host"
  category: string;      // app-data | other-members | home | filesystem | network | gateway | process | credential-store | display | secrets | context | check
  verdict: GuardrailVerdict;
  expected: 'BLOCKED' | 'ALLOWED' | null; // null for INFO lines
  asExpected: boolean;
  evidence: string;
  sandboxOnly: boolean;  // created inside the sandbox only (the sandbox's writable tmpfs mask); never reached the host
  logged: boolean | null; // for gateway refusals: found in the gateway log
}

export interface GuardrailReport {
  id: string;
  subject: { team: string; member: string; cli: 'claude' | 'grok'; synthetic: boolean };
  os: 'linux' | 'macos' | 'windows';
  startedAt: string;
  durationMs: number;
  passed: boolean;
  counts: { blocked: number; allowed: number; unexpected: number; skipped: number; info: number };
  // status 0: the sandbox ran; -2: not started, a granted path is missing on the host; -1 or a sandbox status: refused.
  sandbox: { backend: string; status: number; exitCode: number; timedOut: boolean; refusal: string | null; stderr: string | null };
  allowHosts: string[];
  loosened: string[];
  notes: string[];
  results: GuardrailResult[];
  // The launcher's run the probe rode on (run_records.run_key; runNumber keys the gateway log). Null in reports
  // saved before the check ran through the launcher.
  run: {
    runKey: string;
    runNumber: number | null;
    end: 'completed' | 'failed' | 'timeout' | 'idle' | 'stopped' | 'interrupted' | 'start_failed' | 'credential';
    reason: string | null;
    backend: string;           // "yawble-ffi" (Linux, macOS) or "wxc-exec" (Windows)
    credentialMode: string;    // the mode the run's gateway was set up for; the probe never uses a credential
    credentialConfigured: boolean;
    killLatencyMs: number | null;
  } | null;
}

export interface GuardrailStarted {
  state: 'running';
  subject: { team: string | null; member: string | null };
}

export interface GuardrailStopped {
  stopped: boolean; // false when no check was running for the subject
}

export type GuardrailState =
  | { state: 'none'; error: string | null }
  // phase 'queued': waiting for another check or a global run slot (position in the run queue, or null);
  // phase 'running': the probe's sandbox is up.
  | { state: 'running'; phase: 'queued' | 'running'; position: number | null }
  | { state: 'failed'; error: string }
  | { state: 'done'; report: GuardrailReport };

/** A push from the core: terminal output and state. */
export interface CoreEvent {
  event: string;
  data: any;
}

export interface YawbleApi {
  getInfo(): Promise<CoreInfo>;
  getAvailableBackends(): Promise<AvailableBackend[]>;
  /** A core method of the screens (teams., members., policy., credentials., terminal., ...). */
  call<T = any>(method: string, params?: Record<string, unknown>): Promise<T>;
  /** Core pushes; returns the unsubscribe function. */
  onEvent(listener: (event: CoreEvent) => void): () => void;
  /** Opens (or focuses) the agent's terminal window (a remote view's; the desktop shows terminals in its right pane). */
  openTerminal(memberId: number): Promise<void>;
  /** Quits and runs the downloaded, verified update's installer, which starts the app again (desktop window only). */
  installUpdate?(): Promise<void>;
  /** How this copy is installed, for its updates (desktop window only). */
  updateInfo?(): Promise<UpdateInfo>;
  /** The desktop window's menu commands (Edit, zoom, reload, quit); absent in a remote view. */
  windowCommand?(command: WindowCommand): void;
  /** The OS folder picker, for a team's repository (null when the window has none: a remote view). */
  pickFolder(): Promise<string | null>;
  /** Whether an absolute path is a folder on this computer (desktop window only: a remote view cannot look). */
  folderExists?(path: string): Promise<boolean>;
  /**
   * Set when this window is a remote view of the instance (the same screens in a browser, through the Cloud
   * Link): screens hide what only the person at the computer may do.
   */
  readonly remote?: boolean;
  /** The desktop window's OS (win32, darwin, linux). Set only in the app's own windows, which draw their own title bar. */
  readonly platform?: string;
  /** Dims the window controls the system draws in the title bar while a dialog dims the page (desktop window only). */
  dimTitleBar?(dim: boolean): void;
  /** Colours the window controls the system draws in the title bar for the page's theme (desktop window only). */
  setTheme?(theme: 'light' | 'dark'): void;
  /** The app's own settings: running in the background, starting at sign-in (desktop window only). */
  shellSettings?(): Promise<ShellSettings>;
  setShellSettings?(patch: Partial<ShellSettings>): Promise<ShellSettings>;
  /** Opens a team's documents folder in the system's file manager (desktop window only). */
  openDocuments?(teamId: number): Promise<void>;
}

/** The Cloud Link's state (core Link/CloudLink.cs, link.status and the link.state push). */
export interface LinkStatus {
  state: 'off' | 'pairing' | 'connecting' | 'connected' | 'offline';
  url: string | null;
  /** The cloud site the dialog offers first (https://cloud.yawble.ai/). */
  defaultUrl?: string;
  /** While pairing: the site's pair page with the code filled in, opened in the person's browser. */
  pairUrl?: string | null;
  /** This computer's name, the name the site suggests for it. */
  machineName?: string;
  code: string | null;
  codeExpiresAt: string | null;
  instanceId: string | null;
  instanceName: string | null;
  org: string | null;
  workgroup: string | null;
  allowHostTerminals: boolean;
  /** "Show members' code to cloud viewers" (link.setShowCode {show}; on unless the person turned it off). */
  showCode?: boolean;
  viewers: string[];
  /** The cloud site's version while connected (newer sites say it in their welcome); null otherwise. */
  cloudVersion?: string | null;
  error: string | null;
}

export const Channels = {
  getInfo: 'core:getInfo',
  getAvailableBackends: 'sandbox:getAvailableBackends',
  call: 'core:call',
  event: 'core:event',
  openTerminal: 'window:openTerminal',
  pickFolder: 'dialog:pickFolder',
  folderExists: 'dialog:folderExists',
  shellSettings: 'shell:settings',
  setShellSettings: 'shell:setSettings',
  openDocuments: 'window:openDocuments',
  windowCommand: 'window:command',
  setTheme: 'window:setTheme',
  installUpdate: 'app:installUpdate',
  updateInfo: 'app:updateInfo',
} as const;

/** What the window's menu bar asks of the desktop app (Electron main does it for the window that asked). */
export const WindowCommands = ['undo', 'redo', 'cut', 'copy', 'paste', 'selectAll', 'reload', 'zoomIn', 'zoomOut', 'zoomReset', 'devtools', 'quit'] as const;
export type WindowCommand = (typeof WindowCommands)[number];

/** One line of ops.doctor (core Ops/DoctorRpc.cs). kind "open" is a screen of the app; "run" is ops.doctorFix. */
export interface DoctorFix { id: string; label: string; kind: 'open' | 'run'; confirm: boolean }
/** One column of a log (logs.sources): kind time, text or number; filter: the dialog offers its values. */
export interface LogColumn { key: string; label: string; kind: 'time' | 'text' | 'number'; filter: boolean }
/** One log the app keeps (logs.sources). retentionDays: 0 keeps it forever; null when it is never pruned. */
export interface LogSourceInfo {
  id: string; title: string; group: string; about: string; columns: LogColumn[];
  prunable: boolean; loss: string | null; keptBecause: string | null; defaultDays: number | null; retentionDays: number | null;
  /** Whether the team and member filters apply to it. */
  team: boolean; member: boolean;
  /** Whether its rows carry a level and it offers the Level filter (logs.query level; debug hidden by default). */
  levels?: boolean;
}
/** A page of a log (logs.query). total: on the first page only. next: the offset of the next page, null at the end. */
export interface LogPage { rows: Record<string, string | number | null>[]; total: number | null; asOf: string; next: number | null }
/** The teams and members for the filters (logs.who). key: "Team/Name". */
export interface LogWho { teams: string[]; members: { team: string; name: string; key: string }[] }
/** What pruning would delete (logs.prunePreview). */
export interface LogPrunePreview { source: string; title: string; cutoff: string; count: number; oldest: string | null; newest: string | null; loss: string; files: number }

/** git.check: the git found on this computer (any repository host works; git itself is required). */
export interface GitStatus {
  state: 'ok' | 'missing' | 'old' | 'broken';
  version: string | null;
  path: string | null;
  minimum: string;
  /** This computer can install or update it from the app (Windows with winget). */
  canInstall: boolean;
  detail: string | null;
  downloadUrl: string;
}

/** git.install: what it did, and the git found afterwards. */
export interface GitInstallResult { done: boolean; message: string; status: GitStatus }

export interface DoctorCheck { id: string; title: string; status: 'ok' | 'warn' | 'fail'; detail: string; fix: DoctorFix | null; fixes: DoctorFix[] }
/** One backup (ops.backups). */
export interface BackupItem { name: string; size: number; reason: string; time: string }

/** ops.restoreStatus (core Storage/BackupRpc.cs): a restore still staged, and how the last one went at the core's start. */
export interface RestoreStatus {
  pending: string | null;
  last: { backup: string; state: 'restored' | 'pending' | 'failed'; reason: string | null; at: string } | null;
}

/** yawble-companion (core Companion/CompanionRpc.cs; the window only, never a remote view). Levels are cumulative. */
export type CompanionLevel = 'read' | 'act' | 'destructive';
export interface CompanionClientInfo { name: string; level: CompanionLevel; pairedAt: string; pairedBy: string; lastUsedAt: string | null; revokedAt: string | null }
export interface CompanionPairing { id: string; name: string; level: CompanionLevel; code: string; expires: string }
/** A destructive call waiting for the person: `plan` is agent-influenced text, always shown as plain text. */
/** A companion request waiting for the person. `asked` is when it was made (ISO UTC, like `expires`; an older core leaves it out). */
export interface CompanionAction { id: string; client: string; tool: string; plan: string; expires: string; asked?: string; state: 'pending' | 'running' }
export interface CompanionList { clients: CompanionClientInfo[]; pairings: CompanionPairing[]; actions: CompanionAction[] }
/** companion.decideAction and companion.actionStatus (a read only): running for an approval just made (the run goes on in the core; companion.changed says when it ends),
 *  else how the request ended (changed: the list moved on, nextId is the new request). */
export interface CompanionActionStatus {
  id: string; state: 'pending' | 'running' | 'done' | 'failed' | 'denied' | 'expired' | 'changed'; plan: string; result: string | null; nextId: string | null;
}
/** companion.status: where the control endpoint listens (null when off, with `problem` saying why). */
export interface CompanionStatus { endpoint: string | null; controlFile: string | null; binaryPath: string | null; claudeCommand: string | null; problem?: string | null }

/** The app's own settings (Electron main, not the core): running in the background and starting at sign-in. */
/** packaged: an installed app (it can update itself), not a development run of the source. */
export interface ShellSettings { runInBackground: boolean; startAtLogin: boolean; startAtLoginRegistered?: boolean; packaged?: boolean }

/** concierge.defaults: the agent new Concierge conversations start on (null: the first one available on this computer). */
export interface ConciergeDefaults { defaultAgent: string | null }

/** A default member row for new teams: checked onNewTeams rows start in the New Team dialog's list, the others are offered. */
export interface TeamDefaultMember { name: string; cli: string; onNewTeams: boolean }

/** teams.defaults: the New Team defaults and the member-name list new members are named from. */
export interface TeamDefaults {
  manager: { name: string; cli: string };
  members: TeamDefaultMember[];
  memberNames: string[];
  country: string;
  countries: { code: string; label: string }[];
  /** How many names on the list a member of some team already has. */
  usedCount: number;
  /** The hire list new teams start with (absent from an older core). */
  hireList?: HireRow[];
}

/** roles.list: a role of the catalog (Settings > Teams > Roles). name is one word, also the name word on hire;
 *  job becomes the hire's standing instructions; agent and model are the suggestion (model null: the agent's default). */
export interface RoleRow { name: string; job: string; agent: string; model: string | null }

/** A row of a hire list (who the Manager may hire): a role, its agent and model (null: the agent's default). */
export interface HireRow { role: string; agent: string; model: string | null }

/** teams.hireList: a team's rows (role, agent, model) with the role's job. There is no count or maximum per role. */
export interface TeamHireRow extends HireRow { job: string }

/** teams.setDefaults: what was saved, and the member-name lines that were not (with why). */
export interface TeamDefaultsSaved { saved: unknown; rejected: { line: string; reason: string }[] }

/** updates.status: the app's own updates (core Updates/UpdateService.cs). */
export interface UpdateStatus {
  current: string; state: 'idle' | 'checking' | 'upToDate' | 'available' | 'downloading' | 'ready' | 'error' | 'managed';
  latest: string | null; available: boolean; notesUrl: string; canInstall: boolean; auto: boolean;
  checkedAt: string | null; error: string | null; downloaded: number; size: number;
  /** The organization manages updates (a Windows policy): the app checks nothing. */
  managed?: boolean;
  /** The download's publisher signature is checked (Windows); false on Linux, where only its SHA-256 and contents are. */
  checksPublisher?: boolean;
}

/**
 * How this copy is installed, for its updates (desktop window only). allUsers: installed for everyone on the computer,
 * so updating needs an administrator; admin: this person is one; otherUsers: people in other Windows sessions who have
 * the app open (an all-users update closes it for them).
 */
export interface UpdateInfo { allUsers: boolean; admin: boolean; otherUsers: number }

/** The only core methods the renderer may reach through Channels.call (checked again in Electron main). */
export const UI_METHOD_PREFIXES = ['app.', 'monitor.', 'board.', 'backlog.', 'teams.', 'members.', 'bus.', 'policy.', 'credentials.', 'hostTerminal.', 'terminal.', 'guardrail.', 'agents.', 'agentProfiles.', 'skills.', 'triggers.', 'git.', 'link.', 'concierge.', 'docs.', 'integrations.', 'tasks.', 'ops.', 'companion.', 'code.', 'logs.', 'updates.', 'network.', 'tools.', 'folder.', 'timeline.', 'deliverables.', 'workflows.', 'schemas.', 'roles.', 'goals.'] as const;

export function isUiMethod(method: unknown): method is string {
  return typeof method === 'string' && UI_METHOD_PREFIXES.some((p) => method.startsWith(p)) && /^[a-zA-Z.]+$/.test(method);
}

// ---- shapes of the core's answers (Ui/UiRpc.cs) ----

/** One supported agent CLI as installed on this computer right now (core Agents/DetectedCli.cs, agents.detect). */
export interface DetectedCli {
  cliId: string; // 'claude' | 'grok'
  found: boolean;
  launcherPath: string | null; // what PATH (or a known location) gave
  executablePath: string | null; // the real program after links and shims
  version: string | null; // what the CLI itself reports; null when --version failed
  installKind: 'native' | 'npm' | 'homebrew' | 'winget' | 'other';
  readOnlyFolders: string[]; // the exact folders the program needs, granted read-only
  configFolders: string[]; // the CLI's own config/credential folders, kept blocked
  problems: string[];
  installHelp: string; // shown when not found
  /** Whether the person is signed in to this CLI on this computer (the core's sign-in detection). */
  signIn?: { status: 'signed-in' | 'not-signed-in' | 'pending' | string; detail: string | null } | null;
}

/** agents.commandLine (core Agents/AgentInfoRpc.cs): the exact argv per run kind, per-run values as placeholders, env var NAMES only. */
export interface CommandLines {
  cli: string;
  /** The custom agent's name, or the built-in's id. */
  agent?: string;
  model: { model: string; source: 'member' | 'agent' | 'settings' | 'cli' | string; label: string }; // label shown as the core says it
  headless: CommandLineKind;
  interactive: CommandLineKind;
}
/** One run kind's command line: the whole argv, env NAMES, and (from the same core builder) the launcher tokens and built-in groups. */
export interface CommandLineKind { argv: string[]; env: string[]; launch?: string[]; groups?: ArgGroup[] }
/** One built-in argument (flag + value): locked = the app depends on it (reason says why); tail = it carries the prompt and stays last;
 *  removed = the customization described removes it. Per-run values are placeholders (<prompt>, <session id>, <workspace>/…). */
export interface ArgGroup { tokens: string[]; locked: boolean; tail: boolean; removed: boolean; reason?: string | null }
/** One model of agents.models and where the app found it; fromLabel is the hint shown beside it ("your Codex settings"). */
export interface AgentModelItem { model: string; from: 'cli' | 'settings' | 'runs' | 'builtin' | string; fromLabel: string }
/** agents.models: the models (no CLI is run for it); items, when the core sends them, says where each comes from. */
export interface AgentModelList { models: string[]; source: 'cli' | 'settings' | 'runs' | 'builtin' | string; items?: AgentModelItem[] }
export interface AgentVersionStatus { cli: string; detected: string | null; lastRun: string | null; changed: boolean }
/** agentProfiles.list: every agent one can choose (built-ins with their default model, then the custom ones). */
export interface AgentProfileRow {
  id: number; name: string; baseCli: string; custom: boolean; defaultModel: string;
  argsBefore: string[]; argsAfter: string[]; removedArgs: string[]; createdAt?: string; updatedAt?: string;
}
export interface ArgRefusal { argument: string; reason: string }
/** agents.test {cli, agent?, diagnostic}: the CLI's (or a custom agent's) TUI in a sandbox, on this terminal channel.
 *  agent: the name it runs as; commandLine: the exact interactive command line (also the terminal's first line). */
export interface AgentTestStarted { channel: string; diagnostic: boolean; agent?: string; commandLine?: string; runId?: string }
/** A live agent Terminal / Test session (agents.testSessions; window only). agent: the custom agent's name, or the CLI id. */
export interface AgentTestSession { cli: string; agent: string; kind: 'terminal' | 'test'; startedAt: string; runId?: string }
/** The push when one starts or ends (agents.testChanged; never over the Cloud Link). */
export const AGENT_TEST_CHANGED = 'agents.testChanged';

export interface TeamSummary {
  id: number; name: string; repoPath: string | null; members: number; running: number; queued: number; paused?: boolean; archived?: boolean;
  /** How many of its members have an open needs_decision (older cores: absent, read as 0). */
  waiting?: number;
}
/** Whether a team may be archived or deleted (teams.quiet): what is still working, and its open deliverables. */
export interface TeamQuiet { quiet: boolean; working: string[]; openDeliverables: number[] }
/** A team's deliverable (bus.deliverables): one piece of work and everything it led to. */
export interface DeliverableSummary {
  id: number; title: string; owner: string | null; state: DeliverableState; undeclared: boolean;
  busy: string[]; ending: string | null; startedAt: string; lastAt: string; messages: number;
  /** An ended deliverable's recorded end; for an expired one its last real activity, not the expiry time (older cores: absent). */
  endedAt?: string | null;
  /** When the app closed it as expired; null otherwise. */
  expiredAt?: string | null;
  /** When an open, idle deliverable would expire; null when it is not idle (a run, a wake or a trigger is pending). */
  expiresAt?: string | null;
  /** The question an open deliverable waits on the person for (its number), when the core names one. */
  question?: number | null;
  /** Its overall time and breakdown, computed by the core (older cores: absent). */
  time?: ItemTime | null;
}

// ---- Overall time and the timeline. The core computes every time; the screens only format. ----
/** The categories, each moment of a card or deliverable in exactly one (kebab-case on the wire). */
export type TimeCategory = 'agent-working' | 'stalled' | 'waiting-on-you' | 'waiting-for-slot' | 'waiting-on-teammate'
  | 'blocked' | 'scheduled' | 'not-started' | 'idle' | 'was-done';
/** How a card or deliverable ended: a card is done; a deliverable completed, closed, stopped or expired. */
export type TimeEndedHow = 'done' | 'completed' | 'closed' | 'stopped' | 'expired';
/** BoardCard.time and a deliverable item's time: created -> ended (or open, still counting), and where the time went. */
export interface ItemTime {
  createdAt: string;
  /** A card: the first time it had a member and was queued or told. A deliverable: its first card started. */
  startedAt?: string | null;
  endedAt?: string | null;
  endedHow?: TimeEndedHow | null;
  open: boolean;
  /** created -> ended, or created -> the core's now when open (the screens tick it on from there). */
  overallMs: number;
  /** Built from the trail lines of an older card (before the core recorded timelines). */
  rebuilt: boolean;
  /** Adds up to overallMs exactly. */
  breakdown: { category: TimeCategory; ms: number }[];
}
export type TimelineMarkerKind = 'planned' | 'started' | 'reopen' | 'handback' | 'decision-asked' | 'decision-answered' | 'watchdog-end' | 'expired';
/** One stretch of one category; end null (or absent) while it is still going. */
export interface TimelineBand { category: TimeCategory; start: string; end?: string | null }
export interface TimelineRow { card?: number | null; member?: string | null; title?: string | null; bands: TimelineBand[] }
export interface TimelineMarker { kind: TimelineMarkerKind; at: string; card?: number | null }
/** timeline.get { card } | { deliverable }: a card's own row, or a deliverable's rows (one per card) and its summary row. */
export interface Timeline {
  time: ItemTime;
  rows: TimelineRow[];
  /** A deliverable only: the merged row (Agent working > Waiting on you > Stalled > ...). */
  summary?: TimelineBand[] | null;
  markers: TimelineMarker[];
}
export const TIMELINE_GET = 'timeline.get';
/** A deliverable's state (bus.deliverables): "expired" is closed by the app after deliverableExpiryDays without activity. */
export type DeliverableState = 'open' | 'completed' | 'closed' | 'expired';
/** The RPC that resets an open deliverable's expiry clock ({ teamId, deliverable }). */
export const DELIVERABLE_EXTEND = 'bus.deliverableExtend';
/** A team's "expire idle deliverables after N days" setting ({ teamId, days? } -> { days }); default 3. */
export const TEAM_DELIVERABLE_EXPIRY = 'teams.deliverableExpiry';
export const DeliverableExpiryDefaultDays = 3;
/** A team's "Allow localhost (macOS)" ({ teamId, on? } -> { on }); off by default. Setting it is a full-access change. */
export const TEAM_LOCALHOST = 'teams.localhost';
export const TEAM_LOCALHOST_LABEL = 'Allow localhost (macOS)';
/** The warning shown with the toggle, word for word. */
export const TEAM_LOCALHOST_WARNING = "Agents on this team can open local ports and connect to any service listening on this Mac's localhost (your dev servers, local databases). Needed for classic `dotnet test` (VSTest) and some other test runners. While it is on, a server an agent starts on `0.0.0.0` can be reached from other machines on your network unless the macOS firewall blocks it. Keep the macOS firewall on. Outbound traffic still goes only through the gateway.";
/** The time a deliverable's row shows: its recorded end once ended (an expired one: its last activity), else its last entry. */
export function deliverableTime(w: Pick<DeliverableSummary, 'state' | 'lastAt' | 'endedAt'>): string {
  return w.state !== 'open' && w.endedAt ? w.endedAt : w.lastAt;
}
// ---- The Deliverables page (the core's deliverables.list / deliverables.get contract): every team's deliverables in one table ----
export const DELIVERABLES_LIST = 'deliverables.list';
export const DELIVERABLES_GET = 'deliverables.get';
/** The Status column: an ended deliverable's state, or how an open one stands (first match: needs-you, running, waiting, stopped, idle). */
export type DeliverableRowStatus = 'running' | 'needs-you' | 'waiting' | 'idle' | 'stopped' | 'completed' | 'closed' | 'expired';
/** The backlog item a deliverable was dispatched from (archived: open it in the Backlog's archive). */
export interface DeliverableItem { label: string; title: string; archived: boolean }
/**
 * Why a member's delivery has not started (the core's worker states). queued: in the run queue, waiting for a run
 * slot (the only one that counts as Waiting for a slot on the timeline); waits-for-run: a hand-back held until the run
 * that sent it ends (waitsFor: the sender's name); held: the team is paused or archived; pending: anything else.
 */
export type WorkerWaitState = 'queued' | 'waits-for-run' | 'held' | 'pending';
/** Who works a deliverable now: its run is active, waits for the heavy-work lease, or a delivery waits (WorkerWaitState). */
export interface DeliverableMember {
  id: number; name: string; state: 'running' | 'lease' | WorkerWaitState;
  /** waits-for-run: the member whose run must end first. */
  waitsFor?: string | null;
}
/** A deliverables.list row: every DeliverableSummary field (title cleaned), plus the Deliverables page's fields. No tokens, no cost. */
export interface DeliverableRow extends DeliverableSummary {
  ownerId: number | null;
  team: string; teamId: number;
  status: DeliverableRowStatus;
  /** slot only while a member is in the run queue; else what the undelivered work waits for (WorkerWaitState words). */
  waitingFor: 'slot' | 'lease' | 'wake' | 'waits-for-run' | 'held' | 'pending' | null;
  /** A run of it is active right now (the icon is animated), whatever the status. */
  working: boolean;
  item: DeliverableItem | null;
  members: DeliverableMember[];
  /** The goal it is linked to, or null (Unassigned). Older cores: absent. */
  goal?: GoalRef | null;
}
/** The chips' numbers, over the team and text filters, before the state filter and the limit. */
export interface DeliverableCounts {
  all: number; open: number; needsYou: number; done: number; closed: number;
  running: number; waiting: number; idle: number; stopped: number; completed: number; expired: number;
  completedToday: number;
}
export interface DeliverablesListAnswer {
  deliverables: DeliverableRow[]; counts: DeliverableCounts; truncated: boolean;
  /** The session may read the backlog: the item label is a link (else plain text) and the spec is shown. */
  backlog: boolean;
  now: string;
}
export interface DeliverableCardRow { id: number; title: string; lane: LaneId; member: string | null; status: CardStatus | string; time?: ItemTime | null }
export type DeliverableMessageKind = 'instruction' | 'progress' | 'handback' | 'blocked' | 'decision' | 'completed' | 'failed' | 'stopped' | 'done' | 'closed' | 'expired';
export interface DeliverableMessage { seq: number; at: string; kind: DeliverableMessageKind | string; from: string | null; member: string | null; text: string | null }
export interface DeliverableDetail { deliverable: DeliverableRow; cards: DeliverableCardRow[]; messages: DeliverableMessage[]; spec: string | null; backlog: boolean }

// ---- Goals (the core's goals.* contract): the person's goals, each linking deliverables; a deliverable has at most one ----
export const GOALS_LIST = 'goals.list';
export const GOALS_SAVE = 'goals.save';
export const GOALS_DELETE = 'goals.delete';
export const GOALS_ASSIGN = 'goals.assign';
/** A deliverable's goal as its row names it. */
export interface GoalRef { id: number; name: string }
/** A goal's (or the Unassigned group's) time: the union of its deliverables' overall-time spans, overlaps counted once. */
export interface GoalTime {
  firstAt: string | null;
  /** null while one of its deliverables is open (the time counts on from the answer's now). */
  endAt: string | null;
  elapsedMs: number;
}
export interface GoalTotals { deliverables: number[]; open: number; total: number; time: GoalTime }
export interface Goal extends GoalTotals { id: number; name: string; description: string; createdAt: string; updatedAt: string }
export interface GoalsListAnswer { goals: Goal[]; unassigned: GoalTotals; now: string }
export interface GoalAssignAnswer { deliverable: number; goal: GoalRef | null }
export interface GoalDeleteAnswer { deleted: number; unassigned: number }
/** The longest goal name and description goals.save takes. */
export const GoalNameMax = 80;
export const GoalDescriptionMax = 2000;

/** A team's documents (docs.list / docs.read): paths are relative to the team's documents folder. */
export interface DocEntry { name: string; path: string; folder: boolean; size: number | null; modified: string }
export interface DocList { path: string; entries: DocEntry[] }
export interface DocFile {
  path: string; name: string; size: number; modified: string;
  kind: 'markdown' | 'text' | 'image' | 'binary' | 'large'; text?: string; dataUrl?: string;
}
/** An integration (integrations.list): one of the person's CLIs, where it is enabled, and its rules. */
export interface IntegrationInfo {
  name: string; label: string; description: string; known: boolean; enabled: boolean; all: boolean; teams: number[];
  path: string | null; version: string | null; status: string | null; floors: string[];
  /** The command that signs this CLI in (shown for a terminal login), or null. */
  login: string | null; counts?: DiscoverChecklistData['counts'] | null;
  /** True when the person added or configured this CLI (it has an integration row); the catalog lists the rest. */
  configured?: boolean;
  preset: { pattern: string; decision: 'allow' | 'ask' | 'deny' }[];
  rules: { id: number; teamId: number | null; team: string | null; pattern: string; decision: 'allow' | 'ask' | 'deny' }[];
}
/** What a row shows and what Save can send for it. never = a sign-in row, set by the app only. */
export type RowState = 'allow' | 'ask' | 'off' | 'never';
export type ChoiceState = 'allow' | 'ask' | 'off' | 'default' | 'undecided';
/** One command of a discovered CLI (integrations.checklist). state: free = tickable, refused = destructive, locked = reveals the sign-in. */
export interface DiscoverNode {
  path: string; parent: string; name: string; depth: number; summary: string; hasChildren: boolean;
  class: 'readonly' | 'change' | 'destructive' | 'credential' | 'unknown' | 'group' | string; source: string; reason: string;
  state: 'free' | 'refused' | 'locked'; ticked: boolean; tickedFrom: string | null; defaultTick: boolean; decision: string | null;
  gone: boolean; badge: 'new' | 'changed' | null; seenVersion: string | null;
  suggested: RowState | null; defaultDecision: 'allow' | 'ask' | 'deny' | null; teamDecision: 'allow' | 'ask' | 'deny' | null;
  effective: 'allow' | 'ask' | 'deny' | 'never'; origin: 'discovered' | 'found' | 'builtin';
  /** An API action's tag, HTTP method and route (when the core sends them); the checklist groups by tag and shows "METHOD route". */
  tag?: string | null; method?: string | null; route?: string | null;
}
/** The checklist of one CLI for a scope (integrations.checklist; teamId null = all teams). */
export interface DiscoverChecklistData {
  cli: string; teamId: number | null; version: string | null; discoveredAt: string | null; runs: number; partial: boolean; partialReason: string | null;
  error: string | null; new: number; changed: number; gone: number; choices: number; nodes: DiscoverNode[];
  kind: 'cli' | 'mcp' | 'api'; offeredAll: boolean; offeredTeams: number[]; counts: { allowed: number; ask: number; off: number; undecided: number };
  otherRules: { id: number; teamId: number | null; pattern: string; decision: 'allow' | 'ask' | 'deny' }[];
}
/** The walk's state (integrations.discover / discoverStatus). */
export interface DiscoverJobData {
  state: 'idle' | 'running' | 'done' | 'failed' | 'cancelled' | string; cli?: string; found?: number; walked?: number; current?: string; seconds?: number; error?: string | null; started?: boolean;
}
/** An environment variable of a local MCP server: a secret one never carries its value (`set` and a masked `shown` instead). */
export interface McpEnvView { name: string; secret: boolean; value?: string; set?: boolean; shown?: string | null }
/** An MCP server added as an integration, local or remote (integrations.mcpList). */
export interface McpServerInfo {
  name: string; command: string; args: string[]; status: string | null; all: boolean; teams: number[];
  transport: 'stdio' | 'http'; url: string | null;
  auth: { type: 'none' | 'bearer' | 'header' | 'oauth'; header?: string } | null;
  env: McpEnvView[];
  connection: { id: number; name: string; status: string; statusReason: string | null } | null;
  sources: string[];
  counts?: DiscoverChecklistData['counts'] | null;
  tools: { name: string; description: string }[];
  rules: { id: number; teamId: number | null; team: string | null; pattern: string; decision: 'allow' | 'ask' | 'deny' }[];
}
/** One field of a Connection's sign-in. `shown` is the masked value for a secret (never the secret), the stored text otherwise; `set` says a value is stored. */
export interface ConnectionField { key: string; label: string; secret: boolean; shown: string | null; set: boolean }
/** A shared sign-in, masked (integrations.connections / connectionSave). `usedBy` names the APIs using it. */
/** 'website' is a login kept in the app window's own session: one non-secret field `site`, no secret.
 * 'oauth' is a browser sign-in with the person's own app: secrets access_token, refresh_token, client_secret (masked); shown in full:
 * client_id, authorize_endpoint, token_endpoint, granted_scopes (space-separated), expires_at and provider (when one was given). */
/** 'email': a mailbox over IMAP and SMTP (fields address, imapHost, imapPort, smtpHost, smtpPort, user; password secret). */
export type ConnectionKind = 'secret' | 'oauth' | 'website' | 'email';
export interface Connection {
  id: number; name: string; kind: ConnectionKind; fields: ConnectionField[];
  status: 'untested' | 'works' | 'needs_reconnect'; statusReason: string | null; testedAt: string | null; usedBy: string[];
  /** The host tasks with an environment variable from this Connection; removing it stops them starting. Absent from an older core. */
  usedByTasks?: ConnectionTaskUse[];
}
/** A host task that binds a Connection: its team and its name. */
export interface ConnectionTaskUse { teamId: number; taskName: string }
/** A web service added by its URL (integrations.apiList). `signIn` says what to ask for, from the spec's auth; null when the auth is not known yet. */
export interface ApiRow {
  /** 'website': a site the agents use in a browser window on this computer (six fixed actions); its sign-in is its Connection, never fields. */
  kind: 'http' | 'website';
  name: string; baseUrl: string; docsUrl: string | null; specUrl: string | null; title: string | null;
  connection: { id: number; name: string; status: Connection['status']; statusReason?: string | null } | null;
  /** chosen: the person said how it signs in (the spec did not). */
  /** pathParams: a path parameter (its name in the route) filled from a Connection field that is not secret, e.g. { AccountSid: 'username' }. */
  auth: { type: 'bearer' | 'header' | 'query' | 'basic' | string; name: string | null; description: string | null; chosen?: boolean; pathParams?: Record<string, string> | null } | null;
  /** The path parameters (names in the route) the API's operations take; null for a website. */
  pathParamNames?: string[] | null;
  /** Those nearly every operation takes (an account id in every address, e.g. Twilio's AccountSid): offered to fill from the sign-in. */
  pathParamCommon?: string[] | null;
  signIn: { label: string; description: string | null; fields: { key: string; label: string; secret: boolean }[] } | null;
  /** An OAuth 2 API (auth.type 'oauth2'): the spec's addresses (null when it gave none usable, or only an OpenID address), the scopes the
   * allowed and ask-first actions need, and those its Connection was not granted (non-empty: "Sign in again to allow the new actions"). */
  oauth?: { authorizeUrl: string | null; tokenUrl: string | null; openIdConnectUrl?: string | null; neededScopes: string[]; missingScopes: string[] } | null;
  status: string | null; all: boolean; teams: number[]; counts: DiscoverChecklistData['counts'] | null;
}
/** integrations.emailList: a mailbox (an email account Connection) that workflows on the teams it is turned on for can read. */
export interface EmailIntegration { id: number; name: string; kind: 'email'; connectionId: number | null; connection: unknown; enabledAll: boolean; teamIds: number[] }
/** integrations.apiPeek { url }: what Add API fills in from one address (nothing is saved). */
export type ApiPeekResult =
  | { found: true; specUrl: string; title: string | null; serviceUrl: string | null; name: string; actions: number; signIn: string | null }
  | { found: false; reason: string; serviceUrl: string; name: string };
/** integrations.oauthDiscover { provider }: the provider's addresses and offered scopes from its metadata, or found: false (use Advanced). */
export type OAuthDiscoverResult =
  | { found: true; issuer: string; authorizeUrl: string; tokenUrl: string; scopes: string[] }
  | { found: false };
/** integrations.oauthSignIn params: with `api`, the addresses and scopes come from the API (given addresses must match); an empty
 * clientSecret with connectionId keeps the stored one. */
export interface OAuthSignInParams {
  connectionId?: number; name?: string; provider?: string; authorizeUrl?: string; tokenUrl?: string; clientId: string; clientSecret?: string;
  scopes?: string[]; api?: string;
}
export interface OAuthSignInResult { ok: true; connection: { id: number; name: string; status: Connection['status']; statusReason: string | null } }
/** A watch (integrations.watches): an integration's command looked at every few minutes; new items become events. */
export interface WatchInfo {
  id: number; teamId: number; name: string; integration: string; args: string[]; keyField: string; eventName: string; intervalSec: number;
  enabled: boolean; nextDueAt: string | null; lastOutcome: string | null; lastReason: string | null;
}
/** What an ask about a host task shows the person. */
export interface TaskAskDetail {
  commit: string; program: string; workdir: string; args: string[]; timeout_s: number; env_names: string[]; pin_mode: 'files' | 'commit';
  pinned: string[]; warnings: string[]; standingApproval: boolean;
  /** Each binding as shown ("GH_TOKEN ← Connection 'Name' (field)"): names only, never a value. */
  env_from_connections?: string[];
  changed?: {
    approvedCommit: string; currentCommit: string; diffAvailable: boolean; definitionHashChanged: boolean; filesHashChanged: boolean;
    definition: { field: string; old: string | null; new: string | null }[];
    files: { path: string; change: 'added' | 'removed' | 'changed'; oldBlob: string | null; newBlob: string | null; diff?: string; diffMoreLines?: number }[];
    commits?: string | null; stat?: string | null;
  };
}
/** A call waiting for the person's approval (integrations.asks). Host-task asks have task = true and a detail. */
export interface IntegrationAsk {
  id: number; at: string; integration: string; teamId: number; member: string; command: string; reason: string; deliverable: number;
  repo?: string | null; action?: string | null; canAlways?: boolean; task?: boolean; taskName?: string; commit?: string; detail?: TaskAskDetail | null;
}
export interface HostTaskDef {
  name: string; repo: string; program: string; args: string[]; workdir: string; timeoutSeconds: number; envNames: string[]; watch: string[];
  pinMode: 'files' | 'commit'; commandLineOnly: boolean;
  /** Environment variables whose value is read from a Connection's secret field at start. The binding is pinned, never the value. */
  envFromConnections?: EnvFromConnection[];
  /** One line on what the task does, shown on the task row and to the Concierge. Omitted when empty. */
  description?: string;
  /** What to check once it has run, e.g. a health URL. Omitted when empty. */
  checkAfter?: string;
}
/** One binding: variable `name` takes the secret `field` of Connection `connectionId`; `connectionName` is for display only. */
/** The core sends connectionId as a string ("7") and accepts a number or a string back; the editor normalises it to a number on load. */
export interface EnvFromConnection { name: string; connectionId: number | string; field: string; connectionName: string }
/** A host task run record (tasks.runs / integrations.taskRuns). */
export interface TaskRun {
  id: number; task: string; teamId: number; state: 'running' | 'done' | 'interrupted' | 'refused'; startedAt: string; endedAt: string | null; commit: string | null;
  headAfter: string | null; definitionHash: string | null; filesHash: string | null; program: string | null; exitCode: number | null; timedOut: boolean;
  killedBy: 'timeout' | 'app-exit' | 'person' | null; outputBytes: number | null; truncated: boolean; logPath: string | null; dirtyAfter: boolean | null;
  askId: number | null; requestedBy: string | null; missingEnv: string | null;
}
export interface HostTaskInfo {
  id: number; def: HostTaskDef; commandLine: string; approval: 'asks' | 'always' | 'always-but-changed'; approvedAt: string | null; approvedCommit: string | null;
  approvedBy: string | null; programResolved: string | null; commit: string | null; pinned: string[]; warnings: string[]; problem: string | null;
  runningRunId: number | null; pendingAskId: number | null; lastRun: TaskRun | null; updatedAt: string; updatedBy: string | null;
}
export interface TaskPreview {
  problem: string | null; programResolved: string | null; commit: string | null; pinned: string[]; warnings: string[]; commandLineOnly: boolean;
}
export interface RunSlots { running: number; limit: number; queued: number }
/** folderAsks: folder asks waiting for the person (folder.asks), already counted in needsYou. Older cores omit it. */
/** newest: the newest question or request waiting for the person, with its team; null or absent when none (or an older core). */
export interface Overview { slots: RunSlots; teams: TeamSummary[]; needsYou?: number; needsReconnect?: number; networkAsks?: number; toolAsks?: number; folderAsks?: number; heavy?: LeaseSnapshot; newest?: { kind: string; title: string; team?: string | null; at?: string } | null }
/** How the person answers a folder ask: read-write (only for a read-write ask), read-only, or deny. Never wider than asked. */
export type FolderAskChoice = 'readwrite' | 'read' | 'deny';
/**
 * A folder an agent asked to read or write with request_folder, waiting for the person in Needs you (folder.asks; core:
 * Integrations/FolderAsks.cs; contract: docs/folder-asks.md). member is the member the access is for, askedBy who asked
 * (the member itself, or its Manager), both "Team/Name". path: the resolved absolute path. exists: whether the folder
 * exists now (while pending; the value when it was asked once answered). choices: what the person may answer (empty once
 * answered). grantedAccess: what was granted (approved only). state closed: the member was removed while it waited.
 */
export interface FolderAsk {
  id: number; at: string; teamId: number; team: string; memberId: number; member: string; memberName: string; askedBy: string;
  path: string; access: 'read' | 'readwrite'; reason: string; exists: boolean; existedWhenAsked: boolean;
  state: 'pending' | 'approved' | 'denied' | 'closed'; grantedAccess: 'read' | 'readwrite' | null;
  decidedBy: string | null; decidedVia: string | null; decidedAt: string | null; note: string | null; deliverable: number | null;
  choices: FolderAskChoice[];
}
/** folder.answer {id, choice, note?}'s reply: told is who was told ("Team/Name": the Manager after an approval, the asker after a deny), or null. */
export interface FolderAnswer { ok: boolean; id: number; choice: FolderAskChoice; path: string; member: string; grantedAccess: 'read' | 'readwrite' | null; told: string | null }
export const FOLDER_ASKS = 'folder.asks';
export const FOLDER_ANSWER = 'folder.answer';
/** Pushed when a folder ask is raised or answered: Needs you, the You node and the Monitor refresh. */
export const FOLDER_ASKS_CHANGED = 'folder.asksChanged';
/** How the person answers a network ask: allow the host for the agent, its team or everyone, or deny it. */
export type NetworkAskChoice = 'agent' | 'team' | 'everywhere' | 'deny';
/**
 * A host a run was refused (the gateway, or the agent's request_host tool), waiting for the person in Needs you
 * (network.asks; core: Integrations/NetworkAsks.cs). member is "Team/Name"; reason is the agent's words, else method and
 * pathSample say what was refused; hint says what a well-known package registry is for. plainHttp: refused over plain
 * http (the gateway forwards only https, so allowing the host does not make it work). state closed: the member was
 * removed while the ask waited.
 */
export interface NetworkAsk {
  id: number; at: string; teamId: number; team: string; member: string; memberName: string; host: string;
  reason: string | null; method: string | null; pathSample: string | null; count: number; lastSeen: string; hint: string | null;
  deliverable: number | null; state: 'pending' | 'allowed' | 'denied' | 'closed'; scope: Exclude<NetworkAskChoice, 'deny'> | null;
  decidedBy: string | null; decidedAt: string | null; plainHttp: boolean; choices: NetworkAskChoice[];
  /** The host's registrable domain (digicert.com): one member's waiting asks with the same one are one item in Needs you. Null or absent: the host alone. */
  domain?: string | null;
  /** The domain allow that may be offered ("*.digicert.com"); null or absent when only the host may be allowed. */
  domainPattern?: string | null;
}
/** policy.checkHosts {hosts}: each entry as the core's floor rule judges it (FloorRules.HostProblem); problem null means accepted. */
export interface HostProblem { host: string; problem: string | null }
/** One host on a team's network allow list (policy.teamHosts; core: Policy/PolicyStore.cs). addedVia: how it was added (app, cloud, ...). */
export interface TeamHost { host: string; addedBy: string; addedVia: string | null; addedAt: string }
/** policy.teamHosts {teamId} and policy.removeTeamHost {teamId, host}: the team's list, oldest first. */
export interface TeamHosts { teamId: number; hosts: TeamHost[] }
/**
 * network.answer's reply: closed lists every ask the answer closed (the team's or everyone's for that host or domain).
 * allowed: what the policy now lists, the host or the domain allow ("*.digicert.com"); a grouped answer lists each, comma separated.
 */
export interface NetworkAnswer { ok: boolean; id: number; host: string; choice: NetworkAskChoice; closed: number[]; told: string[]; allowed?: string }
/** One developer tool of the app's fixed catalog (tools.catalog; core: Integrations/ToolAsks.cs). command: the winget line, or the person's own command off Windows. */
export interface ToolCatalogEntry {
  name: string; displayName: string; publisher: string; wingetId: string; scope: 'user' | 'machine'; program: string; command: string;
}
/**
 * How the person answers a tool ask: install it (winget on Windows, pkexec on Linux; the window on this computer only),
 * check again after installing it themselves (Linux and macOS; a remote view may too), or not now.
 */
export type ToolAskChoice = 'install' | 'checkAgain' | 'notNow';
/**
 * A developer tool an agent asked for with request_tool, waiting for the person in Needs you (tools.asks). One per team
 * and tool (count: repeats). error: the last install's or Check again's message (state is back to pending: Try again).
 * canInstall: the app can run command (the exact winget line, or the exact pkexec line on Linux: installer says which);
 * otherwise command is for the person to run (Linux/macOS). passwordPrompt: Linux Install's "Your system asks for your
 * password". manualCommand: the command to run yourself off Windows (sudo apt-get ..., brew ...). homeOnly: Linux, the
 * program is only under the home folder, where the sandbox cannot see it (the core's words). lastResult: the result code of
 * the last Install or Check again, beside error (cancelled or notAuthorized: nothing was installed, so a fresh card shows
 * error as a hint with Install, not Try again); null before the first attempt. Older cores omit the new fields.
 */
export interface ToolAsk {
  id: number; at: string; teamId: number; team: string; member: string; memberName: string; tool: string;
  displayName: string | null; publisher: string | null; wingetId: string | null; scope: 'user' | 'machine' | null;
  reason: string | null; count: number; lastSeen: string; deliverable: number | null;
  state: 'pending' | 'installing' | 'installed' | 'dismissed'; attempts: number; error: string | null;
  decidedBy: string | null; decidedVia: string | null; decidedAt: string | null;
  canInstall: boolean; command: string | null; choices: ToolAskChoice[];
  installer?: 'winget' | 'pkexec' | null; passwordPrompt?: string | null; manualCommand?: string | null; homeOnly?: string | null;
  lastResult?: ToolInstallCode | null;
}
/**
 * What an install or Check again came to: installed (the program is found, the asks closed), failed (the installer ran,
 * the program is still not found: Try again), cancelled (pkexec 126: the password window was closed), notAuthorized
 * (pkexec 127), homeOnly (only under the home folder), notFound (Check again: not there yet), busy (another install
 * runs), manual (the app cannot install here: run the command yourself, then Check again).
 */
export type ToolInstallCode = 'installed' | 'failed' | 'cancelled' | 'notAuthorized' | 'homeOnly' | 'notFound' | 'busy' | 'manual';
/** tools.install's and tools.checkAgain's reply, when it has finished: done when the program is found. result: older cores omit it. */
export interface ToolInstallResult {
  done: boolean; id: number; tool: string; message: string; manual: boolean; result?: ToolInstallCode; closed?: number[]; told?: string[];
}
/** tools.notNow's reply: told is the member told, or null. */
export interface ToolNotNowResult { ok: boolean; id: number; tool: string; told: string | null }
/** The tools.installProgress event while an install runs (cancelled and notAuthorized: pkexec 126 and 127). */
export interface ToolInstallProgress {
  id: number; tool: string; phase: 'installing' | 'checking' | 'installed' | 'failed' | 'cancelled' | 'notAuthorized'; message: string; command: string;
}
/** tools.checkAgain { id }: looks for the ask's program again after the person installed it; installs nothing. */
export function checkToolAgain(api: Pick<YawbleApi, 'call'>, id: number): Promise<ToolInstallResult> {
  return api.call<ToolInstallResult>('tools.checkAgain', { id });
}
/** One holder of the heavy-work lease, or one run waiting for it (core: Bus/BusEngine.Lease.cs). since: ISO time. */
export interface LeaseEntry { team: string; member: string; what: string; since: string; position?: number | null }
/** The heavy-work lease: how many may hold it at once (app.heavyWorkAtOnce), who holds it, who waits in order. */
export interface LeaseSnapshot { limit: number; holders: LeaseEntry[]; queue: LeaseEntry[] }
/** A Connection a service refused, for Needs You (integrations.connectionAttention: on the computer only, never over the Cloud Link). */
export interface ConnectionAttention { id: number; name: string; reason: string | null; where: string }

/** A skill agents load over the bus (core: Skills/SkillCatalog.cs). Built-ins come with the app and are read-only. */
export interface Skill {
  name: string; description: string; roles: string[]; body: string; kind: 'builtin' | 'custom';
  teamId: number | null; team: string | null; id: number | null; updatedAt: string | null;
}

/** A team's lessons, the team-scoped skill the Manager's retrospectives keep (team_retros_write). */
export const TEAM_RETROS = 'team_retros';
/** A team skill's kept versions, newest first ({ teamId, name } → SkillVersion[]). */
export const SKILLS_VERSIONS = 'skills.versions';
/** Brings a version's text back as the current text, a new version by the person ({ id } → Skill). */
export const SKILLS_RESTORE = 'skills.restore';
/** Team settings' "Lessons: n, last updated ..." line ({ teamId } → TeamLessons). */
export const TEAMS_LESSONS = 'teams.lessons';
/** The writer of a version saved or restored in Settings > Skills; any other writer is the member who wrote it (the Manager). */
export const PERSON_WRITER = 'person';
/** One kept write of a team skill (core: Skills/TeamRetros.cs). */
export interface SkillVersion {
  id: number; teamId: number; name: string; version: number; body: string;
  writer: string; deliverable: number | null; restoredFrom: number | null; writtenAt: string;
}
/** A team's lessons: how many (list lines), and the last write (version 0 and nulls: none yet). */
export interface TeamLessons {
  count: number; version: number; updatedAt: string | null; writer: string | null; deliverable: number | null; skillId: number | null;
}
/** Who wrote a version, as a person reads it: "you (Settings > Skills)", "the retro of deliverable 12" or the writer's name. */
export function writerText(v: { writer: string | null; deliverable: number | null; restoredFrom?: number | null }): string {
  if (v.writer === PERSON_WRITER) return v.restoredFrom ? `you (restored version ${v.restoredFrom})` : 'you (Settings > Skills)';
  if (v.deliverable !== null) return `the retro of deliverable ${v.deliverable}`;
  return v.writer ?? 'unknown';
}

/** A trigger (triggers.list / triggers.save): work a member is told on a schedule, as a new deliverable. */
export interface TriggerSchedule {
  kind: 'every' | 'cron' | 'once' | 'folder' | 'event'; intervalSec: number | null; cron: string | null; timezone: string | null; fireAt: string | null;
}
export type WakeManager = 'always' | 'onHandbackOrFailure' | 'never';
/** Which folder changes fire a folder trigger. A new trigger takes 'added'; one saved without it (an older core) is 'any'. */
export type WatchFireOn = 'added' | 'added-or-changed' | 'any';
export const WATCH_FIRE_ON_LABELS: Record<WatchFireOn, string> = { added: 'Added files', 'added-or-changed': 'Added or changed', any: 'Any change' };
export const fireOnOf = (w: { fireOn?: WatchFireOn | null } | null | undefined): WatchFireOn => w?.fireOn ?? 'any';
export interface Trigger {
  id: number; teamId: number; memberId: number; name: string; schedule: TriggerSchedule; instruction: string;
  onlyWhenIdle: boolean; enabled: boolean; nextDueAt: string | null; lastFiredAt: string | null;
  /** Whether the trigger's runs wake the Manager when they end (a hand-back always does). */
  wakeManager: WakeManager;
  /** A folder-change trigger's folder: the team's documents (path inside) or a folder on this computer. */
  watch?: { root: 'documents' | 'host'; path: string; glob: string | null; quietSec: number; fireOn?: WatchFireOn | null } | null;
  /** An event trigger's event name pattern (web.pr, web.*). */
  eventName?: string | null;
  lastOutcome: 'fired' | 'skipped' | 'missed' | 'member-missing' | null; lastReason: string | null; lastSeq: number | null; missedCount: number;
}
export interface TriggerFire { outcome: string; seq: number | null; reason: string | null }

/** The Git dialog (git.status / git.fetch / git.bringCurrent / git.merge / git.askTeam / git.deleteBranches; core Teams/TeamRepos.Git.cs). */
/** Absent from an older core: aheadRange, the pair behind aheadOfBase; push, the branch's local-only flag. */
export interface TeamGitBranch {
  branch: string; sha: string; aheadOfBase: number; inTeamBranch: boolean; aheadRange?: CommitRangeRef | null; push?: BranchPush | null;
}
/** An old-style members/<member> branch left from before member branches were named members/<team>/<member>:
 * here (local) and/or on origin (remote), and whether its commits are merged. */
export interface TeamGitOldBranch { name: string; local: boolean; remote: boolean; merged: boolean }
export interface TeamGitStatus {
  repoPath: string; managed: boolean; baseBranch: string; checkedOut: string | null; dirty: boolean; origin: string | null;
  baseSha: string | null; originBaseSha: string | null; baseAheadOfOrigin: number | null; baseBehindOrigin: number | null;
  teamBranch: string; teamSha: string | null; teamAheadOfBase: number; teamBehindBase: number;
  merge: 'none' | 'up-to-date' | 'fast-forward' | 'merge' | 'conflict'; conflictFiles: string[]; members: TeamGitBranch[];
  /** Old-style members/<member> branches (absent from a core that does not list them). */
  oldBranches?: TeamGitOldBranch[];
  /** members/<slug> branches named like this team's slug, which git cannot hold beside members/<slug>/<member> ones, so
   * they block this team's member branches: one entry per ref, here (local) or on origin (remote). Absent from a core
   * that does not list them. */
  blockingBranches?: TeamGitBlockingBranch[];
  /** The commit pair behind each count (absent from an older core). */
  ranges?: TeamGitRanges | null;
}
/** inUseBy: a member is still on it, its old members/<name> ("team Alpha's member Ada", or "member Docs of
 * this team"); it is not deleted from the dialog but moves to movesTo at that member's next start. Absent otherwise. */
export interface TeamGitBlockingBranch { name: string; repo: string; location: 'local' | 'remote'; inUseBy?: string; movesTo?: string }
/** git.deleteBranches without confirm: what would be deleted, as text for the person, and the token that confirms exactly
 * that plan (empty: nothing to delete). With confirm: token it deletes and answers GitDeleteResult; error code stale
 * means the branches changed since the plan. */
export interface GitDeletePlan { plan: string; token: string; empty: boolean }
export interface GitDeleteResult { result: string }
/** The hosting provider of a repository's origin. none: plain git (no links unless a link pattern). */
export type GitProvider = 'github' | 'gitlab' | 'bitbucket' | 'azuredevops' | 'gitea' | 'none';
export const GIT_PROVIDERS: GitProvider[] = ['github', 'gitlab', 'bitbucket', 'azuredevops', 'gitea', 'none'];
/** What teams.repos adds to main and to each extra (absent from an older core). provider: the person's override
 * (null: none, detection is used); providerDetected: from origin; linkPattern: one or more templates separated by
 * whitespace, each used for the link kind whose placeholders it holds (LINK_PATTERN_KINDS); prSupported, or
 * prUnavailable: why not (shown as is). The core may also send providerOverride, equal to provider: ignored. */
export interface RepoProviderInfo {
  provider?: GitProvider | null; providerDetected?: GitProvider | null; linkPattern?: string | null;
  prSupported?: boolean; prUnavailable?: string | null;
}
/** git.webLink {teamId, team, repo?, kind, commit?, branch?, file?, from?, to?, pr?} -> { url } (null: no link, e.g.
 * a commit not on origin or a host with no provider and no pattern). */
export const GIT_WEB_LINK = 'git.webLink';
export type GitWebLinkKind = 'commit' | 'branch' | 'file' | 'compare' | 'pr';
export interface GitWebLinkQuery { kind: GitWebLinkKind; commit?: string; branch?: string; file?: string; from?: string; to?: string; pr?: number }
/** teams.setRepo {teamId, name? (an extra's; absent: the main one), provider (null: detect), linkPattern (null: none)}. */
export const TEAMS_SET_REPO = 'teams.setRepo';
/** A linkPattern template is used for the kind whose placeholders it holds exactly; {base} and
 * {path} may appear in any template. */
export const LINK_PATTERN_KINDS: { kind: GitWebLinkKind; placeholders: string[] }[] = [
  { kind: 'commit', placeholders: ['{commit}'] },
  { kind: 'branch', placeholders: ['{branch}'] },
  { kind: 'file', placeholders: ['{commit}', '{file}'] },
  { kind: 'compare', placeholders: ['{from}', '{to}'] },
  { kind: 'pr', placeholders: ['{pr}'] },
];
/** git.tidyPlan {team}: every branch of the team's repositories (here, on origin, in the members' clones) in one class.
 * merged: in the base; contained: every commit has an equivalent in the base (proof: git cherry's answer, as text);
 * working: a member's working branch (in use, kept); moved: the team's copy differs from the member's clone (teamSha vs
 * cloneSha; Realign sets the team's and origin's copy to the clone's); own-work: commits of its own (commits), needs a look;
 * origin-only: on origin, nobody has it here; scratch: a clone's local-only scratch/* or tmp/* branch. where: local,
 * origin, clone:<member>. */
export type TidyClass = 'merged' | 'contained' | 'working' | 'moved' | 'own-work' | 'origin-only' | 'scratch';
export interface TidyBranch {
  name: string; where: string[]; class: TidyClass; proof?: string | null; commits?: { sha: string; subject: string }[] | null;
  teamSha?: string | null; cloneSha?: string | null; member?: string | null;
}
/** baseBranch: what merged and contained are measured against; notes: what the core could not read (a member's clone
 * that was skipped, for example), so a branch missing from the list is not silently missing. */
export interface TidyRepo { repo: string; baseBranch?: string | null; branches: TidyBranch[]; notes?: string[] | null }
export interface TidyPlan { repos: TidyRepo[] }
/** git.branchRealign {team, repo, member, expectOld?, expectNew?}: the old and new commit of the member branch in
 * the team repository (and origin). expectOld / expectNew bind the yes to the team and clone commits the person was
 * shown: the core refuses with code "stale" if either moved. Also refused (an error) while the member runs. */
export interface BranchRealignResult { oldSha: string; newSha: string }
/**
 * git.commitShow {team | teamId, sha, repo?} (core TeamRepos.CommitView.cs CommitInfo.ToJson): one commit of the
 * team's repository, from plain git (never an agent's clone). sha: 4 to 64 hex digits, else the error bad_params.
 * found=false: git no longer has it; the answer is then only {found, goneReason, sha} (goneReason is plain words and
 * may name the branch and member). made: the member, card and deliverable that made it (the app's own records); absent
 * when unknown; a merge recorded on the team branch is credited to the Manager. merge: what a merge brought in;
 * intoBranch / fromBranch are null when neither git nor the app's records can tell. where: inMain; branches: the team
 * branch first, then member branches, never main; otherBranches: the other local branches that hold it (not main, the team
 * branch or members/*; absent from an older core, read as []); onlyOnMember only when it is in neither main nor the team branch;
 * pushed (on origin); tags (the release tags that contain it). files: against the first parent for a merge.
 * codeShown=false: a cloud viewer with "Show members' code to cloud viewers" off; the file list only, no diffs.
 */
export interface CommitPerson { name: string; email: string; date: string }
export type CommitFileStatus = 'added' | 'modified' | 'deleted' | 'renamed';
export interface CommitFile { path: string; oldPath?: string | null; status: CommitFileStatus | string; added: number; deleted: number; binary: boolean }
export interface CommitGone { found: false; goneReason?: string | null; sha: string }
export interface CommitFound {
  found: true;
  sha: string; shortSha: string; title: string; body: string;
  author: CommitPerson; committer?: CommitPerson | null;
  isMerge: boolean; parents: string[];
  merge?: { intoBranch: string | null; fromBranch: string | null; fromMember?: string | null; commitCount: number } | null;
  made?: { member: string; card?: number | null; deliverable?: number | null } | null;
  /** deletedFrom: the deleted branch it was on (absent otherwise). */
  where: { inMain: boolean; branches: string[]; otherBranches?: string[]; onlyOnMember?: string | null; pushed: boolean; tags: string[]; deletedFrom?: string };
  files: CommitFile[];
  codeShown: boolean;
}
export type CommitShow = CommitGone | CommitFound;
/**
 * git.commitsResolve {team? | teamId?, ids}: which of the commit-shaped IDs
 * on a screen are real commits, in ONE call. ids: 7 to 40 hex digits each, short or full; at most 200 distinct per call
 * (GIT_COMMITS_RESOLVE_MAX; more is bad_params, so the shell splits above it). Neither team nor teamId: every team.
 * results: one per distinct ID, in the order asked (id lower case); matches: none (not a commit: the text stays text),
 * one, or more (ambiguous: the chooser). repo null: the team's main repository. pushed: as the Commit view's
 * where.pushed. errors: repositories that could not be read (absent when none). A read; a Visitor may call it.
 */
export const GIT_COMMITS_RESOLVE = 'git.commitsResolve';
export const GIT_COMMITS_RESOLVE_MAX = 200;
export interface CommitMatch { team: string; teamId?: number; repo: string | null; fullId: string; shortId?: string; title: string; pushed: boolean }
export interface CommitsResolveParams { team?: string; teamId?: number; ids: string[] }
export interface CommitsResolved {
  max?: number;
  results: { id: string; valid: boolean; matches: CommitMatch[] }[];
  errors?: { team: string; teamId: number; repo: string | null; message: string }[];
}
/**
 * git.commitDiff {team | teamId, sha, path, repo?} (core CommitFileDiff.ToJson): one file's diff against the first
 * parent. Each hunk: git's header line, its starts and counts, and its lines (kind context / added / deleted; old / new:
 * the 1-based line numbers, only on the side(s) the line is on). Binary, over 5000 changed lines or 2M characters, or a
 * pure rename: a summary and no hunks. Errors: gone, no_such_file, bad_params, git_failed; remote_refused for a cloud
 * viewer when members' code is hidden.
 */
export interface CommitDiffLine { kind: 'context' | 'added' | 'deleted'; old?: number | null; new?: number | null; text: string }
export interface CommitDiffHunk { header: string; oldStart: number; oldCount: number; newStart: number; newCount: number; lines: CommitDiffLine[] }
export interface CommitDiff {
  path: string; oldPath?: string | null; status: CommitFileStatus | string; binary: boolean; tooLarge: boolean; summary?: string | null;
  added: number; deleted: number; hunks: CommitDiffHunk[];
}

/** teams.syncToBase: the team's "Bring branches up to <base> before new work" setting, the note of a sync or fetch-back
 * that could not be done (one line per problem, files after "conflicts in:"), and how far the team branch is behind. */
export interface TeamSyncState {
  on: boolean; note: string | null; baseBranch: string | null;
  /** from / to: the pair behind count (from = the team tip, to = the base it is behind), passed unchanged to
   * git.commitRange. The core sends both whenever behind is non-null (contract.md); optional only for an older core. */
  behind: { teamBranch: string; count: number; text: string; from?: string | null; to?: string | null } | null;
}
/** git.sync, "Sync now" (core Teams/TeamRepos.Sync.cs BranchSyncResult). */
export interface BranchSyncResult {
  outcome: 'off' | 'no-base' | 'in-step' | 'synced' | 'conflict'; baseBranch: string; baseSha: string | null; behind: number;
  moved: string[]; conflicts: string[]; note: string | null;
  /** The commits the team branch received from the base, newest first (empty when nothing came in; absent from an
   * older core: the dialog reads them with git.commitRange from the team commit before to the one after). */
  incoming?: CommitRow[]; incomingCount?: number; incomingTruncated?: boolean; incomingRange?: CommitRangeRef | null;
}
export interface TeamGitResult {
  outcome: string; message: string; changed: boolean; sha: string | null; files: string[] | null; status: TeamGitStatus | null;
  /** git.bringCurrent, git.merge: the commits that came in / landed, null otherwise; absent from an older core. */
  commits?: CommitRow[] | null; commitCount?: number; commitsTruncated?: boolean; range?: CommitRangeRef | null;
}

// ---- Clickable counts and previews in the Git dialog (the core's git.commitRange / mergePreview / deletePreview
// contract). Every call takes {teamId | team, repo?} and is a read.
/** One commit of a list, newest first. notPushed: THE local-only flag (no origin ref has it; always true without an
 * origin: hasOrigin words it "local only" then, else "not pushed"). */
export interface CommitRow {
  sha: string; shortSha: string; subject: string; author: { name: string; email: string }; date: string; isMerge: boolean; notPushed: boolean;
}
/** The exact pair a count stands for (full commit IDs): the commits on to that from lacks. Passed unchanged to git.commitRange. */
export interface CommitRangeRef { from: string; to: string }
/** THE branch local-only flag. onOrigin: origin has the branch; notPushed: its commits no origin ref has. */
export interface BranchPush { branch: string; onOrigin: boolean; notPushed: number }
/** git.status's ranges (absent from an older core): the pair behind each count; null when an end is missing. */
export interface TeamGitRanges {
  baseAheadOfOrigin: CommitRangeRef | null; baseBehindOrigin: CommitRangeRef | null;
  teamAheadOfBase: CommitRangeRef | null; teamBehindBase: CommitRangeRef | null;
}
/** git.commitRange {from, to, limit?}: from / to a commit ID or refs/heads/<b> / refs/remotes/origin/<b>; limit 200 (max 1000). */
export const GIT_COMMIT_RANGE = 'git.commitRange';
export interface CommitRange {
  from: string; to: string; fromSha: string; toSha: string; count: number; truncated: boolean; hasOrigin: boolean; commits: CommitRow[];
}
/** git.mergePreview: what Merge to <base>… would do (git merge-tree; nothing moves). summary: the outcome in one line;
 * text: the whole preview as the Concierge shows it; commits: what lands; files: the combined diff, each opened with
 * git.mergeDiff {from: diffFrom, to: diffTo, path} (git.commitDiff's shape). refused: Merge to main would refuse (summary
 * says why). */
export const GIT_MERGE_PREVIEW = 'git.mergePreview';
export const GIT_MERGE_DIFF = 'git.mergeDiff';
export interface MergePreview {
  baseBranch: string; teamBranch: string; outcome: TeamGitStatus['merge'] | 'refused'; summary: string; text: string;
  from: string | null; to: string | null; newSha: string | null; conflictFiles: string[];
  commits: CommitRow[]; count: number; truncated: boolean; files: CommitFile[]; diffFrom: string | null; diffTo: string | null;
  hasOrigin: boolean; teamPush: BranchPush | null;
}
/** git.deletePreview {branch}: the branch's commits not in the base, each in-base ("already in main", git cherry), own
 * ("work of its own") or merge (git cherry skips merges). lost: the one line on what deleting loses. After the delete the
 * core records them, so git.commitShow still opens them (where.deletedFrom names the branch). */
export const GIT_DELETE_PREVIEW = 'git.deletePreview';
export interface DeletePreviewCommit extends CommitRow { state: 'in-base' | 'own' | 'merge' }
export interface DeletePreview {
  branch: string; baseBranch: string; exists: boolean; sha: string | null; merged: boolean; commits: DeletePreviewCommit[];
  own: number; inBase: number; truncated: boolean; lost: string; text: string; hasOrigin: boolean; push: BranchPush | null;
}

export interface MemberRow {
  id: number; teamId: number; team: string; name: string; role: 'manager' | 'member'; cli: 'claude' | 'grok' | 'codex' | 'copilot' | 'antigravity';
  instructions: string; model: string; credential: string;
  /** The role from the team's hire list the member was hired as (Developer, Tester...); null or absent when none. */
  hireRole?: string | null;
}

/** teams.agents' agentModel: the model a member runs and where its name comes from: set on the member, the agent's default,
 *  observed (the CLI's own default; the model its last run used most, at = that run's last request), or cli (not run yet; name ''). */
export interface AgentModelShown { name: string; source: 'member' | 'agent' | 'settings' | 'observed' | 'cli' | string; at: string | null }

export interface AgentCard {
  /** Besides running and idle, the WorkerWaitState of work not started (an older core sends only queued). */
  member: MemberRow; agentModel?: AgentModelShown | null; state: 'running' | 'idle' | WorkerWaitState; queuePosition: number | null; queueLength: number;
  /** Why queued instructions have not started, next to a state of queued or idle (cardState reads either shape). */
  waitState?: WorkerWaitState | null;
  /** waits-for-run: the member whose run must end first. */
  waitsFor?: string | null;
  queuedInstructions: number; statusLine: string | null; runId: number | null; runStartedAt: string | null;
  lastRunState: string | null; lastRunReason: string | null;
  /**
   * The last run's failure class (core FailureClasses: failed, timeout, idle, stopped, credential, start_failed,
   * interrupted, rate); null when it did not fail. "stopped": the person stopped it (recorded failed, never a failure).
   */
  lastRunFailureClass?: RunFailureClass | null;
  /** The heavy-work lease: this member's run holds it or waits in its queue (position), for what step. */
  lease?: 'holding' | 'waiting' | null; leaseWhat?: string | null; leasePosition?: number | null;
  /**
   * What the member needs from a person, loudest first: an open question, blocked, its last run failed (core
   * UiRpc.Attention). "stopped": the person stopped its last run; shown neutral, never as needing the person.
   */
  attention?: 'decision' | 'blocked' | 'failed' | 'stopped' | null;
  /**
   * The open decision this member waits on (its Decision.seq), null when none. While set the tile says "Waiting on
   * you" (status and report chip) and links to that decision card, not the last report.
   */
  decisionId?: number | null;
  net?: string; wallTimeout?: string; loosened?: string[]; policyProblems?: string[];
  /** The member has a working copy (clone) on this computer: its card offers the Code browser (teams.agents). */
  hasClone?: boolean;
  /** 'workflow' for a workflow member (its tile reads workflows.memberGet); absent from an older core: an agent. */
  kind?: 'agent' | 'workflow';
  /** The deliverables the member's current run works on (their numbers), oldest first; [] when not running; absent from an older core. */
  runDeliverables?: number[];
}

/**
 * One answer the asker offers (needs_decision's options, 2 to 4): the button's label and the text sent as the
 * answer. An option may carry an allowlisted app action (e.g. "team.allowLocalhostMac") that core applies when the person
 * answers with it (bus.answer with optionIndex); actionAvailable false (a Visitor, not macOS) disables the button and
 * actionUnavailableReason says why.
 */
export interface DecisionOption {
  label: string; reply: string; action?: string | null;
  actionAvailable?: boolean; actionUnavailableReason?: string | null;
}

/**
 * An open needs_decision: summary (the plain ask), options and details (Markdown findings) when the asker gave
 * them; an older decision has only question. bus.answer takes { teamId, seq, text, optionIndex? } (0-based).
 */
export interface Decision {
  seq: number; from: string; question: string; deliverable: number; at: string;
  summary?: string | null; options?: DecisionOption[] | null; details?: string | null;
}

export interface SecretStatus { connected: boolean; addedAt: string | null; expiresAt: string | null; lastUsedAt: string | null; renewSoon: boolean; problem: string | null }
export interface GrokLoginStatus { signedIn: boolean; expiresAt: string | null; lastRefreshAt: string | null; problem: string | null }
export interface CredentialStatus {
  implementation: string; storage: string; storageWarning: string | null;
  claude: { mode: 'subscription' | 'apiKey'; subscription: SecretStatus; apiKey: SecretStatus };
  grok: { mode: 'login' | 'apiKey'; login: GrokLoginStatus; apiKey: SecretStatus };
}

export interface ComputeValues { idleTimeoutSec: number; wallTimeoutSec: number; memoryMb: number | null; cpuPercent: number | null; maxProcesses: number | null }
export interface GlobalPolicyDoc {
  filesystem: { grantableRoots: string[]; readonly: string[]; readwrite: string[]; denied: string[] };
  network: { allowHosts: string[] };
  compute: Partial<ComputeValues> & { idleTimeoutSec: number; wallTimeoutSec: number };
  ui: { clipboard: string };
  app: { maxConcurrentAgents: number; backend: string; heavyWorkAtOnce?: number };
}
export type PolicyPatchDoc = {
  filesystem?: { readonly?: string[]; readwrite?: string[]; denied?: string[] };
  network?: { allowHosts?: string[] };
  compute?: Partial<ComputeValues>;
  ui?: { clipboard?: string };
};
export interface MemberPolicyView {
  memberId: number;
  global: GlobalPolicyDoc;
  override: PolicyPatchDoc;
  loosen: PolicyPatchDoc;
  loosenSummary: string[];
  loosenApprovedAt: string | null;
  /** The member's team's network allow list (policy.teamHosts has who added each and when). */
  teamHosts?: string[];
  effective: {
    readonly: string[]; readwrite: string[]; denied: string[]; allowHosts: string[];
    compute: ComputeValues; clipboard: string; loosened: string[]; notes: string[];
  };
  implicit: { readwrite: string[]; readonly: string[]; denied: string[] };
  widening: string[];
  violations: string[];
  /** The effective network in one line: "model + bus", "model + bus + 3 hosts". */
  net?: string;
  backend: string;
  machineMemoryMb: number | null;
}

export interface TerminalSessionInfo {
  kind: 'headless' | 'interactive' | 'host'; runId: number | null; startedAt: string; inputEnabled: boolean;
  command: string | null; ended: boolean; exitCode: number | null; endReason: string | null;
}
export interface TerminalSnapshot { channel: string; replay: string; end: number; session: TerminalSessionInfo | null }
export interface TerminalBanner {
  memberId: number; channel: string; title: string; details: string[]; loosened: string[];
  state: 'running' | 'queued' | 'idle'; queuePosition: number | null; queueLength: number; runId: number | null;
  /** The member holds or waits for an INTERACTIVE session (never a headless run). */
  interactive?: boolean;
  lastRunState: string | null; lastRunReason: string | null;
  /** The last run's failure class, as AgentCard.lastRunFailureClass. */
  lastRunFailureClass?: RunFailureClass | null;
}

/** A failed run's class (core Bus/Messages.cs FailureClasses); a string for a class this shell does not know. */
export type RunFailureClass = 'failed' | 'timeout' | 'idle' | 'stopped' | 'credential' | 'start_failed' | 'interrupted' | 'rate' | (string & {});

/**
 * Whether a run (a member's last run, a feed row, a run row) was stopped by the person: failure class "stopped",
 * even when its state still says "failed" (the core records a stopped run as failed with class "stopped").
 */
export function isStoppedRun(r: { state?: string | null; failureClass?: string | null; attention?: string | null } | null | undefined): boolean {
  if (!r) return false;
  return r.attention === 'stopped' || r.failureClass === 'stopped' || r.state === 'stopped';
}

// ---- the Concierge (core: Concierge/ConciergeService.cs) ----
// The person's own chat with an agent that knows every team on this instance and can act on them through its tools.
// Conversations persist; each turn is one headless run of the chosen agent CLI in a sandbox.
//   concierge.list                          -> ConversationSummary[]   (newest first)
//   concierge.get    {id}                   -> Conversation
//   concierge.send   {id?, text, cli?, teamId?} -> {id}            (no id: a new conversation)
//   concierge.stop   {id}                   -> {stopped}
//   concierge.rename {id, title}            -> ConversationSummary
//   concierge.delete {id}                   -> {deleted}
//   concierge.setTeam {id, teamId}          -> ConversationSummary  (the team in focus; null: all teams)
//   concierge.read   {id}                   -> {unread: 0}          (the person has seen its updates)
//   concierge.unwatch {id, watchId}         -> {stopped}
//   concierge.setWatch {id, watchId, followUp} -> ConciergeWatch    (wake the Concierge to review each update, or not)
// Push: concierge.turn    ConciergeTurnEvent   (while a turn runs, and when it ends)
// Push: concierge.update  ConciergeUpdateEvent (the core posted an update a watch was waiting for; no agent ran)
export interface ConversationSummary {
  id: string;
  title: string;
  cli: string;
  teamId: number | null;
  team: string | null;
  createdAt: string;
  updatedAt: string;
  running: boolean;
  /** Updates posted since the person last opened the chat. */
  unread: number;
  /** Active watches: what the Concierge is keeping tabs on in this chat. */
  watching: number;
}

/** A follow-up the Concierge keeps: a deliverable it dispatched (deliverable set) or a whole team. */
export interface ConciergeWatch {
  id: number;
  team: string;
  deliverable: number | null;
  what: string;
  followUp: boolean;
  createdAt: string;
  expiresAt: string;
}

export interface ConciergeUpdateEvent { conversationId: string; message: ConciergeMessage; unread: number }

export interface ConciergeToolUse { name: string; detail: string | null }

export interface ConciergeMessage {
  id: number;
  /** update: posted by the app about a team the chat watches (done, decision, failed, blocked, expired) */
  role: 'user' | 'assistant' | 'update';
  text: string;
  at: string;
  /** assistant only: done | failed | stopped */
  state: string | null;
  error: string | null;
  tools: ConciergeToolUse[];
  cli: string | null;
  team?: string | null;
  kind?: string | null;
}

export interface Conversation extends ConversationSummary {
  messages: ConciergeMessage[];
  /** The turn in progress, if any: what the agent has said and done so far. */
  turn: ConciergeTurnEvent | null;
  watches: ConciergeWatch[];
}

export interface ConciergeTurnEvent {
  conversationId: string;
  state: 'running' | 'done' | 'failed' | 'stopped';
  text: string;
  tools: ConciergeToolUse[];
  error: string | null;
}

// ---- the Board (core Board/BoardService.cs): cards in four lanes, and the backlog ----
/** 'stopped': the person stopped the card's run; lane In Progress / To Do, never Needs you, never red. */
export type CardStatus = 'queued' | 'running' | 'blocked' | 'needs-decision' | 'failed' | 'completed' | 'handback' | 'done' | 'stopped';
export type LaneId = 'todo' | 'in-progress' | 'blocked' | 'done';
export interface BoardCard {
  id: number; team: string; teamId: number | null; member: string | null; item: string | null; title: string; body: string;
  status: CardStatus; lane: LaneId; placed: boolean; awaitingManager: boolean; deliverable: number | null;
  createdAt: string; updatedAt: string; trailCount: number;
  /** When the person cleared this Done card from the board view (0037); null or absent when it shows. */
  clearedAt?: string | null;
  memberState?: { state: AgentCard['state']; attention: AgentCard['attention']; lastRunFailureClass?: AgentCard['lastRunFailureClass'] };
  /** The heavy-work lease of the card's member, on the card it is working on only: held, or waited for (queue position). */
  lease?: { state: 'holding' | 'waiting'; what: string | null; position: number | null };
  /** Its overall time and breakdown, computed by the core (older cores: absent). */
  time?: ItemTime | null;
}
export interface BoardTrailLine { id: number; at: string; source: string; kind: string; text: string }
export interface BoardCardDetail extends BoardCard { trail: BoardTrailLine[]; itemTitle?: string }
export interface BoardList {
  lanes: { id: LaneId; label: string }[]; cards: BoardCard[]; teams: { id: number; name: string }[];
  /** Cards cleared from the board for the list's team filter (an older core sends none). */
  clearedCount?: number;
}
export interface BacklogItem {
  id: number; label: string; team: string | null; title: string; body: string; state: 'pending' | 'ready' | 'declared' | 'implemented';
  archivedAt: string | null; dispatchedTeam: string | null; dispatchedDeliverable: number | null; dispatchedAt: string | null; inFlight: boolean;
  createdAt: string; updatedAt: string; dispatchCount: number;
}
/** One dispatch of a backlog item (0023): its team and deliverable, read live from the bus, the board and usage records. */
export interface BacklogDispatch {
  team: string; teamId: number | null; teamGone: boolean; deliverable: number; dispatchedAt: string; dispatchedBy: string | null;
  state: DeliverableState | null; ending: string | null; busy: string[] | null; lastAt: string | null;
  cards: number; cardsDone: number; runs: number; members: string[]; inputTokens: number; outputTokens: number;
  firstStart: string | null; lastEnd: string | null;
}
export interface BacklogItemDetail extends BacklogItem { dispatches: BacklogDispatch[] }
