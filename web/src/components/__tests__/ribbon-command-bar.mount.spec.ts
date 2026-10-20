// @vitest-environment happy-dom
//
// THE RIBBON IS ONE ROW, AND WHAT DOES NOT FIT IS IN A MENU.
//
// happy-dom lays nothing out, so every width here is STATED: each command is 100px wide and the bar
// is as wide as the case says. What is under test is the component's use of those numbers - which
// commands stay on the strip, whether the overflow button exists, and whether a command reached
// through the menu does what it does on the strip.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

vi.mock('../../lib/useAgentInstallations', () => ({
  useAgentInstallations: () => ({ badge: { value: null } }),
  refreshAgentInstallations: vi.fn(),
}));

import RibbonBar from '../RibbonBar.vue';
import RibbonMobileMenu from '../RibbonMobileMenu.vue';
import { ConnectionsAction, DocumentsAction, PluginsAction, Ribbon, SitesAction, TenantSettingsAction, ribbonEntries } from '../../lib/ribbon';
import { useConsoleStore } from '../../stores/console';
import type { TeamId } from '../../api/types';
import '../../test/mountQuasar';

const EntryWidth = 100;
const ribbonSource = readFileSync(join(import.meta.dirname, '..', 'RibbonBar.vue'), 'utf8');
const entries = ribbonEntries(Ribbon);

let barWidth = 0;
/** The right cluster's width (version, live chip, Needs you, gauge, account), when a case has one. */
let clusterWidth = 0;
/** The overflow button's box, as the browser would measure it (41.6px in Chromium today). */
let overflowWidth = 40;

beforeEach(() => {
  setActivePinia(createPinia());

  vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockImplementation(function (this: HTMLElement) {
    return this.classList.contains('ribbon') ? barWidth : 0;
  });

  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
    if (this.classList.contains('ribbon-overflow')) {
      return { left: 0, right: overflowWidth, top: 0, bottom: 0, width: overflowWidth, height: 0, x: 0, y: 0, toJSON: () => ({}) } as DOMRect;
    }

    if (this.classList.contains('ribbon-end')) {
      return { left: 0, right: clusterWidth, top: 0, bottom: 0, width: clusterWidth, height: 0, x: 0, y: 0, toJSON: () => ({}) } as DOMRect;
    }

    const index = this.classList.contains('ribbon-entry')
      ? [...(this.parentElement?.children ?? [])].indexOf(this)
      : -1;
    const right = index < 0 ? 0 : (index + 1) * EntryWidth;

    return { left: 0, right, top: 0, bottom: 0, width: right, height: 0, x: 0, y: 0, toJSON: () => ({}) } as DOMRect;
  });
});

afterEach(() => {
  clusterWidth = 0;
  overflowWidth = 40;
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
    // Above an open Concierge panel (7000), not behind it at a QMenu's 6000.
    expect(menu!.closest('.q-menu')?.classList.contains('above-concierge')).toBe(true);

    const text = menu!.textContent ?? '';
    for (const entry of entries.slice(3)) {
      if (entry.item.label) expect(text).toContain(entry.item.label);
    }
    expect(text.toUpperCase()).toContain('ADMIN');

    // The last: Active Team › Documents comes first and is gated by an active team, which this
    // spec does not set; Projects › Documents is the ungated one.
    const documents = [...menu!.querySelectorAll('.q-item')].filter((el) => el.textContent?.includes('Documents')).at(-1);
    (documents as HTMLElement).click();
    await flushPromises();

    expect(wrapper.emitted('action')).toEqual([[DocumentsAction]]);
  });
});

describe('the one header row', () => {
  async function withCluster(width: number, cluster: number): Promise<VueWrapper> {
    barWidth = width;
    clusterWidth = cluster;

    const wrapper = mount(RibbonBar, {
      attachTo: document.body,
      slots: {
        start: '<a data-test="logo">logo</a>',
        end: '<span data-test="cluster">cluster</span>',
      },
    });
    await flushPromises();

    return wrapper as VueWrapper;
  }

  /**
   * THE OVERFLOW COUNT RESERVES THE CLUSTER. A bar exactly as wide as every command fits them all
   * alone; with a 250px cluster at its end the same bar keeps only what fits beside it, the
   * overflow button's 40px reserved too: (1000 - 250 - 40) / 100 = 7.
   */
  it('fits the commands into the room the cluster leaves', async () => {
    const width = entries.length * EntryWidth;

    expect(visible(await withCluster(width, 0))).toHaveLength(entries.length);
    document.body.innerHTML = '';

    const narrow = await withCluster(1000, 250);
    expect(visible(narrow)).toHaveLength(7);
    expect(narrow.find('[aria-label="More commands"]').exists()).toBe(true);
  });

  /**
   * THE FIFTH GIVE-WAY STEP: `tight` halves each command's padding and the row is measured again,
   * against the cluster's width as it is THEN measured - a cluster that gave way leaves more room.
   */
  it('measures again when tight, reserving the cluster as it now measures', async () => {
    const wrapper = await withCluster(1000, 250);
    expect(visible(wrapper)).toHaveLength(7);
    expect(wrapper.find('.ribbon').classes()).not.toContain('ribbon--tight');
    const fits = wrapper.emitted('fit')!.length;

    clusterWidth = 150;
    await wrapper.setProps({ tight: true });
    await flushPromises();

    expect(wrapper.find('.ribbon').classes()).toContain('ribbon--tight');
    expect(wrapper.emitted('fit')!.length).toBeGreaterThan(fits);
    // (1000 - 150 - 40) / 100 = 8.
    expect(visible(wrapper)).toHaveLength(8);
    expect(wrapper.emitted('fit')!.at(-1)).toEqual([8]);
  });

  /**
   * THE RESERVE IS THE BUTTON'S MEASURED FOOTPRINT, never a constant: its box plus the margin before
   * it. A wider button leaves fewer commands on the row; the count follows the measurement.
   */
  it('reserves the overflow button as it measures, and the fit count follows its width', async () => {
    const wrapper = await withCluster(1000, 250);
    const button = wrapper.get('.ribbon-overflow').element as HTMLElement;
    const margin = parseFloat(getComputedStyle(button).marginLeft || '0');
    const expected = (reserve: number) => Math.floor((1000 - 250 - reserve) / EntryWidth);

    expect(visible(wrapper)).toHaveLength(expected(40 + margin));

    overflowWidth = 120;
    (wrapper.vm as unknown as { refit: () => void }).refit();
    await flushPromises();
    expect(expected(120 + margin)).toBeLessThan(expected(40 + margin));
    expect(visible(wrapper)).toHaveLength(expected(120 + margin));
    expect(wrapper.emitted('fit')!.at(-1)).toEqual([expected(120 + margin)]);

    overflowWidth = 41.6;
    (wrapper.vm as unknown as { refit: () => void }).refit();
    await flushPromises();
    expect(visible(wrapper)).toHaveLength(expected(41.6 + margin));
  });

  /** Kept for its measurement while nothing spills: hidden, unnamed and out of the tab order. */
  it('keeps the overflow button out of sight and out of reach while nothing spills', async () => {
    const wrapper = await withCluster(entries.length * EntryWidth, 0);
    const button = wrapper.get('.ribbon-overflow');

    expect(button.classes()).toContain('ribbon-overflow--idle');
    expect(button.attributes('aria-hidden')).toBe('true');
    expect(button.attributes('tabindex')).toBe('-1');
    expect(wrapper.find('[aria-label="More commands"]').exists()).toBe(false);
  });

  /**
   * STEPS FIVE AND SIX CAP THE ACTIVE TEAM'S TITLE at 3rem with an ellipsis; the full name stays its
   * aria-label and tooltip. Without `tight` the title keeps its 12rem room.
   */
  it('caps the active team title only when tight, the full name in its aria-label and tooltip', async () => {
    const name = 'A team whose name runs well past twelve rem';
    useConsoleStore().$patch({ teams: [{ id: 'long' as TeamId, name, memberAgents: [], repos: [] }], activeTeamId: 'long' as TeamId });

    const wrapper = await withCluster(1000, 250);
    const title = () => wrapper.get('[data-test="ribbon-group-title"]');
    const tooltip = async () => {
      const tip = title().findComponent({ name: 'QTooltip' });
      (tip.vm as unknown as { show: () => void }).show();
      await flushPromises();
      const text = document.body.querySelector('.q-tooltip')?.textContent ?? '';
      (tip.vm as unknown as { hide: () => void }).hide();
      await flushPromises();
      return text;
    };

    expect(title().classes()).not.toContain('ribbon-group-title--capped');
    expect(title().text()).toBe(name);
    expect(title().attributes('aria-label')).toBe(name);

    await wrapper.setProps({ tight: true });
    await flushPromises();

    expect(title().classes()).toContain('ribbon-group-title--capped');
    expect(ribbonSource).toMatch(/\.ribbon-group-title--capped\s*\{\s*max-width:\s*3rem;\s*\}/);
    expect(title().text()).toBe(name);
    expect(title().attributes('aria-label')).toBe(name);
    expect(await tooltip()).toContain(name);

    await wrapper.setProps({ tight: false });
    await flushPromises();
    expect(title().classes()).not.toContain('ribbon-group-title--capped');
  });

  it('puts the logo at the start and the cluster at the end, outside the commands toolbar', async () => {
    const wrapper = await withCluster(entries.length * EntryWidth, 0);
    const row = wrapper.find('.ribbon');
    const parts = row.findAll(':scope > div').map((part) => part.classes()[0]);

    expect(parts).toEqual(['ribbon-start', 'ribbon-commands', 'ribbon-end']);
    expect(row.find('.ribbon-start [data-test="logo"]').exists()).toBe(true);
    expect(row.find('.ribbon-end [data-test="cluster"]').exists()).toBe(true);
    expect(row.find('[role="toolbar"] [data-test="cluster"]').exists()).toBe(false);
  });

  /** The phone row: the logo and the cluster, no commands - the drawer lists them. */
  it('renders no commands when told the row is a phone row', async () => {
    barWidth = 390;
    const wrapper = mount(RibbonBar, { props: { commands: false }, slots: { end: '<span data-test="cluster" />' } });
    await flushPromises();

    expect(wrapper.findAll('.ribbon-entry')).toHaveLength(0);
    expect(wrapper.find('[role="toolbar"]').exists()).toBe(false);
    expect(wrapper.find('[data-test="cluster"]').exists()).toBe(true);
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
  /**
   * Plugins, then Connections DIRECTLY after it, then Settings: what is installed, the accounts it
   * acts on, how the instance is configured. Each opens its own screen from the bar.
   */
  it('has Plugins, then Connections, then Settings, and runs each from the bar', async () => {
    const wrapper = await render(entries.length * EntryWidth);

    const admin = entries.filter((entry) => entry.tab.id === 'admin').map((entry) => entry.item);
    const plugins = admin.findIndex((item) => item.action === PluginsAction);
    expect(plugins).toBeGreaterThan(-1);
    expect(admin[plugins + 1]?.action).toBe(ConnectionsAction);
    expect(admin[plugins + 2]?.action).toBe(TenantSettingsAction);
    expect(admin[plugins]).toMatchObject({ label: 'Plugins', icon: 'extension' });
    expect(admin[plugins + 1]).toMatchObject({ label: 'Connections', icon: 'link' });

    // As rendered: Plugins, Connections, then the Admin group's Settings.
    const labels = wrapper.findAll('.ribbon-entry').map((entry) => {
      const block = entry.find('.block');
      return block.exists() ? block.text().trim() : '';
    });
    const rendered = labels.lastIndexOf('Plugins');
    expect(rendered).toBeGreaterThan(-1);
    expect(labels[rendered + 1]).toBe('Connections');
    expect(labels[rendered + 2]).toBe('Settings');
    expect(wrapper.findAll('.ribbon-entry')[rendered]!.html()).toContain('extension');

    await wrapper.findAll('.ribbon-entry')[rendered]!.find('button').trigger('click');
    await wrapper.findAll('.ribbon-entry')[rendered + 1]!.find('button').trigger('click');
    expect(wrapper.emitted('action')).toEqual([[PluginsAction], [ConnectionsAction]]);
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
