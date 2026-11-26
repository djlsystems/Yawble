// @vitest-environment happy-dom
// On a phone the team page's ⋯ menu lists Documents, Deliverables, Triggers, Git and Settings, in that order,
// each with an icon of the same size (no emoji); Documents opens its dialog, Deliverables the Deliverables page filtered to the team,
// and both close the menu. The
// menu stays on screen at 375 px and sits above the member panes. The desktop header is unchanged.
// A fake matchMedia gives the width; the API is a scripted fake (no core, no network).
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import TeamView from '../../src/renderer/src/components/TeamView.vue';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';
import { deliverablesRequest } from '../../src/renderer/src/deliverables/page';
import { PHONE_BREAKPOINT, PHONE_QUERY } from '../../src/renderer/src/phone';
import type { TeamSummary, YawbleApi } from '../../src/shared/api';

let width = 375;
beforeEach(() => {
  width = 375;
  Object.defineProperty(window, 'matchMedia', {
    configurable: true, writable: true,
    value: (query: string) => ({
      get matches() { return query === PHONE_QUERY ? width < PHONE_BREAKPOINT : false; },
      media: query, addEventListener: () => {}, removeEventListener: () => {},
    }),
  });
  try { localStorage.clear(); sessionStorage.clear(); } catch { /* none */ }
  applyRemoteAccess('full');
});
let wrappers: VueWrapper[] = [];
afterEach(() => { for (const w of wrappers) w.unmount(); wrappers = []; deliverablesRequest.value = null; document.body.innerHTML = ''; });

const team = { id: 1, name: 'payments', repoPath: null, members: 2, running: 0, queued: 0 } as TeamSummary;
function fakeApi(): YawbleApi {
  return {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async () => [] as any,
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  } as YawbleApi;
}
async function mountTeam() {
  const w = mount(TeamView, { props: { team, api: fakeApi(), pollMs: 60_000 }, attachTo: document.body });
  wrappers.push(w);
  await flushPromises();
  return w;
}
const dialogTitles = () => [...document.querySelectorAll('[role=dialog]')].map((d) => d.getAttribute('aria-label'));
const items = (w: VueWrapper) => w.findAll('[data-test=team-more-menu] [role=menuitem]');

describe('the team ⋯ menu on a phone', () => {
  it('lists Documents, Deliverables, Triggers, Git and Settings in that order, each with a drawn icon of one size and no emoji', async () => {
    const w = await mountTeam();
    await w.get('[data-test=team-more]').trigger('click');
    expect(items(w).map((b) => b.text())).toEqual(['Documents', 'Deliverables', 'Triggers', 'Git', 'Settings']);
    expect(items(w).map((b) => b.attributes('data-test'))).toEqual(['open-documents-menu', 'open-deliverables-menu', 'open-triggers', 'open-git', 'open-manage']);
    for (const b of items(w)) {
      const svgs = b.findAll('svg');
      expect(svgs.length, b.text()).toBe(1);
      expect(b.element.firstElementChild?.tagName.toLowerCase(), b.text()).toBe('svg'); // the icon leads, left-aligned
      expect(svgs[0]!.attributes('width'), b.text()).toBe('18');
      expect(svgs[0]!.get('path').attributes('d'), b.text()).not.toBe(''); // a real icon, not a missing name
    }
    expect(items(w).map((b) => b.text()).join('')).not.toMatch(/[⏰⎇]/u);
    // The same icons as the drawer's Team group for Documents and Deliverables.
    const d = (i: number) => items(w)[i]!.get('path').attributes('d');
    const icon = readFileSync(resolve(__dirname, '../../src/renderer/src/components/Icon.vue'), 'utf8');
    expect(icon).toContain(`docs: '${d(0)}'`);
    expect(icon).toContain(`deliverable: '${d(1)}'`);
  });

  it('Documents closes the menu and opens its dialog; Deliverables closes it and opens the Deliverables page for the team, no dialog', async () => {
    const w = await mountTeam();
    await w.get('[data-test=team-more]').trigger('click');
    await w.get('[data-test=open-documents-menu]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=team-more-menu]').exists()).toBe(false);
    expect(dialogTitles()).toEqual(['payments · documents']);
    await w.get('[data-test=modal-close]').trigger('click');
    expect(dialogTitles()).toEqual([]);

    await w.get('[data-test=team-more]').trigger('click');
    await w.get('[data-test=open-deliverables-menu]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=team-more-menu]').exists()).toBe(false);
    expect(dialogTitles()).toEqual([]);
    expect(deliverablesRequest.value).toMatchObject({ teamId: 1 });
    expect(deliverablesRequest.value?.deliverable ?? null).toBeNull();  });

  it('the Deliverables item is still labelled Deliverables and its tooltip says it opens the Deliverables page', async () => {
    const w = await mountTeam();
    await w.get('[data-test=team-more]').trigger('click');
    const b = w.get('[data-test=open-deliverables-menu]');
    expect(b.text()).toBe('Deliverables');
    expect(b.attributes('title')).toMatch(/Deliverables page/);
  });

  it('the menu CSS: 44 px rows with one gap, left-aligned, kept on screen and above the member panes', () => {
    const src = readFileSync(resolve(__dirname, '../../src/renderer/src/components/TeamView.vue'), 'utf8');
    const phone = src.slice(src.indexOf('@media (max-width: 759.98px)'));
    const menu = /\n\s*\.menu \{([^}]*)\}/.exec(phone)?.[1] ?? '';
    const button = /\n\s*\.menu button \{([^}]*)\}/.exec(phone)?.[1] ?? '';
    expect(menu).toMatch(/right: 0;/); // opens leftward from the ⋯ at the right edge
    expect(menu).toMatch(/max-width: calc\(100vw - 16px\)/);
    expect(menu).toMatch(/max-height: calc\(100dvh - \d+px\); overflow-y: auto/);
    expect(Number(/z-index: (\d+)/.exec(menu)?.[1])).toBeGreaterThan(5); // MemberPane's own menus and grips are 1-5
    expect(button).toMatch(/height: 44px;/);
    expect(button).toMatch(/gap: 10px;/);
    expect(button).toMatch(/justify-content: flex-start;/);
    expect(button).toMatch(/white-space: nowrap;/);
  });

  it('the desktop header is unchanged: five buttons, no ⋯', async () => {
    width = 1280;
    const w = await mountTeam();
    expect(w.find('[data-test=team-more]').exists()).toBe(false);
    expect(w.findAll('.teamhead .actions > button').map((b) => b.attributes('data-test')))
      .toEqual(['open-documents', 'open-deliverables', 'open-triggers', 'open-git', 'open-manage']);
  });
});
