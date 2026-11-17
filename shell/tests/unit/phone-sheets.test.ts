// @vitest-environment happy-dom
// B057 items 1 and 2 (card 274): on a phone (below 760 px), as measured on an iPhone SE's instance frame (375 x 521
// portrait, 667 x 263 landscape):
// - the Logs sheet's list fills what the sheet leaves it (no fixed 200 px), the filters and Sort by scroll away at the
//   top of the list instead of eating into the sheet, and Load more is the list's last row (reached by scrolling to the
//   end), with Filters closed and open;
// - a member's terminal (in its pane on the team page since B061, card 307) is the whole screen with one header row (title, the Formatted / Raw tabs, a ⋯ holding the
//   bar's state, Open interactive CLI, Policy and Stop, and ✕), the terminal taking the rest (at least 60% of the height).
// At 1280 px nothing changes.
//
// The real styles are in the page: styles.css and every component's <style> blocks under that component's scope id, so
// getComputedStyle is the browser's cascade (media queries, specificity). happy-dom does no layout: a height here is
// worked out from the computed CSS (the declared heights, paddings and flex rules), not a measured box. Fakes only: a
// scripted core, xterm stubbed out.
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { compileStyle, parse } from '@vue/compiler-sfc';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import LogsDialog from '../../src/renderer/src/components/LogsDialog.vue';
import MemberPane from '../../src/renderer/src/components/MemberPane.vue';
import TerminalPane from '../../src/renderer/src/components/TerminalPane.vue';
import { getMemberView, resetMemberViews } from '../../src/renderer/src/memberPaneView';
import { PHONE_BREAKPOINT, PHONE_QUERY } from '../../src/renderer/src/phone';
import { showChannelTerminal, terminalPane } from '../../src/renderer/src/terminalPane';
import type { AgentCard, LogSourceInfo, TerminalBanner, YawbleApi } from '../../src/shared/api';

// ---- the page's styles: styles.css, then each SFC's style blocks under its scope id
const modules = import.meta.glob<{ default: { __scopeId?: string } }>('../../src/renderer/src/**/*.vue', { eager: true });
beforeAll(() => {
  const css: string[] = [readFileSync(resolve(__dirname, '../../src/renderer/src/styles.css'), 'utf8')];
  for (const [key, mod] of Object.entries(modules)) {
    const file = resolve(__dirname, key);
    const id = mod.default.__scopeId;
    const { descriptor } = parse(readFileSync(file, 'utf8'), { filename: file });
    for (const style of descriptor.styles) css.push(compileStyle({ source: style.content, filename: file, id: id ?? 'data-v-none', scoped: !!style.scoped && !!id }).code);
  }
  const el = document.createElement('style');
  el.textContent = css.join('\n');
  document.head.appendChild(el);
});

// ---- the screen: happy-dom's viewport (what the media queries see) and the matchMedia usePhone() / useNarrow() read
type Frame = { name: string; width: number; height: number };
const Portrait: Frame = { name: 'portrait 375 x 521', width: 375, height: 521 };
const Landscape: Frame = { name: 'landscape 667 x 263', width: 667, height: 263 };
const Phones = [Portrait, Landscape];
const Desktop: Frame = { name: 'desktop 1280 x 800', width: 1280, height: 800 };
let width = 1280;
function setScreen(f: Frame) {
  width = f.width;
  (window as unknown as { happyDOM: { setViewport(v: { width: number; height: number }): void } }).happyDOM.setViewport({ width: f.width, height: f.height });
}
let scrollHeight: PropertyDescriptor | undefined;
beforeEach(() => {
  Object.defineProperty(window, 'matchMedia', {
    configurable: true, writable: true,
    value: (query: string) => ({ get matches() { return query === PHONE_QUERY && width < PHONE_BREAKPOINT; }, media: query, addEventListener: () => {}, removeEventListener: () => {} }),
  });
  // The Logs list asks for pages until it overflows its box: say it already does, so Load more stays for the test.
  scrollHeight = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'scrollHeight');
  Object.defineProperty(HTMLElement.prototype, 'scrollHeight', { configurable: true, get: () => 5000 });
  localStorage.clear();
  resetMemberViews();
  terminalPane.open = false;
  terminalPane.target = null;
});

let wrappers: VueWrapper[] = [];
afterEach(() => {
  for (const w of wrappers) w.unmount();
  wrappers = [];
  document.body.innerHTML = '';
  if (scrollHeight) Object.defineProperty(HTMLElement.prototype, 'scrollHeight', scrollHeight);
});
const keep = <T extends VueWrapper>(w: T): T => { wrappers.push(w); return w; };
const settle = async (n = 6) => { for (let i = 0; i < n; i++) await flushPromises(); };

const css = (el: Element) => getComputedStyle(el);
const px = (v: string) => (/^-?[\d.]+px$/.test(v.trim()) ? parseFloat(v) : 0);
const shown = (el: Element) => { for (let e: Element | null = el; e; e = e.parentElement) if (css(e).display === 'none') return false; return true; };

// ---- the fake core
function fakeApi(answers: Record<string, (p: any) => unknown>): { api: YawbleApi; calls: string[] } {
  const calls: string[] = [];
  return {
    calls,
    api: {
      getInfo: async () => { throw new Error('no'); },
      getAvailableBackends: async () => [],
      call: async (method: string, params?: Record<string, unknown>) => {
        calls.push(method);
        const a = answers[method];
        if (!a) throw new Error(`not in this fake: ${method}`);
        return a(params) as any;
      },
      onEvent: () => () => {},
      openTerminal: async () => {},
      pickFolder: async () => null,
    } as unknown as YawbleApi,
  };
}

// ================================================================ Logs

const col = (key: string, label: string, kind = 'text', filter = false) => ({ key, label, kind, filter });
const sources = [{
  id: 'activity', title: 'All activity', group: 'Activity', about: 'Everything.',
  columns: [col('at', 'Time', 'time'), col('member', 'Member'), col('event', 'Event', 'text', true), col('details', 'Details')],
  prunable: true, loss: 'the history', keptBecause: null, defaultDays: 90, retentionDays: 90, team: true, member: true,
}] as unknown as LogSourceInfo[];
function logsApi() {
  const page = (offset: number) => ({
    rows: Array.from({ length: 100 }, (_, i) => ({ _id: String(offset + i), at: '2026-11-13T12:00:00Z', member: 'web/DeveloperAda', event: `run ${offset + i}`, details: 'details' })),
    total: 1000, asOf: '2026-11-13T12:00:00Z', next: offset + 100 < 1000 ? offset + 100 : null,
  });
  return fakeApi({
    'logs.sources': () => structuredClone(sources), 'logs.who': () => ({ teams: ['web'], members: [{ key: 'web/DeveloperAda', name: 'DeveloperAda', team: 'web' }] }),
    'logs.values': () => ['run 1'], 'logs.query': (p) => page(p.offset),
  });
}
async function openLogs() {
  const fake = logsApi();
  const w = keep(mount(LogsDialog, { props: { api: fake.api }, attachTo: document.body }));
  await settle();
  return { w, ...fake };
}

/** The phone sheet's own header (Modal.vue: 4 px + a 44 px Close + 4 px, and its 1 px line) and the body's padding. */
function sheetChrome(sheet: Element) {
  const head = sheet.querySelector('header')!;
  const body = sheet.querySelector('.body')!;
  const h = css(head);
  const b = css(body);
  return { header: 4 + 44 + 4 + px(h.borderBottomWidth || '1px'), body: px(b.paddingTop) || 12, bodyBottom: px(b.paddingBottom) || 16 };
}
/** How many rows a wrapping flex bar takes at `inner` px: its shown children in `order`, each at its flex-basis, min-width
 *  or (a button with only a label) a 100 px estimate. */
function barRows(bar: Element, inner: number) {
  const items = [...bar.children].filter((c) => shown(c) && !['absolute', 'fixed'].includes(css(c).position))
    .flatMap((c) => (css(c).display === 'contents' ? [...c.children].filter(shown) : [c]))
    .map((c) => ({ order: Number(css(c).order || 0), w: px(css(c).flexBasis) || px(css(c).minWidth) || 100 }))
    .sort((a, b) => a.order - b.order);
  const gap = px(css(bar).columnGap || css(bar).gap) || 8;
  let rows = 1; let used = 0;
  for (const it of items) {
    if (used && used + gap + it.w > inner) { rows++; used = it.w; } else used += (used ? gap : 0) + it.w;
  }
  return rows;
}

describe('Logs on a phone: the list fills the sheet, Load more is always reachable', () => {
  for (const frame of Phones) {
    for (const filtersOpen of [false, true]) {
      it(`${frame.name}, Filters ${filtersOpen ? 'open' : 'closed'}`, async () => {
        setScreen(frame);
        const { w, calls } = await openLogs();
        if (filtersOpen) await w.get('[data-test=logs-filters-toggle]').trigger('click');
        const sheet = document.querySelector('.modal.sheet')!;
        expect(sheet, 'the dialog is a full-screen sheet').toBeTruthy();
        const scroller = w.get('[data-test=logs-scroll]').element;

        // The list is a flex child that takes what is left: no fixed height, no floor, scrolling on its own.
        const s = css(scroller);
        expect(s.flexGrow).toBe('1');
        expect(px(s.minHeight)).toBe(0);
        expect(['', 'auto']).toContain(s.height);
        expect(['', 'none']).toContain(s.maxHeight);
        expect(s.overflow + s.overflowY).toMatch(/auto/);
        // ... and every box from it up to the sheet is a flex column that may shrink, the sheet the screen's height:
        // the list's bottom is the sheet's bottom (less the body's padding), never below it.
        const logs = scroller.parentElement!;
        const body = logs.parentElement!;
        expect([css(logs).display, css(logs).flexDirection, css(logs).flexGrow, px(css(logs).minHeight)]).toEqual(['flex', 'column', '1', 0]);
        expect([css(body).display, css(body).flexDirection, css(body).flexGrow, px(css(body).minHeight), css(body).overflow]).toEqual(['flex', 'column', '1', 0, 'hidden']);
        // (The sheet itself is the screen's height: Modal.vue's `.modal.sheet.full { height: 100dvh }`, B037. happy-dom
        // drops dvh values as unknown, so it is not asserted here; the frame height is used below.)
        expect(sheet.classList.contains('full')).toBe(true);

        // Above the list, only the bar and the count line: the filters and Sort by are inside it, scrolling away.
        const above = [...logs.children].filter((c) => c !== scroller && shown(c) && (c.compareDocumentPosition(scroller) & Node.DOCUMENT_POSITION_FOLLOWING));
        expect(above.map((c) => c.getAttribute('data-test') ?? c.className)).toEqual([expect.stringContaining('bar'), 'logs-count']);
        const filters = w.get('[data-test=logs-filters]').element;
        const sort = w.get('[data-test=logs-sort]').element;
        expect(filters.closest('[data-test=logs-scroll]')).toBe(scroller);
        expect(sort.closest('[data-test=logs-scroll]')).toBe(scroller);
        expect(shown(filters)).toBe(filtersOpen);
        expect(shown(sort)).toBe(filtersOpen);
        expect(css(sort).position, 'Sort by is no longer stuck over the list').not.toMatch(/sticky|fixed/);
        const cards = w.get('[data-test=logs-cards]').element;
        expect(filters.compareDocumentPosition(cards) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();

        // The list's height, worked out: the frame less the sheet's header and padding, the bar's rows, the count
        // line and the gaps. Landscape: the bar is one row (Prune and Retention behind ⋯), so the list keeps > 100 px
        // (it had 26 px); portrait: more than half the frame.
        const chrome = sheetChrome(sheet);
        const bar = above[0]!;
        const rows = barRows(bar, frame.width - 24);
        if (frame === Landscape) expect(rows, 'landscape: the bar is one row').toBe(1);
        expect(rows).toBeLessThanOrEqual(2);
        const gap = px(css(logs).rowGap || css(logs).gap) || 8;
        const list = frame.height - chrome.header - chrome.body - chrome.bodyBottom - (rows * 44 + (rows - 1) * gap) - gap - 18 - gap;
        expect(list).toBeGreaterThan(frame === Landscape ? 100 : frame.height / 2);

        // Load more is the list's last row, in its flow: scrolling to the end shows it; it loads the next page.
        const more = w.get('[data-test=logs-more]').element;
        expect(scroller.lastElementChild).toBe(more);
        expect(shown(more)).toBe(true);
        expect(css(more).position).not.toMatch(/absolute|fixed/);
        scroller.scrollTop = 5000;
        const before = calls.filter((c) => c === 'logs.query').length;
        await w.get('[data-test=logs-more]').trigger('click');
        await settle();
        expect(calls.filter((c) => c === 'logs.query').length).toBe(before + 1);

        // Every select and text field: no zoom on focus, a 44 px target (styles.css).
        for (const f of logs.querySelectorAll('select, input')) {
          if (!shown(f)) continue;
          expect(parseFloat(css(f).fontSize)).toBeGreaterThanOrEqual(16);
          expect(px(css(f).minHeight)).toBeGreaterThanOrEqual(44);
        }
      });
    }
  }

  it('Prune… and Retention… fold behind ⋯ on the phone and still open their panels', async () => {
    setScreen(Portrait);
    const { w } = await openLogs();
    const actions = w.get('[data-test=logs-actions]').element;
    expect(shown(actions)).toBe(false);
    expect(css(actions).position).toBe('absolute'); // a menu over the page, not a row
    await w.get('[data-test=logs-actions-toggle]').trigger('click');
    expect(shown(actions)).toBe(true);
    await w.get('[data-test=logs-retention-open]').trigger('click');
    expect(w.find('[data-test=logs-retention]').exists()).toBe(true);
    expect(shown(actions)).toBe(false); // picked: the menu closes
  });

  it('1280: unchanged - the filters over the table, outside its scroller; Prune and Retention in the bar; no ⋯', async () => {
    setScreen(Desktop);
    const { w } = await openLogs();
    const scroller = w.get('[data-test=logs-scroll]').element;
    const filters = w.get('[data-test=logs-filters]').element;
    expect(filters.parentElement).toBe(scroller.parentElement);
    expect(filters.compareDocumentPosition(scroller) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(shown(filters)).toBe(true);
    expect(w.find('[data-test=logs-sort]').exists()).toBe(false);
    expect(w.find('[data-test=logs-filters-toggle]').exists()).toBe(false);
    expect(w.find('[data-test=logs-actions-toggle]').exists()).toBe(false);
    expect(css(w.get('[data-test=logs-actions]').element).display).toBe('contents');
    expect(shown(w.get('[data-test=logs-prune-open]').element)).toBe(true);
    expect(w.get('[data-test=logs-phone-tools]').element.children).toHaveLength(0);
    expect([css(scroller).flexGrow, css(scroller).minHeight]).toEqual(['1', '120px']);
    expect(w.find('table.grid').exists()).toBe(true);
    expect(w.find('[data-test=logs-about]').exists()).toBe(true);
  });
});

// ================================================================ the member terminal (in its pane on the team page)
// B061 moved a member's terminal from the right-hand TerminalPane into its MemberPane on the team page; card 307: B057's
// phone terminal (full screen, one header row, the ⋯ with the bar, the capped Policy strip) came with it. The member's
// terminal is opened the way the team page does it: its pane switched to Terminal (memberPaneView).

const banner = (): TerminalBanner => ({
  title: 'web / DeveloperAda · sandbox', details: ['timeout 15 min'], loosened: [], interactive: false,
  state: 'idle', runId: null, queuePosition: null, queueLength: 0, lastRunState: 'completed', lastRunReason: null,
} as unknown as TerminalBanner);
/** A long policy (B057 verify O2): 8 entries and 2 loosened. */
const longBanner = (): TerminalBanner => ({
  ...banner(),
  details: ['sandbox job object', 'CLI claude 2.1', 'network via proxy', 'allow api.anthropic.com', 'allow registry.npmjs.org',
    'timeout 15 min', 'idle timeout 5 min', 'memory 4 GB'],
  loosened: ['writes outside the clone allowed', 'network host github.com added by the person'],
} as unknown as TerminalBanner);
/** The iPhone SE frames the card names for the terminal (375 x 501 portrait, 667 x 263 landscape). */
const TermPortrait: Frame = { name: 'portrait 375 x 501', width: 375, height: 501 };
const TermPhones = [TermPortrait, Landscape];
/** A CSS length in px at the frame's height (happy-dom may hand back a vh value as declared). */
const lengthPx = (v: string, frame: Frame) => (/^[\d.]+vh$/.test(v.trim()) ? (parseFloat(v) * frame.height) / 100 : px(v));
const ada = (): AgentCard => ({
  member: { id: 4, teamId: 1, team: 'web', name: 'DeveloperAda', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' },
  state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: 'Done.', runId: null, runStartedAt: null,
  lastRunState: 'completed', lastRunReason: null, hasClone: true,
} as unknown as AgentCard);
async function openTerminal(make: () => TerminalBanner = banner) {
  const { api } = fakeApi({ 'terminal.banner': () => make(), 'terminal.stop': () => null });
  const w = keep(mount(MemberPane, { props: { card: ada(), api, pollMs: 60_000 }, attachTo: document.body, global: { stubs: { TerminalView: true, TranscriptView: true } } }));
  // Tapped open (B064: a remembered Terminal applies at desktop widths only).
  await w.get('[data-test=member-view-terminal]').trigger('click');
  await settle();
  return w;
}

describe('the member terminal on a phone: full screen, one header row', () => {
  for (const frame of TermPhones) {
    it(frame.name, async () => {
      setScreen(frame);
      const w = await openTerminal();
      const pane = w.get('[data-test=member-terminal]').element as HTMLElement;
      const head = w.get('[data-test=terminal-pane-head]').element;
      expect(head.parentElement).toBe(pane);

      // The whole screen over the page, not an area of the stacked pane: fixed to every edge, no width of its own, no
      // resize handle.
      const p = css(pane);
      expect(p.position).toBe('fixed');
      expect([p.top, p.right, p.bottom, p.left].every((v) => v === '0px' || v === '0') || /^0(px)?$/.test(p.inset)).toBe(true);
      expect(pane.style.width).toBe('');
      expect([px(p.minWidth.replace(/^0$/, '0px')), p.maxWidth]).toEqual([0, 'none']);
      // (Its height: 100dvh too, which happy-dom drops as unknown; fixed to all four edges is the screen either way.)
      expect(w.find('.tile-grip').exists()).toBe(false);
      expect(pane.querySelector('.handle')).toBeNull();

      // One header row: the title, ⋯ and ✕ are all in it; the bar (state, Stop) is in its ⋯ menu, closed - never Open
      // interactive CLI (the pane's terminal is never interactive); no Formatted / Raw tabs and no Policy; nothing else
      // stands between it and the terminal.
      expect(css(head).height).toBe('44px');
      expect(w.get('[data-test=terminal-pane-title]').text()).toBe('Developer Ada · web');
      for (const sel of ['[data-test=terminal-pane-title]', '[data-test=terminal-pane-more]', '[data-test=terminal-pane-close]'])
        expect(head.querySelector(sel), sel).toBeTruthy();
      expect(pane.querySelector('[data-test=terminal-modes]')).toBeNull();
      const menu = w.get('[data-test=terminal-pane-menu]').element;
      for (const sel of ['[data-test=terminal-state]', '[data-test=terminal-stop]'])
        expect(menu.querySelector(sel), sel).toBeTruthy();
      expect(pane.querySelector('[data-test=open-interactive]')).toBeNull();
      expect(menu.querySelector('[data-test=terminal-details-toggle]')).toBeNull();
      expect(pane.querySelector('[data-test=banner]')).toBeNull();
      expect(shown(menu)).toBe(false);
      expect(css(menu).position).toBe('absolute');
      const body = pane.querySelector(':scope > .term-window')!;
      const inFlowRows = [...pane.querySelectorAll('.bar, .modes, header')].filter((e) => shown(e) && !head.contains(e));
      expect(inFlowRows, 'no second or third header row').toEqual([]);

      // The terminal takes the rest and never runs past the bottom: a flex child with no floor, all the way down.
      expect([css(body).flexGrow, px(css(body).minHeight), css(body).overflow]).toEqual(['1', 0, 'hidden']);
      expect(css(pane).overflow).toBe('hidden');
      const screen = body.querySelector('.screen')!;
      expect([css(screen).flexGrow, px(css(screen).minHeight)]).toEqual(['1', 0]);
      expect(px(css(body.querySelector('.screen-wrap')!).minHeight)).toBe(0);
      const transcript = frame.height - 44;
      expect(transcript / frame.height).toBeGreaterThanOrEqual(0.6);

      // ⋯ opens the menu.
      await w.get('[data-test=terminal-pane-more]').trigger('click');
      expect(shown(menu)).toBe(true);
      // Every control in the header is a 44 px target.
      for (const b of head.querySelectorAll(':scope > button')) expect(px(css(b).minHeight) || px(css(b).height), b.textContent ?? '').toBeGreaterThanOrEqual(44);

      // ✕ goes back to the member's Report: the terminal (and its header) is gone.
      await w.get('[data-test=terminal-pane-close]').trigger('click');
      expect(getMemberView('web', 'DeveloperAda', true)).toBe('report');
      expect(w.find('[data-test=member-terminal]').exists()).toBe(false);
    });
  }

  it('1280: the terminal is the pane\'s one view: just the screen, Formatted, no bar, tabs or Policy, never interactive', async () => {
    setScreen(Desktop);
    const w = await openTerminal();
    const pane = w.get('[data-test=member-terminal]').element as HTMLElement;
    expect(css(pane).position).not.toBe('fixed');
    expect(pane.classList.contains('full')).toBe(false);
    expect(w.get('[data-test=member-pane-body]').element.contains(pane)).toBe(true);
    // The pane has no width bar of its own any more (one view, full width); the height handles are the team page's gaps.
    expect(w.find('.tile-grip').exists()).toBe(false);
    expect(w.find('[data-test=terminal-pane-head]').exists()).toBe(false);
    expect(w.find('[data-test=terminal-pane-more]').exists()).toBe(false);
    expect(w.find('[data-test=terminal-pane-menu]').exists()).toBe(false);
    expect(w.find('.term-window > .bar').exists()).toBe(false);
    expect(w.find('[data-test=terminal-details-toggle]').exists()).toBe(false);
    expect(w.find('[data-test=banner]').exists()).toBe(false);
    expect(w.find('[data-test=terminal-modes]').exists()).toBe(false);
    // Open interactive CLI (it opens the agent's terminal window) and Stop are in the pane's ☰ menu at this width.
    expect(w.find('[data-test=member-terminal] [data-test=open-interactive]').exists()).toBe(false);
    expect(w.find('[data-test=member-terminal] [data-test=terminal-stop]').exists()).toBe(false);
    await w.get('[data-test=member-more]').trigger('click');
    expect(w.find('[data-test=member-more-menu] [data-test=open-interactive-cli]').exists()).toBe(true);
    expect(w.find('[data-test=member-more-menu] [data-test=stop]').exists()).toBe(true);
    expect(css(w.get('.screen-wrap').element).minHeight).toBe('160px');
  });
});

describe('the ⋯ state line (B057 verify O3): no separator for an empty field', () => {
  it('a headless run with no command known reads "Run 42 · input off during headless run"', async () => {
    setScreen(Landscape);
    const w = await openTerminal(() => ({ ...banner(), state: 'running', runId: 42 } as unknown as TerminalBanner));
    const line = w.get('[data-test=terminal-pane-menu] [data-test=terminal-state]').text();
    expect(line).toBe('Run 42 · input off during headless run');
    expect(line).not.toMatch(/·\s*·/);
  });
});

// ================================================================ a channel terminal (still the right-hand TerminalPane)

describe('a channel terminal on a phone keeps B057 in TerminalPane', () => {
  it('full screen, one 44 px header row with the tabs; 1280: a side pane with its handle', async () => {
    const { api } = fakeApi({});
    showChannelTerminal('agent-test:claude', 'Claude · Test', () => {});
    setScreen(Landscape);
    const w = keep(mount(TerminalPane, { props: { api, pollMs: 60_000 }, attachTo: document.body, global: { stubs: { TerminalView: true, TranscriptView: true } } }));
    await settle();
    expect(css(w.get('[data-test=terminal-pane]').element).position).toBe('fixed');
    const head = w.get('[data-test=terminal-pane-head]').element;
    expect(css(head).height).toBe('44px');
    expect(head.querySelector('[data-test=terminal-modes]')).toBeTruthy();
    expect(head.querySelector('[data-test=terminal-pane-more]')).toBeTruthy();
    w.unmount(); wrappers = wrappers.filter((x) => x !== w);

    setScreen(Desktop);
    const d = keep(mount(TerminalPane, { props: { api, pollMs: 60_000 }, attachTo: document.body, global: { stubs: { TerminalView: true, TranscriptView: true } } }));
    await settle();
    const pane = d.get('[data-test=terminal-pane]').element as HTMLElement;
    expect(css(pane).position).toBe('relative');
    expect(pane.style.width).toBe(`${terminalPane.width}px`);
    expect(pane.querySelector('.handle')).toBeTruthy();
    expect(d.find('[data-test=terminal-pane-more]').exists()).toBe(false);
  });
});
