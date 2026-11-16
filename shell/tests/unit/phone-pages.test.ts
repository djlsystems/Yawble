// @vitest-environment happy-dom
// B054 (card 268): on a phone (below 760 px) Backlog, the Team page and Documents give the screen to their content: the
// page header is one compact row that scrolls away with the page (one scroller, no pinned header stack, no nested list
// scrollers), and the first content row is within about 120 px of the top of the instance frame (375x501 portrait) and on
// screen in landscape (667x263). At 1280 px nothing changes.
//
// happy-dom does no layout, so the top of the first row is worked out from what is rendered and the component's own
// phone styles: the app bar, the paddings and margins of the page around the row, and each block rendered above it (a
// block above the first row must declare its height on the phone, or the test fails: it would not be a compact row).
// Fakes only: matchMedia answers from a set width, the core is a scripted fake.
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import BacklogView from '../../src/renderer/src/board/BacklogView.vue';
import TeamView from '../../src/renderer/src/components/TeamView.vue';
import DocumentsView from '../../src/renderer/src/components/DocumentsView.vue';
import DocumentsBrowser from '../../src/renderer/src/components/DocumentsBrowser.vue';
import MemberActivity from '../../src/renderer/src/components/MemberActivity.vue';
import type { AgentCard, BacklogItem, YawbleApi } from '../../src/shared/api';
import type { ActivityRow } from '../../src/renderer/src/monitor/model';

// ---------------------------------------------------------------- the fakes

/** A window `width` px wide: matchMedia answers max-width queries from it. */
function screen(width: number) {
  vi.stubGlobal('matchMedia', vi.fn((query: string) => {
    const max = /\(max-width:\s*([\d.]+)px\)/.exec(query);
    return { matches: max ? width <= Number(max[1]) : false, media: query, addEventListener: () => {}, removeEventListener: () => {} };
  }));
}
/** The instance frame of the iPhone SE (B054), below the cloud site's bar. */
const Portrait = { width: 375, height: 501 };
const Landscape = { width: 667, height: 263 };
const Frames = [Portrait, Landscape];
const Desktop = 1280;
/** Portrait: the first content row within about 120 px of the frame's top. */
const PortraitTarget = 120;
/** A compact row's height on the phone (a 44 px tap target). */
const Row = 44;

const wrappers: VueWrapper[] = [];
const keep = <T extends VueWrapper>(w: T) => { wrappers.push(w); return w; };
afterEach(() => { while (wrappers.length) wrappers.pop()!.unmount(); vi.unstubAllGlobals(); document.body.innerHTML = ''; });
const settle = async (n = 6) => { for (let i = 0; i < n; i++) await flushPromises(); };

function fakeApi(answers: Record<string, (params: any) => unknown>) {
  return {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: any) => {
      const a = answers[method];
      if (!a) throw new Error(`unexpected ${method}`);
      return a(params);
    },
    onEvent: () => () => {},
  } as unknown as YawbleApi;
}

const item = (id: number, title: string): BacklogItem => ({
  id, label: `B00${id}`, title, body: `# ${title}`, state: 'pending', createdAt: '2026-11-08T10:00:00Z', updatedAt: '2026-11-08T10:00:00Z',
  archivedAt: null, dispatchedAt: null, dispatchedTeam: null, dispatchCount: 0, inFlight: false,
} as BacklogItem);
const backlogApi = () => fakeApi({
  'backlog.list': () => [item(1, 'First'), item(2, 'Second'), item(3, 'Third')],
  'backlog.item': (p) => ({ ...item(p.id, 'First'), dispatches: [] }),
});

const team = { id: 1, name: 'payments', repoPath: null, members: 2, running: 0, queued: 0 } as any;
const agent = (id: number, name: string, statusLine: string | null = null): AgentCard => ({
  member: { id, teamId: 1, team: 'payments', name, role: id === 1 ? 'manager' : 'member', cli: 'claude', instructions: '', model: '', credential: 'default' },
  state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine, runId: null, runStartedAt: null, lastRunState: null, lastRunReason: null,
} as unknown as AgentCard);
const teamApi = (behind: boolean) => fakeApi({
  'teams.agents': () => [agent(1, 'Manager', 'A long report.\n\n'.repeat(30)), agent(2, 'DeveloperAda')], 'bus.decisions': () => [], 'integrations.asks': () => [],
  'monitor.messages': () => ({ end: 0, messages: [] }), 'monitor.steps': () => ({ end: 0, pending: false, steps: [] }),
  'agentProfiles.list': () => [], 'bus.workflows': () => [],
  'teams.syncToBase': () => (behind ? { note: null, on: true, behind: { text: '3 commits behind main', count: 3 } } : { note: null, on: true, behind: null }),
});

const docsApi = () => fakeApi({
  'docs.list': (p) => ({
    path: p.path ?? '',
    entries: [{ name: 'report.md', path: `${p.path ? `${p.path}/` : ''}report.md`, folder: false, size: 20 }, { name: 'specs', path: `${p.path ? `${p.path}/` : ''}specs`, folder: true, size: null }],
  }),
  'docs.read': (p) => ({ path: p.path, name: 'report.md', kind: 'markdown', size: 20, text: '# Report\n\nDone.' }),
});

// ---------------------------------------------------------------- the styles, and the layout worked out from them

const read = (p: string) => readFileSync(resolve(__dirname, '../../src/renderer/src', p), 'utf8').replace(/\r\n/g, '\n');
const PhoneBlock = /@media \(max-width: 759\.98px\) \{([\s\S]*?)\n\}/;
const styleOf = (src: string) => /<style scoped>([\s\S]*?)<\/style>/.exec(src)?.[1] ?? '';
const phoneCss = (src: string) => PhoneBlock.exec(src)?.[1] ?? '';
const desktopCss = (src: string) => styleOf(src).replace(PhoneBlock, '');

type Rule = { sels: string[]; decls: Record<string, string> };
/** The innermost rules of a stylesheet (an @media wrapper's own line is left out), comments removed. */
function rules(css: string): Rule[] {
  const out: Rule[] = [];
  for (const m of css.replace(/\/\*[\s\S]*?\*\//g, '').matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const decls: Record<string, string> = {};
    for (const d of m[2]!.split(';')) { const i = d.indexOf(':'); if (i > 0) decls[d.slice(0, i).trim()] = d.slice(i + 1).trim(); }
    out.push({ sels: m[1]!.split(',').map((s) => s.trim()).filter(Boolean), decls });
  }
  return out;
}
const matches = (el: Element, sel: string) => { try { return el.matches(sel.replace(/:deep\(([^)]*)\)/g, '$1')); } catch { return false; } };
/** A component's styles: the phone rules win over the desktop ones (a phone rule is later in the file). */
class Styles {
  readonly phone: Rule[];
  readonly desktop: Rule[];
  constructor(...files: string[]) {
    const srcs = files.map(read);
    this.phone = srcs.flatMap((s) => rules(phoneCss(s)));
    this.desktop = srcs.flatMap((s) => rules(desktopCss(s)));
  }
  /** The value of `prop` for `el` on the phone: the last matching phone rule, else the last matching desktop rule. */
  value(el: Element, ...props: string[]): string | undefined {
    for (const set of [this.phone, this.desktop]) {
      let found: string | undefined;
      for (const r of set) if (r.sels.some((s) => matches(el, s))) for (const p of props) if (r.decls[p] !== undefined) found = r.decls[p];
      if (found !== undefined) return found;
    }
    return undefined;
  }
  hidden(el: Element) { return this.value(el, 'display') === 'none'; }
}
const px = (v: string | undefined) => (v && /^-?[\d.]+px/.test(v) ? parseFloat(v) : 0);
/** Top and bottom of a margin or padding shorthand ("4px auto 0" is 4 and 0). */
function sides(el: Element, st: Styles, prop: 'margin' | 'padding') {
  const short = st.value(el, prop)?.split(/\s+/) ?? [];
  const top = st.value(el, `${prop}-top`) ?? short[0];
  const bottom = st.value(el, `${prop}-bottom`) ?? short[2] ?? short[0];
  return { top: px(top), bottom: px(bottom) };
}
const rendered = (el: Element, st: Styles) => !(el as HTMLElement).hidden && (el as HTMLElement).style.display !== 'none' && !st.hidden(el);
/** The blocks rendered above `el` inside `root`: at each level up, the siblings before it. */
function blocksAbove(el: Element, root: Element, st: Styles): Element[] {
  const out: Element[] = [];
  for (let n: Element = el; n !== root; n = n.parentElement!) {
    const before: Element[] = [];
    for (let s = n.previousElementSibling; s; s = s.previousElementSibling) if (rendered(s, st)) before.unshift(s);
    out.unshift(...before);
  }
  return out;
}
/** A block's height on the phone: its declared height (or min-height), else its rendered children's, stacked. */
function heightOf(el: Element, st: Styles): number {
  const own = st.value(el, 'height') ?? st.value(el, 'min-height');
  if (own && /^[\d.]+px$/.test(own)) return px(own);
  const kids = [...el.children].filter((k) => rendered(k, st));
  if (!kids.length) throw new Error(`no phone height for <${el.tagName.toLowerCase()} class="${el.className}">: not a compact row`);
  return kids.reduce((h, k) => { const m = sides(k, st, 'margin'); return h + m.top + heightOf(k, st) + m.bottom; }, 0);
}
/** The app's top bar on the phone (App.vue). */
const AppBar = px(rules(styleOf(read('App.vue'))).find((r) => r.sels.includes('.phone .bar'))?.decls['min-height']);
/** The Team page's host (App.vue's .teams-place on a phone): its top padding. */
const teamsPlacePad = px(rules(styleOf(read('App.vue'))).find((r) => r.sels.includes('.phone .teams-place'))?.decls.padding?.split(/\s+/)[0]);

/** Where `el` starts, from the top of the frame: the app bar, the page's paddings and margins around it, the blocks above. */
function topOf(el: Element, root: Element, st: Styles, hostPad = 0) {
  let top = AppBar + hostPad;
  const chain: Element[] = [];
  for (let n: Element | null = el.parentElement; n && n !== root.parentElement; n = n.parentElement) chain.unshift(n);
  for (const a of chain) { const p = sides(a, st, 'padding'); const m = a === root ? { top: 0 } : sides(a, st, 'margin'); top += p.top + m.top; }
  const above = blocksAbove(el, root, st);
  for (const b of above) { const m = sides(b, st, 'margin'); top += m.top + heightOf(b, st) + m.bottom; }
  top += sides(el, st, 'margin').top;
  return { top, above: above.map((b) => b.getAttribute('data-test') ?? b.classList[0] ?? b.tagName.toLowerCase()) };
}
const fits = (top: number, frame: { height: number }) => (frame === Portrait ? top <= PortraitTarget : top + Row <= frame.height);

/** The desktop rules outside each phone block, as they were at the base (8b39e07): a desktop change must be on purpose. */
const DesktopCss: Record<string, string> = {
  'board/BacklogView.vue': '55673d4cef46ac87',
  // B061 (card 286): the member's card and activity became one pane (MemberPane.vue): the team page's member-row rules went.
  'components/TeamView.vue': 'ec127edc70b3a1a4',
  'components/TeamSyncBanner.vue': '8305253f8317a684',
  'components/MemberPane.vue': '0dcbae2a8c95e6e4',
  'components/MemberActivity.vue': '5b65b8b04e4b41c1',
  'components/DocumentsView.vue': '66dcab6b89792c38',
  'components/DocumentsBrowser.vue': 'e2d8b12b24e93037',
};

/** Selectors that scroll vertically on the desktop (overflow / overflow-y auto or scroll). */
const scrollers = (src: string) => rules(desktopCss(src)).filter((r) => /^(auto|scroll)/.test(r.decls.overflow ?? r.decls['overflow-y'] ?? '')).flatMap((r) => r.sels);
/** Whether the phone rules turn `sel` (or a narrower form of it) into a plain block that grows with its content. */
function phoneUnscrolled(src: string, sel: string) {
  return rules(phoneCss(src)).some((r) => r.sels.some((s) => { const t = s.replace(/:not\([^)]*\)/g, ''); return t === sel || t.endsWith(` ${sel}`); })
    && ['visible'].includes((r.decls['overflow-y'] ?? r.decls.overflow ?? '').split(/\s+/)[0]!));
}

// ---------------------------------------------------------------- the pages at 375x501 and 667x263

async function backlog(width: number) {
  screen(width);
  const w = keep(mount(BacklogView, { props: { api: backlogApi(), teams: [], pollMs: 60_000 } }));
  await settle();
  return w;
}
async function teamPage(width: number, behind = false, of = team) {
  screen(width);
  const dialogs = { GitDialog: true, TriggersDialog: true, TeamManageDialog: true, WorkflowsDialog: true, DocumentsDialog: true, LogsDialog: true };
  const w = keep(mount(TeamView, { props: { team: of, api: teamApi(behind), pollMs: 100_000 }, global: { stubs: dialogs } }));
  await settle();
  return w;
}
async function documentsRoot(width: number) {
  screen(width);
  const w = keep(mount(DocumentsView, { props: { api: docsApi(), teams: [team], archived: [] } }));
  await settle();
  return w;
}
async function documentsFolder(width: number) {
  const w = await documentsRoot(width);
  await w.get('[data-test=documents-team-payments]').trigger('click');
  await settle();
  return w;
}

describe('the first content row is near the top on a phone (B054)', () => {
  for (const frame of Frames) {
    const at = `${frame.width}x${frame.height}`;

    it(`Backlog at ${at}: only the compact header row is above the first item`, async () => {
      const w = await backlog(frame.width);
      const st = new Styles('board/BacklogView.vue');
      const { top, above } = topOf(w.get('[data-test=backlog-row]').element, w.get('[data-test=backlog-view]').element, st);
      expect(above).toEqual(['backlog-phone-head']);
      expect(top, `first item at ${top}`).toBeLessThanOrEqual(frame === Portrait ? PortraitTarget : frame.height - Row);
      expect(fits(top, frame)).toBe(true);
      // The intro, the search, the team filter, Upload and Sort are folded away until asked for.
      for (const t of ['backlog-search', 'backlog-team-filter', 'backlog-upload', 'backlog-sort']) expect(w.find(`[data-test=${t}]`).exists(), t).toBe(false);
      expect(w.find('.intro').exists()).toBe(false);
    });

    it(`the Team page at ${at}: the header row, then Tell the Manager, then the members`, async () => {
      const w = await teamPage(frame.width);
      const st = new Styles('components/TeamView.vue', 'components/TeamSyncBanner.vue', 'components/MemberPane.vue', 'components/MemberActivity.vue');
      const root = w.get('section.team').element;
      const tell = topOf(w.get('.tell').element, root, st, teamsPlacePad);
      expect(tell.above).toEqual(['teamhead']);
      expect(fits(tell.top, frame), `Tell at ${tell.top}`).toBe(true);
      const member = topOf(w.get('.member-cell').element, root, st, teamsPlacePad);
      expect(member.above).toEqual(['teamhead', 'tell']);
      expect(member.top + Row, `first member at ${member.top}`).toBeLessThanOrEqual(frame.height);
    });

    it(`the Team page at ${at} with the "commits behind" bar: Tell is still the first row, the bar under it, the first member on screen`, async () => {
      const w = await teamPage(frame.width, true);
      const st = new Styles('components/TeamView.vue', 'components/TeamSyncBanner.vue', 'components/MemberPane.vue', 'components/MemberActivity.vue');
      expect(w.get('[data-test=team-behind]').text()).toContain('3 commits behind main');
      const root = w.get('section.team').element;
      // Card 282: the bar sat between the header and Tell (Tell at 156 px in portrait); now Tell is where it is without it.
      const tell = topOf(w.get('.tell').element, root, st, teamsPlacePad);
      expect(tell.above).toEqual(['teamhead']);
      expect(fits(tell.top, frame), `Tell at ${tell.top}`).toBe(true);
      if (frame === Portrait) expect(tell.top).toBeLessThanOrEqual(PortraitTarget);
      const sync = topOf(w.get('[data-test=team-sync]').element, root, st, teamsPlacePad);
      expect(sync.above).toEqual(['teamhead', 'tell']);
      expect(sync.top + Row, `sync bar at ${sync.top}`).toBeLessThanOrEqual(frame.height);
      const member = topOf(w.get('.member-cell').element, root, st, teamsPlacePad);
      expect(member.above).toEqual(['teamhead', 'tell', 'team-sync']);
      expect(member.top + Row, `first member at ${member.top}`).toBeLessThanOrEqual(frame.height);
    });

    it(`Documents at ${at}, the root: the title, then the team folders (the intro left out)`, async () => {
      const w = await documentsRoot(frame.width);
      const st = new Styles('components/DocumentsView.vue');
      const { top, above } = topOf(w.get('[data-test=documents-team-payments]').element, w.get('[data-test=documents-view]').element, st);
      expect(above).toEqual(['h2']);
      expect(fits(top, frame), `first folder at ${top}`).toBe(true);
      expect(w.find('.wrap > p.muted').exists()).toBe(false);
    });

    it(`Documents at ${at}, a team's folder: the breadcrumb and the + menu in one row, then the files`, async () => {
      const w = await documentsFolder(frame.width);
      const st = new Styles('components/DocumentsView.vue', 'components/DocumentsBrowser.vue');
      const first = w.get('[data-test=docs-list] li').element;
      expect(first.textContent).toContain('report.md');
      const { top, above } = topOf(first, w.get('[data-test=documents-view]').element, st);
      expect(above).toEqual(['bar']);
      expect(fits(top, frame), `first file at ${top}`).toBe(true);
      // The toolbar is in the + menu, in the same row as the breadcrumb.
      const bar = w.get('.bar');
      expect(bar.find('[data-test=docs-crumbs]').exists()).toBe(true);
      expect(bar.find('[data-test=docs-add]').exists()).toBe(true);
      for (const t of ['docs-new-file', 'docs-new-folder', 'docs-upload']) expect(w.find(`[data-test=${t}]`).exists(), t).toBe(false);
    });
  }
});

describe('one scroller per page on a phone (B054)', () => {
  const pages: Record<string, { files: string[]; root: string[] }> = {
    Backlog: { files: ['board/BacklogView.vue'], root: ['.backlog-place'] },
    Team: { files: ['components/TeamView.vue', 'components/TeamSyncBanner.vue', 'components/MemberPane.vue', 'components/MemberActivity.vue'], root: [] },
    Documents: { files: ['components/DocumentsView.vue', 'components/DocumentsBrowser.vue'], root: ['.docs-place'] },
  };
  // A code block in a report scrolls sideways at its own height (no fixed height): not a vertical scroller.
  const sideways = ['.output :deep(pre)'];

  for (const [page, { files, root }] of Object.entries(pages)) {
    it(`${page}: every box that scrolls on the desktop grows with its content on the phone, but the page itself`, () => {
      for (const f of files) {
        const src = read(f);
        for (const sel of scrollers(src)) {
          if (root.includes(sel) || sideways.includes(sel)) continue;
          expect(phoneUnscrolled(src, sel), `${f}: ${sel} is a nested scroller on the phone`).toBe(true);
        }
      }
    });
  }

  it('the pages are the scroller: Backlog and an open Documents folder are plain blocks, not a filled column', () => {
    const b = rules(phoneCss(read('board/BacklogView.vue')));
    expect(b.find((r) => r.sels.includes('.backlog-place.fill'))?.decls.display).toBe('block');
    expect(b.find((r) => r.sels.includes('.wrap.fill'))?.decls.display).toBe('block');
    const d = rules(phoneCss(read('components/DocumentsView.vue')));
    expect(d.find((r) => r.sels.includes('.docs-place.fill'))?.decls).toMatchObject({ display: 'block', overflow: 'auto' });
    const br = rules(phoneCss(read('components/DocumentsBrowser.vue')));
    expect(br.find((r) => r.sels.includes('.browser.fill'))?.decls).toMatchObject({ height: 'auto', display: 'block' });
  });

  it("the Team page: each member's activity and output are at their own height (no inline desktop height on the phone)", async () => {
    const w = await teamPage(Portrait.width);
    for (const b of w.findAll('[data-test=member-pane-body]')) expect((b.element as HTMLElement).style.height).toBe('');
    for (const s of w.findAll('[data-test=member-side]')) expect((s.element as HTMLElement).style.flexBasis).toBe('');
    const pane = rules(phoneCss(read('components/MemberPane.vue')));
    expect(pane.find((r) => r.sels.includes('.output') && r.decls.height)?.decls.height).toBe('auto');
    expect(pane.find((r) => r.sels.includes('.side') && r.decls.height)?.decls.height).toBe('auto');
    // The activity is capped: it renders as a plain list, its ol not positioned to scroll.
    for (const a of w.findAll('[data-test=member-activity]')) expect(a.classes()).toContain('capped');
    const act = rules(phoneCss(read('components/MemberActivity.vue')));
    expect(act.find((r) => r.sels.includes('.activity.capped ol'))?.decls).toMatchObject({ position: 'static', overflow: 'visible' });
  });

  it('a capped activity shows the newest lines and how many more are in Logs; uncapped (the desktop) shows every line', () => {
    const rows: ActivityRow[] = Array.from({ length: 8 }, (_, i) => ({ key: `k${i}`, seq: i, tone: 'tell', team: 'payments', member: 'Ada', verb: 'told', who: 'Ada', text: `line ${i}`, at: '2026-11-16T10:00:00Z' } as ActivityRow));
    const capped = keep(mount(MemberActivity, { props: { rows, loaded: true, now: Date.now(), limit: 5 } }));
    const lines = capped.findAll('[data-test=member-activity-row]').map((r) => r.text());
    expect(lines).toHaveLength(5);
    lines.forEach((l, i) => expect(l).toContain(`line ${i}`));
    expect(capped.get('[data-test=member-activity-more]').text()).toBe('+3 older in Logs');
    const all = keep(mount(MemberActivity, { props: { rows, loaded: true, now: Date.now() } }));
    expect(all.findAll('[data-test=member-activity-row]')).toHaveLength(8);
    expect(all.find('[data-test=member-activity-more]').exists()).toBe(false);
    expect(all.get('[data-test=member-activity]').classes()).not.toContain('capped');
  });
});

describe('no sideways overflow on a phone (B054)', () => {
  // The header rows do not wrap: their icon buttons are fixed (44 px), the title or breadcrumb gives way (min-width 0).
  const Char = 8.8; // a 16 px font's average glyph, generous
  const content = (width: number, pad: number) => width - 2 * pad;

  it('Backlog: the tabs and the two icon buttons fit in 375 px', async () => {
    const w = await backlog(Portrait.width);
    const st = new Styles('board/BacklogView.vue');
    const head = w.get('[data-test=backlog-phone-head]').element;
    expect(st.value(head, 'flex-wrap')).toBe('nowrap');
    const icons = [...head.querySelectorAll('.hbtn')];
    expect(icons.length).toBe(2);
    for (const b of icons) expect(st.value(b, 'width')).toBe('44px');
    const tabs = [...head.querySelectorAll('.tabs button')].reduce((n, b) => n + b.textContent!.length * Char + 20, 0);
    expect(tabs + icons.length * 44 + 3 * px(st.value(head, 'gap'))).toBeLessThanOrEqual(content(Portrait.width, 10));
  });

  it('the Team page: the title gives way to Documents, Workflows and ⋯; the Tell placeholder is whole at 375 px', async () => {
    const w = await teamPage(Portrait.width);
    const st = new Styles('components/TeamView.vue');
    const head = w.get('.teamhead').element;
    expect(st.value(head, 'height')).toBe('44px');
    expect(st.value(w.get('.titleline').element, 'min-width')).toBe('0');
    expect(st.value(w.get('.titleline h2').element, 'text-overflow')).toBe('ellipsis');
    expect(w.findAll('[data-test=team-phone-actions] > .hbtn, [data-test=team-phone-actions] .more-wrap > .hbtn').map((b) => b.attributes('aria-label')))
      .toEqual(['Documents', 'Workflows', 'More team actions']);
    // Tell: the box beside two 44 px buttons; its placeholder fits the room left (16 px font).
    const tell = w.get('[data-test=tell-text]');
    const placeholder = tell.attributes('placeholder')!;
    expect(placeholder).toBe('Tell the Manager…');
    expect(st.value(w.get('.tell').element, 'flex-wrap')).toBe('nowrap');
    expect(st.value(tell.element, 'min-width')).toBe('0');
    expect(w.get('[data-test=tell-send]').text()).toBe('✉');
    expect(w.get('[data-test=tell-send]').attributes('aria-label')).toBe('Tell Manager');
    const room = content(Portrait.width, 12) - 2 * 44 - 2 * 6 - 16;
    expect(placeholder.length * Char).toBeLessThanOrEqual(room);
    expect('Tell the Manager… (paste an image to attach it)'.length * Char).toBeGreaterThan(room); // why the phone has a short one
  });

  it('the Team page of a running team at 375 px: the name is whole beside the state chip (card 282: it read "Tea…")', async () => {
    const w = await teamPage(Portrait.width, false, { ...team, running: 1 });
    const st = new Styles('components/TeamView.vue');
    const line = w.get('.titleline').element;
    const h2 = w.get('.titleline h2').element;
    // The title still reads "Team payments" (what a screen reader and the tests read); "Team " is left out on screen.
    expect(h2.textContent).toBe('Team payments');
    const prefix = w.get('.titleline h2 .title-prefix').element;
    expect(st.hidden(prefix)).toBe(true);
    const shown = h2.textContent!.replace(prefix.textContent!, '');
    expect(shown).toBe('payments');
    // No count chip takes room (the live "1 running" one was what pushed the name out); only the state chip is left.
    const kpis = [...line.querySelectorAll('.kpi')];
    expect(kpis.length).toBe(3);
    for (const k of kpis) expect(st.hidden(k), k.getAttribute('data-test')!).toBe(true);
    const chip = w.get('[data-test=team-page-state]').element;
    expect(chip.textContent).toBe('Running');
    expect([...line.children].filter((k) => !st.hidden(k))).toEqual([h2, chip]);
    // The room the title line has: the row less the three 44 px icons and the gaps.
    const room = (Portrait.width - 2 * teamsPlacePad) - 3 * 44 - 2 * px(st.value(w.get('.phone-actions').element, 'gap'))
      - px(st.value(w.get('.teamhead').element, 'gap'));
    const chipWidth = chip.textContent!.length * 7 + 14 + 11; // 11 px bold label, its padding, the pulsing dot
    expect(shown.length * Char + px(st.value(line, 'gap')) + chipWidth, `room ${room}`).toBeLessThanOrEqual(room);
    // The old title would not fit (why "Team " and the counts go on a phone).
    expect('Team payments'.length * Char + 6 + chipWidth + 6 + '1 running'.length * 7.5 + 20).toBeGreaterThan(room);
    // A longer name: the state chip gives way first.
    expect(st.value(chip, 'flex')).toBe('0 1000 auto');
    expect(st.value(chip, 'min-width')).toBe('0');
    expect(st.value(h2, 'flex')).toBe('0 1 auto');
  });

  it('the Team page: Triggers, Git and Settings are in the ⋯ menu, and each opens what it did', async () => {
    const w = await teamPage(Portrait.width);
    expect(w.find('[data-test=open-triggers]').exists()).toBe(false);
    await w.get('[data-test=team-more]').trigger('click');
    expect(w.get('[data-test=team-more]').attributes('aria-expanded')).toBe('true');
    expect(w.findAll('[data-test=team-more-menu] [role=menuitem]').map((b) => b.attributes('data-test'))).toEqual(['open-triggers', 'open-git', 'open-manage']);
    await w.get('[data-test=open-git]').trigger('click');
    expect(w.find('[data-test=team-more-menu]').exists()).toBe(false); // the menu closes as the dialog opens
    expect(w.find('git-dialog-stub').exists()).toBe(true);
  });

  it('Documents: deep in a folder the breadcrumb is "…" (the folder above) and the folder you are in; the + menu holds the toolbar', async () => {
    screen(Portrait.width);
    const w = keep(mount(DocumentsBrowser, { props: { api: docsApi(), team, fill: true, above: 'Documents' } }));
    await settle();
    await w.get('[data-test="doc-specs"]').trigger('click');
    await settle();
    await w.get('[data-test="doc-specs/specs"]').trigger('click');
    await settle();
    expect(w.findAll('nav.crumbs > a').map((a) => a.text())).toEqual(['Documents', '…', 'specs']);
    expect(w.findAll('nav.crumbs > a')[1]!.attributes('title')).toBe('specs');
    const st = new Styles('components/DocumentsBrowser.vue');
    expect(st.value(w.get('.bar').element, 'flex-wrap')).toBe('nowrap');
    expect(st.value(w.get('nav.crumbs').element, 'min-width')).toBe('0');
    await w.get('[data-test=docs-add]').trigger('click');
    expect(w.findAll('[data-test=docs-add-menu] [role=menuitem]').map((b) => b.text())).toEqual(['New file', 'New folder', 'Upload']);
    await w.get('[data-test=docs-new-folder]').trigger('click');
    expect(w.find('[data-test=docs-add-menu]').exists()).toBe(false);
    expect(w.get('[data-test=docs-name]').attributes('placeholder')).toBe('Folder name');
  });

  it("Backlog: the fold opens under the header row, and its button counts what is set in it", async () => {
    const w = await backlog(Portrait.width);
    await w.get('[data-test=backlog-tools-toggle]').trigger('click');
    expect(w.get('[data-test=backlog-tools-toggle]').attributes('aria-expanded')).toBe('true');
    expect(w.get('[data-test=backlog-phone-head]').element.nextElementSibling).toBe(w.get('[data-test=backlog-tools]').element);
    expect(w.find('[data-test=backlog-tools-count]').exists()).toBe(false);
    await w.get('[data-test=backlog-search]').setValue('Sec');
    await w.get('[data-test=backlog-sort-key]').setValue('title');
    expect(w.get('[data-test=backlog-tools-count]').text()).toBe('2');
    expect(w.findAll('[data-test=backlog-row]').map((r) => r.get('.ttl').text())).toEqual(['Second']);
    // Folded again, the filter still holds and the count says so.
    await w.get('[data-test=backlog-tools-toggle]').trigger('click');
    expect(w.find('[data-test=backlog-tools]').exists()).toBe(false);
    expect(w.get('[data-test=backlog-tools-count]').text()).toBe('2');
    expect(w.findAll('[data-test=backlog-row]')).toHaveLength(1);
  });
});

describe('at 1280 px the desktop is as it was (B054)', () => {
  it('the desktop rules of every touched component are unchanged', () => {
    for (const [f, hash] of Object.entries(DesktopCss)) expect(createHash('sha256').update(desktopCss(read(f))).digest('hex').slice(0, 16), f).toBe(hash);
  });

  it('Backlog: the header with its title, tabs, search, team filter, Upload document and + Add item, then the intro', async () => {
    const w = await backlog(Desktop);
    const head = w.get('header.head');
    expect(head.get('h2').text()).toBe('Backlog');
    expect([...head.element.children].map((e) => e.getAttribute('data-test') ?? (e.className || e.tagName.toLowerCase())))
      .toEqual(['h2', 'tabs', 'grow', 'backlog-search', 'backlog-team-filter', 'backlog-upload', 'backlog-upload-input', 'backlog-add']);
    expect(head.get('[data-test=backlog-add]').text()).toBe('+ Add item');
    expect(w.get('.wrap > p.intro').text()).toMatch(/^Work you want done, most important first\./);
    expect(w.find('[data-test=backlog-phone-head]').exists()).toBe(false);
    expect(w.find('[data-test=backlog-tools-toggle]').exists()).toBe(false);
    expect(w.get('[data-test=backlog-view]').classes()).toEqual(['backlog-place', 'fill']);
  });

  it('the Team page: the five labelled actions, the long Tell placeholder and "Tell Manager ✉", the activity uncapped', async () => {
    const w = await teamPage(Desktop);
    expect(w.findAll('.teamhead .actions > button').map((b) => b.text())).toEqual(['Documents', 'Workflows', '⏰ Triggers', '⎇ Git', 'Settings']);
    expect(w.find('[data-test=team-more]').exists()).toBe(false);
    expect(w.get('[data-test=tell-text]').attributes('placeholder')).toBe('Tell the Manager… (paste an image to attach it)');
    expect(w.get('[data-test=tell-send]').text()).toBe('Tell Manager ✉');
    expect(w.get('[data-test=tell-send]').attributes('aria-label')).toBeUndefined();
    for (const a of w.findAll('[data-test=member-activity]')) expect(a.classes()).not.toContain('capped');
  });

  it('the Team page: the "commits behind" bar is still above Tell (card 282 moves it on a phone only)', async () => {
    const w = await teamPage(Desktop, true);
    const order = [...w.get('section.team').element.children].map((e) => e.getAttribute('data-test') ?? e.classList[0]);
    expect(order.indexOf('team-sync')).toBeGreaterThan(order.indexOf('teamhead'));
    expect(order.indexOf('team-sync')).toBeLessThan(order.indexOf('tell'));
    expect(w.findAll('[data-test=team-sync]')).toHaveLength(1);
  });

  it('Documents: the root intro, and in a folder the breadcrumb then New file, New folder and Upload', async () => {
    const w = await documentsRoot(Desktop);
    expect(w.get('.wrap > p.muted').text()).toMatch(/^Each team keeps its documents here/);
    await w.get('[data-test=documents-team-payments]').trigger('click');
    await settle();
    expect([...w.get('.bar').element.children].map((e) => e.getAttribute('data-test') ?? e.className))
      .toEqual(['docs-crumbs', 'grow', 'docs-new-file', 'docs-new-folder', 'docs-upload', 'docs-upload-input']);
    expect(w.find('[data-test=docs-add]').exists()).toBe(false);
    expect(w.get('[data-test=documents-view]').classes()).toEqual(['docs-place', 'fill']);
  });
});
