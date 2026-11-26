// @vitest-environment happy-dom
// Leaving a team page on a phone. The drawer's "Team <name>" group goes when the team
// page is left for the Teams list (from a team page, not only before one opens) and for the Monitor / Documents pages;
// it follows the open team when another team's page opens; widening to the desktop drops it and brings the header's
// Documents and Deliverables back. A fake matchMedia gives the width; the API is a scripted fake (no core, no network).
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { nextTick } from 'vue';
import App from '../../src/renderer/src/App.vue';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';
import { PHONE_BREAKPOINT, PHONE_QUERY } from '../../src/renderer/src/phone';
import type { TeamSummary, YawbleApi } from '../../src/shared/api';

type Listener = (e: { matches: boolean }) => void;
const screen = { width: 1280, listeners: new Set<Listener>() };
const isPhoneWidth = () => screen.width < PHONE_BREAKPOINT;
function fakeMatchMedia(query: string) {
  return {
    get matches() { return query === PHONE_QUERY ? isPhoneWidth() : false; },
    media: query,
    addEventListener: (_: string, l: Listener) => { if (query === PHONE_QUERY) screen.listeners.add(l); },
    removeEventListener: (_: string, l: Listener) => { screen.listeners.delete(l); },
  };
}
function setWidth(width: number) {
  const before = isPhoneWidth();
  screen.width = width;
  if (before !== isPhoneWidth()) for (const l of screen.listeners) l({ matches: isPhoneWidth() });
}

beforeEach(() => {
  screen.listeners.clear();
  screen.width = 1280;
  Object.defineProperty(window, 'matchMedia', { configurable: true, writable: true, value: fakeMatchMedia });
  try { localStorage.clear(); sessionStorage.clear(); } catch { /* none */ }
  applyRemoteAccess('full');
});
let wrappers: VueWrapper[] = [];
afterEach(() => { for (const w of wrappers) w.unmount(); wrappers = []; document.body.innerHTML = ''; });

const teams = [
  { id: 1, name: 'payments', repoPath: null, members: 2, running: 0, queued: 0 },
  { id: 2, name: 'search', repoPath: null, members: 1, running: 0, queued: 0 },
] as TeamSummary[];
const core: Record<string, () => unknown> = {
  'app.overview': () => ({ slots: { running: 0, limit: 4, queued: 0 }, teams }),
  'link.status': () => ({ state: 'off', viewers: [] }),
  'agents.detect': () => [],
  'concierge.list': () => [],
};
function fakeApi(): YawbleApi {
  return {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string) => (core[method] ? core[method]!() : []) as any,
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  } as YawbleApi;
}
async function mountApp() {
  const w = mount(App, { props: { api: fakeApi(), pollMs: 60_000 }, attachTo: document.body });
  wrappers.push(w);
  await flushPromises();
  await flushPromises();
  return w;
}
/** Opens the n-th team's page from the drawer (the drawer closes on going there). */
async function openTeam(w: VueWrapper, n: number) {
  await w.get('[data-test=drawer-toggle]').trigger('click');
  await w.findAll('[data-test=team-item]')[n]!.trigger('click');
  await flushPromises();
  expect(w.find('[data-test=team-title]').exists()).toBe(true);
}
/** Opens the drawer, says whether the team group is there, and closes it again. */
async function groupInDrawer(w: VueWrapper): Promise<string | null> {
  await w.get('[data-test=drawer-toggle]').trigger('click');
  const g = w.find('[data-test=drawer-team]');
  const name = g.exists() ? g.get('.team-name').text() : null;
  await w.get('[data-test=drawer-toggle]').trigger('click');
  return name;
}

describe('the drawer team group follows the open team page', () => {
  it('goes when the team page is left for the Teams list, the Monitor or Documents', async () => {
    setWidth(375);
    const w = await mountApp();
    for (const place of ['nav-teams', 'nav-monitor', 'nav-documents']) {
      await openTeam(w, 0);
      expect(await groupInDrawer(w)).toBe('Team payments');
      await w.get('[data-test=drawer-toggle]').trigger('click');
      await w.get(`[data-test=${place}]`).trigger('click');
      await flushPromises();
      expect(w.find('[data-test=team-title]').exists(), place).toBe(false);
      expect(await groupInDrawer(w), place).toBeNull();
    }
  });

  it('names the team now open when another team page opens, and its items open that team\'s dialog', async () => {
    setWidth(375);
    const w = await mountApp();
    await openTeam(w, 0);
    await openTeam(w, 1);
    expect(await groupInDrawer(w)).toBe('Team search');
    await w.get('[data-test=drawer-toggle]').trigger('click');
    await w.get('[data-test=drawer-team-documents]').trigger('click');
    await flushPromises();
    expect([...document.querySelectorAll('[role=dialog]')].map((d) => d.getAttribute('aria-label'))).toEqual(['search · documents']);
  });

  it('widening to the desktop with a team page open: no group, the header has Documents and Deliverables again', async () => {
    setWidth(375);
    const w = await mountApp();
    await openTeam(w, 0);
    expect(w.find('[data-test=open-documents]').exists()).toBe(false);
    expect(w.find('[data-test=open-deliverables]').exists()).toBe(false);
    setWidth(1280);
    await nextTick();
    await flushPromises();
    expect(w.find('[data-test=drawer-team]').exists()).toBe(false);
    expect(w.find('[data-test=open-documents]').text()).toBe('Documents');
    expect(w.find('[data-test=open-deliverables]').text()).toBe('Deliverables');
    setWidth(375);
    await nextTick();
    expect(w.find('[data-test=open-documents]').exists()).toBe(false);
  });
});
