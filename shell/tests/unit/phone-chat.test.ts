// @vitest-environment happy-dom
// B057 item 3 (card 275): the chat composer on a phone (below 760 px wide), as measured on an iPhone SE: 375 x 521 in
// portrait and 667 x 263 in landscape. Concierge and team chats share this layout (ConciergeView + Composer). Idle, the
// composer is one row (the pickers' button, the text, Attach, Send) of at most ~70 px; the team and agent pickers fold
// out behind that one button, each on its own full-width row at 16 px and 44 px; the note under the box folds out with
// them; the messages keep at least half the frame in portrait. At 1280 px nothing changes.
//
// The real styles are in the page: styles.css and every component's <style> block, compiled with that component's own
// scope id, so getComputedStyle is the browser's cascade (media queries, !important, specificity). happy-dom does no
// layout: a height here is added up from the computed CSS (padding, border, min-height, height), not a measured box.
// The API is a scripted fake (no core, no network).
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { compileStyle, parse } from '@vue/compiler-sfc';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import App from '../../src/renderer/src/App.vue';
import Composer from '../../src/renderer/src/components/Composer.vue';
import { PHONE_BREAKPOINT, PHONE_QUERY } from '../../src/renderer/src/phone';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';
import type { CoreEvent, Conversation, YawbleApi } from '../../src/shared/api';

// ---- the page's styles: styles.css, then each SFC's style blocks under its scope id
const modules = import.meta.glob<{ default: { __scopeId?: string } }>('../../src/renderer/src/**/*.vue', { eager: true });
beforeAll(() => {
  const css: string[] = [readFileSync(resolve(__dirname, '../../src/renderer/src/styles.css'), 'utf8')];
  for (const [key, mod] of Object.entries(modules)) {
    const file = resolve(__dirname, key);
    const id = mod.default.__scopeId;
    const { descriptor } = parse(readFileSync(file, 'utf8'), { filename: file });
    for (const style of descriptor.styles) {
      css.push(compileStyle({ source: style.content, filename: file, id: id ?? 'data-v-none', scoped: !!style.scoped && !!id }).code);
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
  try { localStorage.clear(); sessionStorage.clear(); } catch { /* none */ }
  applyRemoteAccess('full');
});

let wrappers: VueWrapper[] = [];
afterEach(() => { for (const w of wrappers) w.unmount(); wrappers = []; document.body.innerHTML = ''; setScreen(1280, 800); });
function keep<T extends VueWrapper>(w: T): T { wrappers.push(w); return w; }

// ---- the fake core: one team with a long name, one chat with a few messages
const longTeam = 'Yawble Engineering Platform';
const chat: Conversation = {
  id: 'c1', title: 'Payments today', cli: 'claude', teamId: 1, team: longTeam, createdAt: '', updatedAt: '', running: false, unread: 0, watching: 0, watches: [],
  messages: [
    { id: 1, role: 'user', text: 'How is payments doing?', at: '', state: null, error: null, tools: [], cli: null },
    { id: 2, role: 'assistant', text: 'All green.', at: '', state: 'done', error: null, tools: [], cli: 'claude' },
  ],
  turn: null,
} as unknown as Conversation;
function fakeApi(): YawbleApi {
  const answers: Record<string, (p: any) => unknown> = {
    'app.overview': () => ({ slots: { running: 0, limit: 4, queued: 0 }, teams: [{ id: 1, name: longTeam, repoPath: null, members: 2, running: 0, queued: 0 }] }),
    'link.status': () => ({ state: 'off', viewers: [] }),
    'agents.detect': () => [{ cliId: 'claude', found: true, signIn: { status: 'signed-in', detail: null } }],
    'concierge.list': () => [{ ...chat, messages: undefined }],
    'concierge.get': () => JSON.parse(JSON.stringify(chat)),
    'concierge.read': () => ({}),
    'concierge.setTeam': () => ({}),
    'network.asks': () => [],
    'tools.asks': () => [],
  };
  return {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      const a = answers[method];
      if (!a) throw new Error(`not in this fake: ${method}`);
      return a(params) as any;
    },
    onEvent: (_l: (e: CoreEvent) => void) => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
}

/** The App with the chat open (the sidebar's, or on a phone the drawer's, chat entry). */
async function openChat() {
  const w = keep(mount(App, { props: { api: fakeApi(), pollMs: 60_000 }, attachTo: document.body }));
  await flushPromises();
  await flushPromises();
  await w.get('.chat .open').trigger('click');
  await flushPromises();
  await flushPromises();
  expect(w.find('[data-test=concierge-thread]').exists()).toBe(true);
  return w;
}

// ---- sizes from the computed CSS
const css = (e: Element) => getComputedStyle(e);
/** A length from computed CSS; a calc() of px terms (e.g. calc(8px + env(safe-area-inset-bottom, 0px))) adds them up. */
function len(v: string): number {
  if (!v) return 0;
  const n = Number.parseFloat(v);
  if (!v.includes('(') && !Number.isNaN(n)) return n;
  return [...v.matchAll(/(-?\d+(?:\.\d+)?)px/g)].reduce((s, m) => s + Number(m[1]), 0);
}
const boxY = (e: Element) => { const s = css(e); return len(s.paddingTop) + len(s.paddingBottom) + len(s.borderTopWidth) + len(s.borderBottomWidth); };
const tall = (e: Element) => { const s = css(e); return Math.max(len(s.minHeight), len(s.height)); };
/** The composer at rest: its box (padding + border) around its one row, as tall as the row's tallest item. */
function idleComposer(composer: Element): number {
  const bar = composer.querySelector('.bar')!;
  const row = [...bar.children].filter((c) => !c.hasAttribute('hidden') && css(c).display !== 'none').map(tall);
  const others = [...composer.children].filter((c) => c !== bar);
  expect(others, 'at rest the composer holds only its one row').toEqual([]);
  return boxY(composer) + boxY(bar) + Math.max(...row);
}

const Phones = [
  { name: 'portrait 375 x 521', width: 375, height: 521, portrait: true },
  { name: 'landscape 667 x 263', width: 667, height: 263, portrait: false },
];

describe.each(Phones)('a chat on a phone, $name', ({ width, height, portrait }) => {
  beforeEach(() => setScreen(width, height));

  it('the idle composer is one row: the pickers\' button, the text, Attach, Send; the pickers and the note are folded away', async () => {
    const w = await openChat();
    const composer = w.get('[data-test=composer]');
    expect(composer.classes()).toContain('phone');
    const bar = composer.get('.bar').element;
    expect([...bar.querySelectorAll('[data-test]')].map((e) => e.getAttribute('data-test')))
      .toEqual(['composer-pickers-toggle', 'composer-text', 'composer-attach', 'composer-attach-input', 'composer-send']);
    expect(composer.find('[data-test=composer-pickers]').exists()).toBe(false);
    expect(composer.find('[data-test=composer-team]').exists()).toBe(false);
    expect(composer.find('[data-test=composer-agent]').exists()).toBe(false);
    expect(w.find('[data-test=concierge-hint]').exists()).toBe(false); // the note under the box folds away
    // Every item of the row is a 44 px tap target, the text 16 px.
    for (const sel of ['[data-test=composer-pickers-toggle]', '[data-test=composer-attach]', '[data-test=composer-send]']) {
      const s = css(composer.get(sel).element);
      expect(len(s.minWidth), sel).toBeGreaterThanOrEqual(44);
      expect(tall(composer.get(sel).element), sel).toBeGreaterThanOrEqual(44);
    }
    const text = css(composer.get('[data-test=composer-text]').element);
    expect(len(text.fontSize)).toBeGreaterThanOrEqual(16);
    expect(len(text.minHeight)).toBeGreaterThanOrEqual(44);
    expect(text.flexGrow).toBe('1');
    // The button names what is picked.
    expect(composer.get('[data-test=composer-pickers-toggle]').attributes('title')).toContain(`Team: ${longTeam}`);
    expect(composer.get('[data-test=composer-pickers-toggle]').attributes('aria-expanded')).toBe('false');
  });

  it(portrait ? 'the messages get at least half the frame\'s height' : 'the idle composer takes at most ~70 px, and so does its whole dock', async () => {
    const w = await openChat();
    const composer = idleComposer(w.get('[data-test=composer]').element);
    const dock = w.get('.dock').element;
    const dockH = boxY(dock) + composer;
    const topBar = tall(w.get('.main > header.bar').element);
    const messages = height - topBar - dockH;
    expect(composer).toBeLessThanOrEqual(70);
    if (portrait) expect(messages / height).toBeGreaterThanOrEqual(0.5);
    else expect(dockH).toBeLessThanOrEqual(72);
    // Before B057 the landscape thread had 69 px; now it has the frame less the top bar and the one-row dock.
    expect(messages).toBeGreaterThan(portrait ? 300 : 120);
  });

  it('the one button folds out the team and agent pickers, each on its own full-width row at >= 16 px and >= 44 px, and the note', async () => {
    const w = await openChat();
    const composer = w.get('[data-test=composer]');
    await composer.get('[data-test=composer-pickers-toggle]').trigger('click');
    expect(composer.get('[data-test=composer-pickers-toggle]').attributes('aria-expanded')).toBe('true');
    const panel = composer.get('[data-test=composer-pickers]');
    expect(css(panel.element).flexDirection).toBe('column'); // one row each
    for (const sel of ['[data-test=composer-team]', '[data-test=composer-agent]']) {
      const select = panel.get(sel).element;
      const s = css(select);
      expect(len(s.fontSize), sel).toBeGreaterThanOrEqual(16);
      expect(len(s.minHeight), sel).toBeGreaterThanOrEqual(44);
      expect(s.flexGrow, sel).toBe('1'); // it takes the row, all but its label
      expect(css(select.closest('.row-pick')!).width, sel).toBe('100%');
      expect(select.closest('.bar'), sel).toBeNull(); // not squeezed into the bar beside the text
    }
    // The long team name is the picked option, whole, in a select that is no longer capped at a third of the screen.
    const team = panel.get('[data-test=composer-team]').element as HTMLSelectElement;
    expect(team.value).toBe('1');
    expect(team.selectedOptions[0].textContent).toBe(longTeam);
    expect(css(team).maxWidth).not.toMatch(/vw/);
    expect(panel.get('[data-test=composer-note]').text()).toContain('The Concierge sees every team');
    // Picking still works; the button folds it all away again.
    await panel.get('[data-test=composer-team]').setValue('');
    await flushPromises();
    expect((w.get('[data-test=composer-team]').element as HTMLSelectElement).value).toBe('');
    await composer.get('[data-test=composer-pickers-toggle]').trigger('click');
    expect(composer.find('[data-test=composer-pickers]').exists()).toBe(false);
  });

  it('a new chat\'s composer is the same compact one row', async () => {
    const w = keep(mount(App, { props: { api: fakeApi(), pollMs: 60_000 }, attachTo: document.body }));
    await flushPromises();
    await flushPromises();
    expect(w.find('[data-test=concierge-hero]').exists()).toBe(true);
    expect(idleComposer(w.get('[data-test=composer]').element)).toBeLessThanOrEqual(70);
  });

  it('Send sends from the one row (Enter is a new line on a phone)', async () => {
    const w = keep(mount(Composer, { props: { teams: [], agents: [{ id: 'claude', label: 'Claude', ready: true }], cli: 'claude', teamId: null, running: false } }));
    await w.get('[data-test=composer-text]').setValue('Hello');
    await w.get('[data-test=composer-text]').trigger('keydown', { key: 'Enter' });
    expect(w.emitted('send')).toBeUndefined();
    await w.get('[data-test=composer-send]').trigger('click');
    expect(w.emitted('send')![0][0]).toBe('Hello');
  });
});

describe('a chat on the desktop, 1280 x 800: unchanged', () => {
  beforeEach(() => setScreen(1280, 800));

  it('the text box above a bar of team picker, Attach, agent picker and Send; the note under the box; no pickers button', async () => {
    const w = await openChat();
    const composer = w.get('[data-test=composer]');
    expect(composer.classes()).not.toContain('phone');
    const kids = [...composer.element.children];
    expect(kids[0].getAttribute('data-test')).toBe('composer-text');
    expect(kids[1].classList.contains('bar')).toBe(true);
    const bar = composer.get('.bar');
    expect(bar.get('[data-test=composer-team]').element.closest('.pick')).not.toBeNull();
    expect(bar.get('[data-test=composer-agent]').element.closest('.pick')).not.toBeNull();
    expect(bar.find('[data-test=composer-attach]').exists()).toBe(true);
    expect(bar.find('[data-test=composer-send]').attributes('title')).toBe('Send (Enter)');
    expect(composer.find('[data-test=composer-pickers-toggle]').exists()).toBe(false);
    expect(composer.find('[data-test=composer-note]').exists()).toBe(false);
    expect(w.get('[data-test=concierge-hint]').text()).toContain('The Concierge sees every team');
    // The desktop's own sizes.
    const s = css(composer.element);
    expect([s.paddingTop, s.paddingRight, s.paddingBottom, s.paddingLeft]).toEqual(['12px', '12px', '8px', '16px']);
    expect(css(bar.get('[data-test=composer-team]').element).fontSize).toBe('13px');
    expect(css(composer.get('[data-test=composer-text]').element).fontSize).toBe('15px');
    expect(css(bar.element).marginTop).toBe('4px');
  });
});
