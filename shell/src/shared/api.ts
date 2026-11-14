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
}

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
  /**
   * Set when this window is a remote view of the instance (the same screens in a browser, through the Cloud
   * Link): screens hide what only the person at the computer may do.
   */
  readonly remote?: boolean;
  /** The desktop window's OS (win32, darwin, linux). Set only in the app's own windows, which draw their own title bar. */
  readonly platform?: string;
  /** Dims the window controls the system draws in the title bar while a dialog dims the page (desktop window only). */
  dimTitleBar?(dim: boolean): void;
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
  shellSettings: 'shell:settings',
  setShellSettings: 'shell:setSettings',
  openDocuments: 'window:openDocuments',
  windowCommand: 'window:command',
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
export interface CompanionAction { id: string; client: string; tool: string; plan: string; expires: string; state: 'pending' | 'running' }
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
export const UI_METHOD_PREFIXES = ['app.', 'monitor.', 'board.', 'backlog.', 'teams.', 'members.', 'bus.', 'policy.', 'credentials.', 'hostTerminal.', 'terminal.', 'guardrail.', 'agents.', 'agentProfiles.', 'skills.', 'triggers.', 'git.', 'link.', 'concierge.', 'docs.', 'integrations.', 'tasks.', 'ops.', 'companion.', 'code.', 'logs.', 'updates.', 'network.', 'tools.'] as const;

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
  model: { model: string; source: 'member' | 'agent' | 'cli' | string; label: string };
  headless: CommandLineKind;
  interactive: CommandLineKind;
}
/** One run kind's command line: the whole argv, env NAMES, and (from the same core builder) the launcher tokens and built-in groups. */
export interface CommandLineKind { argv: string[]; env: string[]; launch?: string[]; groups?: ArgGroup[] }
/** One built-in argument (flag + value): locked = the app depends on it (reason says why); tail = it carries the prompt and stays last;
 *  removed = the customization described removes it. Per-run values are placeholders (<prompt>, <session id>, <workspace>/…). */
export interface ArgGroup { tokens: string[]; locked: boolean; tail: boolean; removed: boolean; reason?: string | null }
export interface AgentModelList { models: string[]; source: 'app' | string } // the app's own list: no CLI is run for it
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

export interface TeamSummary { id: number; name: string; repoPath: string | null; members: number; running: number; queued: number; paused?: boolean; archived?: boolean }
/** Whether a team may be archived or deleted (teams.quiet): what is still working, and its open workflows. */
export interface TeamQuiet { quiet: boolean; working: string[]; openWorkflows: number[] }
/** A team's workflow (bus.workflows): one piece of work and everything it led to. */
export interface WorkflowSummary {
  id: number; title: string; owner: string | null; state: 'open' | 'completed' | 'closed'; undeclared: boolean;
  busy: string[]; ending: string | null; startedAt: string; lastAt: string; messages: number;
}
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
export type ConnectionKind = 'secret' | 'oauth' | 'website';
export interface Connection {
  id: number; name: string; kind: ConnectionKind; fields: ConnectionField[];
  status: 'untested' | 'works' | 'needs_reconnect'; statusReason: string | null; testedAt: string | null; usedBy: string[];
}
/** A web service added by its URL (integrations.apiList). `signIn` says what to ask for, from the spec's auth; null when the auth is not known yet. */
export interface ApiRow {
  /** 'website': a site the agents use in a browser window on this computer (six fixed actions); its sign-in is its Connection, never fields. */
  kind: 'http' | 'website';
  name: string; baseUrl: string; docsUrl: string | null; specUrl: string | null; title: string | null;
  connection: { id: number; name: string; status: Connection['status']; statusReason?: string | null } | null;
  /** chosen: the person said how it signs in (the spec did not). */
  auth: { type: 'bearer' | 'header' | 'query' | 'basic' | string; name: string | null; description: string | null; chosen?: boolean } | null;
  signIn: { label: string; description: string | null; fields: { key: string; label: string; secret: boolean }[] } | null;
  /** An OAuth 2 API (auth.type 'oauth2'): the spec's addresses (null when it gave none usable, or only an OpenID address), the scopes the
   * allowed and ask-first actions need, and those its Connection was not granted (non-empty: "Sign in again to allow the new actions"). */
  oauth?: { authorizeUrl: string | null; tokenUrl: string | null; openIdConnectUrl?: string | null; neededScopes: string[]; missingScopes: string[] } | null;
  status: string | null; all: boolean; teams: number[]; counts: DiscoverChecklistData['counts'] | null;
}
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
  changed?: {
    approvedCommit: string; currentCommit: string; diffAvailable: boolean; definitionHashChanged: boolean; filesHashChanged: boolean;
    definition: { field: string; old: string | null; new: string | null }[];
    files: { path: string; change: 'added' | 'removed' | 'changed'; oldBlob: string | null; newBlob: string | null; diff?: string; diffMoreLines?: number }[];
    commits?: string | null; stat?: string | null;
  };
}
/** A call waiting for the person's approval (integrations.asks). Host-task asks have task = true and a detail. */
export interface IntegrationAsk {
  id: number; at: string; integration: string; teamId: number; member: string; command: string; reason: string; workflow: number;
  repo?: string | null; action?: string | null; canAlways?: boolean; task?: boolean; taskName?: string; commit?: string; detail?: TaskAskDetail | null;
}
export interface HostTaskDef {
  name: string; repo: string; program: string; args: string[]; workdir: string; timeoutSeconds: number; envNames: string[]; watch: string[];
  pinMode: 'files' | 'commit'; commandLineOnly: boolean;
}
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
export interface Overview { slots: RunSlots; teams: TeamSummary[]; needsYou?: number; needsReconnect?: number; networkAsks?: number; toolAsks?: number; heavy?: LeaseSnapshot }
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
  workflow: number | null; state: 'pending' | 'allowed' | 'denied' | 'closed'; scope: Exclude<NetworkAskChoice, 'deny'> | null;
  decidedBy: string | null; decidedAt: string | null; plainHttp: boolean; choices: NetworkAskChoice[];
}
/** One host on a team's network allow list (policy.teamHosts; core: Policy/PolicyStore.cs). addedVia: how it was added (app, cloud, ...). */
export interface TeamHost { host: string; addedBy: string; addedVia: string | null; addedAt: string }
/** policy.teamHosts {teamId} and policy.removeTeamHost {teamId, host}: the team's list, oldest first. */
export interface TeamHosts { teamId: number; hosts: TeamHost[] }
/** network.answer's reply: closed lists every ask the answer closed (the team's or everyone's for that host). */
export interface NetworkAnswer { ok: boolean; id: number; host: string; choice: NetworkAskChoice; closed: number[]; told: string[] }
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
  reason: string | null; count: number; lastSeen: string; workflow: number | null;
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

/** A trigger (triggers.list / triggers.save): work a member is told on a schedule, as a new workflow. */
export interface TriggerSchedule {
  kind: 'every' | 'cron' | 'once' | 'folder' | 'event'; intervalSec: number | null; cron: string | null; timezone: string | null; fireAt: string | null;
}
export type WakeManager = 'always' | 'onHandbackOrFailure' | 'never';
export interface Trigger {
  id: number; teamId: number; memberId: number; name: string; schedule: TriggerSchedule; instruction: string;
  onlyWhenIdle: boolean; enabled: boolean; nextDueAt: string | null; lastFiredAt: string | null;
  /** Whether the trigger's runs wake the Manager when they end (a hand-back always does). */
  wakeManager: WakeManager;
  /** A folder-change trigger's folder: the team's documents (path inside) or a folder on this computer. */
  watch?: { root: 'documents' | 'host'; path: string; glob: string | null; quietSec: number } | null;
  /** An event trigger's event name pattern (web.pr, web.*). */
  eventName?: string | null;
  lastOutcome: 'fired' | 'skipped' | 'missed' | 'member-missing' | null; lastReason: string | null; lastSeq: number | null; missedCount: number;
}
export interface TriggerFire { outcome: string; seq: number | null; reason: string | null }

/** The Git dialog (git.status / git.fetch / git.bringCurrent / git.merge / git.askTeam / git.deleteBranches; core Teams/TeamRepos.Git.cs). */
export interface TeamGitBranch { branch: string; sha: string; aheadOfBase: number; inTeamBranch: boolean }
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
}
export interface TeamGitBlockingBranch { name: string; repo: string; location: 'local' | 'remote' }
/** git.deleteBranches without confirm: what would be deleted, as text for the person, and the token that confirms exactly
 * that plan (empty: nothing to delete). With confirm: token it deletes and answers GitDeleteResult; error code stale
 * means the branches changed since the plan. */
export interface GitDeletePlan { plan: string; token: string; empty: boolean }
export interface GitDeleteResult { result: string }
/** teams.syncToBase: the team's "Bring branches up to <base> before new work" setting, the note of a sync or fetch-back
 * that could not be done (one line per problem, files after "conflicts in:"), and how far the team branch is behind. */
export interface TeamSyncState {
  on: boolean; note: string | null; baseBranch: string | null;
  behind: { teamBranch: string; count: number; text: string } | null;
}
/** git.sync, "Sync now" (core Teams/TeamRepos.Sync.cs BranchSyncResult). */
export interface BranchSyncResult {
  outcome: 'off' | 'no-base' | 'in-step' | 'synced' | 'conflict'; baseBranch: string; baseSha: string | null; behind: number;
  moved: string[]; conflicts: string[]; note: string | null;
}
export interface TeamGitResult {
  outcome: string; message: string; changed: boolean; sha: string | null; files: string[] | null; status: TeamGitStatus | null;
}

export interface MemberRow {
  id: number; teamId: number; team: string; name: string; role: 'manager' | 'member'; cli: 'claude' | 'grok' | 'codex' | 'copilot' | 'antigravity';
  instructions: string; model: string; credential: string;
}

export interface AgentCard {
  member: MemberRow; state: 'running' | 'queued' | 'idle'; queuePosition: number | null; queueLength: number;
  queuedInstructions: number; statusLine: string | null; runId: number | null; runStartedAt: string | null;
  lastRunState: string | null; lastRunReason: string | null;
  /** The heavy-work lease: this member's run holds it or waits in its queue (position), for what step. */
  lease?: 'holding' | 'waiting' | null; leaseWhat?: string | null; leasePosition?: number | null;
  /** What the member needs from a person, loudest first: an open question, blocked, its last run failed (core UiRpc.Attention). */
  attention?: 'decision' | 'blocked' | 'failed' | null;
  net?: string; wallTimeout?: string; loosened?: string[]; policyProblems?: string[];
  /** The member has a working copy (clone) on this computer: its card offers the Code browser (teams.agents). */
  hasClone?: boolean;
}

export interface Decision { seq: number; from: string; question: string; workflow: number; at: string }

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

/** A follow-up the Concierge keeps: a workflow it dispatched (workflow set) or a whole team. */
export interface ConciergeWatch {
  id: number;
  team: string;
  workflow: number | null;
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
export type CardStatus = 'queued' | 'running' | 'blocked' | 'needs-decision' | 'failed' | 'completed' | 'handback' | 'done';
export type LaneId = 'todo' | 'in-progress' | 'blocked' | 'done';
export interface BoardCard {
  id: number; team: string; teamId: number | null; member: string | null; item: string | null; title: string; body: string;
  status: CardStatus; lane: LaneId; placed: boolean; awaitingManager: boolean; workflow: number | null;
  createdAt: string; updatedAt: string; trailCount: number;
  /** When the person cleared this Done card from the board view (0037); null or absent when it shows. */
  clearedAt?: string | null;
  memberState?: { state: AgentCard['state']; attention: AgentCard['attention'] };
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
  archivedAt: string | null; dispatchedTeam: string | null; dispatchedWorkflow: number | null; dispatchedAt: string | null; inFlight: boolean;
  createdAt: string; updatedAt: string; dispatchCount: number;
}
/** One dispatch of a backlog item (0023): its team and workflow, read live from the bus, the board and usage records. */
export interface BacklogDispatch {
  team: string; teamId: number | null; teamGone: boolean; workflow: number; dispatchedAt: string; dispatchedBy: string | null;
  state: 'open' | 'completed' | 'closed' | null; ending: string | null; busy: string[] | null; lastAt: string | null;
  cards: number; cardsDone: number; runs: number; members: string[]; inputTokens: number; outputTokens: number;
  firstStart: string | null; lastEnd: string | null;
}
export interface BacklogItemDetail extends BacklogItem { dispatches: BacklogDispatch[] }
