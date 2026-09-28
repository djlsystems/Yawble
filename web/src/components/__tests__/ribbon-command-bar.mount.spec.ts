// @vitest-environment happy-dom
//
// THE RIBBON IS ONE ROW, AND WHAT DOES NOT FIT IS IN A MENU.
//
// happy-dom lays nothing out, so every width here is STATED: each command is 100px wide and the bar
// is as wide as the case says. What is under test is the component's use of those numbers - which
// commands stay on the strip, whether the overflow button exists, and whether a command reached
// through the menu does what it does on the strip.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

vi.mock('../../lib/useAgentInstallations', () => ({
  useAgentInstallations: () => ({ badge: { value: null } }),
  refreshAgentInstallations: vi.fn(),
}));

import RibbonBar from '../RibbonBar.vue';
import RibbonMobileMenu from '../RibbonMobileMenu.vue';
import { DocumentsAction, PluginsAction, Ribbon, SitesAction, TenantSettingsAction, ribbonEntries } from '../../lib/ribbon';
import '../../test/mountQuasar';

const EntryWidth = 100;
const entries = ribbonEntries(Ribbon);

let barWidth = 0;

beforeEach(() => {
  setActivePinia(createPinia());

  vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockImplementation(function (this: HTMLElement) {
    return this.classList.contains('ribbon') ? barWidth : 0;
  });

  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
    const index = this.classList.contains('ribbon-entry')
      ? [...(this.parentElement?.children ?? [])].indexOf(this)
      : -1;
    const right = index < 0 ? 0 : (index + 1) * EntryWidth;

    return { left: 0, right, top: 0, bottom: 0, width: right, height: 0, x: 0, y: 0, toJSON: () => ({}) } as DOMRect;
  });
});

afterEach(() => {
  vi.restoreAllMocks();
  document.body.innerHTML = '';
});

async function render(width: number): Promise<VueWrapper> {
  barWidth = width;

  const wrapper = mount(RibbonBar, { attachTo: document.body });
  await flushPromises();

  return wrapper as VueWrapper;
}

function visible(wrapper: VueWrapper) {
  return wrapper.findAll('.ribbon-entry').filter((entry) => (entry.element as HTMLElement).style.display !== 'none');
}

describe('the command bar', () => {
  it('shows every command and no overflow button when they all fit', async () => {
    const wrapper = await render(entries.length * EntryWidth);

    expect(visible(wrapper)).toHaveLength(entries.length);
    expect(wrapper.find('[aria-label="More commands"]').exists()).toBe(false);
  });

  it('spills the tail into an overflow button at a narrow width, keeping room for the button', async () => {
    // 350 wide: 310 once the 40px button is reserved, so three 100px commands stay.
    const wrapper = await render(350);

    expect(visible(wrapper)).toHaveLength(3);
    expect(wrapper.find('[aria-label="More commands"]').exists()).toBe(true);
  });

  it('lists the spilled commands in the menu under their tab names, and runs them from there', async () => {
    const wrapper = await render(350);

    await wrapper.find('[aria-label="More commands"]').trigger('click');
    await flushPromises();

    const menu = document.body.querySelector('.ribbon-overflow-menu');
    expect(menu).not.toBeNull();

    const text = menu!.textContent ?? '';
    for (const entry of entries.slice(3)) {
      if (entry.item.label) expect(text).toContain(entry.item.label);
    }
    expect(text.toUpperCase()).toContain('ADMIN');

    const documents = [...menu!.querySelectorAll('.q-item')].find((el) => el.textContent?.includes('Documents'));
    (documents as HTMLElement).click();
    await flushPromises();

    expect(wrapper.emitted('action')).toEqual([[DocumentsAction]]);
  });
});

describe('the mobile drawer', () => {
  /** Same definition, so the same commands - every one the bar can show, the drawer lists. */
  it('lists every command the bar carries', async () => {
    const wrapper = mount(RibbonMobileMenu, { attachTo: document.body });
    await flushPromises();

    for (const entry of entries) {
      if (entry.item.label) expect(wrapper.text()).toContain(entry.item.label);
    }
  });
});

describe('the Admin group', () => {
  /** Plugins sits DIRECTLY before Settings, with the `extension` icon, and opens the Plugins screen. */
  it('has Plugins directly before Settings, and runs it from the bar', async () => {
    const wrapper = await render(entries.length * EntryWidth);

    const admin = entries.filter((entry) => entry.tab.id === 'admin').map((entry) => entry.item);
    const plugins = admin.findIndex((item) => item.action === PluginsAction);
    expect(plugins).toBeGreaterThan(-1);
    expect(admin[plugins + 1]?.action).toBe(TenantSettingsAction);
    expect(admin[plugins]).toMatchObject({ label: 'Plugins', icon: 'extension' });

    // As rendered: the button labelled Plugins is the one before the Admin group's Settings.
    const labels = wrapper.findAll('.ribbon-entry').map((entry) => {
      const block = entry.find('.block');
      return block.exists() ? block.text().trim() : '';
    });
    const rendered = labels.lastIndexOf('Plugins');
    expect(rendered).toBeGreaterThan(-1);
    expect(labels[rendered + 1]).toBe('Settings');
    expect(wrapper.findAll('.ribbon-entry')[rendered]!.html()).toContain('extension');

    await wrapper.findAll('.ribbon-entry')[rendered]!.find('button').trigger('click');
    expect(wrapper.emitted('action')).toEqual([[PluginsAction]]);
  });

  /** Sites sits in the Admin group before Settings, and opens the Sites screen. */
  it('has Sites before Settings, and runs it from the bar', async () => {
    const wrapper = await render(entries.length * EntryWidth);

    const admin = entries.filter((entry) => entry.tab.id === 'admin').map((entry) => entry.item);
    const sites = admin.findIndex((item) => item.action === SitesAction);
    expect(sites).toBeGreaterThan(-1);
    expect(sites).toBeLessThan(admin.findIndex((item) => item.action === TenantSettingsAction));
    expect(admin[sites]).toMatchObject({ label: 'Sites', icon: 'public' });

    // As rendered: the LAST button labelled Sites is the Admin group's; Active Team has its own.
    const labels = wrapper.findAll('.ribbon-entry').map((entry) => {
      const block = entry.find('.block');
      return block.exists() ? block.text().trim() : '';
    });
    const rendered = labels.lastIndexOf('Sites');
    expect(rendered).toBeGreaterThan(-1);
    expect(rendered).toBeLessThan(labels.lastIndexOf('Settings'));

    await wrapper.findAll('.ribbon-entry')[rendered]!.find('button').trigger('click');
    expect(wrapper.emitted('action')).toEqual([[SitesAction]]);
  });
});
