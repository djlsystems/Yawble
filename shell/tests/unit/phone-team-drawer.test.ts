// @vitest-environment happy-dom
// Card 316: on a phone (below 760 px) the team page's Documents and Workflows move from its header into the drawer,
// under the open team's name; a tap closes the drawer and opens that team's dialog. The desktop keeps the header
// buttons. Also: a dialog sheet's header never shrinks over its body (the Workflows title sat on its intro text).
// A fake matchMedia gives the width; the API is a scripted fake (no core, no network).
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { nextTick } from 'vue';
import App from '../../src/renderer/src/App.vue';
import Sidebar from '../../src/renderer/src/components/Sidebar.vue';
import TeamView from '../../src/renderer/src/components/TeamView.vue';
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
function keep<T extends VueWrapper>(w: T): T { wrappers.push(w); return w; }

const team = { id: 1, name: 'payments', repoPath: null, members: 2, running: 0, queued: 0 } as TeamSummary;
const core: Record<string, () => unknown> = {
  'app.overview': () => ({ slots: { running: 0, limit: 4, queued: 0 }, teams: [team] }),
  'link.status': () => ({ state: 'off', viewers: [] }),
  'agents.detect': () => [],
  'concierge.list': () => [],
};
function fakeApi(): YawbleApi {
  return {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    // Anything the team page or a dialog asks for that the test does not care about: an empty list.
    call: async (method: string) => (core[method] ? core[method]!() : []) as any,
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  } as YawbleApi;
}

async function mountApp() {
  const w = keep(mount(App, { props: { api: fakeApi(), pollMs: 60_000 }, attachTo: document.body }));
  await flushPromises();
  await flushPromises();
  return w;
}
const drawerState = (w: VueWrapper) => w.get('[data-test=sidebar]').attributes('data-drawer');
const dialogTitles = () => [...document.querySelectorAll('[role=dialog]')].map((d) => d.getAttribute('aria-label'));

/** Opens the payments team's page from the drawer (the drawer closes on going there). */
async function openTeamPage(w: VueWrapper) {
  await w.get('[data-test=drawer-toggle]').trigger('click');
  await w.get('[data-test=team-item]').trigger('click');
  await flushPromises();
  expect(w.find('[data-test=team-title]').exists()).toBe(true);
  expect(drawerState(w)).toBe('closed');
}

describe('the drawer on a phone: the open team\'s Documents and Workflows (card 316)', () => {
  it('shows the two items only while a team page is open, under the team\'s name, near the drawer\'s top', async () => {
    setWidth(390);
    const w = await mountApp();
    await w.get('[data-test=drawer-toggle]').trigger('click');
    expect(w.find('[data-test=drawer-team-documents]').exists()).toBe(false); // the Concierge: no team open
    expect(w.find('[data-test=drawer-team-workflows]').exists()).toBe(false);
    await w.get('[data-test=nav-teams]').trigger('click');
    await flushPromises();
    await w.get('[data-test=drawer-toggle]').trigger('click');
    expect(w.find('[data-test=drawer-team]').exists()).toBe(false); // the teams list: still no team open
    await w.get('[data-test=sidebar-collapse]').trigger('click');

    await openTeamPage(w);
    await w.get('[data-test=drawer-toggle]').trigger('click');
    const group = w.get('[data-test=drawer-team]');
    expect(group.get('.team-name').text()).toBe('Team payments');
    expect(group.get('[data-test=drawer-team-documents]').text()).toBe('Documents');
    expect(group.get('[data-test=drawer-team-workflows]').text()).toBe('Workflows');
    // Near the top: right after New chat, before the places.
    const order = [...w.get('[data-test=sidebar]').element.querySelectorAll('[data-test]')].map((e) => e.getAttribute('data-test'));
    expect(order.indexOf('new-chat')).toBeLessThan(order.indexOf('drawer-team-documents'));
    expect(order.indexOf('drawer-team-workflows')).toBeLessThan(order.indexOf('nav-concierge'));

    // Going elsewhere (the Board): the team page is gone, and so are the items.
    await w.get('[data-test=nav-board]').trigger('click');
    await flushPromises();
    await w.get('[data-test=drawer-toggle]').trigger('click');
    expect(w.find('[data-test=drawer-team]').exists()).toBe(false);
  });

  it('a tap on Documents closes the drawer and opens the team\'s documents; Workflows its workflows', async () => {
    setWidth(390);
    const w = await mountApp();
    await openTeamPage(w);
    expect(dialogTitles()).toEqual([]);

    await w.get('[data-test=drawer-toggle]').trigger('click');
    await w.get('[data-test=drawer-team-documents]').trigger('click');
    await flushPromises();
    expect(drawerState(w)).toBe('closed');
    expect(dialogTitles()).toEqual(['payments · documents']);
    await w.get('[data-test=modal-close]').trigger('click');
    expect(dialogTitles()).toEqual([]);

    await w.get('[data-test=drawer-toggle]').trigger('click');
    await w.get('[data-test=drawer-team-workflows]').trigger('click');
    await flushPromises();
    expect(drawerState(w)).toBe('closed');
    expect(dialogTitles()).toEqual(['payments · workflows']);
    expect(document.querySelector('.modal')?.classList.contains('sheet')).toBe(true);
  });

  it('on the desktop: no team group in the sidebar; the header keeps both buttons', async () => {
    setWidth(1024);
    const w = await mountApp();
    await w.get('[data-test=team-item]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=drawer-team]').exists()).toBe(false);
    expect(w.find('[data-test=open-documents]').exists()).toBe(true);
    expect(w.find('[data-test=open-workflows]').exists()).toBe(true);
  });

  it('the Sidebar itself: the group needs the drawer and an open team', () => {
    const base = { view: 'teams' as const, conversations: [], teams: [team], conversationId: null, teamId: 1, slots: null, remote: false };
    expect(keep(mount(Sidebar, { props: { ...base, openTeamName: 'payments' } })).find('[data-test=drawer-team]').exists()).toBe(false);
    expect(keep(mount(Sidebar, { props: { ...base, drawer: true, open: true, openTeamName: null } })).find('[data-test=drawer-team]').exists()).toBe(false);
    const w = keep(mount(Sidebar, { props: { ...base, drawer: true, open: true, openTeamName: 'payments' } }));
    w.get('[data-test=drawer-team-documents]').trigger('click');
    w.get('[data-test=drawer-team-workflows]').trigger('click');
    expect(Object.keys(w.emitted())).toEqual(expect.arrayContaining(['teamDocuments', 'teamWorkflows']));
  });
});

describe('the team page header (card 316)', () => {
  const mountTeam = async () => {
    const w = keep(mount(TeamView, { props: { team, api: fakeApi(), pollMs: 60_000 }, attachTo: document.body }));
    await flushPromises();
    return w;
  };

  it('on a phone: no Documents or Workflows in the header; the rest stay behind ⋯ (B054); the exposed openers still open the dialogs', async () => {
    setWidth(390);
    const w = await mountTeam();
    expect(w.find('[data-test=open-documents]').exists()).toBe(false);
    expect(w.find('[data-test=open-workflows]').exists()).toBe(false);
    await w.get('[data-test=team-more]').trigger('click');
    expect(w.findAll('[data-test=team-more-menu] [role=menuitem]').map((b) => b.attributes('data-test'))).toEqual(['open-triggers', 'open-git', 'open-manage']);
    await w.get('[data-test=team-more]').trigger('click');
    (w.vm as unknown as { openDocuments: () => void }).openDocuments();
    await flushPromises();
    expect(dialogTitles()).toEqual(['payments · documents']);
  });

  it('on the desktop: both buttons, opening their dialogs; openWorkflows(id) still selects a workflow', async () => {
    setWidth(1024);
    const w = await mountTeam();
    await w.get('[data-test=open-documents]').trigger('click');
    await flushPromises();
    expect(dialogTitles()).toEqual(['payments · documents']);
    await w.get('[data-test=modal-close]').trigger('click');
    await w.get('[data-test=open-workflows]').trigger('click');
    await flushPromises();
    expect(dialogTitles()).toEqual(['payments · workflows']);
    await w.get('[data-test=modal-close]').trigger('click');
    (w.vm as unknown as { openWorkflows: (id?: number) => void }).openWorkflows(42);
    await nextTick();
    expect(dialogTitles()).toEqual(['payments · workflows']);
  });

  it('follows the width: the buttons go below 760 px and come back at 760 px', async () => {
    setWidth(1024);
    const w = await mountTeam();
    setWidth(759);
    await nextTick();
    expect(w.find('[data-test=open-workflows]').exists()).toBe(false);
    setWidth(760);
    await nextTick();
    expect(w.find('[data-test=open-workflows]').exists()).toBe(true);
  });
});

describe('a dialog sheet\'s header over its body (card 316, Modal.vue)', () => {
  const css = readFileSync(resolve(__dirname, '../../src/renderer/src/components/Modal.vue'), 'utf8');
  it('never shrinks, is opaque and is not sticky: the title sits above the body, never on it', () => {
    expect(css).toMatch(/\nheader \{[^}]*flex: none;[^}]*background: var\(--surface\)/);
    expect(css).toMatch(/\.modal\.sheet \{ overflow: hidden; \}/);
    expect(css).not.toMatch(/\.modal\.sheet header \{[^}]*sticky/);
  });
});
