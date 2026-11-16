// @vitest-environment happy-dom
// B057 items 1 and 2 (card 274): on a phone (below 760 px), as measured on an iPhone SE's instance frame (375 x 521
// portrait, 667 x 263 landscape):
// - the Logs sheet's list fills what the sheet leaves it (no fixed 200 px), the filters and Sort by scroll away at the
//   top of the list instead of eating into the sheet, and Load more is the list's last row (reached by scrolling to the
//   end), with Filters closed and open;
// - a member's terminal has one header row (the Formatted / Raw tabs, and a ⋯ holding the bar's state line and Policy)
//   over a transcript of at least 60% of the height. Since B061 (card 286 fix round, from verify 288) that terminal is
//   the one inline in the member's pane on the team page (MemberPane, TerminalApp in-pane): Open interactive CLI and Stop
//   are the pane's toolbar there, and the right-hand pane no longer shows a member's terminal.
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
import { resetMemberViews, setMemberView } from '../../src/renderer/src/memberPaneView';
import { PHONE_BREAKPOINT, PHONE_QUERY } from '../../src/renderer/src/phone';
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

// ================================================================ the member terminal, inline in the member's pane

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
/** A CSS length in px at the frame's height (happy-dom may hand back a vh value as declared). */
const lengthPx = (v: string, frame: Frame) => (/^[\d.]+vh$/.test(v.trim()) ? (parseFloat(v) * frame.height) / 100 : px(v));
const card = (): AgentCard => ({
  member: { id: 4, teamId: 1, team: 'web', name: 'DeveloperAda', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' },
  state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: 'Done.', runId: null, runStartedAt: null,
  lastRunState: 'completed', lastRunReason: null, hasClone: true,
} as unknown as AgentCard);
/** DeveloperAda's pane on the team page, on Terminal (the real TerminalApp and TerminalScreen; xterm stubbed out). */
async function openTerminal(make: () => TerminalBanner = banner) {
  const { api, calls } = fakeApi({ 'terminal.banner': () => make(), 'terminal.stop': () => null });
  setMemberView('web', 'DeveloperAda', 'terminal');
  const w = keep(mount(MemberPane, { props: { card: card(), api, pollMs: 60_000 }, attachTo: document.body, global: { stubs: { TerminalView: true, TranscriptView: true } } }));
  await settle();
  return { w, calls };
}
/** The transcript's declared height on a phone (TerminalApp's `.in-pane .screen`, a calc of dvh that happy-dom drops). */
function transcriptRule(): { dvh: number; plus: number } {
  const src = readFileSync(resolve(__dirname, '../../src/renderer/src/TerminalApp.vue'), 'utf8');
  const m = /\.in-pane \.screen \{[^}]*\bheight: calc\(([\d.]+)dvh \+ ([\d.]+)px\)/.exec(src);
  expect(m, 'TerminalApp gives the in-pane transcript a height of its own on a phone').not.toBeNull();
  return { dvh: Number(m![1]), plus: Number(m![2]) };
}

describe("the member's terminal on a phone: one header row over the transcript, in the member's pane", () => {
  for (const frame of Phones) {
    it(frame.name, async () => {
      setScreen(frame);
      const { w } = await openTerminal();
      const term = w.get('[data-test=member-terminal]').element as HTMLElement;
      const head = w.get('[data-test=member-terminal-head]').element;
      // Inline in the pane, not the right-hand pane over the page.
      expect(document.querySelector('[data-test=terminal-pane]')).toBeNull();
      expect(term.querySelector('.term-window')!.classList.contains('in-pane')).toBe(true);

      // One header row, the terminal's first: the Formatted / Raw tabs and ⋯ are in it; the bar (state line, Policy) is
      // in its ⋯ menu, closed; Open interactive CLI and Stop are the pane's toolbar, not the terminal's. Nothing else
      // stands between the header and the transcript.
      expect(term.firstElementChild).toBe(head);
      expect(css(head).height).toBe('44px');
      for (const sel of ['[data-test=terminal-modes]', '[data-test=member-terminal-more]']) expect(head.querySelector(sel), sel).toBeTruthy();
      const menu = w.get('[data-test=member-terminal-menu]').element;
      for (const sel of ['[data-test=terminal-state]', '[data-test=terminal-details-toggle]']) expect(menu.querySelector(sel), sel).toBeTruthy();
      expect(term.querySelector('[data-test=open-interactive]')).toBeNull();
      expect(term.querySelector('[data-test=terminal-stop]')).toBeNull();
      expect(w.find('[data-test=member-toolbar] [data-test=stop]').exists()).toBe(true);
      expect(shown(menu)).toBe(false);
      expect(css(menu).position).toBe('absolute');
      const inFlowRows = [...term.querySelectorAll('.bar, .modes, header')].filter((e) => shown(e) && !head.contains(e));
      expect(inFlowRows, 'no second or third header row').toEqual([]);

      // The transcript has a height of its own (no flex share the frame's borders could eat into): at least 60% of the
      // screen, with no 160 px floor. The whole terminal (its 1 px borders, the 44 px row and its 1 px line, the
      // transcript) fits the screen under the app's 52 px top bar, so scrolled to the transcript's end its header shows.
      const screen = term.querySelector('.term-window > .screen')!;
      expect(css(screen).flexGrow).toBe('0');
      expect(px(css(term.querySelector('.screen-wrap')!).minHeight)).toBe(0);
      const rule = transcriptRule();
      const transcript = (rule.dvh * frame.height) / 100 + rule.plus;
      expect(transcript / frame.height, `${transcript} of ${frame.height}`).toBeGreaterThanOrEqual(0.6);
      expect(rule.plus).toBeGreaterThan(0); // a margin over 60% for rounding
      expect(1 + 44 + px(css(head).borderBottomWidth) + transcript + 1).toBeLessThanOrEqual(frame.height - 52);

      // ⋯ opens the menu; Policy there shows the policy (and the menu closes).
      await w.get('[data-test=member-terminal-more]').trigger('click');
      expect(shown(menu)).toBe(true);
      await w.get('[data-test=terminal-details-toggle]').trigger('click');
      expect(shown(menu)).toBe(false);
      expect(shown(w.get('[data-test=banner]').element)).toBe(true);
      // Every control in the header is a 44 px target.
      for (const b of head.querySelectorAll(':scope > button, [data-test=terminal-modes] button')) {
        expect(px(css(b).minHeight) || px(css(b).height), b.textContent ?? '').toBeGreaterThanOrEqual(44);
        if (b.matches('[data-test=member-terminal-more]')) expect(px(css(b).minWidth)).toBeGreaterThanOrEqual(44);
      }
    });
  }

  it('the menu goes when the pane switches to Activity, and comes back closed', async () => {
    setScreen(Portrait);
    const { w } = await openTerminal();
    await w.get('[data-test=member-terminal-more]').trigger('click');
    expect(shown(w.get('[data-test=member-terminal-menu]').element)).toBe(true);
    await w.get('[data-test=member-view-activity]').trigger('click');
    await settle();
    expect(w.find('[data-test=member-terminal]').exists()).toBe(false);
    await w.get('[data-test=member-view-terminal]').trigger('click');
    await settle();
    expect(shown(w.get('[data-test=member-terminal-menu]').element)).toBe(false);
    expect(w.get('[data-test=member-terminal-menu] [data-test=terminal-state]').text()).toContain('Idle');
  });

  it('1280: unchanged - no header row; the bar (state, Policy) and the tabs over the terminal', async () => {
    setScreen(Desktop);
    const { w } = await openTerminal();
    expect(w.find('[data-test=member-terminal-head]').exists()).toBe(false);
    expect(w.find('[data-test=member-terminal-menu]').exists()).toBe(false);
    const bar = w.get('[data-test=member-terminal] .term-window > .bar').element;
    expect(bar.querySelector('[data-test=terminal-state]')).toBeTruthy();
    expect(bar.querySelector('[data-test=terminal-details-toggle]')).toBeTruthy();
    expect(w.get('.screen-wrap > [data-test=terminal-modes]').exists()).toBe(true);
    expect(css(w.get('.term-window > .screen').element).flexGrow).toBe('1');
  });
});

describe('the Policy strip from ⋯ on a phone (B057 verify O2): capped, scrolls on its own, closes with its own ✕', () => {
  for (const frame of Phones) {
    it(frame.name, async () => {
      setScreen(frame);
      const { w } = await openTerminal(longBanner);
      await w.get('[data-test=member-terminal-more]').trigger('click');
      await w.get('[data-test=terminal-details-toggle]').trigger('click');
      const strip = w.get('[data-test=banner]').element;
      expect(shown(strip)).toBe(true);
      expect(strip.closest('[data-test=member-terminal]')).toBeTruthy();
      expect(w.get('[data-test=banner-loosened]').text()).toContain('github.com');

      // A cap of at most a fifth of the screen; it adds to the terminal and takes nothing from the transcript, whose
      // height is its own (at least 60%, above).
      const cap = lengthPx(css(strip).maxHeight, frame);
      expect(cap, 'the strip has a max-height').toBeGreaterThan(0);
      expect(cap).toBeLessThanOrEqual(frame.height * 0.2);
      expect(css(strip).flexShrink === '0' || css(strip).flex.startsWith('0 0')).toBe(true);
      const rule = transcriptRule();
      expect(((rule.dvh * frame.height) / 100 + rule.plus) / frame.height).toBeGreaterThanOrEqual(0.6);
      // The text scrolls inside the strip, under the same cap; the strip itself clips.
      const text = w.get('[data-test=banner-text]').element;
      expect(css(text).overflowY).toBe('auto');
      expect(lengthPx(css(text).maxHeight, frame)).toBeLessThanOrEqual(cap);
      expect(css(strip).overflow).toBe('hidden');

      // Its ✕ is a 44 px target that fits in the strip, and hides it without reopening ⋯.
      const close = w.get('[data-test=banner-close]');
      expect(px(css(close.element).minHeight)).toBeGreaterThanOrEqual(44);
      expect(px(css(close.element).minWidth)).toBeGreaterThanOrEqual(44);
      expect(cap).toBeGreaterThanOrEqual(44);
      await close.trigger('click');
      expect(shown(strip)).toBe(false);
      expect(shown(w.get('[data-test=member-terminal-menu]').element)).toBe(false);
      expect(localStorage.getItem('yawble.terminal.details')).toBeNull();
    });
  }

  it('1280: unchanged - no cap, no ✕ on the strip', async () => {
    setScreen(Desktop);
    localStorage.setItem('yawble.terminal.details', '1');
    const { w } = await openTerminal(longBanner);
    const strip = w.get('[data-test=banner]').element;
    expect(shown(strip)).toBe(true);
    expect(['none', '']).toContain(css(strip).maxHeight);
    expect(w.find('[data-test=banner-close]').exists()).toBe(false);
    expect(css(w.get('[data-test=banner-text]').element).overflowY).not.toBe('auto');
  });
});

describe('the ⋯ state line (B057 verify O3): no separator for an empty field', () => {
  it('a headless run with no command known reads "Run 42 · input off during headless run"', async () => {
    setScreen(Landscape);
    const { w } = await openTerminal(() => ({ ...banner(), state: 'running', runId: 42 } as unknown as TerminalBanner));
    const state = w.get('[data-test=terminal-state]');
    expect(state.element.closest('[data-test=member-terminal-menu]'), "in the terminal header's ⋯").toBeTruthy();
    const line = state.text();
    expect(line).toBe('Run 42 · input off during headless run');
    expect(line).not.toMatch(/·\s*·/);
  });
});
