// @vitest-environment happy-dom
//
// MARKETPLACE IS AN ADDRESS, `#/solutions`, AND THE RIBBON'S BUTTON GOES THERE - from the row at a
// desktop width, and from the overflow (...) menu when the row is too narrow to hold it.
//
// THE REAL MainLayout IS MOUNTED with a router that has both addresses, and the CONTROL IS CLICKED:
// emitting `action` from RibbonBar would skip the very wiring a person's click goes through.
//
// happy-dom lays nothing out, so widths are STATED as in ribbon-command-bar.mount.spec: each command
// is 100px wide, the overflow button 40px, and the bar as wide as the case says.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { createMemoryHistory, createRouter } from 'vue-router';
import MainLayout from '../MainLayout.vue';
import { useConsoleStore } from '../../stores/console';
import { Ribbon, ribbonEntries } from '../../lib/ribbon';
import { resetBody } from '../../test/mountQuasar';

const EntryWidth = 100;
const OverflowWidth = 40;
const entries = ribbonEntries(Ribbon);

let barWidth = 0;

function router() {
  const page = { template: '<div />' };
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/console', component: page },
      { path: '/solutions', component: page },
      { path: '/:pathMatch(.*)*', component: page },
    ],
  });
}

async function mountAt(width: number) {
  barWidth = width;
  setActivePinia(createPinia());
  useConsoleStore().$patch({ teams: [], activeTeamId: '', overviewLanded: true });

  const r = router();
  await r.push('/console');

  const wrapper = mount(MainLayout, { attachTo: document.body, global: { plugins: [r] } });
  await flushPromises();

  return { wrapper, r };
}

beforeEach(() => {
  // Anything the shell reads stays pending rather than reaching for a server.
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));

  vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockImplementation(function (this: HTMLElement) {
    return this.classList.contains('ribbon') ? barWidth : 0;
  });

  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
    const box = (right: number) => ({ left: 0, right, top: 0, bottom: 0, width: right, height: 0, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect;
    if (this.classList.contains('ribbon-overflow')) return box(OverflowWidth);

    const index = this.classList.contains('ribbon-entry') ? [...(this.parentElement?.children ?? [])].indexOf(this) : -1;
    return box(index < 0 ? 0 : (index + 1) * EntryWidth);
  });
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  resetBody();
});

describe('the ribbon\'s Marketplace', () => {
  it('opens #/solutions from the row at a desktop width', async () => {
    const { wrapper, r } = await mountAt(entries.length * EntryWidth);

    const onRow = wrapper.findAll('.ribbon-band .ribbon-entry')
      .filter((entry) => (entry.element as HTMLElement).style.display !== 'none')
      .flatMap((entry) => entry.findAll('button'))
      .filter((button) => button.text().includes('Marketplace'));
    expect(onRow, 'Marketplace is not on the row at a desktop width').toHaveLength(1);

    await onRow[0].trigger('click');
    await flushPromises();

    expect(r.currentRoute.value.path).toBe('/solutions');
    wrapper.unmount();
  });

  it('opens #/solutions from the overflow (...) menu when the row is too narrow to hold it', async () => {
    // 140 wide: 100 once the overflow button is reserved, so only New Team stays and Marketplace spills.
    const { wrapper, r } = await mountAt(EntryWidth + OverflowWidth);

    const onRow = wrapper.findAll('.ribbon-band .ribbon-entry')
      .filter((entry) => (entry.element as HTMLElement).style.display !== 'none');
    expect(onRow.map((entry) => entry.text()).join(' ')).not.toContain('Marketplace');

    await wrapper.find('[aria-label="More commands"]').trigger('click');
    await flushPromises();

    const item = [...document.body.querySelectorAll('.ribbon-overflow-menu .q-item')]
      .find((el) => el.textContent?.includes('Marketplace'));
    expect(item, 'Marketplace is not in the overflow menu').toBeDefined();

    (item as HTMLElement).click();
    await flushPromises();

    expect(r.currentRoute.value.path).toBe('/solutions');
    wrapper.unmount();
  });
});
