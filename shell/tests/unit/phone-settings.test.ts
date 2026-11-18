// @vitest-environment happy-dom
// B054 (card 269): Settings on a phone (below 760 px wide), as measured on an iPhone SE: 375 x 501 in portrait and
// 667 x 263 in landscape. The back control stays in reach at the bottom of a long section, a picked skill opens as its
// own view, nothing inside a section scrolls on its own or sideways, and every text field, select and textarea is at
// least 16 px and 44 px tall (no zoom on focus). At 1280 px nothing changes.
//
// The real styles are in the page: styles.css and every component's <style> block, compiled with that component's own
// scope id (what the test build gives its elements), so getComputedStyle is the browser's cascade (media queries,
// !important, specificity). happy-dom does no layout: a size here is the computed CSS (min-height, font-size,
// overflow, position), not a measured box. The API is a scripted fake (no core, no network).
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { compileStyle, parse } from '@vue/compiler-sfc';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import SettingsView, { type SettingsTab } from '../../src/renderer/src/components/SettingsView.vue';
import BoardView from '../../src/renderer/src/board/BoardView.vue';
import SkillsDialog from '../../src/renderer/src/components/SkillsDialog.vue';
import { PHONE_BREAKPOINT, PHONE_QUERY } from '../../src/renderer/src/phone';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';
import type { Skill, YawbleApi } from '../../src/shared/api';

// ---- the page's styles: styles.css, then each SFC's style blocks under its scope id
const modules = import.meta.glob<{ default: { __scopeId?: string } }>('../../src/renderer/src/**/*.vue', { eager: true });
const scopeOf = new Map<string, string>(); // file name (BacklogView.vue) -> data-v-xxxx
beforeAll(() => {
  const css: string[] = [readFileSync(resolve(__dirname, '../../src/renderer/src/styles.css'), 'utf8')];
  for (const [key, mod] of Object.entries(modules)) {
    const file = resolve(__dirname, key);
    const id = mod.default.__scopeId;
    if (id) scopeOf.set(key.split('/').pop()!, id);
    const { descriptor } = parse(readFileSync(file, 'utf8'), { filename: file });
    for (const style of descriptor.styles) {
      const out = compileStyle({ source: style.content, filename: file, id: id ?? 'data-v-none', scoped: !!style.scoped && !!id });
      css.push(out.code);
    }
  }
  const el = document.createElement('style');
  el.textContent = css.join('\n');
  document.head.appendChild(el);
});

// ---- the screen: happy-dom's viewport (what the media queries see) and a matchMedia usePhone() reads
type Listener = (e: { matches: boolean }) => void;
const screen = { width: 1280, listeners: new Set<Listener>() };
function setScreen(width: number, height: number) {
  const was = screen.width < PHONE_BREAKPOINT;
  screen.width = width;
  (window as unknown as { happyDOM: { setViewport(v: { width: number; height: number }): void } }).happyDOM.setViewport({ width, height });
  if (was !== width < PHONE_BREAKPOINT) for (const l of screen.listeners) l({ matches: width < PHONE_BREAKPOINT });
}
beforeEach(() => {
  screen.listeners.clear();
  Object.defineProperty(window, 'matchMedia', {
    configurable: true, writable: true,
    value: (query: string) => ({
      get matches() { return query === PHONE_QUERY && screen.width < PHONE_BREAKPOINT; },
      media: query,
      addEventListener: (_: string, l: Listener) => { if (query === PHONE_QUERY) screen.listeners.add(l); },
      removeEventListener: (_: string, l: Listener) => { screen.listeners.delete(l); },
    }),
  });
  applyRemoteAccess('full');
});

let wrappers: VueWrapper[] = [];
afterEach(() => { for (const w of wrappers) w.unmount(); wrappers = []; document.body.innerHTML = ''; vi.restoreAllMocks(); });
function keep<T extends VueWrapper>(w: T): T { wrappers.push(w); return w; }

// ---- the fake core: enough for every section to draw its fields; anything else is refused (the panel shows an error)
const skill = (name: string, body = 'Do the thing.\n'.repeat(80)): Skill =>
  ({ kind: 'builtin', id: null, name, description: `when ${name} applies`, roles: ['member'], body, teamId: null, team: null }) as Skill;
const answers: Record<string, (p: any) => unknown> = {
  'skills.list': () => Array.from({ length: 14 }, (_, i) => skill(`skill-${i}`)),
  'agents.detect': () => [{ cliId: 'claude', found: true, launcherPath: '/bin/claude', executablePath: '/opt/claude/2.2.0', version: '2.2.0', installKind: 'native',
    readOnlyFolders: [], configFolders: [], problems: [], installHelp: '' }],
  'agentProfiles.list': () => [],
  'agents.testSessions': () => [],
  'policy.getGlobal': () => ({
    filesystem: { grantableRoots: ['~/src'], readonly: ['/opt/tools'], readwrite: [], denied: ['~/.ssh'] },
    network: { allowHosts: ['api.github.com'] },
    compute: { wallTimeoutSec: 7200, idleTimeoutSec: 1800, memoryMb: null, cpuPercent: null, maxProcesses: null },
    ui: { clipboard: 'read' },
    app: { maxConcurrentAgents: 4, backend: 'sandbox' },
  }),
  'guardrail.get': () => ({ state: 'none', error: null }),
  'integrations.list': () => ({ integrations: [{ name: 'gh', label: 'GitHub CLI', path: '/usr/bin/gh', enabled: true, status: 'signed in', login: true, teams: [] }] }),
  'integrations.connections': () => [],
  'integrations.apiList': () => [],
  'integrations.mcpList': () => [],
  'tasks.list': () => ({ tasks: [], repos: [] }),
  'link.status': () => ({ state: 'off', viewers: [] }),
  'ops.doctor': () => ({ checks: [] }),
  'ops.backups': () => [],
  'ops.keepDaily': () => ({ keepDaily: 7 }),
  'companion.list': () => ({ devices: [] }),
  'concierge.defaults': () => ({ defaultAgent: null }),
  'teams.defaults': () => ({ manager: { name: 'Manager', cli: 'claude' }, members: [{ name: 'Developer', cli: 'claude', onNewTeams: true }],
    memberNames: ['Amara', 'Tomas'], country: 'en-US', countries: [{ code: 'en-US', label: 'United States (US English)' }], usedCount: 0 }),
};
function fakeApi(): YawbleApi {
  return {
    getInfo: async () => ({ version: '1.0.0', platform: 'win32', os: 'Windows', dotnet: '9', sandbox: { sdkVersion: '1', nativeVersion: '1', requestContract: 'v1', telemetryEnabled: false }, schema: [] }) as any,
    getAvailableBackends: async () => [
      { backend: 'appcontainer-with-a-long-name', tier: 'strong', capabilities: ['filesystem', 'network', 'process-tree', 'ui-isolation'], warnings: ['a long warning about what this backend cannot do here'] },
    ] as any,
    call: async (method: string, params?: Record<string, unknown>) => {
      const a = answers[method];
      if (!a) throw new Error(`not in this fake: ${method}`);
      return a(params) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
}

const Sections: { tab: SettingsTab; open: string }[] = [
  { tab: 'general', open: 'open-general' }, { tab: 'agents', open: 'open-agents' }, { tab: 'concierge', open: 'open-concierge' },
  { tab: 'teams', open: 'open-teams' }, { tab: 'skills', open: 'open-skills' },
  { tab: 'globalPolicy', open: 'open-global-policy' }, { tab: 'integrations', open: 'open-integrations' }, { tab: 'link', open: 'open-link' },
  { tab: 'health', open: 'open-health' }, { tab: 'companion', open: 'open-companion' }, { tab: 'system', open: 'open-system' },
];
const Phones = [{ name: 'portrait 375 x 501', width: 375, height: 501 }, { name: 'landscape 667 x 263', width: 667, height: 263 }];

async function openSection(tab: SettingsTab) {
  const w = keep(mount(SettingsView, { props: { api: fakeApi(), tab, teams: [], landing: false }, attachTo: document.body }));
  await flushPromises();
  await flushPromises();
  return w;
}
const px = (v: string) => parseFloat(v) || 0;
const fields = (root: Element) => [...root.querySelectorAll<HTMLElement>('input, select, textarea')]
  .filter((e) => !['checkbox', 'radio', 'hidden', 'range', 'color'].includes((e as HTMLInputElement).type));
const scrolls = (v: string) => v === 'auto' || v === 'scroll';

describe.each(Phones)('Settings on a phone, $name', ({ width, height }) => {
  beforeEach(() => setScreen(width, height));

  describe.each(Sections)('$tab', ({ tab }) => {
    it('the "‹ Settings" back control sits in a slim header stuck to the top of the page scroller, so it stays in view at the bottom', async () => {
      const w = await openSection(tab);
      const page = w.get('[data-test=settings]').element as HTMLElement;
      const head = page.querySelector<HTMLElement>('.section-head')!;
      expect(head.querySelector('[data-test=settings-back]')).not.toBeNull();
      // The page is the one scroller; the header is its direct child, stuck at its top, painted over what scrolls under.
      const ps = getComputedStyle(page);
      expect(scrolls(ps.overflowY) || scrolls(ps.overflow), `overflow ${ps.overflow} / ${ps.overflowY}`).toBe(true);
      expect(head.parentElement).toBe(page);
      const hs = getComputedStyle(head);
      expect(hs.position).toBe('sticky');
      expect(hs.top).toBe('0px');
      expect(hs.backgroundColor === '' || hs.backgroundColor === 'transparent').toBe(false);
      expect(px(hs.minHeight)).toBeLessThanOrEqual(48); // one slim row, not the stacked back + title
      expect(hs.flexDirection).toBe('row');
      // Scrolled to the bottom: the header is still there, still sticky, and Back still goes back.
      page.scrollTop = 100_000;
      page.dispatchEvent(new Event('scroll'));
      await w.vm.$nextTick();
      expect(getComputedStyle(head).position).toBe('sticky');
      await w.get('[data-test=settings-back]').trigger('click');
      expect(w.get('[data-test=settings]').attributes('data-phone-screen')).toBe('list');
    });

    it('nothing inside the section scrolls on its own, up and down or sideways', async () => {
      const w = await openSection(tab);
      const page = w.get('[data-test=settings]').element as HTMLElement;
      const offenders = [...page.querySelectorAll<HTMLElement>('*')].filter((e) => {
        const s = getComputedStyle(e);
        return scrolls(s.overflowY) || scrolls(s.overflowX) || scrolls(s.overflow);
      }).map((e) => `${e.tagName.toLowerCase()}.${e.className}`);
      expect(offenders).toEqual([]);
    });

    it('nothing is set wider than the screen (no horizontal overflow)', async () => {
      const w = await openSection(tab);
      const page = w.get('[data-test=settings]').element as HTMLElement;
      const room = width - 24; // the page's side padding
      const wide = [...page.querySelectorAll<HTMLElement>('*')].filter((e) => {
        const s = getComputedStyle(e);
        return (s.minWidth.endsWith('px') && px(s.minWidth) > room) || (s.width.endsWith('px') && px(s.width) > room);
      }).map((e) => `${e.tagName.toLowerCase()}.${e.className}`);
      expect(wide).toEqual([]);
    });

    it('every text field, select and textarea is at least 16 px and 44 px tall', async () => {
      const w = await openSection(tab);
      if (tab === 'skills') await w.get('[data-test=skill-new]').trigger('click'); // the editor's fields too
      for (const f of fields(w.element)) {
        const s = getComputedStyle(f);
        const label = `${tab}: ${f.tagName.toLowerCase()}[data-test=${f.getAttribute('data-test')}]`;
        expect(px(s.fontSize), label).toBeGreaterThanOrEqual(16);
        expect(Math.max(px(s.minHeight), px(s.height)), label).toBeGreaterThanOrEqual(44);
      }
    });
  });

  it('Guardrails has its fields, and they are no longer the 12 px "Files and folders" inputs', async () => {
    const w = await openSection('globalPolicy');
    const inputs = w.findAll('[data-test=global-section-files] input');
    expect(inputs.length).toBeGreaterThan(0);
    for (const i of inputs) expect(getComputedStyle(i.element).fontSize).toBe('16px');
  });

  it('Concierge and Teams draw their fields (the size checks above cover them)', async () => {
    const c = await openSection('concierge');
    expect(c.find('[data-test=concierge-default-agent]').exists()).toBe(true);
    const t = await openSection('teams');
    for (const id of ['default-manager-name', 'default-member-name-0', 'member-names', 'names-country', 'names-generate', 'team-defaults-save'])
      expect(t.find(`[data-test=${id}]`).exists(), id).toBe(true);
  });

  it('Skills: a picked skill opens as its own view in place of the list, scrolled into view; Back returns to the list', async () => {
    const into = vi.spyOn(HTMLElement.prototype, 'scrollIntoView').mockImplementation(() => {});
    const w = await openSection('skills');
    expect(w.get('[data-test=skills-layout]').attributes('data-phone-view')).toBe('list');
    expect(w.find('[data-test=skills-list]').exists()).toBe(true);
    expect(w.find('[data-test=skills-pane]').exists()).toBe(false);
    await w.get('[data-test=skill-skill-12]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=skills-layout]').attributes('data-phone-view')).toBe('detail');
    expect(w.find('[data-test=skills-list]').exists()).toBe(false); // not below the list: instead of it
    const pane = w.get('[data-test=skills-pane]');
    expect(pane.get('h3').text()).toBe('skill-12');
    expect(pane.find('[data-test=skill-view]').exists()).toBe(true);
    expect(into).toHaveBeenCalledTimes(1);
    expect(into.mock.contexts[0]).toBe(pane.element);
    // Clear of the sticky Settings bar when it is scrolled to.
    expect(px(getComputedStyle(pane.element).scrollMarginTop)).toBeGreaterThanOrEqual(44);
    // One scroll level: the page, not the list, the pane or the skill's text.
    for (const sel of ['[data-test=skills-pane]', '[data-test=skill-view]']) expect(scrolls(getComputedStyle(w.get(sel).element).overflowY), sel).toBe(false);
    await w.get('[data-test=skills-back]').trigger('click');
    expect(w.get('[data-test=skills-layout]').attributes('data-phone-view')).toBe('list');
    expect(w.find('[data-test=skills-pane]').exists()).toBe(false);
    // + New skill opens the editor the same way; Cancel goes back to the list.
    await w.get('[data-test=skill-new]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=skill-name]').exists()).toBe(true);
    expect(w.find('[data-test=skills-list]').exists()).toBe(false);
    await w.findAll('[data-test=skills-pane] button').find((b) => b.text() === 'Cancel')!.trigger('click');
    expect(w.find('[data-test=skills-list]').exists()).toBe(true);
  });

  it('Integrations: the sub-tab row wraps instead of running off the screen', async () => {
    const w = await openSection('integrations');
    const tabs = w.get('[data-test=integrations] .tabs').element;
    expect(getComputedStyle(tabs).flexWrap).toBe('wrap');
    expect(w.findAll('[data-test=integrations] .tabs .pill')).toHaveLength(4);
    expect(getComputedStyle(w.get('[data-test=integration-new]').element).minWidth).not.toBe('260px');
  });

  it('System: each backend is a stack of labelled lines, Capabilities and Warnings included', async () => {
    const w = await openSection('system');
    const table = w.get('[data-test=backends] table').element;
    expect(getComputedStyle(table).display).toBe('block');
    expect(scrolls(getComputedStyle(table).overflowX)).toBe(false);
    expect(getComputedStyle(table.querySelector('thead')!).display).toBe('none');
    const cells = w.findAll('[data-test=backend-row] td');
    expect(cells.map((c) => c.attributes('data-label'))).toEqual(['Backend', 'Tier', 'Capabilities', 'Warnings']);
    for (const c of cells) expect(getComputedStyle(c.element).display).toBe('block');
    expect(w.get('[data-test=backend-row]').text()).toContain('ui-isolation');
    expect(w.get('[data-test=backend-row]').text()).toContain('a long warning');
  });
});

describe('the 16 px / 44 px phone rule beats the scoped field sizes of other screens (without editing them)', () => {
  /** A field under a parent with `cls`, both carrying `file`'s scope id: what that component's own rules match. */
  function scopedField(file: string, cls: string, tag: 'input' | 'select' | 'textarea', own = '') {
    const id = scopeOf.get(file);
    expect(id, file).toBeTruthy();
    const host = document.createElement('div');
    host.className = cls;
    host.setAttribute(id!, '');
    const f = document.createElement(tag);
    f.setAttribute(id!, '');
    if (own) f.className = own;
    host.appendChild(f);
    document.body.appendChild(host);
    return f;
  }
  const cases: [string, string, 'input' | 'select' | 'textarea', string, string?][] = [
    ['BacklogView.vue', 'head', 'select', '13px'], // the team filter
    ['BacklogView.vue', 'head', 'input', '13px', 'search'], // the search
    ['BacklogView.vue', 'dispatch', 'input', '13px'],
    ['BacklogView.vue', 'phone-sort', 'select', '13px'], // Sort by
    ['BacklogView.vue', 'itemform', 'textarea', '12.5px'],
    ['TeamView.vue', 'remember', 'input', '13px'],
    ['ListEditor.vue', 'mono', 'input', '12px'], // Guardrails' Files and folders
    ['IntegrationsPanel.vue', 'add-cli', 'input', '13px'],
    ['BoardView.vue', 'filters', 'select', '13px'],
    ['CardPanel.vue', 'edit', 'textarea', '13px'],
    ['TasksPanel.vue', 'x', 'select', '13px'],
    ['WatchesSection.vue', 'x', 'input', '13px'],
  ];

  it.each(cases)('%s .%s %s: 16 px and 44 px on a phone, its own %s at 1280 px', (file, cls, tag, desktop, own) => {
    setScreen(375, 501);
    const f = scopedField(file, cls, tag, own);
    expect(getComputedStyle(f).fontSize).toBe('16px');
    expect(px(getComputedStyle(f).minHeight)).toBeGreaterThanOrEqual(44);
    setScreen(1280, 800);
    const d = scopedField(file, cls, tag, own); // a fresh one (happy-dom keeps a computed style until the DOM changes)
    expect(getComputedStyle(d).fontSize).toBe(desktop);
    expect(px(getComputedStyle(d).minHeight)).toBeLessThan(44);
  });

  it('no component makes a field size !important or sets it inline, which is all that could beat the phone rule', () => {
    for (const key of Object.keys(modules)) {
      const src = readFileSync(resolve(__dirname, key), 'utf8');
      const styles = [...src.matchAll(/<style[^>]*>([\s\S]*?)<\/style>/g)].map((m) => m[1]).join('\n');
      for (const rule of styles.split('}')) {
        const [sel, body = ''] = rule.split('{');
        if (/\b(input|select|textarea)\b/.test(sel ?? '')) expect(body, `${key}: ${sel}`).not.toMatch(/(font-size|font|height)\s*:[^;]*!important/);
      }
      expect(src, key).not.toMatch(/<(input|select|textarea)\b[^>]*\bstyle="[^"]*(font|height)/);
    }
  });
});

describe('card 283: a 16 px field is never wider than its container (the Kanban team filter at 360 x 740)', () => {
  const longTeam = 'Yawble Code Browser Team with a much longer name than fits';
  const boardApi = () => ({
    call: async (method: string) => {
      if (method === 'board.list') return { lanes: [{ id: 'todo', label: 'To Do' }], cards: [], teams: [{ id: 1, name: longTeam }] };
      throw new Error(`not in this fake: ${method}`);
    },
    onEvent: () => () => {},
  }) as unknown as YawbleApi;

  it('the board team select is capped at its row (max-width 100%, min-width 0) and nothing sets it wider than 340 px', async () => {
    setScreen(360, 740);
    const w = keep(mount(BoardView, { props: { api: boardApi(), pollMs: 60_000 }, attachTo: document.body }));
    await flushPromises();
    const sel = w.get('[data-test=board-team]').element as HTMLSelectElement;
    expect([...sel.options].map((o) => o.text)).toContain(longTeam);
    const s = getComputedStyle(sel);
    expect(s.fontSize).toBe('16px'); // still no zoom on focus
    expect(px(s.minHeight)).toBeGreaterThanOrEqual(44);
    expect(s.maxWidth).toBe('100%'); // so its right edge stays inside the 340 px filter row
    expect(px(s.minWidth)).toBe(0);
    expect(s.boxSizing).toBe('border-box'); // padding and border inside that 100%
    for (const v of [s.width, s.minWidth]) if (v.endsWith('px')) expect(px(v)).toBeLessThanOrEqual(340);
    // Its row wraps, so the member select and the search go to their own lines rather than pushing it out.
    expect(getComputedStyle(sel.parentElement!).flexWrap).toBe('wrap');
    for (const f of fields(w.get('.filters').element)) expect(getComputedStyle(f).maxWidth, f.getAttribute('data-test')!).toBe('100%');
  });

  it('at 1280 px the board filters keep their desktop size (no cap from the phone rule)', async () => {
    setScreen(1280, 800);
    const w = keep(mount(BoardView, { props: { api: boardApi(), pollMs: 60_000 }, attachTo: document.body }));
    await flushPromises();
    const s = getComputedStyle(w.get('[data-test=board-team]').element);
    expect(s.fontSize).toBe('13px');
    expect(s.maxWidth === 'none' || s.maxWidth === '').toBe(true);
  });
});

describe('card 300: a growing field keeps a floor and its row wraps (the decline notes beside their buttons)', () => {
  /** `tree` built under `file`'s scope id: [tag, class, children]; returns the elements by data-test. */
  type Node = [string, string, Node[]?, string?];
  function build(file: string, node: Node, parent: Element = document.body, out: Record<string, HTMLElement> = {}) {
    const id = scopeOf.get(file);
    expect(id, file).toBeTruthy();
    const [tag, cls, kids = [], test] = node;
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    e.setAttribute(id!, '');
    if (test) { e.setAttribute('data-test', test); out[test] = e; }
    parent.appendChild(e);
    for (const k of kids) build(file, k, e, out);
    return out;
  }
  const btn = (n: number): Node[] => Array.from({ length: n }, () => ['button', ''] as Node);
  // TeamView.vue:258, the integration ask: the note, five buttons and the scope select in one .row.
  const integrationAsk: Node = ['div', 'decision ask', [['div', 'row', [['input', 'grow', [], 'note'], ...btn(5), ['select', '']], 'row']]];
  /** min(12rem, 100%) at a 16 px root, for a container `room` px wide. */
  const floorOf = (minWidth: string, room: number) => {
    const m = /^min\(\s*([\d.]+)(rem|px)\s*,\s*100%\s*\)$/.exec(minWidth); // happy-dom gives min(192px, 100%)
    return m ? Math.min(parseFloat(m[1]) * (m[2] === 'rem' ? 16 : 1), room) : px(minWidth);
  };

  it.each([[360, 740], [375, 667], [390, 844], [667, 263]])('at %i x %i the Team page decline note is at least 120 px and its row wraps', (width, height) => {
    setScreen(width, height);
    const { note, row } = build('TeamView.vue', integrationAsk);
    const s = getComputedStyle(note);
    expect(s.minWidth).not.toBe('0px'); // was 0: 15 px at 390 x 844, 30 px at 667 x 263 (re-verify 284)
    expect(floorOf(s.minWidth, width - 24 - 20), s.minWidth).toBeGreaterThanOrEqual(120); // page and card padding
    expect(s.maxWidth).toBe('100%');
    expect(s.fontSize).toBe('16px');
    expect(getComputedStyle(row).flexWrap).toBe('wrap');
  });

  it('a .row anywhere holding a .grow field wraps on a phone; one without a .grow field keeps its own wrapping', () => {
    setScreen(390, 844);
    const id = scopeOf.get('NewTeamDialog.vue')!;
    const row = document.createElement('span');
    row.className = 'row';
    row.setAttribute(id, '');
    const f = document.createElement('input');
    f.className = 'grow';
    f.setAttribute(id, '');
    row.appendChild(f);
    const plain = document.createElement('div');
    plain.className = 'row';
    plain.appendChild(document.createElement('input'));
    document.body.append(row, plain);
    expect(getComputedStyle(row).flexWrap).toBe('wrap');
    expect(floorOf(getComputedStyle(f).minWidth, 390 - 24)).toBeGreaterThanOrEqual(120);
    expect(getComputedStyle(plain).flexWrap).not.toBe('wrap');
    expect(px(getComputedStyle(plain.firstElementChild!).minWidth)).toBe(0); // a plain field still may shrink
  });

  it("a component's own .grow rule still wins: the Tell row stays one line, TaskAskCard's buttons stack under a full-width note", () => {
    setScreen(375, 667);
    const { tell, text } = build('TeamView.vue', ['div', 'row tell', [['input', 'grow', [], 'text'], ...btn(2)], 'tell']);
    expect(getComputedStyle(tell).flexWrap).toBe('nowrap');
    expect(px(getComputedStyle(text).minWidth)).toBe(0);
    // TaskAskCard.vue:59 on a phone: .buttons is a column and each child, the note included, is 100% wide.
    const { buttons, note } = build('TaskAskCard.vue', ['div', 'task-ask phone', [['div', 'row buttons', [['input', 'grow', [], 'note'], ...btn(3)], 'buttons']]]);
    expect(getComputedStyle(buttons).flexDirection).toBe('column');
    expect(getComputedStyle(note).width).toBe('100%');
  });

  it('at 1280 px no floor and no forced wrap (desktop unchanged)', () => {
    setScreen(1280, 800);
    const { note, row } = build('TeamView.vue', integrationAsk);
    const s = getComputedStyle(note);
    expect(s.minWidth === '' || s.minWidth === 'auto' || s.minWidth === '0px').toBe(true);
    expect(s.maxWidth === 'none' || s.maxWidth === '').toBe(true);
    expect(getComputedStyle(row).flexWrap).not.toBe('wrap');
  });
});

describe('Settings at 1280 px: the desktop layout as before', () => {
  beforeEach(() => setScreen(1280, 800));

  it('tabs and pane together, no back bar, Skills fills the page with the list beside the skill', async () => {
    const w = await openSection('skills');
    expect(w.get('[data-test=settings]').attributes('data-phone-screen')).toBeUndefined();
    expect(w.find('.section-head').exists()).toBe(false);
    expect(w.get('[data-test=settings]').classes()).toContain('fill');
    const layout = w.get('[data-test=skills-layout]');
    expect(layout.classes()).toEqual(expect.arrayContaining(['layout', 'fill']));
    expect(layout.classes()).not.toContain('phone');
    expect(layout.attributes('data-phone-view')).toBeUndefined();
    await w.get('[data-test=skill-skill-3]').trigger('click');
    expect(w.find('[data-test=skills-list]').exists()).toBe(true);
    expect(w.find('[data-test=skills-pane]').exists()).toBe(true);
    expect(w.find('[data-test=skills-back]').exists()).toBe(false);
    expect(getComputedStyle(layout.element).display).toBe('grid');
    expect(scrolls(getComputedStyle(w.get('[data-test=skills-list]').element).overflow)).toBe(true);
  });

  it('the Skills dialog (not in Settings) keeps both panes too', async () => {
    const w = keep(mount(SkillsDialog, { props: { api: fakeApi(), teams: [] }, attachTo: document.body }));
    await flushPromises();
    await w.get('[data-test=skill-skill-0]').trigger('click');
    expect(w.find('[data-test=skills-list]').exists()).toBe(true);
    expect(w.find('[data-test=skills-pane]').exists()).toBe(true);
  });

  it('fields keep their own sizes; the Integrations tabs stay one row; System keeps its table', async () => {
    const policy = await openSection('globalPolicy');
    const files = policy.findAll('[data-test=global-section-files] input');
    expect(files.length).toBeGreaterThan(0);
    for (const i of files) {
      expect(getComputedStyle(i.element).fontSize).toBe('12px');
      expect(getComputedStyle(i.element).minHeight).not.toBe('44px');
    }
    const integ = await openSection('integrations');
    expect(getComputedStyle(integ.get('[data-test=integrations] .tabs').element).flexWrap).not.toBe('wrap');
    expect(getComputedStyle(integ.get('[data-test=integration-new]').element).fontSize).toBe('13px');
    expect(getComputedStyle(integ.get('[data-test=integration-new]').element).minWidth).toBe('260px');
    const sys = await openSection('system');
    const table = sys.get('[data-test=backends] table').element;
    expect(getComputedStyle(table).display).toBe('table');
    expect(getComputedStyle(table.querySelector('thead')!).display).not.toBe('none');
    expect(getComputedStyle(sys.get('[data-test=backend-row] td').element).display).not.toBe('block');
  });
});
