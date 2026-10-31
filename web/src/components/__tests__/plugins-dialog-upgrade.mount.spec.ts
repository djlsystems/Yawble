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
//
// PLG 3: the rollback words name a folder the install accepts (never the kept copy under the plugins
// folder); a slot already bound says so, naming the connection, and "Secrets moved into connections"
// heads the moves only when something moved; field names render as code, no literal backtick; and the
// icon sits on the active version's tile, not an inactive older one listed first. SEEN TO FAIL on
// 234829f: the rollback line, already-bound, heading, code and active-tile tests below.
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
    expect(bodyFind('[data-upgrade-rollback]')?.textContent?.trim()).toBe(
      '1.0.0 is kept under the plugins folder, inactive. To roll back, install 1.0.0 again with Replace from the folder it was '
      + 'built into, or from Documents › Marketplace › fixture-mail-1.0.0 if it came from the Marketplace and that folder is still '
      + 'there. The copy under the plugins folder cannot be installed.');
    expect(bodyText()).not.toContain('kept on disk');
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
    expect(bodyFind('[data-upgrade-move="alpha/Mailer"]')?.textContent).toContain('password moved into a connection on slot mailbox');

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

  // ---- PLG 3 ---------------------------------------------------------------------------------------

  it('rolls back from a folder the install accepts: no rollback line on a first install', async () => {
    installPlugin.mockResolvedValue(installed({ previous: null }));
    const wrapper = await mountPlugins();
    await confirmUpgrade();
    expect(bodyFind('[data-upgrade-done]')).not.toBeNull();
    expect(bodyFind('[data-upgrade-rollback]')).toBeNull();
    wrapper.unmount();
  });

  it('a slot already bound says so naming the connection, with no "moved" heading', async () => {
    installPlugin.mockResolvedValue(installed({
      moves: [{
        team: 'alpha', member: 'Mailer', slot: 'mailbox', outcome: 'alreadyBound', fields: ['password'],
        connection: 'conn-1', connectionName: 'Alpha Team - Mailer', key: null, reason: null,
      }],
    }));
    const wrapper = await mountPlugins();
    await confirmUpgrade();

    const move = bodyFind('[data-upgrade-move="alpha/Mailer"]')!;
    expect(move.textContent?.trim()).toBe('alpha / Mailer: no value for password to move; slot mailbox is already bound to Alpha Team - Mailer.');
    expect(move.textContent).not.toContain('left for a person to bind');
    expect(bodyFind('[data-upgrade-moves-heading]')).toBeNull();
    expect(bodyText()).not.toContain('Secrets moved into connections');
    wrapper.unmount();
  });

  it('a first upgrade still says moved, under the heading; a slot left unbound alone has no heading', async () => {
    installPlugin.mockResolvedValue(installed({
      moves: [
        { team: 'alpha', member: 'Mailer', slot: 'mailbox', outcome: 'moved', fields: ['password'], connection: 'conn-1', connectionName: 'Alpha Team - Mailer', key: 'mailbox', reason: null },
        { team: 'beta', member: 'Inbox', slot: 'mailbox', outcome: 'nothing', fields: ['password'], connection: null, connectionName: null, key: null, reason: null },
      ],
    }));
    const wrapper = await mountPlugins();
    await confirmUpgrade();
    expect(bodyFind('[data-upgrade-moves-heading]')?.textContent?.trim()).toBe('Secrets moved into connections');
    expect(bodyFind('[data-upgrade-move="alpha/Mailer"]')?.textContent?.trim()).toBe('alpha / Mailer: password moved into a connection on slot mailbox.');
    expect(bodyFind('[data-upgrade-move="beta/Inbox"]')?.textContent?.trim()).toBe('beta / Inbox: no value for password, so slot mailbox is left for a person to bind.');
    wrapper.unmount();

    installPlugin.mockResolvedValue(installed({
      moves: [{ team: 'beta', member: 'Inbox', slot: 'mailbox', outcome: 'nothing', fields: ['password'], connection: null, connectionName: null, key: null, reason: null }],
    }));
    const again = await mountPlugins();
    await confirmUpgrade();
    expect(bodyFind('[data-upgrade-moves]')).not.toBeNull();
    expect(bodyFind('[data-upgrade-moves-heading]')).toBeNull();
    again.unmount();
  });

  it('shows field names as code, with no literal backtick, in the move and attention sentences', async () => {
    installPlugin.mockResolvedValue(installed({
      moves: [{ team: 'alpha', member: 'Mailer', slot: 'mailbox', outcome: 'moved', fields: ['password', 'user'], connection: 'conn-1', connectionName: null, key: 'mailbox', reason: null }],
      attention: [{ team: 'alpha', member: 'Mailer', teamName: 'Alpha Team', memberName: 'Mailer', fields: [
        { field: 'region', reason: '`region` is required by fixture-mail 1.1.0, and this member has no value for it and the plugin gives no default.' },
      ] }],
    }));
    const wrapper = await mountPlugins();
    await confirmUpgrade();

    const move = bodyFind('[data-upgrade-move="alpha/Mailer"]')!;
    expect(move.textContent).not.toContain('`');
    expect([...move.querySelectorAll('code')].map((c) => c.textContent)).toEqual(['password', 'user']);
    const field = bodyFind('[data-attention-field="region"]')!;
    expect(field.textContent).not.toContain('`');
    expect(field.textContent?.trim()).toBe('region is required by fixture-mail 1.1.0, and this member has no value for it and the plugin gives no default.');
    expect([...field.querySelectorAll('code')].map((c) => c.textContent)).toEqual(['region']);
    expect(bodyFind('[data-plugin-upgrade-done]')!.textContent).not.toContain('`');
    wrapper.unmount();
  });

  it('puts the icon on the active version\'s tile, not an inactive older version listed first', async () => {
    listPlugins.mockResolvedValue({
      ...list,
      versions: [
        { id: 'fixture-mail', version: '0.9.0', name: 'Fixture Mail', active: false, verdict: 'inactive', reason: null },
        ...list.versions,
      ],
    } satisfies PluginList);
    const wrapper = await mountPlugins();

    const tiles = [...document.body.querySelectorAll('[data-plugin="fixture-mail"]')];
    expect(tiles.map((t) => t.getAttribute('data-plugin-version'))).toEqual(['0.9.0', '1.0.0']);
    expect(tiles[0]!.querySelector('[data-plugin-upgrade]')).toBeNull();
    const onActive = tiles[1]!.querySelector('[data-plugin-upgrade]') as HTMLElement;
    expect(onActive.getAttribute('aria-label')).toBe('Upgrade to 1.1.0');
    expect(tiles[1]!.querySelector('[data-active]')).not.toBeNull();

    // A refusal shows on that same tile.
    installPlugin.mockRejectedValue(new ActionRefused('fixture-mail 1.1.0 is older than Beta Team needs.', { installed: false }));
    onActive.click();
    await settle();
    (bodyFind('[data-upgrade-confirm]') as HTMLElement).click();
    await settle();
    await settle();
    const after = [...document.body.querySelectorAll('[data-plugin="fixture-mail"]')];
    expect(after[0]!.querySelector('[data-upgrade-refused]')).toBeNull();
    expect(after[1]!.querySelector('[data-upgrade-refused]')).not.toBeNull();
    wrapper.unmount();
  });
});
