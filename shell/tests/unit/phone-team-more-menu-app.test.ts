// @vitest-environment happy-dom
// The team page's ⋯ menu through the whole App at 375 px. Each of the five
// ⋯ items (Documents, Deliverables, Triggers, Git, Settings) closes the menu on the tap; Deliverables opens the Deliverables page
// filtered to the team, the others their own dialog, one at a time; the drawer's Team group still offers Documents (its
// dialog) and Deliverables (the page); widening to 1280 px
// shows the desktop header with no ⋯. A fake matchMedia gives the width; the API is a scripted fake.
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { nextTick } from 'vue';
import App from '../../src/renderer/src/App.vue';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';
import { PHONE_BREAKPOINT, PHONE_QUERY } from '../../src/renderer/src/phone';
import type { TeamSummary, YawbleApi } from '../../src/shared/api';

type Listener = (e: { matches: boolean }) => void;
const screen = { width: 375, listeners: new Set<Listener>() };
const isPhoneWidth = () => screen.width < PHONE_BREAKPOINT;
function setWidth(width: number) {
  const before = isPhoneWidth();
  screen.width = width;
  if (before !== isPhoneWidth()) for (const l of screen.listeners) l({ matches: isPhoneWidth() });
}
beforeEach(() => {
  screen.listeners.clear();
  screen.width = 375;
  Object.defineProperty(window, 'matchMedia', {
    configurable: true, writable: true,
    value: (query: string) => ({
      get matches() { return query === PHONE_QUERY ? isPhoneWidth() : false; },
      media: query,
      addEventListener: (_: string, l: Listener) => { if (query === PHONE_QUERY) screen.listeners.add(l); },
      removeEventListener: (_: string, l: Listener) => { screen.listeners.delete(l); },
    }),
  });
  try { localStorage.clear(); sessionStorage.clear(); } catch { /* none */ }
  history.replaceState(null, '', '/');
  applyRemoteAccess('full');
});
let wrappers: VueWrapper[] = [];
afterEach(() => { for (const w of wrappers) w.unmount(); wrappers = []; document.body.innerHTML = ''; });

const teams = [{ id: 1, name: 'payments', repoPath: null, members: 2, running: 0, queued: 0 }] as TeamSummary[];
const core: Record<string, () => unknown> = {
  'app.overview': () => ({ slots: { running: 0, limit: 4, queued: 0 }, teams }),
  'link.status': () => ({ state: 'off', viewers: [] }),
  'agents.detect': () => [],
  'concierge.list': () => [],
  'deliverables.list': () => ({ deliverables: [], counts: null, truncated: false, backlog: true, now: '2026-11-19T10:00:00Z' }),
  'git.status': () => ({
    repoPath: 'C:\\work\\payments', managed: false, baseBranch: 'main', checkedOut: 'main', dirty: false, origin: null,
    baseSha: 'aaaaaaaaaaaa', originBaseSha: null, baseAheadOfOrigin: 0, baseBehindOrigin: 0,
    teamBranch: 'team/payments', teamSha: 'bbbbbbbbbbbb', teamAheadOfBase: 0, teamBehindBase: 0,
    merge: 'up-to-date', conflictFiles: [], members: [],
  }),
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
async function openTeamPage() {
  const w = mount(App, { props: { api: fakeApi(), pollMs: 60_000 }, attachTo: document.body });
  wrappers.push(w);
  await flushPromises();
  await flushPromises();
  await w.get('[data-test=drawer-toggle]').trigger('click');
  await w.findAll('[data-test=team-item]')[0]!.trigger('click');
  await flushPromises();
  expect(w.find('[data-test=team-title]').exists()).toBe(true);
  return w;
}
const dialogTitles = () => [...document.querySelectorAll('[role=dialog]')].map((d) => d.getAttribute('aria-label'));
async function closeDialog(w: VueWrapper) {
  await w.get('[data-test=modal-close]').trigger('click');
  await flushPromises();
  expect(dialogTitles()).toEqual([]);
}

/** The Deliverables page is shown, filtered to team payments (id 1), and no dialog is open. */
async function onDeliverablesPage(w: VueWrapper, what: string) {
  await vi.waitFor(() => expect(w.find('[data-test=deliverables-view]').exists(), what).toBe(true));
  expect(w.find('[data-test=team-title]').exists(), what).toBe(false);
  expect((w.get('[data-test=deliverables-team-filter]').element as HTMLSelectElement).value, what).toBe('1');
  expect(dialogTitles(), what).toEqual([]);
}

const ITEMS: [string, string][] = [
  ['open-documents-menu', 'payments · documents'],
  ['open-triggers', 'Triggers · payments'],
  ['open-git', 'Git · payments'],
  ['open-manage', 'Settings · payments'],
];

describe('the team ⋯ menu in the App at 375 px', () => {
  it('each of the four dialog items closes the menu on the tap and opens its own dialog, one after another', async () => {
    const w = await openTeamPage();
    for (const [id, title] of ITEMS) {
      await w.get('[data-test=team-more]').trigger('click');
      expect(w.get('[data-test=team-more]').attributes('aria-expanded'), id).toBe('true');
      await w.get(`[data-test=team-more-menu] [data-test=${id}]`).trigger('click');
      await flushPromises();
      expect(w.find('[data-test=team-more-menu]').exists(), id).toBe(false);
      expect(w.get('[data-test=team-more]').attributes('aria-expanded'), id).toBe('false');
      expect(dialogTitles(), id).toEqual([title]);
      await closeDialog(w);
    }
  });

  it('Deliverables closes the menu on the tap and opens the Deliverables page filtered to the team', async () => {
    const w = await openTeamPage();
    await w.get('[data-test=team-more]').trigger('click');
    await w.get('[data-test=team-more-menu] [data-test=open-deliverables-menu]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=team-more-menu]').exists()).toBe(false);
    await onDeliverablesPage(w, 'open-deliverables-menu');
  });

  it('⋯ toggles the menu shut again without opening anything', async () => {
    const w = await openTeamPage();
    await w.get('[data-test=team-more]').trigger('click');
    expect(w.find('[data-test=team-more-menu]').exists()).toBe(true);
    await w.get('[data-test=team-more]').trigger('click');
    expect(w.find('[data-test=team-more-menu]').exists()).toBe(false);
    expect(dialogTitles()).toEqual([]);
  });

  it('the drawer Team group still offers Documents (its dialog) and Deliverables (the Deliverables page for the team)', async () => {
    const w = await openTeamPage();
    await w.get('[data-test=drawer-toggle]').trigger('click');
    expect(w.get('[data-test=drawer-team] .team-name').text()).toBe('Team payments');
    await w.get('[data-test=drawer-team-documents]').trigger('click');
    await flushPromises();
    expect(dialogTitles()).toEqual(['payments · documents']);
    await closeDialog(w);
    await w.get('[data-test=drawer-toggle]').trigger('click');
    expect(w.get('[data-test=drawer-team] .team-name').text()).toBe('Team payments');
    await w.get('[data-test=drawer-team-deliverables]').trigger('click');
    await flushPromises();
    await onDeliverablesPage(w, 'drawer-team-deliverables');
  });

  it('at 1280 px the header is the desktop one: five labelled buttons, no ⋯, no phone menu', async () => {
    const w = await openTeamPage();
    await w.get('[data-test=team-more]').trigger('click'); // leave the phone menu open, then widen
    setWidth(1280);
    await nextTick();
    await flushPromises();
    expect(w.find('[data-test=team-more]').exists()).toBe(false);
    expect(w.find('[data-test=team-more-menu]').exists()).toBe(false);
    expect(w.find('[data-test=team-phone-actions]').exists()).toBe(false);
    const buttons = w.findAll('.teamhead .actions > button');
    expect(buttons.map((b) => b.attributes('data-test'))).toEqual(['open-documents', 'open-deliverables', 'open-triggers', 'open-git', 'open-manage']);
    expect(buttons.map((b) => b.text())).toEqual(['Documents', 'Deliverables', '⏰ Triggers', '⎇ Git', 'Settings']);
    // and the desktop buttons still open the dialogs
    for (const [i, title] of [[2, 'Triggers · payments'], [3, 'Git · payments'], [4, 'Settings · payments']] as const) {
      await buttons[i]!.trigger('click');
      await flushPromises();
      expect(dialogTitles()).toEqual([title]);
      await closeDialog(w);
    }
  });
});
