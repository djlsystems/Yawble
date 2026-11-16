// @vitest-environment happy-dom
// B061 (card 286): a member's pane on a phone (375x501 portrait, 667x263 landscape): stacked - the title, a compact toolbar
// (Stop, Activity | Terminal and ⋯ with the rest), what was said, then the Activity or the terminal full width; 44 px tap
// targets; the terminal's transcript at least 60% of the visible height and on screen. At 1280 px: one frame, the two
// areas side by side, no taller than the card + activity row it replaces.
// happy-dom does no layout: the sizes are worked out from the components' own (phone / desktop) styles.
// Fakes only: matchMedia answers from a set width, the core is a scripted fake, the terminal screen a stand-in.
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import TeamView from '../../src/renderer/src/components/TeamView.vue';
import { OUTPUT_DEFAULT, setOutputHeight } from '../../src/renderer/src/agentOutputHeight';
import { focusMemberTerminal, resetMemberViews, setMemberView } from '../../src/renderer/src/memberPaneView';
import type { AgentCard, YawbleApi } from '../../src/shared/api';

function screen(width: number) {
  vi.stubGlobal('matchMedia', vi.fn((query: string) => {
    const max = /\(max-width:\s*([\d.]+)px\)/.exec(query);
    return { matches: max ? width <= Number(max[1]) : false, media: query, addEventListener: () => {}, removeEventListener: () => {} };
  }));
}
const Portrait = { width: 375, height: 501 };
const Landscape = { width: 667, height: 263 };
const Desktop = 1280;

const wrappers: VueWrapper[] = [];
afterEach(() => { while (wrappers.length) wrappers.pop()!.unmount(); vi.unstubAllGlobals(); document.body.innerHTML = ''; });
beforeEach(() => { localStorage.clear(); resetMemberViews(); setOutputHeight(OUTPUT_DEFAULT); });
const settle = async (n = 6) => { for (let i = 0; i < n; i++) await flushPromises(); };

const team = { id: 1, name: 'payments', repoPath: null, members: 2, running: 0, queued: 0 } as any;
const agent = (id: number, name: string): AgentCard => ({
  member: { id, teamId: 1, team: 'payments', name, role: id === 1 ? 'manager' : 'member', cli: 'claude', instructions: '', model: '', credential: 'default' },
  state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: 'Done.', runId: null, runStartedAt: null, lastRunState: null, lastRunReason: null,
  hasClone: true,
} as unknown as AgentCard);
const api = {
  getInfo: async () => { throw new Error('no'); }, getAvailableBackends: async () => [], onEvent: () => () => {},
  call: async (method: string, params?: any) => {
    const a: Record<string, () => unknown> = {
      'teams.agents': () => [agent(1, 'Manager'), agent(2, 'DeveloperAda')], 'bus.decisions': () => [], 'integrations.asks': () => [],
      'monitor.messages': () => ({ end: 0, messages: [] }), 'monitor.steps': () => ({ end: 0, pending: false, steps: [] }),
      'agentProfiles.list': () => [], 'bus.workflows': () => [], 'teams.syncToBase': () => ({ note: null, on: true, behind: null }),
      'terminal.banner': () => ({ memberId: params?.memberId, title: 't', state: 'idle', details: [], loosened: [], interactive: false }),
    };
    if (!a[method]) throw new Error(`unexpected ${method}`);
    return a[method]!();
  },
} as unknown as YawbleApi;
async function teamPage(width: number) {
  screen(width);
  const stubs = { GitDialog: true, TriggersDialog: true, TeamManageDialog: true, WorkflowsDialog: true, DocumentsDialog: true, LogsDialog: true, TerminalScreen: true };
  // In the document, as in the app: the phone terminal's bar teleports into its header's ⋯ menu by id.
  const w = mount(TeamView, { props: { team, api, pollMs: 100_000 }, attachTo: document.body, global: { stubs } });
  wrappers.push(w);
  await settle();
  return w;
}

// ---------------------------------------------------------------- the styles
const read = (p: string) => readFileSync(resolve(__dirname, '../../src/renderer/src', p), 'utf8').replace(/\r\n/g, '\n');
const PhoneBlock = /@media \(max-width: 759\.98px\) \{([\s\S]*?)\n\}/;
const NarrowBlock = /@media \(max-width: 900px\) \{([\s\S]*?)\n\}/;
const styleOf = (src: string) => /<style scoped>([\s\S]*?)<\/style>/.exec(src)?.[1] ?? '';
type Rule = { sels: string[]; decls: Record<string, string> };
function rules(css: string): Rule[] {
  const out: Rule[] = [];
  for (const m of css.replace(/\/\*[\s\S]*?\*\//g, '').matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const decls: Record<string, string> = {};
    for (const d of m[2]!.split(';')) { const i = d.indexOf(':'); if (i > 0) decls[d.slice(0, i).trim()] = d.slice(i + 1).trim(); }
    out.push({ sels: m[1]!.split(',').map((s) => s.trim().replace(/^@media[^{]*$/, '')).filter(Boolean), decls });
  }
  return out;
}
const phoneRules = (f: string) => rules(PhoneBlock.exec(styleOf(read(f)))?.[1] ?? '');
const desktopRules = (f: string) => rules(styleOf(read(f)).replace(PhoneBlock, '').replace(NarrowBlock, '').replace(/@media \(prefers-reduced-motion[^\n]*\n/, ''));
const matches = (el: Element, sel: string) => { try { return el.matches(sel.replace(/:deep\(([^)]*)\)/g, '$1')); } catch { return false; } };
/** The value of one of `props` for `el`: the last matching rule of the first set that has one. */
function value(el: Element, sets: Rule[][], ...props: string[]) {
  for (const set of sets) {
    let found: string | undefined;
    for (const r of set) if (r.sels.some((s) => matches(el, s))) for (const p of props) if (r.decls[p] !== undefined) found = r.decls[p];
    if (found !== undefined) return found;
  }
  return undefined;
}
const px = (v: string | undefined) => (v && /^-?[\d.]+px/.test(v) ? parseFloat(v) : NaN);

const paneOf = (w: VueWrapper, name: string) => w.get(`[data-test=agent-${name}]`);

for (const frame of [Portrait, Landscape]) {
  const at = `${frame.width}x${frame.height}`;
  describe(`a member's pane on a phone at ${at} (B061)`, () => {
    const st = [phoneRules('components/MemberPane.vue'), desktopRules('components/MemberPane.vue')];

    it('is stacked: title, toolbar, what was said, then the Activity, full width, with no resize handles', async () => {
      const w = await teamPage(frame.width);
      const p = paneOf(w, 'DeveloperAda');
      expect([...p.element.children].map((c) => c.getAttribute('data-test'))).toEqual(['agent-title', 'member-toolbar', 'member-pane-body']);
      const body = p.get('[data-test=member-pane-body]').element as HTMLElement;
      expect(value(body, st, 'flex-direction')).toBe('column');
      expect(body.style.height).toBe('');
      expect([...body.children].map((c) => c.getAttribute('data-test'))).toEqual(['member-said', 'member-side']);
      expect((body.children[1] as HTMLElement).style.flexBasis).toBe('');
      expect(p.find('.tile-grip').exists()).toBe(false);
    });

    it('the toolbar is compact: Stop, Activity | Terminal and ⋯ (the rest in its menu), each a 44 px target', async () => {
      const w = await teamPage(frame.width);
      const bar = paneOf(w, 'DeveloperAda').get('[data-test=member-toolbar]');
      const shown = bar.findAll('button').map((b) => b.attributes('data-test'));
      expect(shown).toEqual(['stop', 'member-view-activity', 'member-view-terminal', 'member-more']);
      for (const b of bar.findAll('button')) {
        expect(px(value(b.element, st, 'height', 'min-height')), b.attributes('data-test')).toBeGreaterThanOrEqual(44);
        expect(px(value(b.element, st, 'min-width')) || 44, b.attributes('data-test')).toBeGreaterThanOrEqual(44);
      }
      await bar.get('[data-test=member-more]').trigger('click');
      const menu = bar.get('[data-test=member-more-menu]');
      expect(menu.findAll('button').map((b) => b.attributes('data-test'))).toEqual(['open-interactive-cli', 'open-code', 'open-settings', 'open-logs', 'remove-agent']);
      for (const b of menu.findAll('button')) expect(px(value(b.element, st, 'min-height'))).toBeGreaterThanOrEqual(44);
      await menu.get('[data-test=open-settings]').trigger('click');
      expect(bar.find('[data-test=member-more-menu]').exists()).toBe(false);
    });

    // Card 307: B057's phone terminal followed it here - the whole screen, one 44 px header row (the full checks are in
    // phone-sheets.test.ts, with the real cascade).
    it("the terminal is the whole screen with one header row: its transcript is at least 60% of the visible height", async () => {
      setMemberView('payments', 'DeveloperAda', 'terminal');
      const w = await teamPage(frame.width);
      const side = paneOf(w, 'DeveloperAda').get('[data-test=member-side]');
      const term = side.get('[data-test=member-terminal]');
      expect(term.classes()).toContain('full');
      expect(term.find('.term-window').classes()).toContain('in-pane');
      // Over the page: fixed to every edge at the screen's height; its place in the stack takes no height.
      expect(value(term.element, st, 'position')).toBe('fixed');
      expect(value(term.element, st, 'inset')).toBe('0');
      expect(value(term.element, st, 'height')).toBe('100dvh');
      expect(value(side.element, st, 'height')).toBe('auto');
      // One 44 px header row: title, the tabs' slot, ⋯ and ✕; the terminal's bar (state, Open, Policy, Stop) in its ⋯.
      const head = term.get('[data-test=terminal-pane-head]');
      expect(px(value(head.element, st, 'height'))).toBe(44);
      const own = [...head.element.querySelectorAll(':scope > button')];
      expect(own.map((b) => b.getAttribute('data-test'))).toEqual(['terminal-pane-more', 'terminal-pane-close']);
      for (const b of own) expect(px(value(b, st, 'height', 'min-height'))).toBeGreaterThanOrEqual(44);
      const menu = term.get('[data-test=terminal-pane-menu]');
      for (const sel of ['terminal-state', 'open-interactive', 'terminal-details-toggle', 'terminal-stop'])
        expect(menu.find(`[data-test=${sel}]`).exists(), sel).toBe(true);
      const transcript = frame.height - 44;
      expect(transcript / frame.height, `${transcript} of ${frame.height}`).toBeGreaterThanOrEqual(0.6);
      // ✕: back to Activity.
      await head.get('[data-test=terminal-pane-close]').trigger('click');
      expect(paneOf(w, 'DeveloperAda').find('[data-test=member-terminal]').exists()).toBe(false);
      expect(paneOf(w, 'DeveloperAda').find('.member-activity').exists()).toBe(true);
    });
  });
}

describe("a member's pane at 1280 px (B061)", () => {
  it('one frame, the two areas side by side, the shared width and height, the full toolbar', async () => {
    const w = await teamPage(Desktop);
    const p = paneOf(w, 'DeveloperAda');
    const body = p.get('[data-test=member-pane-body]').element as HTMLElement;
    expect(body.style.height).toBe(`${OUTPUT_DEFAULT}px`);
    expect((body.children[1] as HTMLElement).style.flexBasis).toMatch(/px$/);
    expect(p.get('[data-test=member-toolbar]').findAll('button').map((b) => b.attributes('data-test')))
      .toEqual(['open-interactive-cli', 'open-code', 'open-settings', 'open-logs', 'stop', 'remove-agent', 'member-view-activity', 'member-view-terminal']);
    expect(p.find('[data-test=member-more]').exists()).toBe(false);
  });

  it('focusMemberTerminal: the pane shows the terminal and is scrolled into view, on mount or while the page is open', async () => {
    const scrolled: string[] = [];
    (Element.prototype as any).scrollIntoView ??= () => {};
    const spy = vi.spyOn(Element.prototype as any, 'scrollIntoView').mockImplementation(function (this: Element) { scrolled.push(this.getAttribute('data-test')!); });
    try {
      // Asked before the page opens (an entry point opens the team page next).
      focusMemberTerminal('payments', 'DeveloperAda');
      const w = await teamPage(Desktop);
      expect(scrolled).toEqual(['agent-DeveloperAda']);
      expect(paneOf(w, 'DeveloperAda').find('[data-test=member-terminal]').exists()).toBe(true);
      expect(paneOf(w, 'Manager').find('[data-test=member-terminal]').exists()).toBe(false);
      // Asked while it is open; another team's request is left for that team.
      focusMemberTerminal('other team', 'Someone');
      focusMemberTerminal('payments', 'Manager');
      await settle();
      expect(scrolled).toEqual(['agent-DeveloperAda', 'agent-Manager']);
      expect(paneOf(w, 'Manager').find('[data-test=member-terminal]').exists()).toBe(true);
    } finally { spy.mockRestore(); }
  });

  it("is no taller than the card + activity row it replaces, at the default heights", () => {
    // The old row was the card (AgentCard.vue at 8b39e07): 6 + 8 px padding, 2 px border, the 32 px title bar, the state line
    // (12.5 px x 1.5 + 4 px padding), 2 px above the output, and the output at the shared height; the Activity beside it was
    // 9rem (144 px) at least, less than the card.
    const before = 6 + 8 + 2 + 32 + Math.ceil(12.5 * 1.5 + 4) + 2 + OUTPUT_DEFAULT;
    const d = desktopRules('components/MemberPane.vue');
    const decl = (sel: string, prop: string) => d.filter((r) => r.sels.includes(sel)).map((r) => r.decls[prop]).filter(Boolean).pop();
    const [padTop, , padBottom] = decl('.pane', 'padding')!.split(/\s+/).map(px);
    const border = 2; // 1 px top and bottom (the 3 px is the left edge)
    const title = px(decl('.title', 'height'));
    const toolbar = px(decl('.toolbar', 'height')) + px(decl('.toolbar', 'margin-bottom'));
    const after = padTop! + padBottom! + border + title + toolbar + OUTPUT_DEFAULT;
    expect(after, `pane ${after} px, row ${before} px`).toBeLessThanOrEqual(before);
    // The body holds what was said and the right area at the shared height (the activity's own 9rem floor is lifted in it).
    expect(decl('.side > .member-activity', 'min-height')).toBe('0');
  });
});
