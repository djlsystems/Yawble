// B037 verify (card 169): a FAKE instance core for remote.html. It answers the screens' JSON-RPC over the page's
// WebSocket with canned data only: no core, no sandbox, no real programs, no network. A method it does not know fails
// with method_not_found (the screens keep going without it) and is recorded, so a test can say what was not faked.

export type Access = 'full' | 'read';

const now = Date.now();
const iso = (minsAgo: number) => new Date(now - minsAgo * 60_000).toISOString();
const later = (mins: number) => new Date(now + mins * 60_000).toISOString();

const teamRows = [
  { id: 1, name: 'payments', repoPath: 'C:/repos/payments', members: 3, running: 1, queued: 1 },
  { id: 2, name: 'checkout-web-with-a-rather-long-team-name', repoPath: 'C:/repos/checkout', members: 2, running: 0, queued: 0 },
];
/** Like the core's TeamsAsync (teams.list and app.overview): each team with its workflowExpiryDays (B085, default 3). */
const teamList = () => teamRows.map((t) => ({ ...t, workflowExpiryDays: expiryDays(t.id) }));

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
  2: [agent(4, 2, teamRows[1].name, 'Manager', 'manager', 'idle'), agent(5, 2, teamRows[1].name, 'DeveloperCleo', 'member', 'idle')],
};

/** B086 (core card 429's wire contract): each card's stretches, back to back from its creation, as minutes; the last one
 *  of an open card runs on to now. From them the fake builds BoardCard.time, timeline.get's rows and bands, workflow #10's
 *  summary row (the core's priority merge) and its time, so every figure adds up like the core's. */
type Cat = 'agent-working' | 'stalled' | 'waiting-on-you' | 'waiting-for-slot' | 'waiting-on-teammate' | 'blocked' | 'scheduled' | 'not-started' | 'idle' | 'was-done';
const Merge: Cat[] = ['agent-working', 'waiting-on-you', 'stalled', 'waiting-for-slot', 'waiting-on-teammate', 'blocked', 'scheduled', 'not-started', 'idle', 'was-done'];
const stretches: Record<number, { member: string; title: string; open: boolean; rebuilt?: boolean; segs: [Cat, number][] }> = {
  1: { member: 'DeveloperAda', title: 'Add partial refunds', open: true, segs: [['not-started', 120]] },
  2: { member: 'DeveloperAda', title: 'A very long card title', open: true, segs: [['not-started', 120]] },
  3: { member: 'DeveloperAda', title: 'Refund webhook', open: true, segs: [['not-started', 20], ['waiting-for-slot', 8], ['agent-working', 34], ['stalled', 38], ['agent-working', 20]] },
  4: { member: 'Manager', title: 'Pick the refund limit', open: true, segs: [['not-started', 5], ['agent-working', 10], ['waiting-on-you', 105]] },
  5: { member: 'TesterBen', title: 'Refund model', open: false, segs: [['not-started', 10], ['agent-working', 25], ['waiting-on-teammate', 10], ['agent-working', 15]] },
  6: { member: 'DeveloperAda', title: 'Docs for refunds', open: false, rebuilt: true, segs: [['not-started', 30], ['agent-working', 12], ['was-done', 6], ['agent-working', 8]] },
};
const timeline = (() => {
  const t0 = now - 120 * 60_000;
  const at = (ms: number) => new Date(ms).toISOString();
  const cards: Record<number, { time: unknown; row: unknown; markers: unknown[]; bands: { category: Cat; s: number; e: number | null }[] }> = {};
  for (const [id, c] of Object.entries(stretches)) {
    let x = t0;
    const bands = c.segs.map(([category, mins], i) => {
      const s = x; x += mins * 60_000;
      return { category, s, e: c.open && i === c.segs.length - 1 ? null : x };
    });
    const sum = new Map<Cat, number>();
    for (const b of bands) sum.set(b.category, (sum.get(b.category) ?? 0) + ((b.e ?? now) - b.s));
    const started = bands.find((b) => b.category !== 'not-started')?.s;
    const time = {
      createdAt: at(t0), startedAt: started ? at(started) : null, endedAt: c.open ? null : at(x), endedHow: c.open ? null : 'done',
      open: c.open, overallMs: (c.open ? now : x) - t0, rebuilt: !!c.rebuilt, breakdown: [...sum].map(([category, ms]) => ({ category, ms })),
    };
    const markers: unknown[] = [{ kind: 'planned', at: at(t0), card: Number(id) }];
    if (started) markers.push({ kind: 'started', at: at(started), card: Number(id) });
    bands.forEach((b, i) => {
      if (b.category === 'stalled' && b.e) markers.push({ kind: 'watchdog-end', at: at(b.e), card: Number(id) });
      if (b.category === 'was-done') markers.push({ kind: 'reopen', at: at(b.e!), card: Number(id) });
      if (b.category === 'waiting-on-you') markers.push({ kind: 'decision-asked', at: at(b.s), card: Number(id) });
      if (!c.open && i === bands.length - 1) markers.push({ kind: 'handback', at: at(b.e!), card: Number(id) });
    });
    const row = { card: Number(id), member: c.member, title: c.title, bands: bands.map((b) => ({ category: b.category, start: at(b.s), end: b.e == null ? null : at(b.e) })) };
    cards[Number(id)] = { time, row, markers, bands };
  }
  // Workflow #10: every card's stretches merged per moment, the higher category winning (the core's merge).
  const edges = [...new Set(Object.values(cards).flatMap((c) => c.bands.flatMap((b) => [b.s, b.e ?? now])))].sort((a, b) => a - b);
  const summary: { category: Cat; s: number; e: number }[] = [];
  for (let i = 1; i < edges.length; i++) {
    const mid = (edges[i - 1] + edges[i]) / 2;
    const here = Object.values(cards).flatMap((c) => c.bands.filter((b) => b.s <= mid && (b.e ?? now) > mid).map((b) => b.category));
    const cat = Merge.find((m) => here.includes(m)) ?? 'idle';
    const last = summary[summary.length - 1];
    if (last && last.category === cat && last.e === edges[i - 1]) last.e = edges[i]; else summary.push({ category: cat, s: edges[i - 1], e: edges[i] });
  }
  const wsum = new Map<Cat, number>();
  for (const b of summary) wsum.set(b.category, (wsum.get(b.category) ?? 0) + (b.e - b.s));
  const workflow = {
    time: { createdAt: at(t0), startedAt: at(t0 + 10 * 60_000), endedAt: null, endedHow: null, open: true, overallMs: now - t0, rebuilt: false,
      breakdown: [...wsum].map(([category, ms]) => ({ category, ms })) },
    rows: Object.values(cards).map((c) => c.row),
    summary: summary.map((b) => ({ category: b.category, start: at(b.s), end: b.e >= now ? null : at(b.e) })),
    markers: Object.values(cards).flatMap((c) => c.markers),
  };
  return { cards, workflow };
})();

const lanes = [{ id: 'todo', label: 'To Do' }, { id: 'in-progress', label: 'In Progress' }, { id: 'blocked', label: 'Needs You' }, { id: 'done', label: 'Done' }];
const card = (id: number, lane: string, title: string, status: string, memberName = 'DeveloperAda') => ({
  id, team: 'payments', teamId: 1, member: memberName, item: 'B012', title, body: `${title}\n\nThe details of the card, with a long line that has to wrap on a phone screen without making it scroll sideways.`,
  status, lane, placed: false, awaitingManager: false, workflow: 10, createdAt: iso(120), updatedAt: iso(id), trailCount: 2,
});
const cards = [
  card(1, 'todo', 'Add partial refunds', 'queued'), card(2, 'todo', 'A very long card title that goes on and on to check the wrapping of card titles on a phone', 'queued'),
  card(3, 'in-progress', 'Refund webhook', 'running'), card(4, 'blocked', 'Pick the refund limit', 'needs-decision', 'Manager'),
  card(5, 'done', 'Refund model', 'done', 'TesterBen'), card(6, 'done', 'Docs for refunds', 'done'),
].map((c) => ({ ...c, time: timeline.cards[c.id]?.time }));

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

const workflows = [{ id: 10, title: 'Partial refunds', owner: 'Manager', state: 'open', undeclared: false, busy: ['DeveloperAda'], ending: null, startedAt: iso(120), lastAt: iso(2), messages: 14, endedAt: null, expiredAt: null, expiresAt: null, expiryWarning: null, question: null, time: timeline.workflow.time }];
/** B085 (card 389), shown with setExpiry(true): an open workflow waiting on question 3 that expires in 20 h (Needs you,
 *  Extend), and one the app closed as expired (endedAt: its last activity, 3 days before expiredAt). */
const expiryWorkflows = () => [
  { id: 12, title: 'Refund limits per country', owner: 'Manager', state: 'open', undeclared: true, busy: [], ending: null, startedAt: iso(4000), lastAt: iso(2 * 1440 + 240), messages: 6,
    endedAt: null, expiredAt: null, expiresAt: expiry.extended.get(12) ?? later(20 * 60),
    // Like the core: the warning text while the workflow is warned (it waits on a question and expires within a day), else null.
    expiryWarning: expiry.extended.has(12) ? null : warningText(12, 3), question: 3, time: idleTime(4000, null, null) },
  // The core's ending for an expired workflow is the detail only; the dialog adds "Expired: " itself.
  { id: 9, title: 'Old refund export', owner: 'Manager', state: 'expired', undeclared: false, busy: [], ending: 'no activity for 3 days', startedAt: iso(9000), lastAt: iso(4 * 1440), messages: 4,
    endedAt: iso(4 * 1440), expiredAt: iso(1440), expiresAt: null, expiryWarning: null, question: null, time: idleTime(9000, 4 * 1440, 'expired') },
];
/** B086: a quiet workflow's time: 40 minutes worked after its first message, then idle to its end (or now, open). */
function idleTime(createdAgo: number, endedAgo: number | null, how: string | null) {
  const overallMs = (createdAgo - (endedAgo ?? 0)) * 60_000;
  return { createdAt: iso(createdAgo), startedAt: iso(createdAgo - 5), endedAt: endedAgo == null ? null : iso(endedAgo), endedHow: how, open: endedAgo == null,
    overallMs, rebuilt: false, breakdown: [{ category: 'not-started', ms: 5 * 60_000 }, { category: 'agent-working', ms: 40 * 60_000 }, { category: 'idle', ms: overallMs - 45 * 60_000 }] };
}
/** timeline.get for a quiet workflow: its summary row from its breakdown, back to back (the long idle stretch is compressed on screen). */
function quietTimeline(time: ReturnType<typeof idleTime>) {
  let x = Date.parse(time.createdAt);
  const summary = time.breakdown.map((b, i) => { const s = x; x += b.ms; return { category: b.category, start: new Date(s).toISOString(), end: time.open && i === time.breakdown.length - 1 ? null : new Date(x).toISOString() }; });
  return { time, rows: [], summary, markers: [{ kind: 'planned', at: time.createdAt }, ...(time.endedHow === 'expired' ? [{ kind: 'expired', at: time.endedAt! }] : [])] };
}
/** The core's BusEngine.WarningText. */
const warningText = (workflow: number, question: number) => `#${workflow} will expire tomorrow; it waits on your answer to question ${question}`;
/** B085: the expiry fixtures on or off, the workflows extended (id -> their new expiresAt), and each team's workflowExpiryDays. */
const expiry = { on: false, extended: new Map<number, string>(), days: new Map<number, number>() };
const expiryDays = (teamId: number) => expiry.days.get(teamId) ?? 3;

/** B104 (card 477): the Workflows page's rows (workflows.list / workflows.get, YAWBLE_TEAM_DOCS/B104/contract.md), across
 *  both teams: #10 running (B011, Ada on it), #12 waiting on you, #15 waiting for the lease, #16 idle (expires in 20 h),
 *  and ended ones: #9 expired, #14 completed today (B013, archived), #8 closed. */
function b104Rows() {
  const wfTime = (createdAgo: number, endedAgo: number | null, how: string | null, segs: [Cat, number][]) => {
    const overallMs = (createdAgo - (endedAgo ?? 0)) * 60_000;
    const known = segs.reduce((a, [, m]) => a + m * 60_000, 0);
    return { createdAt: iso(createdAgo), startedAt: iso(createdAgo - 5), endedAt: endedAgo == null ? null : iso(endedAgo), endedHow: how, open: endedAgo == null,
      overallMs, rebuilt: false, breakdown: [...segs.map(([category, m]) => ({ category, ms: m * 60_000 })), ...(overallMs > known ? [{ category: 'idle', ms: overallMs - known }] : [])] };
  };
  const base = { undeclared: false, busy: [] as string[], ending: null as string | null, endedAt: null as string | null, expiredAt: null, expiresAt: null as string | null,
    expiryWarning: null, question: null as number | null, waitingFor: null as string | null, working: false, item: null as unknown, members: [] as unknown[], ownerId: 1 };
  const t1 = { team: 'payments', teamId: 1 }, t2 = { team: teamRows[1].name, teamId: 2 };
  return [
    { ...base, ...t1, id: 10, title: 'Partial refunds', owner: 'Manager', state: 'open', status: 'running', working: true, busy: ['DeveloperAda is running'], startedAt: iso(120), lastAt: iso(1), messages: 14,
      item: { label: 'B011', title: 'Partial refunds', archived: false }, members: [{ id: 2, name: 'DeveloperAda', state: 'running' }, { id: 3, name: 'TesterBen', state: 'queued' }], time: timeline.workflow.time },
    { ...base, ...t1, id: 12, title: 'Refund limits per merchant', owner: 'Manager', state: 'open', status: 'needs-you', question: 3, startedAt: iso(400), lastAt: iso(30), messages: 6,
      item: { label: 'B012', title: 'Refund limits per merchant, set by the Owner in Settings', archived: false }, time: wfTime(400, null, null, [['not-started', 5], ['agent-working', 60], ['waiting-on-you', 300]]) },
    { ...base, ...t2, id: 15, title: 'Checkout button states', owner: 'Manager', state: 'open', status: 'waiting', waitingFor: 'lease', working: true, startedAt: iso(90), lastAt: iso(4), messages: 9,
      members: [{ id: 5, name: 'DeveloperCleo', state: 'lease' }], time: wfTime(90, null, null, [['not-started', 4], ['agent-working', 50], ['waiting-for-slot', 30]]) },
    { ...base, ...t2, id: 16, title: 'Tidy the checkout styles', owner: 'Manager', state: 'open', status: 'idle', undeclared: true, startedAt: iso(2000), lastAt: iso(1500), messages: 3,
      expiresAt: later(20 * 60), time: wfTime(2000, null, null, [['not-started', 5], ['agent-working', 40]]) },
    { ...base, ...t1, id: 14, title: 'Refund report', owner: 'Manager', state: 'completed', status: 'completed', ending: 'Delivered the report', startedAt: iso(300), lastAt: iso(20), endedAt: iso(20), messages: 11,
      item: { label: 'B013', title: 'Refund report', archived: true }, time: wfTime(300, 20, 'completed', [['not-started', 10], ['agent-working', 200], ['waiting-on-teammate', 40], ['stalled', 30]]) },
    { ...base, ...t1, id: 9, title: 'Old refund export', owner: 'Manager', state: 'expired', status: 'expired', ending: 'no activity for 3 days', startedAt: iso(9000), lastAt: iso(4 * 1440), endedAt: iso(4 * 1440), expiredAt: iso(1440), messages: 4,
      time: wfTime(9000, 4 * 1440, 'expired', [['not-started', 5], ['agent-working', 40]]) },
    { ...base, ...t2, id: 8, title: 'Try a new payment provider', owner: 'Manager', state: 'closed', status: 'closed', ending: 'Not now', startedAt: iso(6000), lastAt: iso(3000), endedAt: iso(3000), messages: 2,
      time: wfTime(6000, 3000, 'closed', [['not-started', 5], ['agent-working', 15]]) },
  ];
}
const b104Chip: Record<string, (s: string) => boolean> = {
  all: () => true, open: (s) => ['running', 'needs-you', 'waiting', 'idle', 'stopped'].includes(s), 'needs-you': (s) => s === 'needs-you', done: (s) => s === 'completed', closed: (s) => s === 'closed' || s === 'expired',
};
function workflowsList(p: any) {
  let rows = b104Rows();
  if (p?.teamId != null) rows = rows.filter((w) => w.teamId === Number(p.teamId));
  const words = String(p?.q ?? '').toLowerCase().split(/\s+/).filter(Boolean);
  rows = rows.filter((w) => words.every((x) => [`#${w.id}`, w.title, (w.item as any)?.label ?? '', w.team, w.owner, ...w.members.map((m: any) => m.name)].some((f) => f.toLowerCase().includes(x))));
  const n = (f: (s: string) => boolean) => rows.filter((w) => f(w.status)).length;
  const counts = { all: rows.length, open: n(b104Chip.open), needsYou: n(b104Chip['needs-you']), done: n(b104Chip.done), closed: n(b104Chip.closed),
    running: n((s) => s === 'running'), waiting: n((s) => s === 'waiting'), idle: n((s) => s === 'idle'), stopped: 0, completed: n((s) => s === 'completed'), expired: n((s) => s === 'expired'), completedToday: n((s) => s === 'completed') };
  const keep = b104Chip[String(p?.state ?? 'all').toLowerCase()];
  if (!keep) throw Object.assign(new Error('state: not a chip or a status'), { code: 'bad_params' });
  return { workflows: rows.filter((w) => keep(w.status)).sort((a, b) => Date.parse(b.lastAt) - Date.parse(a.lastAt)), counts, truncated: false, backlog: true, now: new Date().toISOString() };
}
function workflowsGet(p: any) {
  const w = b104Rows().find((x) => x.id === Number(p?.workflow));
  if (!w) throw Object.assign(new Error('no such workflow'), { code: 'no_such_workflow' });
  const own = w.id === 10 ? cards : [];
  return {
    workflow: w,
    cards: own.map((c) => ({ id: c.id, title: c.title, lane: c.lane, member: c.member, status: c.status, time: c.time })),
    messages: [
      { seq: 120, at: iso(1), kind: 'progress', from: 'DeveloperAda', member: 'DeveloperAda', text: 'Card 3: wiring the refund webhook' },
      { seq: 110, at: iso(25), kind: 'handback', from: 'TesterBen', member: 'TesterBen', text: `Card 5 verified on ${COMMIT_ADA.slice(0, 7)}: 42 tests pass.` },
      { seq: 100, at: iso(118), kind: 'instruction', from: 'person', member: 'Manager', text: 'Backlog item B011 is dispatched to your team.' },
    ],
    spec: w.item ? `**Why:** refunds are asked for every day.\n\n## What\n- Partial refunds from the payment page\n- A refund report\n- Limits per merchant\n\n## Checks\n- Unit tests for every amount\n- An e2e run at 1280 and 375 px\n- The docs updated` : null,
    backlog: true,
  };
}

export const answers: Record<string, (p: any, access: Access) => unknown> = {
  'workflows.list': (p) => workflowsList(p),
  'workflows.get': (p) => workflowsGet(p),
  'core.getInfo': (_p, access) => ({ product: 'Yawble', version: '0.0.0+dev.fake', platform: 'win32', os: 'Windows (fake)', dotnet: '10', pid: 1, sandbox: { backend: 'fake' }, schema: [], hostChecks: [], remoteAccess: access }),
  'sandbox.getAvailableBackends': () => [],
  'teams.list': () => teamList(),
  'app.overview': () => ({ slots: { running: 1, limit: 4, queued: 1 }, teams: teamList(), needsYou: 4, networkAsks: 1, toolAsks: 1, heavy: { limit: 1, holders: [{ team: 'payments', member: 'DeveloperAda', what: 'full test suite', since: iso(2) }], queue: [] } }),
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
  'bus.workflows': () => (expiry.on ? [...workflows, ...expiryWorkflows()] : workflows),
  // B085: Extend resets the workflow's clock to now + the team's workflowExpiryDays (its warning goes); teams.workflowExpiry
  // reads, or sets with days. Like the core: returns the updated list item; an ended workflow is refused with already_ended.
  'bus.workflowExtend': (p) => {
    const id = Number(p?.workflow);
    const w = [...workflows, ...(expiry.on ? expiryWorkflows() : [])].find((x) => x.id === id);
    if (w && w.state !== 'open') throw Object.assign(new Error(`workflow #${id} has already ended`), { code: 'already_ended' });
    const expiresAt = new Date(Date.now() + expiryDays(Number(p?.teamId ?? 1)) * 86400_000).toISOString();
    expiry.extended.set(id, expiresAt);
    return { ...(w ?? { id }), expiresAt, expiryWarning: null };
  },
  'teams.workflowExpiry': (p, access) => {
    if (p?.days != null) {
      if (access === 'read') throw Object.assign(new Error('Read-only (Visitor): refused by the fake'), { code: 'remote_refused' });
      if (!Number.isInteger(p.days) || p.days < 1 || p.days > 365) throw Object.assign(new Error('days: a whole number from 1 to 365'), { code: 'bad_request' });
      expiry.days.set(Number(p.teamId), p.days);
    }
    return { days: expiryDays(Number(p?.teamId)) };
  },
  'teams.agents': (p) => agents[p?.teamId ?? p?.id ?? 1] ?? [],
  'teams.quiet': () => ({ quiet: false, working: ['DeveloperAda'], openWorkflows: [10] }),
  'teams.approvals': () => [],
  'teams.repos': () => [],
  'agentProfiles.list': () => [],
  'board.list': () => ({ lanes, cards, teams: teamRows.map((t) => ({ id: t.id, name: t.name })), clearedCount: 0 }),
  'board.card': (p) => ({ ...cards.find((c) => c.id === p?.id)!, trail: [{ id: 1, at: iso(30), source: 'Manager', kind: 'planned', text: 'Planned the card.' }, { id: 2, at: iso(20), source: 'Manager', kind: 'assigned', text: 'Assigned to DeveloperAda.' },
    // B072: a hand-back naming commits (card 5 only): one the repository has, one git no longer has.
    ...(p?.id === 5 ? [{ id: 3, at: iso(10), source: 'TesterBen', kind: 'handback', text: `Handed back: committed ${COMMIT_ADA.slice(0, 7)} on members/payments/developerada (the earlier ${COMMIT_GONE} is gone).` }] : [])], itemTitle: 'Partial refunds' }),
  // B072 (card 444): the Commit view against the contract DeveloperTomas builds in core (git.commitShow, git.commitDiff).
  'git.commitShow': (p) => commitShow(String(p?.sha ?? '')),
  'git.commitDiff': (p) => commitDiff(String(p?.sha ?? ''), String(p?.path ?? '')),
  // B074 (card 471): which IDs on a screen are commits, as the card's contract says (B074/contract.md): per ID its
  // commits; one git no longer has is none (it stays text).
  'git.commitsResolve': (p) => ({ max: 200, results: ((p?.ids ?? []) as string[]).map((id) => ({ id: id.toLowerCase(), valid: true, matches: commitsResolve(id) })) }),
  // B086: a card's own row, or workflow #10's rows and summary; an unknown one is refused like the core (not_found).
  'timeline.get': (p) => {
    if (p?.workflow != null) { const quiet = (expiry.on ? expiryWorkflows() : []).find((w) => w.id === Number(p.workflow));
      if (quiet) return quietTimeline(quiet.time);
      if (Number(p.workflow) !== 10) throw Object.assign(new Error('no such workflow'), { code: 'not_found' }); return timeline.workflow; }
    const c = timeline.cards[Number(p?.card)];
    if (!c) throw Object.assign(new Error('no such card'), { code: 'not_found' });
    return { time: c.time, rows: [c.row], summary: null, markers: c.markers };
  },
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
  'git.status': (p) => (p?.teamId == null ? { installed: true, version: '2.50', path: 'C:/fake/git.exe' } : b073.on ? b073Status() : {
    repoPath: 'C:/repos/payments', managed: true, baseBranch: 'main', checkedOut: 'main', dirty: false, origin: null, baseSha: 'abc1234', originBaseSha: null,
    baseAheadOfOrigin: null, baseBehindOrigin: null, teamBranch: 'team/payments', teamSha: 'def5678', teamAheadOfBase: 2, teamBehindBase: 0, merge: 'fast-forward',
    conflictFiles: [], members: [{ branch: 'members/payments/developerada', sha: COMMIT_ADA, aheadOfBase: 2, inTeamBranch: true }], oldBranches: [], blockingBranches: [],
  }),
  // B067 (card 362): the Git dialog's Tidy… against the contract DeveloperMarlo builds in core (git.tidyPlan,
  // git.branchRealign; delete through git.deleteBranches, plan then token). DeveloperAda runs, so realigning her
  // branch is refused; TesterBen's goes through.
  'git.tidyPlan': () => tidyPlan(),
  'git.deleteBranches': (p) => {
    const names: string[] = p?.branches ?? [];
    if (!p?.confirm) return { plan: names.map((n) => `Delete ${n}${p?.remote ? ' here and on origin' : ' here'} (merged into main).`).join('\n'), token: `tok:${names.join(',')}`, empty: names.length === 0 };
    for (const n of names) { tidy.deleted.add(n); b073.deleted.add(n); }
    return { result: `Deleted ${names.join(', ')}.` };
  },
  // B073 (card 465): the Git dialog's counts and previews, as the contract in YAWBLE_TEAM_DOCS\B073\contract.md proposes.
  'git.commitRange': (p) => commitRange(String(p?.from ?? ''), String(p?.to ?? '')),
  'git.mergePreview': () => mergePreview(),
  'git.mergeDiff': (p) => commitDiff(COMMIT_ADA, String(p?.path ?? '')),
  'git.deletePreview': (p) => deletePreview(String(p?.branch ?? '')),
  'git.merge': (p) => {
    b073.merged = true;
    return { outcome: 'merge', message: 'Merged team/payments into main and pushed main to origin.', changed: true, sha: COMMIT_MERGE, files: null, status: b073Status(),
      commits: [R.merge(), R.ada()], commitCount: 2, commitsTruncated: false, range: pair(COMMIT_BASE, COMMIT_MERGE) };
  },
  'git.bringCurrent': () => {
    b073.broughtCurrent = true;
    return { outcome: 'fast-forward', message: 'main fast-forwarded to origin/main.', changed: true, sha: B073_ORIGIN_SHA, files: null, status: b073Status(),
      commits: [R.feature()], commitCount: 1, commitsTruncated: false, range: pair(COMMIT_BASE, B073_ORIGIN_SHA) };
  },
  'git.sync': () => {
    b073.synced = true;
    return { outcome: 'synced', baseBranch: 'main', baseSha: COMMIT_BASE, behind: 0, moved: ['team/payments'], conflicts: [], note: null,
      incoming: [R.base()], incomingCount: 1, incomingTruncated: false, incomingRange: pair(COMMIT_MERGE, COMMIT_BASE) };
  },
  'git.branchRealign': (p) => {
    if (p?.member === 'DeveloperAda') throw Object.assign(new Error('DeveloperAda is running: realign it when the run ends.'), { code: 'running' });
    // As the core (TeamRepos.Tidy.cs): the yes is bound to the commits shown; a branch that moved since is refused.
    if ((p?.expectOld != null && p.expectOld !== TIDY_TEAM_SHA) || (p?.expectNew != null && p.expectNew !== TIDY_CLONE_SHA))
      throw Object.assign(new Error(`${p?.member}'s branch changed since it was shown (now: team 5bbb6ba450, clone e0f2a18c0f). Nothing changed; look at the new plan.`), { code: 'stale' });
    tidy.realigned.add(p?.member);
    return { oldSha: TIDY_TEAM_SHA, newSha: TIDY_CLONE_SHA };
  },
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
  // B073: with setB073(true), team/payments is 1 behind main until Sync now or Merge, with the contract's from / to.
  'teams.syncToBase': () => (b073.on ? { on: true, note: null, baseBranch: 'main',
    behind: b073.synced || b073.merged ? null : { teamBranch: 'team/payments', count: 1, text: '1 commit behind main', from: COMMIT_MERGE, to: COMMIT_BASE } }
    : syncBehind ? { on: true, note: null, baseBranch: 'main', behind: { teamBranch: 'team/payments', count: 3, text: '3 commits behind main' } } : { on: true, note: null, baseBranch: 'main', behind: null }),
  'tasks.list': () => [],
  'members.get': (p) => (agents[1] as any[]).map((a) => a.member).find((m) => m.id === p?.id) ?? (agents[1] as any)[0].member,
};

/** B067: what Tidy… deleted and realigned (resetTidy puts the plan back). */
const tidy = { deleted: new Set<string>(), realigned: new Set<string>() };
const TIDY_TEAM_SHA = '5bbb6ba450df8fe26f82';
const TIDY_CLONE_SHA = 'e0f2a18c0ffee1234567';
/** Shaped as the core's UiRpc.TidyPlanJson: repo, baseBranch, branches (commits on moved, own-work and origin-only rows;
 * proof where the core gives one), and notes when the core could not read something. */
function tidyPlan() {
  const moved = (name: string, member: string) => (tidy.realigned.has(member)
    ? { name, where: ['local', 'origin', `clone:${member}`], class: 'working', member, teamSha: TIDY_CLONE_SHA, cloneSha: TIDY_CLONE_SHA }
    : { name, where: ['local', 'origin', `clone:${member}`], class: 'moved', member, teamSha: TIDY_TEAM_SHA, cloneSha: TIDY_CLONE_SHA,
        commits: [{ sha: 'aa11bb22cc33dd44ee55', subject: 'Retry the upload three times' }],
        proof: `the team's copy (5bbb6ba450) has 1 commit(s) ${member}'s clone (e0f2a18c0f) does not have and that are not merged into main: the clone was reset or rebased; nothing is lost by realigning it (git cherry main: - aa11bb22cc)` });
  const main = [
    { name: 'members/payments/developerada-and-a-very-long-suffix-that-must-wrap-on-a-phone', where: ['local', 'clone:DeveloperAda'], class: 'working', member: 'DeveloperAda' },
    { name: 'members/payments/old-cleo', where: ['local', 'origin'], class: 'merged' },
    { name: 'members/payments/kofi', where: ['local', 'origin'], class: 'contained', proof: 'git cherry main members/payments/kofi: - 1a2b3c4d Fix the refund rounding (an equivalent commit is in main: 9f8e7d6c)' },
    moved('members/payments/testerben', 'TesterBen'),
    moved('members/payments/developerada-moved', 'DeveloperAda'),
    { name: 'feature/refund-limits', where: ['local'], class: 'own-work', commits: [{ sha: '77aa88bb99cc', subject: 'Refund limits per merchant (half done)' }] },
  ];
  const cloud = [
    { name: 'members/payments/old-wren', where: ['origin'], class: 'merged' },
    { name: 'feature/old-idea', where: ['origin'], class: 'origin-only', commits: [{ sha: '4444444444dddddddddd', subject: 'Try a new cache' }], proof: 'only on origin, nobody has it here; 1 commit(s) not in main: needs a look' },
    { name: 'scratch/try-a-thing', where: ['clone:DeveloperAda'], class: 'scratch', member: 'DeveloperAda' },
  ];
  const keep = (b: { name: string }) => !tidy.deleted.has(b.name);
  return { repos: [{ repo: 'payments', baseBranch: 'main', branches: main.filter(keep) },
    { repo: 'payments-cloud', baseBranch: 'main', branches: cloud.filter(keep), notes: ["TesterBen's clone was not read: its .git is a file (a gitdir pointer)"] }] };
}

/** B072: the commits the fake repository has (any prefix of 7+ finds one), as the core's git.commitShow answers them. */
const COMMIT_ADA = '3f9c2a1b7d4e5f60718293a4b5c6d7e8f9012345';
const COMMIT_MERGE = 'def5678a9b0c1d2e3f405162738495a6b7c8d9e0';
const COMMIT_BASE = 'abc1234f5e6d7c8b9a0f1e2d3c4b5a6978695a4b';
const COMMIT_GONE = '0dead00b1';
/** Card 459: a commit only on the person's own feature/x branch (not a team or member branch, not in main). */
const COMMIT_FEATURE = 'fea7c0de1a2b3c4d5e6f708192a3b4c5d6e7f809';
const commitState = { codeShown: true };
const person = (name: string, mins: number) => ({ name, email: `${name.toLowerCase()}@yawble.local`, date: iso(mins) });
function commitShow(sha: string) {
  // As the core: a malformed ID is bad_params; a commit git no longer has is only {found, goneReason, sha}.
  if (!/^[0-9a-f]{4,64}$/i.test(sha)) throw Object.assign(new Error('sha must be 4 to 64 hex digits'), { code: 'bad_params' });
  const s = sha.toLowerCase();
  const pick = [COMMIT_ADA, COMMIT_MERGE, COMMIT_BASE, COMMIT_FEATURE].find((c) => c.startsWith(s));
  if (!pick) return { found: false, goneReason: `Commit ${sha} was on members/payments/old-cleo (Old Cleo's branch), and git no longer has it: that branch was deleted and git has since cleaned up its commits.`, sha };
  const common = { found: true, sha: pick, shortSha: pick.slice(0, 7), codeShown: commitState.codeShown };
  if (pick === COMMIT_ADA) return { ...common, title: 'Partial refunds: refund part of a payment', body: 'The refund takes an amount now.\n\nCard 3, workflow #10.',
    author: person('DeveloperAda', 40), isMerge: false, parents: [COMMIT_BASE], made: { member: 'DeveloperAda', card: 3, workflow: 10 },
    where: { inMain: false, branches: ['team/payments', 'members/payments/developerada'], otherBranches: [], pushed: false, tags: [] },
    files: [
      { path: 'src/payments/refunds.ts', oldPath: 'src/payments/refund.ts', status: 'renamed', added: 4, deleted: 2, binary: false },
      { path: 'src/payments/a-rather-long-folder-name/and-a-long-file-name-that-must-wrap-on-a-phone.test.ts', status: 'added', added: 12, deleted: 0, binary: false },
      { path: 'docs/refund-flow.png', status: 'modified', added: 0, deleted: 0, binary: true },
    ] };
  if (pick === COMMIT_MERGE) return { ...common, title: "Merge DeveloperAda's branch into team/payments", body: '', author: person('Manager', 30), committer: person('Yawble', 30),
    isMerge: true, parents: [COMMIT_BASE, COMMIT_ADA], merge: { intoBranch: 'team/payments', fromBranch: 'members/payments/developerada', fromMember: 'DeveloperAda', commitCount: 2 },
    made: { member: 'Manager', workflow: 10 }, where: { inMain: false, branches: ['team/payments'], otherBranches: [], pushed: false, tags: [] },
    files: [{ path: 'src/payments/refunds.ts', oldPath: 'src/payments/refund.ts', status: 'renamed', added: 4, deleted: 2, binary: false }] };
  if (pick === COMMIT_FEATURE) return { ...common, title: 'Try a new receipt layout', body: '', author: person('Owner', 20), isMerge: false, parents: [COMMIT_BASE],
    where: { inMain: false, branches: [], otherBranches: ['feature/x'], pushed: false, tags: [], ...(b073.deleted.has(B073_BO) ? { deletedFrom: B073_BO } : {}) },
    files: [{ path: 'src/payments/receipt.ts', status: 'modified', added: 3, deleted: 1, binary: false }] };
  return { ...common, title: 'Release 2026.11.18.1', body: '', author: person('Owner', 600), isMerge: false, parents: [],
    where: { inMain: true, branches: ['team/payments'], otherBranches: [], pushed: true, tags: ['v2026.11.18.1'] },
    files: [{ path: 'README.md', status: 'modified', added: 1, deleted: 1, binary: false }] };
}
function commitsResolve(id: string) {
  const c = commitShow(id) as { found: boolean; sha?: string; title?: string; where?: { pushed: boolean } };
  return c.found ? [{ team: 'payments', teamId: 1, repo: null, fullId: c.sha!, shortId: c.sha!.slice(0, 12), title: c.title ?? '', pushed: !!c.where?.pushed }] : [];
}
function commitDiff(sha: string, path: string) {
  const c = commitShow(sha) as { found: boolean; codeShown?: boolean; goneReason?: string; files: { path: string; oldPath?: string; status: string; added: number; deleted: number; binary: boolean }[] };
  if (!c.found) throw Object.assign(new Error(c.goneReason!), { code: 'gone' });
  if (!c.codeShown) throw Object.assign(new Error("Members' code is not shown to cloud viewers."), { code: 'remote_refused' });
  const f = c.files.find((x) => x.path === path);
  if (!f) throw Object.assign(new Error(`commit ${sha.slice(0, 12)} does not change ${path}`), { code: 'no_such_file' });
  const head = { path, ...(f.oldPath ? { oldPath: f.oldPath } : {}), status: f.status, added: f.added, deleted: f.deleted };
  if (f.binary) return { ...head, binary: true, tooLarge: false, summary: 'Binary file, 12 KB to 14 KB', hunks: [] };
  const lines = [
    { kind: 'context', old: 1, new: 1, text: 'export function refund(amount: number, limitDays = 30): boolean {' },
    { kind: 'deleted', old: 2, text: '  return amount > 0;' },
    { kind: 'added', new: 2, text: '  if (amount <= 0) return false; // a long comment that runs past the edge of a phone screen, to scroll inside the diff only' },
    { kind: 'added', new: 3, text: '  return limitDays <= 90;' },
    { kind: 'context', old: 3, new: 4, text: '}' },
  ];
  return { ...head, binary: false, tooLarge: false, hunks: [{ header: '@@ -1,3 +1,4 @@ export function refund', oldStart: 1, oldCount: 3, newStart: 1, newCount: 4, lines }] };
}

/** B073 (card 465): with setB073(true), git.status is a repository with an origin, shaped as DeveloperTomas's contract
 *  (YAWBLE_TEAM_DOCS\B073\contract.md): main 1 behind origin's, team/payments 2 ahead and 1 behind main (a merge), not
 *  pushed; DeveloperAda's branch pushed, DeveloperBo's not and not in the team branch (one commit of its own, one already
 *  in main). Each count carries its full-ID pair; Merge, Bring main current and Sync now move it on and list what came in. */
const b073 = { on: false, merged: false, broughtCurrent: false, synced: false, deleted: new Set<string>() };
const B073_BO = 'members/payments/developerbo';
const B073_ORIGIN_SHA = '0f1e2d3c4b5a69788796a5b4c3d2e1f0a9b8c7d6';
const B073_SYNCED_SHA = '5ce0d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f7a8b';
const commitRow = (sha: string, subject: string, author: string, notPushed: boolean, isMerge = false) =>
  ({ sha, shortSha: sha.slice(0, 12), subject, author: { name: author, email: `${author.toLowerCase()}@yawble.local` }, date: iso(30), isMerge, notPushed });
const R = {
  merge: () => commitRow(COMMIT_MERGE, "Merge DeveloperAda's branch into team/payments", 'Manager', true, true),
  ada: () => commitRow(COMMIT_ADA, 'Partial refunds: refund part of a payment', 'DeveloperAda', false),
  base: () => commitRow(COMMIT_BASE, 'Release 2026.11.18.1', 'Owner', false),
  feature: () => commitRow(COMMIT_FEATURE, 'Try a new receipt layout', 'DeveloperBo', true),
};
const pair = (from: string, to: string) => ({ from, to });
/** The commits behind each pair the fake hands out (git rev-list to ^from). */
const B073_RANGES: Record<string, () => ReturnType<typeof commitRow>[]> = {
  [`${COMMIT_BASE}..${COMMIT_MERGE}`]: () => [R.merge(), R.ada()],
  [`${COMMIT_MERGE}..${COMMIT_BASE}`]: () => [R.base()],
  [`${COMMIT_BASE}..${COMMIT_ADA}`]: () => [R.ada()],
  [`${COMMIT_BASE}..${COMMIT_FEATURE}`]: () => [R.feature(), R.ada()],
  [`${COMMIT_BASE}..${B073_ORIGIN_SHA}`]: () => [R.feature()],
  [`${COMMIT_MERGE}..${B073_SYNCED_SHA}`]: () => [R.base()],
};
function b073Status() {
  const members = [
    { branch: 'members/payments/developerada', sha: COMMIT_ADA, aheadOfBase: 1, inTeamBranch: true, aheadRange: pair(COMMIT_BASE, COMMIT_ADA),
      push: { branch: 'members/payments/developerada', onOrigin: true, notPushed: 0 } },
    { branch: B073_BO, sha: COMMIT_FEATURE, aheadOfBase: 2, inTeamBranch: false, aheadRange: pair(COMMIT_BASE, COMMIT_FEATURE),
      push: { branch: B073_BO, onOrigin: false, notPushed: 1 } },
  ].filter((m) => !b073.deleted.has(m.branch));
  const behindBase = b073.synced || b073.merged ? 0 : 1;
  return {
    repoPath: 'C:/repos/payments', managed: true, baseBranch: 'main', checkedOut: 'main', dirty: false, origin: 'https://example.invalid/payments.git',
    baseSha: b073.broughtCurrent ? B073_ORIGIN_SHA : COMMIT_BASE, originBaseSha: B073_ORIGIN_SHA, baseAheadOfOrigin: 0, baseBehindOrigin: b073.broughtCurrent ? 0 : 1,
    teamBranch: 'team/payments', teamSha: b073.synced ? B073_SYNCED_SHA : COMMIT_MERGE,
    teamAheadOfBase: b073.merged ? 0 : 2, teamBehindBase: behindBase,
    merge: b073.merged ? 'up-to-date' : b073.synced ? 'fast-forward' : 'merge', conflictFiles: [], members, oldBranches: [], blockingBranches: [],
    ranges: {
      baseAheadOfOrigin: null, baseBehindOrigin: b073.broughtCurrent ? null : pair(COMMIT_BASE, B073_ORIGIN_SHA),
      teamAheadOfBase: b073.merged ? null : pair(COMMIT_BASE, COMMIT_MERGE), teamBehindBase: behindBase ? pair(COMMIT_MERGE, COMMIT_BASE) : null,
    },
  };
}
function commitRange(from: string, to: string) {
  if (!/^[0-9a-f]{4,64}$/i.test(from) && !/^refs\/(heads|remotes\/origin)\//.test(from)) throw Object.assign(new Error(`bad ref ${from}`), { code: 'bad_params' });
  const list = B073_RANGES[`${from}..${to}`]?.() ?? [];
  return { from, to, fromSha: from, toSha: to, count: list.length, truncated: false, hasOrigin: true, commits: list };
}
function mergePreview() {
  const commits = [R.merge(), R.ada()];
  return {
    baseBranch: 'main', teamBranch: 'team/payments', outcome: 'merge', summary: 'A merge commit will be made: main has 1 commit team/payments lacks.',
    text: 'A merge commit will be made: main has 1 commit team/payments lacks.\n2 commits land.\n1 file changes.',
    from: COMMIT_BASE, to: COMMIT_MERGE, newSha: null, conflictFiles: [], commits, count: commits.length, truncated: false,
    files: [{ path: 'src/payments/refunds.ts', oldPath: 'src/payments/refund.ts', status: 'renamed', added: 4, deleted: 2, binary: false }],
    diffFrom: COMMIT_BASE, diffTo: COMMIT_MERGE, hasOrigin: true, teamPush: { branch: 'team/payments', onOrigin: true, notPushed: 1 },
  };
}
function deletePreview(branch: string) {
  const push = { branch, onOrigin: branch !== B073_BO, notPushed: branch === B073_BO ? 1 : 0 };
  if (branch === B073_BO) return { branch, baseBranch: 'main', exists: true, sha: COMMIT_FEATURE, merged: false, own: 1, inBase: 1, truncated: false,
    commits: [{ ...R.feature(), state: 'own' }, { ...R.ada(), state: 'in-base' }],
    lost: `Deleting it loses 1 commit of its own (${COMMIT_FEATURE.slice(0, 12)}); it is not on origin either.`, text: '', hasOrigin: true, push };
  return { branch, baseBranch: 'main', exists: true, sha: COMMIT_ADA, merged: false, own: 0, inBase: 1, truncated: false, commits: [{ ...R.ada(), state: 'in-base' }],
    lost: 'Nothing is lost: each of its 1 commits not in main already has the same change there.', text: '', hasOrigin: true, push };
}
/** B054: with true, teams.syncToBase says the team branch is behind (the team page shows its sync bar). */
let syncBehind = false;
/** B054: with false, no integration ask and no Manager decision is pending (the team page shows its members under Tell). */
let needsYou = true;

/** Methods a viewer with "read" access may not call (the fake refuses them, like the Cloud Link). Card 305: the workflows
 *  dialog's bus.nudgeWorkflow and bus.closeWorkflow too (bus.stopWorkflow already matched "stop"), as core's
 *  RemoteAccess lists them. */
const isChange = (m: string) => /\.(send|answer|save|delete|remove|create|add|update|install|run|stop|move|rename|tellManager|approve|decline|nudgeWorkflow|closeWorkflow|workflowExtend)/.test(m);

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
    /** B085: the expiring and expired workflows listed (off: only #10), Extend forgotten, workflowExpiryDays back to 3. */
    setExpiry(on: boolean) { expiry.on = on; expiry.extended.clear(); expiry.days.clear(); },
    /** B067: put the Tidy… plan back (nothing deleted or realigned). */
    resetTidy() { tidy.deleted.clear(); tidy.realigned.clear(); },
    /** B072: with false, git.commitShow says codeShown=false (Show members' code to cloud viewers off). */
    setCodeShown(on: boolean) { commitState.codeShown = on; },
    /** B073: the Git dialog's repository with an origin, counts and previews (off: the plain status); resets what moved. */
    setB073(on: boolean) { b073.on = on; b073.merged = false; b073.broughtCurrent = false; b073.synced = false; b073.deleted.clear(); },
    answer(text: string): string | null {
      const msg = JSON.parse(text) as { id: number; method: string; params: any };
      calls.push(msg.method);
      if (access === 'read' && isChange(msg.method)) return JSON.stringify({ id: msg.id, error: { code: 'remote_refused', message: 'Read-only (Visitor): refused by the fake' } });
      if (!asks && (msg.method === 'network.asks' || msg.method === 'tools.asks')) return JSON.stringify({ id: msg.id, result: [] });
      const a = answers[msg.method];
      if (!a) { unknown.add(msg.method); return JSON.stringify({ id: msg.id, error: { code: 'method_not_found', message: `fake core: ${msg.method} is not faked` } }); }
      try { return JSON.stringify({ id: msg.id, result: a(msg.params, access) ?? null }); }
      catch (e) {
        const code = (e as { code?: string })?.code;
        return JSON.stringify({ id: msg.id, error: code ? { code, message: (e as Error).message } : { code: 'fake_error', message: String(e) } });
      }
    },
  };
}
