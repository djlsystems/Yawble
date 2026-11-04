// @vitest-environment happy-dom
//
// THE TEAM PICTURE PAGE, drawn from its site data: the template's own draw.js and page.js, run in a
// DOM against a fake helper script (`site.data.list`), the only thing the page calls.
//
// Pinned here: before anything is drawn the live parts show with one line saying when the picture
// is drawn; one tab per open item with the work in flight first; a build item's progress bar,
// architecture (status from the item's cards), screens, stand-in cards and flows; in-flight work
// glows with the path into it animated and its member, how long and last progress named, while
// finished work is still; an operational team's figures, latest items, last message and run strip;
// "not measured", never 0; agent-written text drawn as text; the Concierge's hidden sections,
// colours and extra sections; the bounded history; and a re-read every few seconds.
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';

// happy-dom's own URL stands in for node's here, so the folder is resolved as a path.
const folder = resolve(dirname(fileURLToPath(import.meta.url)), '../../../templates/team-picture') + '/';
const source = (name: string) => readFileSync(folder + name, 'utf8');

type Row = { id: string; doc: unknown; updatedAt?: string; updatedBy?: string };
type Data = Record<string, Row[]>;

interface TeamPictureApi {
  refresh(site: unknown): Promise<void>;
  start(site: unknown, interval?: number): void;
  stop(): void;
  reset(): void;
  INTERVAL_MS: number;
  NOT_DRAWN: string;
}

const api = () => (window as unknown as { TeamPicture: TeamPictureApi }).TeamPicture;

beforeAll(() => {
  // The template's scripts are plain scripts, as the page loads them: no module, no build step.
  new Function(source('draw.js'))();
  new Function(source('page.js'))();
});

const NOW = '2026-11-04T10:40:00Z';
const ago = (minutes: number) => new Date(Date.parse(NOW) - minutes * 60000).toISOString();

function fakeSite(data: Data) {
  const calls: string[] = [];
  return {
    calls,
    data: {
      list: (name: string) => {
        calls.push(name);
        if (name === 'broken') return Promise.reject(new Error('nope'));
        return Promise.resolve(data[name] ?? []);
      },
    },
  };
}

function team(): Row {
  return { id: 'team', doc: { team: 'Core', label: 'Core Engineering', generatedAt: NOW } };
}

function item(key: string, startedAt: string, cards: unknown[], progress: Record<string, number>): Row {
  return { id: key, doc: { key, kind: 'backlog', title: `Item ${key}`, startedAt, progress, cards } };
}

function nowRow(member: string, itemKey: string | null, cardId: string | null, title: string, startedMinutesAgo: number): Row {
  return {
    id: member,
    doc: {
      member,
      kind: 'agent',
      item: itemKey,
      card: cardId ? { id: cardId, title } : null,
      startedAt: ago(startedMinutesAgo),
      lastProgress: { text: 'Running the Sites tests', at: ago(2) },
    },
  };
}

const architecture = {
  caption: 'Focused on what item-report adds or changes',
  backdrop: 'The Host',
  nodes: [
    { id: 'ledger', label: 'Runs and usage ledger', status: 'unchanged', column: 0, row: 0 },
    { id: 'cost', label: 'Cost service', detail: ['Estimated Execution Cost'], column: 1, row: 0, cards: ['c1'] },
    { id: 'report', label: 'Report tool for agents', column: 2, row: 0, cards: ['c2'] },
    { id: 'csv', label: 'CSV exports', column: 2, row: 1, cards: ['c3'] },
  ],
  edges: [
    { from: 'ledger', to: 'cost' },
    { from: 'cost', to: 'report' },
    { from: 'cost', to: 'csv' },
    { from: 'cost', to: 'nowhere' },
  ],
};

const buildPicture = {
  kind: 'build',
  title: 'Cost and spend',
  architecture,
  screens: [
    {
      title: 'Spend page',
      change: 'new',
      cards: ['c1'],
      regions: [
        { type: 'figures', items: [{ label: 'Actual Spend', value: '$1,240.00', note: '3 accounts' }] },
        { type: 'table', columns: ['Account', 'Total'], rows: [['Anthropic API', '$900.00']] },
        { type: 'buttons', items: ['Export CSV'] },
      ],
    },
  ],
  work: [{ title: 'report tool for agents', change: 'new', cards: ['c2'], regions: [{ type: 'terminal', lines: ['> report action: spend'] }] }],
  flows: [
    {
      title: 'How one run is priced',
      steps: [
        { id: 'start', label: 'A run ends', shape: 'start' },
        { id: 'plugin', label: 'Plugin member?', shape: 'decision', status: 'done' },
        { id: 'rate', label: 'duration x rate', status: 'done' },
      ],
      links: [{ from: 'start', to: 'plugin' }, { from: 'plugin', to: 'rate', label: 'yes' }],
    },
  ],
};

const cards = [
  { id: 'c1', title: 'Cost service', lane: 'done', status: 'done' },
  { id: 'c2', title: 'Report tool', lane: 'doing', status: 'running' },
  { id: 'c3', title: 'CSV', lane: 'todo', status: 'planned' },
];

function twoItems(): Data {
  return {
    'tp-team': [team()],
    'tp-items': [
      item('item-export', ago(10), [], { total: 2, done: 0, running: 0, todo: 2, blocked: 0, failed: 0 }),
      item('item-report', ago(600), cards, { total: 3, done: 1, running: 1, todo: 1, blocked: 0, failed: 0 }),
    ],
    'tp-now': [nowRow('DeveloperQuinn', 'item-report', 'c2', 'Report tool', 34)],
    'tp-picture': [{ id: 'item-report', doc: buildPicture }],
    'tp-spend': [
      {
        id: 'team',
        doc: {
          scope: 'team',
          words: "Tokens the team's runs reported, input and output together.",
          today: { measured: true, tokens: 52000, input: 40000, output: 12000, runsMeasured: 14, runsNotMeasured: 0 },
          last7Days: { measured: false, tokens: null, input: null, output: null, runsMeasured: 0, runsNotMeasured: 3 },
          estimatedExecutionCost: { available: false, text: 'Estimated Execution Cost is not available on this instance yet.' },
        },
      },
    ],
  };
}

async function draw(data: Data) {
  const site = fakeSite(data);
  await api().refresh(site);
  return site;
}

const $ = (selector: string) => document.querySelector(selector);
const $$ = (selector: string) => [...document.querySelectorAll(selector)];
const text = (selector: string) => ($(selector)?.textContent ?? '').replace(/\s+/g, ' ').trim();

beforeEach(() => {
  api().reset();
  const page = source('index.html');
  document.body.innerHTML = /<body>([\s\S]*)<\/body>/.exec(page)![1]!;
});

afterEach(() => {
  api().stop();
  vi.useRealTimers();
});

describe('the Team picture page before anything is drawn', () => {
  it('shows the live parts only, with one line saying the picture is drawn when the team plans', async () => {
    await draw({ 'tp-team': [team()] });

    expect($('[data-section="now"]')).not.toBeNull();
    expect(text('[data-section="now"]')).toContain('Nobody is working right now.');
    expect($('[data-section="spend"]')).not.toBeNull();
    expect($$('[data-tab]')).toHaveLength(0);
    expect(text('[data-section="work"]')).toContain(api().NOT_DRAWN);
    expect($('.tp-graph')).toBeNull();
  });

  it('shows an open item with no picture yet: its progress, and the same one line', async () => {
    const data = twoItems();
    data['tp-picture'] = [];
    await draw(data);

    expect($('[data-panel="item-report"] [data-section="progress"]')).not.toBeNull();
    expect(text('[data-panel="item-report"]')).toContain(api().NOT_DRAWN);
    expect($('[data-panel="item-report"] .tp-graph')).toBeNull();
  });
});

describe('a build item', () => {
  it('has one tab per open item, the work in flight first', async () => {
    await draw(twoItems());

    expect($$('[data-tab]').map((tab) => tab.getAttribute('data-tab'))).toEqual(['item-report', 'item-export']);
    expect($('[data-tab="item-report"]')!.getAttribute('aria-selected')).toBe('true');
    expect($('[data-tab="item-report"] .tp-dot')).not.toBeNull();
    expect($('[data-panel="item-report"]')!.getAttribute('data-kind')).toBe('build');
  });

  it('leads with the work in flight: member, card, how long and last progress', async () => {
    await draw(twoItems());

    const first = $('#tp-main')!.firstElementChild!;
    expect(first.getAttribute('data-section')).toBe('now');
    const row = text('[data-now-member="DeveloperQuinn"]');
    expect(row).toContain('Report tool');
    expect(row).toContain('Developer Quinn, 34 min in, last progress 2 min ago: Running the Sites tests');
  });

  it('draws the progress bar with a striped, moving segment for what is in flight', async () => {
    await draw(twoItems());

    expect(text('[data-panel="item-report"] .tp-progress-words')).toContain('1 of 3 cards done.');
    const done = $('[data-panel="item-report"] .tp-bar-done')!;
    expect(done.getAttribute('width')).toBe('333');
    const segment = $('[data-panel="item-report"] [data-bar-now] .tp-bar-active')!;
    expect(segment.getAttribute('x')).toBe('333');
    expect(segment.getAttribute('width')).toBe('333');
    expect($('[data-panel="item-report"] .tp-stripes.tp-moving')).not.toBeNull();
  });

  it('colours the architecture from the cards, dashes what is unchanged, and skips a broken edge', async () => {
    await draw(twoItems());

    const status = (id: string) => $(`[data-section="architecture"] [data-node="${id}"]`)!.getAttribute('data-status');
    expect(status('ledger')).toBe('unchanged');
    expect(status('cost')).toBe('done');
    expect(status('report')).toBe('active');
    expect(status('csv')).toBe('todo');
    expect($('[data-node="ledger"] .tp-s-unchanged')).not.toBeNull();
    expect($$('[data-section="architecture"] .tp-edge')).toHaveLength(3);
    expect(text('[data-section="architecture"] .tp-frame-label')).toBe('The Host');
  });

  it('makes the in-flight piece glow, animates only the path into it, and names who and how long', async () => {
    await draw(twoItems());

    const live = $$('[data-section="architecture"] .tp-now');
    expect(live).toHaveLength(1);
    expect(live[0]!.closest('[data-node]')!.getAttribute('data-node')).toBe('report');
    const moving = $$('[data-section="architecture"] .tp-live');
    expect(moving).toHaveLength(1);
    expect(text('[data-section="architecture"] .tp-run-note')).toContain('Developer Quinn, 34 min in');
  });

  it('keeps finished work still: nothing glows or moves when nothing is in flight', async () => {
    const data = twoItems();
    data['tp-now'] = [];
    await draw(data);
    ($('[data-tab="item-report"]') as HTMLButtonElement).click();

    expect($('[data-panel="item-report"] [data-bar-now]')).not.toBeNull();
    expect($$('.tp-now, .tp-live, .tp-moving')).toHaveLength(0);
  });

  it('draws each screen with its status, and a stand-in card for work with no screen', async () => {
    await draw(twoItems());

    const screens = $$('[data-section="screens"] .tp-mockup');
    expect(screens).toHaveLength(2);
    expect(screens[0]!.getAttribute('data-screen-status')).toBe('done');
    expect(screens[0]!.textContent).toContain('Spend page (new)');
    expect(screens[0]!.querySelector('td')!.textContent).toBe('Anthropic API');
    expect(screens[0]!.querySelector('.tp-m-button')!.textContent).toBe('Export CSV');
    expect(screens[1]!.getAttribute('data-standin')).toBe('true');
    expect(screens[1]!.getAttribute('data-screen-status')).toBe('active');
    expect(screens[1]!.classList.contains('tp-now')).toBe(true);
    expect(screens[1]!.textContent).toContain('(new, no screen)');
  });

  it('draws the flow chart: a decision, its labelled links', async () => {
    await draw(twoItems());

    expect(text('[data-section="flows"] .tp-mockup-header')).toBe('How one run is priced');
    expect($('[data-section="flows"] polygon.tp-shape-decision')).not.toBeNull();
    expect(text('[data-section="flows"] .tp-edge-label')).toBe('yes');
  });

  it('switches tabs on a click', async () => {
    await draw(twoItems());
    ($('[data-tab="item-export"]') as HTMLButtonElement).click();

    expect($('[data-panel="item-export"]')).not.toBeNull();
    expect($('[data-tab="item-export"]')!.getAttribute('aria-selected')).toBe('true');
  });
});

describe('figures the platform did not measure', () => {
  it('read "not measured", never 0', async () => {
    await draw(twoItems());

    expect(text('[data-spend="Today"]')).toBe('52,000 tokens (40,000 input, 12,000 output)');
    expect(text('[data-spend="Last 7 days"]')).toBe('not measured');
    expect(text('[data-section="spend"]')).toContain('Estimated Execution Cost is not available on this instance yet.');
  });

  it('says not measured for spend when the platform has written none', async () => {
    const data = twoItems();
    data['tp-spend'] = [];
    await draw(data);

    expect(text('[data-section="spend"]')).toContain('not measured');
    expect(text('[data-section="spend"]')).not.toMatch(/\b0 tokens/);
  });
});

describe('agent-written words', () => {
  it('are drawn as text, never as markup', async () => {
    const data = twoItems();
    const hostile = '<img src=x onerror="alert(1)">';
    data['tp-picture'] = [{
      id: 'item-report',
      doc: {
        ...buildPicture,
        summary: hostile,
        architecture: { nodes: [{ id: 'a', label: hostile }] },
        screens: [{ title: hostile, regions: [{ type: 'text', text: hostile }] }],
      },
    }];
    await draw(data);

    expect($('img')).toBeNull();
    expect(text('.tp-summary')).toBe(hostile);
    expect(text('[data-section="screens"] .tp-m-text')).toBe(hostile);
  });
});

function operational(): Data {
  return {
    'tp-team': [team()],
    'tp-now': [{
      id: 'Triager',
      doc: { member: 'Triager', kind: 'agent', item: 'ongoing', card: null, startedAt: ago(1), lastProgress: { text: 'Triaging 3 emails', at: ago(0.5) } },
    }],
    'tp-runs': [
      { id: 'r2', doc: { id: 'r2', member: 'Triager', kind: 'agent', item: 'ongoing', startedAt: ago(1), endedAt: null, outcome: 'running', tokens: { measured: false, input: null, output: null } } },
      { id: 'r1', doc: { id: 'r1', member: 'Triager', kind: 'agent', item: 'ongoing', startedAt: ago(6), endedAt: ago(5), outcome: 'completed', tokens: { measured: true, input: 1200, output: 300 } } },
    ],
    'tp-picture': [{
      id: 'ongoing',
      doc: {
        kind: 'operational',
        title: 'Support desk',
        kinds: [{ id: 'alert', label: 'Alerts and errors' }, { id: 'support', label: 'Support' }],
        howItWorks: {
          nodes: [
            { id: 'mailbox', label: 'support@ mailbox', status: 'unchanged' },
            { id: 'triager', label: 'Triager (agent)', member: 'Triager', status: 'done' },
            { id: 'alerts', label: 'Alerts log', kind: 'alert', status: 'done' },
          ],
          edges: [{ from: 'mailbox', to: 'triager' }, { from: 'triager', to: 'alerts' }],
        },
        rule: {
          steps: [{ id: 's', label: 'Run starts', shape: 'start' }, { id: 'q', label: 'Any unread?', shape: 'decision' }],
          links: [{ from: 's', to: 'q' }],
        },
      },
    }],
    'tp-ops': [{
      id: 'today',
      doc: {
        date: '2026-11-04',
        counts: { support: 12, inquiry: 6, alert: 3, spam: 26 },
        lastMessage: { to: 'Administrator', sentAt: ago(48), text: 'Support Desk: 2 need you.\nALERT: Disk 92% on db-2' },
        runs: [{ at: ago(20), kind: null }, { at: ago(15), kind: 'support' }, { at: ago(10), kind: 'alert' }, { at: ago(5), kind: 'spam' }],
        items: [
          { at: ago(48), kind: 'alert', title: 'Disk 92% on db-2' },
          { at: ago(1), kind: null, title: 'Checkout returns 500' },
        ],
      },
    }],
  };
}

describe('an operational team', () => {
  it('has its single ongoing picture: today\'s figures per kind, most urgent first', async () => {
    await draw(operational());

    expect($$('[data-tab]').map((tab) => tab.getAttribute('data-tab'))).toEqual(['ongoing']);
    expect($('[data-panel="ongoing"]')!.getAttribute('data-kind')).toBe('operational');
    const kinds = $$('[data-section="figures"] [data-kind]').map((kpi) => kpi.getAttribute('data-kind'));
    expect(kinds).toEqual(['alert', 'support', 'inquiry', 'spam']);
    expect(text('[data-kind="alert"]')).toContain('Alerts and errors');
    expect(text('[data-kind="alert"] .tp-kpi-n')).toBe('3');
    expect(text('[data-section="figures"] .tp-kpi .tp-kpi-n')).toBe('47');
  });

  it('reads a kind with no count as not measured, never 0', async () => {
    const data = operational();
    (data['tp-ops']![0]!.doc as { counts: Record<string, number> }).counts = { support: 12 };
    await draw(data);

    expect(text('[data-kind="alert"] .tp-kpi-n')).toBe('not measured');
    expect(text('[data-kind="support"] .tp-kpi-n')).toBe('12');
  });

  it('shows where the run in progress is: the member\'s piece glows and the path into it moves', async () => {
    await draw(operational());

    const live = $$('[data-section="howItWorks"] .tp-now');
    expect(live).toHaveLength(1);
    expect(live[0]!.closest('[data-node]')!.getAttribute('data-node')).toBe('triager');
    expect($$('[data-section="howItWorks"] .tp-live')).toHaveLength(1);
    expect(text('[data-section="howItWorks"] .tp-mockup-header')).toContain('Triaging 3 emails');
    expect($('[data-node="alerts"] .tp-kc.tp-k-0')).not.toBeNull();
  });

  it('shows what it did: latest items, the last message as sent, and the run strip', async () => {
    await draw(operational());

    const rows = $$('[data-section="didToday"] .tp-row');
    expect(rows[0]!.classList.contains('tp-row-sorting')).toBe(true);
    expect(rows[0]!.textContent).toContain('Checkout returns 500');
    expect(rows[1]!.querySelector('.tp-tag')!.textContent).toBe('Alerts and errors');

    const bubble = $('.tp-bubble')!;
    expect(bubble.querySelectorAll('br')).toHaveLength(1);
    expect(bubble.textContent).toBe('Support Desk: 2 need you.ALERT: Disk 92% on db-2');

    const bars = $$('.tp-strip rect').map((bar) => bar.getAttribute('data-run-kind'));
    expect(bars).toEqual(['', 'support', 'alert', 'spam', 'running']);
    expect($('.tp-strip rect')!.getAttribute('class')).toBe('tp-strip-quiet');
    expect($$('.tp-strip rect')[2]!.getAttribute('class')).toBe('tp-kc tp-k-0');
    expect($$('.tp-strip rect')[4]!.getAttribute('class')).toContain('tp-strip-running');
  });

  it('draws the rule it follows as a flow chart', async () => {
    await draw(operational());
    expect($('[data-section="rule"] .tp-graph')).not.toBeNull();
    expect($('[data-section="rule"] polygon.tp-shape-decision')).not.toBeNull();
  });

  it('defaults to the operational view when only its own figures are written', async () => {
    const data = operational();
    data['tp-picture'] = [];
    await draw(data);

    expect($('[data-panel="ongoing"]')!.getAttribute('data-kind')).toBe('operational');
    expect(text('[data-panel="ongoing"]')).toContain(api().NOT_DRAWN);
  });
});

describe("the Concierge's layer (tp-settings)", () => {
  it('hides the sections it names', async () => {
    const data = twoItems();
    data['tp-settings'] = [{ id: 'page', doc: { hidden: ['spend', 'architecture'] } }];
    await draw(data);

    expect($('[data-section="spend"]')).toBeNull();
    expect($('[data-section="architecture"]')).toBeNull();
    expect($('[data-section="screens"]')).not.toBeNull();
  });

  it('applies its colours as a constructed stylesheet, never inline, and ignores anything not a colour', async () => {
    const data = operational();
    data['tp-settings'] = [{
      id: 'page',
      doc: { colours: { done: '#15803d', active: 'red; background: url(x)', 'kind:alert': '#b91c1c' } },
    }];
    await draw(data);

    const rules = document.adoptedStyleSheets.flatMap((sheet) => [...sheet.cssRules]).map((rule) => rule.cssText.replace(/\s+/g, ''));
    expect(rules.some((rule) => rule.includes('--tp-done:#15803d'))).toBe(true);
    expect(rules.some((rule) => rule.includes('.tp-k-0') && rule.includes('#b91c1c'))).toBe(true);
    expect(rules.join('')).not.toContain('url(');
    expect($$('[style]')).toHaveLength(0);
  });

  it('adds its extra sections after the section each names, from regions and a tp-extra collection', async () => {
    const data = twoItems();
    data['tp-settings'] = [{
      id: 'page',
      doc: {
        extraSections: [
          { id: 'contacts', title: 'Who to call', after: 'now', regions: [{ type: 'text', text: 'On-call: Dana' }] },
          { id: 'deploys', title: 'Deploys', collection: 'tp-extra-deploys' },
          { id: 'sneaky', title: 'Other', collection: 'tp-now' },
        ],
      },
    }];
    data['tp-extra-deploys'] = [{ id: 'd1', doc: { title: 'v42 shipped', text: 'to production' }, updatedAt: ago(3) }];
    const site = await draw(data);

    const order = [...$('#tp-main')!.children].map((section) => section.getAttribute('data-section'));
    expect(order.indexOf('contacts')).toBe(order.indexOf('now') + 1);
    expect(order.indexOf('deploys')).toBeLessThan(order.indexOf('history'));
    expect(text('[data-section="contacts"]')).toContain('On-call: Dana');
    expect(text('[data-section="deploys"]')).toContain('v42 shipped');
    expect(site.calls).toContain('tp-extra-deploys');
    expect(text('[data-section="sneaky"]')).toBe('Other');
  });
});

describe('the history of finished items', () => {
  it('is bounded, newest first, and draws a kept picture still when opened', async () => {
    const data = twoItems();
    data['tp-now'] = [];
    data['tp-history'] = Array.from({ length: 14 }, (_, index) => ({
      id: `done-${10 + index}`,
      doc: { key: `done-${10 + index}`, title: `Finished ${index}`, finishedAt: ago(1000 - index), progress: { total: 2, done: 2 }, picture: buildPicture },
    }));
    await draw(data);

    const items = $$('[data-history]');
    expect(items).toHaveLength(10);
    expect(items[0]!.getAttribute('data-history')).toBe('done-23');
    expect($('#tp-main')!.lastElementChild!.getAttribute('data-section')).toBe('history');

    const first = items[0] as HTMLDetailsElement;
    first.open = true;
    first.dispatchEvent(new Event('toggle'));
    expect(first.querySelector('.tp-graph')).not.toBeNull();
    expect(first.querySelectorAll('.tp-now, .tp-live, .tp-moving')).toHaveLength(0);
  });
});

describe('reading again', () => {
  it('re-reads the site data every few seconds and redraws what moved', async () => {
    vi.useFakeTimers();
    const data = twoItems();
    const site = fakeSite(data);
    api().start(site, 5000);
    await vi.advanceTimersByTimeAsync(0);
    const firstReads = site.calls.filter((name) => name === 'tp-now').length;
    expect(firstReads).toBe(1);
    expect(text('[data-section="now"]')).toContain('Developer Quinn');

    data['tp-now'] = [];
    await vi.advanceTimersByTimeAsync(5000);
    expect(site.calls.filter((name) => name === 'tp-now').length).toBe(2);
    expect(text('[data-section="now"]')).toContain('Nobody is working right now.');
    expect(api().INTERVAL_MS).toBeLessThanOrEqual(10000);
  });

  it('says so in one line when a read fails, and keeps what was read last', async () => {
    await draw(twoItems());
    const failing = { data: { list: () => Promise.reject(Object.assign(new Error('This page\'s access has expired'), { status: 401 })) } };
    await api().refresh(failing);

    expect(($('#tp-status') as HTMLElement).hidden).toBe(false);
    expect(text('#tp-status')).toContain("This page's access has expired");
    expect(text('#tp-status')).toContain('Showing what was read last.');
    expect($('[data-panel="item-report"]')).not.toBeNull();
  });
});
