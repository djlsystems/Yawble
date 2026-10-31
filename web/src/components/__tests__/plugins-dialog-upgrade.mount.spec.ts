// @vitest-environment happy-dom
//
// ADMIN > PLUGINS, UPGRADE (PLG 2). The plugin's first tile shows an upgrade icon only when the
// Marketplace lists a newer version. It opens ONE confirm - the install press - warning that settings
// may not map and naming who will move and each solution team's minimum. Cancel changes nothing.
// Upgrade fetches and installs in one go; afterwards it says so, shows the secret moves, and each
// member whose settings need attention with its fields and a link to its settings. A refusal at either
// step shows the Host's sentence on the tile.
//
// SEEN TO FAIL: before PLG 2 the dialog read no catalog and offered no upgrade at all.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const {
  listPlugins,
  getMarketplace,
  fetchMarketplacePackage,
  installPlugin,
  getPluginSettings,
  getMember,
  listCatalog,
} = vi.hoisted(() => ({
  listPlugins: vi.fn(),
  getMarketplace: vi.fn(),
  fetchMarketplacePackage: vi.fn(),
  installPlugin: vi.fn(),
  getPluginSettings: vi.fn(),
  getMember: vi.fn(),
  listCatalog: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listPlugins,
  getMarketplace,
  fetchMarketplacePackage,
  installPlugin,
  getPluginSettings,
  getMember,
  listCatalog,
}));

vi.mock('../../api/documents', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listDocumentsRoot: vi.fn(async () => ({ folders: [], root: '/data/documents' })),
}));

import PluginsDialog from '../PluginsDialog.vue';
import { ActionRefused } from '../../api/client';
import { useConsoleStore } from '../../stores/console';
import {
  asMemberId,
  asTeamId,
  type ContainerSnapshot,
  type MarketplaceCatalog,
  type MarketplacePackage,
  type PluginInstallResult,
  type PluginList,
  type Team,
} from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostPlugin, hostSettings } from '../../test/pluginFixtures';
import { settle } from '../../test/formProbe';

const mail = hostPlugin({
  id: 'fixture-mail',
  name: 'Fixture Mail',
  version: '1.0.0',
  members: [{ team: 'alpha', member: 'Mailer' }, { team: 'beta', member: 'Inbox' }],
  solutions: [{ team: 'beta', teamName: 'Beta Team', packageId: 'inbox', package: 'Inbox', version: '2.0.0', minimum: '1.0.0' }],
});

const list: PluginList = {
  plugins: [mail],
  refused: [],
  versions: [{ id: 'fixture-mail', version: '1.0.0', name: 'Fixture Mail', active: true, verdict: 'installed', reason: null }],
};

function listed(fields: Partial<MarketplacePackage> = {}): MarketplacePackage {
  return {
    id: 'fixture-mail',
    name: 'Fixture Mail',
    kind: 'plugin',
    summary: 'Mail.',
    description: '',
    version: '1.1.0',
    needs: [],
    catalogNeeds: { connections: [], secrets: [], inputs: [], runtimes: [] },
    installed: true,
    installedVersion: '1.0.0',
    installedOn: [],
    updateAvailable: true,
    ...fields,
  };
}

const catalog = (packages: MarketplacePackage[], checked = true): MarketplaceCatalog =>
  ({ checked, reason: checked ? null : 'not yet', checkedAt: null, packages });

function installed(fields: Partial<PluginInstallResult> = {}): PluginInstallResult {
  return { id: 'fixture-mail', version: '1.1.0', installed: true, replaced: false, reason: null, previous: '1.0.0', moves: [], attention: [], ...fields };
}

const member = (team: string, id: string) => ({
  team: asTeamId(team), id: asMemberId(id), name: id, agent: 'plugin:fixture-mail', kind: 'plugin', state: 'Idle',
  queueDepth: 0, ceiling: 16, subscribes: [], currentCorrelation: null, sinceSeq: 0,
}) as unknown as ContainerSnapshot;

beforeEach(() => {
  for (const mock of [listPlugins, getMarketplace, fetchMarketplacePackage, installPlugin, getPluginSettings, getMember, listCatalog]) mock.mockReset();
  listPlugins.mockResolvedValue(list);
  getMarketplace.mockResolvedValue(catalog([listed()]));
  fetchMarketplacePackage.mockResolvedValue({ id: 'fixture-mail', version: '1.1.0', kind: 'plugin', folder: 'Marketplace/fixture-mail-1.1.0', alreadyThere: false });
  installPlugin.mockResolvedValue(installed());
  listCatalog.mockResolvedValue({ agents: [] });
  getPluginSettings.mockResolvedValue(hostSettings(mail, { team: 'alpha', member: 'Mailer' }));
});

afterEach(resetBody);

async function mountPlugins() {
  setActivePinia(createPinia());
  useConsoleStore().teams = [
    { id: asTeamId('alpha'), name: 'Alpha Team', memberAgents: ['claude'], containers: [member('alpha', 'Mailer')] } as unknown as Team,
    { id: asTeamId('beta'), name: 'Beta Team', memberAgents: ['claude'], containers: [member('beta', 'Inbox')] } as unknown as Team,
  ];
  const wrapper = await mountDialog(PluginsDialog, {}, { pinia: false });
  await settle();
  await settle();
  return wrapper;
}

const tile = () => bodyFind('[data-plugin="fixture-mail"]')!;
const icon = () => tile().querySelector('[data-plugin-upgrade]') as HTMLElement | null;

async function confirmUpgrade() {
  icon()!.click();
  await settle();
  (bodyFind('[data-upgrade-confirm]') as HTMLElement).click();
  await settle();
  await settle();
}

describe('PluginsDialog upgrade', () => {
  it('shows the upgrade icon, labelled with the version, only when the catalog lists a newer one', async () => {
    const wrapper = await mountPlugins();
    expect(icon()?.getAttribute('aria-label')).toBe('Upgrade to 1.1.0');
    expect(icon()?.querySelector('.q-icon')?.textContent).toBe('upgrade');
    wrapper.unmount();
  });

  it.each([
    ['the catalog lists the installed version', catalog([listed({ version: '1.0.0', updateAvailable: false })])],
    ['the catalog does not list the plugin', catalog([])],
    ['the catalog has not been checked', catalog([listed()], false)],
  ])('shows no icon when %s', async (_why, read) => {
    getMarketplace.mockResolvedValue(read);
    const wrapper = await mountPlugins();
    expect(tile()).not.toBeNull();
    expect(icon()).toBeNull();
    wrapper.unmount();
  });

  it('shows no icon when the catalog cannot be read', async () => {
    getMarketplace.mockRejectedValue(new Error('offline'));
    const wrapper = await mountPlugins();
    expect(icon()).toBeNull();
    wrapper.unmount();
  });

  it('asks first, with the warning, who will move and the solution minimums; Cancel changes nothing', async () => {
    const wrapper = await mountPlugins();
    icon()!.click();
    await settle();

    expect(bodyFind('[data-upgrade-title]')?.textContent?.trim()).toBe('Upgrade Fixture Mail to 1.1.0?');
    expect(bodyFind('[data-upgrade-warning]')?.textContent?.trim()).toBe(
      'Settings from the old version are not guaranteed to map to the new version. Are you sure you want to replace the installed plugin with the latest version?');
    expect(bodyFind('[data-upgrade-members]')?.textContent?.trim()).toBe('Used by alpha / Mailer, beta / Inbox');
    expect(bodyFind('[data-upgrade-solution="beta"]')?.textContent?.trim()).toBe('Beta Team (Inbox 2.0.0) needs 1.0.0 or newer');
    expect([...bodyFind('[data-plugin-upgrade-confirm]')!.querySelectorAll('.q-btn')].map((b) => b.textContent?.trim())).toEqual(['Cancel', 'Upgrade']);

    (bodyFind('[data-upgrade-cancel]') as HTMLElement).click();
    await settle();
    expect(bodyFind('[data-plugin-upgrade-confirm]')).toBeNull();
    expect(fetchMarketplacePackage).not.toHaveBeenCalled();
    expect(installPlugin).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it('Upgrade fetches and installs the fetched folder in one go, and says it is done with nothing to attend to', async () => {
    const wrapper = await mountPlugins();
    await confirmUpgrade();

    expect(fetchMarketplacePackage).toHaveBeenCalledWith('fixture-mail');
    expect(installPlugin).toHaveBeenCalledWith('/data/documents/Marketplace/fixture-mail-1.1.0', true);
    expect(bodyFind('[data-install-dialog]')).toBeNull();
    expect(bodyFind('[data-upgrade-done]')?.textContent?.trim()).toBe('Upgraded Fixture Mail to 1.1.0');
    expect(bodyText()).toContain('1.0.0 is kept on disk, inactive');
    expect(bodyFind('[data-upgrade-attention]')).toBeNull();
    expect(bodyFind('[data-upgrade-moves]')).toBeNull();
    expect(listPlugins.mock.calls.length).toBeGreaterThan(1);
    wrapper.unmount();
  });

  it('a fetch that answers alreadyThere installs that folder as it is', async () => {
    fetchMarketplacePackage.mockResolvedValue({ id: 'fixture-mail', version: '1.1.0', kind: 'plugin', folder: 'Marketplace/fixture-mail-1.1.0', alreadyThere: true });
    const wrapper = await mountPlugins();
    await confirmUpgrade();
    expect(installPlugin).toHaveBeenCalledWith('/data/documents/Marketplace/fixture-mail-1.1.0', true);
    expect(bodyFind('[data-upgrade-done]')).not.toBeNull();
    wrapper.unmount();
  });

  it('names each member whose settings need attention, with its fields and a link to its settings, and the secret moves', async () => {
    installPlugin.mockResolvedValue(installed({
      moves: [{ team: 'alpha', member: 'Mailer', slot: 'mailbox', outcome: 'moved', fields: ['password'], connection: 'conn-1', key: 'mailbox', reason: null }],
      attention: [
        { team: 'alpha', member: 'Mailer', teamName: 'Alpha Team', memberName: 'Mailer', fields: [
          { field: 'mode', reason: '`mode` has a saved value, and fixture-mail 1.1.0 no longer has this setting: the value is kept and not used.' },
          { field: 'region', reason: '`region` is required by fixture-mail 1.1.0, and this member has no value for it and the plugin gives no default.' },
        ] },
        { team: 'beta', member: 'Inbox', teamName: 'Beta Team', memberName: 'Inbox', fields: [
          { field: 'mode', reason: '`mode` has a saved value, and fixture-mail 1.1.0 no longer has this setting: the value is kept and not used.' },
          { field: 'region', reason: '`region` is required by fixture-mail 1.1.0, and this member has no value for it and the plugin gives no default.' },
        ] },
      ],
    }));
    const wrapper = await mountPlugins();
    await confirmUpgrade();

    const attention = bodyFind('[data-upgrade-attention]')!;
    expect([...attention.querySelectorAll('[data-attention-member]')].map((m) => m.getAttribute('data-attention-member'))).toEqual(['alpha/Mailer', 'beta/Inbox']);
    for (const who of ['alpha/Mailer', 'beta/Inbox']) {
      const entry = attention.querySelector(`[data-attention-member="${who}"]`)!;
      expect([...entry.querySelectorAll('[data-attention-field]')].map((f) => f.getAttribute('data-attention-field'))).toEqual(['mode', 'region']);
    }
    expect(attention.querySelector('[data-attention-link="alpha/Mailer"]')?.textContent?.trim()).toBe('Alpha Team / Mailer');
    expect(attention.textContent).toContain('required by fixture-mail 1.1.0');
    expect(bodyFind('[data-upgrade-move="alpha/Mailer"]')?.textContent).toContain('`password` moved into a connection on slot mailbox');

    (attention.querySelector('[data-attention-link="alpha/Mailer"]') as HTMLElement).click();
    await settle();
    await settle();
    expect(bodyText()).toContain('Member settings');
    expect(bodyFind('[data-member-plugin-settings]')).not.toBeNull();
    wrapper.unmount();
  });

  it('a fetch refusal shows its sentence on the tile and installs nothing', async () => {
    fetchMarketplacePackage.mockRejectedValue(new ActionRefused('The package could not be downloaded: 503.', {}));
    const wrapper = await mountPlugins();
    await confirmUpgrade();

    expect(installPlugin).not.toHaveBeenCalled();
    expect(bodyFind('[data-plugin-upgrade-confirm]')).toBeNull();
    expect(tile().querySelector('[data-upgrade-refused]')?.textContent).toContain('Fixture Mail was not fetched: The package could not be downloaded: 503');
    expect(tile().querySelector('[data-active]')).not.toBeNull();
    wrapper.unmount();
  });

  it('an install refusal shows the Host sentence on the tile and the installed version stays active', async () => {
    installPlugin.mockRejectedValue(new ActionRefused('fixture-mail 1.1.0 is older than Beta Team needs.', { installed: false }));
    const wrapper = await mountPlugins();
    await confirmUpgrade();

    expect(bodyFind('[data-upgrade-done]')).toBeNull();
    const refused = tile().querySelector('[data-upgrade-refused]')?.textContent ?? '';
    expect(refused).toContain('Fixture Mail 1.1.0 was not installed: fixture-mail 1.1.0 is older than Beta Team needs');
    expect(refused).toContain('The installed version is still active.');
    expect(tile().getAttribute('data-plugin-version')).toBe('1.0.0');
    expect(tile().querySelector('[data-active]')).not.toBeNull();
    wrapper.unmount();
  });
});
