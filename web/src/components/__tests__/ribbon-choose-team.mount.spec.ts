// @vitest-environment happy-dom
//
// CHOOSE TEAM OPENS THE TEAM, MOUNTED. Picked from the ribbon while the Teams list or the Kanban is
// on screen, the team's board is shown, not only its tab added: the ribbon used to make the team
// active and leave the list on screen whenever no team's board was showing.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';

import RibbonBar from '../RibbonBar.vue';
import RibbonMobileMenu from '../RibbonMobileMenu.vue';
import { useConsoleStore } from '../../stores/console';
import '../../test/mountQuasar';

const teams = [
  { id: 'alpha', name: 'Alpha', containers: [] },
  { id: 'beta', name: 'Beta', containers: [] },
];

beforeEach(() => {
  localStorage.clear();
  setActivePinia(createPinia());
  // Every pull the switch starts answers nothing; the view and the active team are what is checked.
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('no server in this test')));
  // Wide enough that every command sits on the strip.
  vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockImplementation(function (this: HTMLElement) {
    return this.classList.contains('ribbon') ? 100_000 : 0;
  });
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  document.body.innerHTML = '';
});

function onTheList(view: 'teams' | 'kanban') {
  const board = useConsoleStore();
  board.$patch({ teams: teams as never, view, activeTeamId: '', openTeamTabs: [] });
  return board;
}

describe('Choose Team on the ribbon', () => {
  it.each(['teams', 'kanban'] as const)('opens the picked team on its board from the %s view', async (view) => {
    const board = onTheList(view);
    const ribbon = mount(RibbonBar, { attachTo: document.body });
    await flushPromises();

    const chooser = [...(ribbon.element as HTMLElement).querySelectorAll<HTMLElement>('.q-btn-dropdown')]
      .find((button) => button.textContent?.includes('Choose Team'));
    if (!chooser) throw new Error('no Choose Team on the ribbon');
    chooser.click();
    await flushPromises();

    const alpha = [...document.body.querySelectorAll<HTMLElement>('.q-menu .q-item')]
      .find((item) => item.textContent?.includes('Alpha'));
    if (!alpha) throw new Error('Alpha is not offered');
    alpha.click();
    await flushPromises();

    expect(board.activeTeamId).toBe('alpha');
    expect(board.view).toBe('board');
    // The menu closes after the pick: unmounting mid-animation leaves Quasar's cancelled animation
    // as an unhandled rejection, which fails the run though every test passed.
    await vi.waitFor(() => {
      if (document.body.querySelector('.q-menu')) throw new Error('the menu is still closing');
    });
    ribbon.unmount();
  });

  it('opens the picked team on its board from the phone menu', async () => {
    const board = onTheList('teams');
    const menu = mount(RibbonMobileMenu, { attachTo: document.body });
    await flushPromises();

    const chooser = [...(menu.element as HTMLElement).querySelectorAll<HTMLElement>('.q-expansion-item .q-item')]
      .find((item) => item.textContent?.includes('Choose Team'));
    if (!chooser) throw new Error('no Choose Team in the menu');
    chooser.click();
    await flushPromises();

    const alpha = [...(menu.element as HTMLElement).querySelectorAll<HTMLElement>('.q-item')]
      .find((item) => item.textContent?.trim().endsWith('Alpha'));
    if (!alpha) throw new Error('Alpha is not offered');
    alpha.click();
    await flushPromises();

    expect(board.activeTeamId).toBe('alpha');
    expect(board.view).toBe('board');
    // Choose Team slides open: unmounting mid-slide cancels the animation, an unhandled rejection
    // that fails the run though every test passed.
    await vi.waitFor(() => {
      const sliding = [...document.body.querySelectorAll('*')]
        .some((element) => element.getAnimations().some((animation) => animation.playState === 'running'));
      if (sliding) throw new Error('still sliding');
    });
    menu.unmount();
  });
});
