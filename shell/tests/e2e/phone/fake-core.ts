// B037 verify (card 169): a FAKE instance core for remote.html. It answers the screens' JSON-RPC over the page's
// WebSocket with canned data only: no core, no sandbox, no real programs, no network. A method it does not know fails
// with method_not_found (the screens keep going without it) and is recorded, so a test can say what was not faked.

export type Access = 'full' | 'read';

const now = Date.now();
const iso = (minsAgo: number) => new Date(now - minsAgo * 60_000).toISOString();
const later = (mins: number) => new Date(now + mins * 60_000).toISOString();

const teams = [
  { id: 1, name: 'payments', repoPath: 'C:/repos/payments', members: 3, running: 1, queued: 1 },
  { id: 2, name: 'checkout-web-with-a-rather-long-team-name', repoPath: 'C:/repos/checkout', members: 2, running: 0, queued: 0 },
];

const member = (id: number, teamId: number, team: string, name: string, role: 'manager' | 'member') =>
  ({ id, teamId, team, name, role, cli: 'claude', instructions: 'Builds things.', model: '', credential: 'default' });
const agent = (id: number, teamId: number, team: string, name: string, role: 'manager' | 'member', state: 'running' | 'queued' | 'idle') => ({
  member: member(id, teamId, team, name, role), state, queuePosition: state === 'queued' ? 1 : null, queueLength: state === 'queued' ? 1 : 0,
  queuedInstructions: 0, statusLine: state === 'running' ? 'Editing src/payments/refund.ts to add the partial refund path' : null,
  runId: state === 'running' ? 41 : null, runStartedAt: state === 'running' ? iso(3) : null, lastRunState: 'completed', lastRunReason: null,
  attention: null, net: 'allowlist', wallTimeout: '2h', loosened: [], policyProblems: [], hasClone: true,
});
const agents: Record<number, unknown[]> = {
  1: [agent(1, 1, 'payments', 'Manager', 'manager', 'idle'), agent(2, 1, 'payments', 'DeveloperAda', 'member', 'running'), agent(3, 1, 'payments', 'TesterBen', 'member', 'queued')],
  2: [agent(4, 2, teams[1].name, 'Manager', 'manager', 'idle'), agent(5, 2, teams[1].name, 'DeveloperCleo', 'member', 'idle')],
};

const lanes = [{ id: 'todo', label: 'To Do' }, { id: 'in-progress', label: 'In Progress' }, { id: 'blocked', label: 'Needs You' }, { id: 'done', label: 'Done' }];
const card = (id: number, lane: string, title: string, status: string, memberName = 'DeveloperAda') => ({
  id, team: 'payments', teamId: 1, member: memberName, item: 'B012', title, body: `${title}\n\nThe details of the card, with a long line that has to wrap on a phone screen without making it scroll sideways.`,
  status, lane, placed: false, awaitingManager: false, workflow: 10, createdAt: iso(120), updatedAt: iso(id), trailCount: 2,
});
const cards = [
  card(1, 'todo', 'Add partial refunds', 'queued'), card(2, 'todo', 'A very long card title that goes on and on to check the wrapping of card titles on a phone', 'queued'),
  card(3, 'in-progress', 'Refund webhook', 'running'), card(4, 'blocked', 'Pick the refund limit', 'needs-decision', 'Manager'),
  card(5, 'done', 'Refund model', 'done', 'TesterBen'), card(6, 'done', 'Docs for refunds', 'done'),
];

const backlogItem = (id: number, title: string, state: string) => ({
  id, label: `B0${10 + id}`, team: null, title, body: `# ${title}\n\nWhat it is and how it is checked.`, state, archivedAt: null,
  dispatchedTeam: state === 'declared' ? 'payments' : null, dispatchedWorkflow: state === 'declared' ? 10 : null, dispatchedAt: state === 'declared' ? iso(60) : null,
  inFlight: state === 'declared', createdAt: iso(600 + id), updatedAt: iso(100 + id), dispatchCount: state === 'declared' ? 1 : 0,
});
const backlog = [backlogItem(1, 'Partial refunds', 'declared'), backlogItem(2, 'Refund limits per merchant, set by the Owner in Settings', 'ready'), backlogItem(3, 'Refund report', 'pending')];

const networkAsk = {
  id: 7, at: iso(10), teamId: 1, team: 'payments', member: 'payments/DeveloperAda', memberName: 'DeveloperAda', host: 'api.nuget.org',
  reason: 'restore the NuGet packages for the build', method: 'CONNECT', pathSample: null, count: 3, lastSeen: iso(5), hint: 'NuGet: .NET packages',
  workflow: 10, state: 'pending', scope: null, decidedBy: null, decidedAt: null, plainHttp: false, choices: ['agent', 'team', 'everywhere', 'deny'],
};
const toolAsk = {
  id: 4, at: iso(10), teamId: 1, team: 'payments', member: 'payments/TesterBen', memberName: 'TesterBen', tool: 'dotnet-sdk-10',
  displayName: '.NET SDK 10', publisher: 'Microsoft', wingetId: 'Microsoft.DotNet.SDK.10', scope: 'machine', reason: 'build and test', count: 2,
  lastSeen: iso(5), workflow: 10, state: 'pending', attempts: 0, error: null, decidedBy: null, decidedVia: null, decidedAt: null,
  canInstall: true, command: 'winget install --id Microsoft.DotNet.SDK.10 --exact --silent', choices: ['install', 'checkAgain', 'notNow'], installer: 'winget',
};
const integrationAsk = {
  id: 9, at: iso(8), integration: 'gh', teamId: 1, member: 'DeveloperAda', command: 'gh pr create --title "Partial refunds" --body-file pr.md',
  reason: 'open the pull request for card 1', workflow: 10, repo: 'payments', action: 'pr create', canAlways: true,
};
const decision = { seq: 77, from: 'Manager', question: 'What should the refund limit be: 30 days or 90 days?', workflow: 10, at: iso(4) };

const conversations = [
  { id: 'c1', title: 'Payments today', cli: 'claude', teamId: 1, team: 'payments', createdAt: iso(300), updatedAt: iso(2), running: false, unread: 1, watching: 1 },
  { id: 'c2', title: 'A long chat title that should be cut off in the drawer', cli: 'claude', teamId: null, team: null, createdAt: iso(900), updatedAt: iso(200), running: false, unread: 0, watching: 0 },
];
const conversation = (id: string) => ({
  ...(conversations.find((c) => c.id === id) ?? conversations[0]),
  messages: [
    { id: 1, role: 'user', text: 'What is payments doing?', at: iso(10), state: null, error: null, tools: [], cli: null },
    { id: 2, role: 'assistant', text: 'DeveloperAda is on **partial refunds** (card 1); TesterBen is queued behind it.\n\n```\nnpm test -- refunds\n```\n\nA very long line: https://example.invalid/a/very/long/path/that/does/not/break/naturally/without/overflow-wrap/anywhere', at: iso(9), state: 'done', error: null, tools: [{ name: 'board', detail: 'show 1' }], cli: 'claude' },
    { id: 3, role: 'update', text: 'payments: card 3 is done.', at: iso(3), state: null, error: null, tools: [], cli: null, team: 'payments', kind: 'done' },
  ],
  turn: null,
  watches: [{ id: 1, team: 'payments', workflow: 10, what: 'partial refunds', followUp: true, createdAt: iso(10), expiresAt: later(600) }],
});

const col = (key: string, label: string, kind: 'time' | 'text' | 'number' = 'text') => ({ key, label, kind, filter: kind === 'text' });
const logSources = [{
  id: 'activity', title: 'All activity', group: 'Activity', about: 'Everything the agents did.', columns: [col('at', 'Time', 'time'), col('member', 'Member'), col('event', 'Event'), col('details', 'Details')],
  prunable: false, loss: null, keptBecause: 'read from the others', defaultDays: null, retentionDays: null, team: true, member: true,
}];
const logRows = Array.from({ length: 12 }, (_, i) => ({ _id: String(i + 1), at: iso(i * 7), member: i % 2 ? 'payments/DeveloperAda' : 'payments/Manager', event: `run ${40 - i}`, details: i === 0 ? 'a-very-long-unbroken-detail-string-'.repeat(4) : 'completed in 2m' }));

const workflows = [{ id: 10, title: 'Partial refunds', owner: 'Manager', state: 'open', undeclared: false, busy: ['DeveloperAda'], ending: null, startedAt: iso(120), lastAt: iso(2), messages: 14 }];

export const answers: Record<string, (p: any, access: Access) => unknown> = {
  'core.getInfo': (_p, access) => ({ product: 'Yawble', version: '0.0.0+dev.fake', platform: 'win32', os: 'Windows (fake)', dotnet: '10', pid: 1, sandbox: { backend: 'fake' }, schema: [], hostChecks: [], remoteAccess: access }),
  'sandbox.getAvailableBackends': () => [],
  'app.overview': () => ({ slots: { running: 1, limit: 4, queued: 1 }, teams, needsYou: 4, networkAsks: 1, toolAsks: 1, heavy: { limit: 1, holders: [{ team: 'payments', member: 'DeveloperAda', what: 'full test suite', since: iso(2) }], queue: [] } }),
  'app.version': () => ({ version: '0.0.0+dev.fake', commit: 'fake', dirty: false, released: false, cloudVersion: null }),
  'link.status': () => ({ state: 'connected', url: 'https://cloud.example.invalid/', viewers: ['kofi@example.invalid'], code: null, codeExpiresAt: null, instanceId: 'i1', instanceName: 'fake' }),
  'agents.detect': () => [{ cliId: 'claude', found: true, version: '2.0.0', path: 'C:/fake/claude.exe', signIn: { status: 'signed-in', detail: null } }],
  'concierge.list': () => conversations,
  'concierge.get': (p) => conversation(p?.id ?? 'c1'),
  'concierge.read': () => ({ ok: true }),
  'network.asks': () => [networkAsk],
  'tools.asks': () => [toolAsk],
  'integrations.asks': () => (needsYou ? [integrationAsk] : []),
  'integrations.connectionAttention': () => [{ id: 3, name: 'Stripe test key', reason: '401 from the API', where: 'Settings, Connections' }],
  'companion.list': () => ({ clients: [], pairings: [{ id: 'p1', name: 'claude-code', level: 'act', code: 'K7Q2ZD', expires: later(5) }], actions: [] }),
  'companion.status': () => ({ endpoint: null, controlFile: null, binaryPath: null, claudeCommand: null }),
  'bus.decisions': (p) => !needsYou ? [] : (p?.teamId === 1 || p?.team === 'payments' || p?.teamId === undefined ? [decision] : []),
  'bus.workflows': () => workflows,
  'teams.agents': (p) => agents[p?.teamId ?? p?.id ?? 1] ?? [],
  'teams.quiet': () => ({ quiet: false, working: ['DeveloperAda'], openWorkflows: [10] }),
  'teams.approvals': () => [],
  'teams.repos': () => [],
  'agentProfiles.list': () => [],
  'board.list': () => ({ lanes, cards, teams: teams.map((t) => ({ id: t.id, name: t.name })), clearedCount: 0 }),
  'board.card': (p) => ({ ...cards.find((c) => c.id === p?.id)!, trail: [{ id: 1, at: iso(30), source: 'Manager', kind: 'planned', text: 'Planned the card.' }, { id: 2, at: iso(20), source: 'Manager', kind: 'assigned', text: 'Assigned to DeveloperAda.' }], itemTitle: 'Partial refunds' }),
  'backlog.list': () => backlog,
  'backlog.item': (p) => ({ ...backlog.find((b) => b.id === p?.id)!, dispatches: [] }),
  'monitor.messages': () => ({ end: 0, messages: [] }),
  'monitor.steps': () => ({ end: 0, pending: false, steps: [] }),
  'logs.sources': () => structuredClone(logSources),
  'logs.who': () => ({ teams: ['payments'], members: [{ team: 'payments', name: 'DeveloperAda', key: 'payments/DeveloperAda' }] }),
  'logs.values': () => [],
  'logs.query': () => ({ rows: logRows, total: logRows.length, asOf: iso(0), next: null }),
  'docs.list': (p) => ({ path: p?.path ?? '', entries: [
    { name: 'B037-report.md', path: 'B037-report.md', folder: false, size: 2048, modified: iso(30) },
    { name: 'a-document-with-a-very-long-file-name-that-should-be-cut-off-on-a-phone.md', path: 'long.md', folder: false, size: 10, modified: iso(60) },
    { name: 'specs', path: 'specs', folder: true, size: null, modified: iso(90) },
  ] }),
  'docs.read': (p) => ({ path: p?.path, name: 'B037-report.md', size: 2048, modified: iso(30), kind: 'markdown', text: '# B037 report\n\n| Screen | 390 | 360 |\n|---|---|---|\n| Teams | ok | ok |\n\nA paragraph of text.' }),
  'code.open': () => ({ session: 's1', root: 'C:/fake/clone', latest: { path: 'src/refund.ts', line: 3 }, clonePresent: true }),
  'code.list': (p) => ({ path: p?.path ?? '', entries: [
    { name: 'src', path: 'src', kind: 'dir', size: null, mtime: iso(5), marker: null, hidden: false },
    { name: 'refund.ts', path: 'src/refund.ts', kind: 'file', size: 80, mtime: iso(1), marker: 'live', hidden: false },
    { name: 'README.md', path: 'README.md', kind: 'file', size: 30, mtime: iso(50), marker: null, hidden: false },
  ] }),
  'code.read': (p) => ({ path: p?.path, type: 'text', size: 80, content: 'export function refund(amount: number, limitDays = 30): boolean {\n  return amount > 0 && limitDays <= 90; // a long comment that runs past the edge of a phone screen\n}\n', lines: [], removed: [], linesUnknown: false }),
  'code.close': () => ({}),
  // A team's Git dialog asks with its teamId (a TeamGitStatus); without one, the computer's git (B054 verify: the Git
  // sheet behind the team page's ⋯ rendered nothing with the computer's answer).
  'git.status': (p) => (p?.teamId == null ? { installed: true, version: '2.50', path: 'C:/fake/git.exe' } : {
    repoPath: 'C:/repos/payments', managed: true, baseBranch: 'main', checkedOut: 'main', dirty: false, origin: null, baseSha: 'abc1234', originBaseSha: null,
    baseAheadOfOrigin: null, baseBehindOrigin: null, teamBranch: 'team/payments', teamSha: 'def5678', teamAheadOfBase: 2, teamBehindBase: 0, merge: 'fast-forward',
    conflictFiles: [], members: [], oldBranches: [], blockingBranches: [],
  }),
  'updates.status': () => ({ state: 'idle', current: '0.0.0+dev.fake', available: null, auto: false }),
  'credentials.status': () => ({ mode: 'subscription', claude: { connected: true, addedAt: iso(1000), expiresAt: null, lastUsedAt: iso(5), renewSoon: false, problem: null } }),
  'triggers.list': () => [],
  // B054 verify (card 270): skills to pick on a phone, and a Guardrails page long enough to scroll to its bottom.
  'skills.list': () => Array.from({ length: 12 }, (_, i) => ({
    name: `skill-${i}`, description: `When to load skill ${i}: a one-line description that wraps on a phone.`, roles: ['member'],
    body: `# Skill ${i}\n\n${'What to do, step by step. '.repeat(30)}`, kind: i < 6 ? 'builtin' : 'custom', teamId: null, team: null, id: i < 6 ? null : i, updatedAt: iso(i * 10),
  })),
  'policy.getGlobal': () => ({
    filesystem: { grantableRoots: ['~/src', 'D:/work'], readonly: ['C:/tools', 'C:/sdk', 'C:/Program Files/Git'], readwrite: ['C:/scratch'], denied: ['~/.ssh', '~/.aws', '~/.azure'] },
    network: { allowHosts: ['api.github.com', 'registry.npmjs.org', 'api.nuget.org', 'pypi.org', 'files.pythonhosted.org', 'objects.githubusercontent.com'] },
    compute: { wallTimeoutSec: 7200, idleTimeoutSec: 1800, memoryMb: null, cpuPercent: null, maxProcesses: null },
    ui: { clipboard: 'read' }, app: { maxConcurrentAgents: 4, backend: 'sandbox', heavyWorkAtOnce: 1 },
  }),
  'teams.syncToBase': () => (syncBehind ? { on: true, note: null, baseBranch: 'main', behind: { teamBranch: 'team/payments', count: 3, text: '3 commits behind main' } } : { on: true, note: null, baseBranch: 'main', behind: null }),
  'tasks.list': () => [],
  'members.get': (p) => (agents[1] as any[]).map((a) => a.member).find((m) => m.id === p?.id) ?? (agents[1] as any)[0].member,
};

/** Methods a viewer with "read" access may not call (the fake refuses them, like the Cloud Link). */
/** B054: with true, teams.syncToBase says the team branch is behind (the team page shows its sync bar). */
let syncBehind = false;
/** B054: with false, no integration ask and no Manager decision is pending (the team page shows its members under Tell). */
let needsYou = true;

const isChange = (m: string) => /\.(send|answer|save|delete|remove|create|add|update|install|run|stop|move|rename|tellManager|approve|decline)/.test(m);

export function createFakeCore() {
  const unknown = new Set<string>();
  const calls: string[] = [];
  let access: Access = 'full';
  let asks = true;
  return {
    unknown, calls,
    setAccess(a: Access) { access = a; },
    /** With false, no network or tool asks are pending (the global Needs-you banners stay away). */
    setAsks(on: boolean) { asks = on; },
    /** B054: the team branch behind its base (the team page's sync bar shows). */
    setSync(behind: boolean) { syncBehind = behind; },
    /** B054: with false, no integration ask and no decision is pending. */
    setNeedsYou(on: boolean) { needsYou = on; },
    answer(text: string): string | null {
      const msg = JSON.parse(text) as { id: number; method: string; params: any };
      calls.push(msg.method);
      if (access === 'read' && isChange(msg.method)) return JSON.stringify({ id: msg.id, error: { code: 'remote_refused', message: 'Read-only (Visitor): refused by the fake' } });
      if (!asks && (msg.method === 'network.asks' || msg.method === 'tools.asks')) return JSON.stringify({ id: msg.id, result: [] });
      const a = answers[msg.method];
      if (!a) { unknown.add(msg.method); return JSON.stringify({ id: msg.id, error: { code: 'method_not_found', message: `fake core: ${msg.method} is not faked` } }); }
      try { return JSON.stringify({ id: msg.id, result: a(msg.params, access) ?? null }); }
      catch (e) { return JSON.stringify({ id: msg.id, error: { code: 'fake_error', message: String(e) } }); }
    },
  };
}
