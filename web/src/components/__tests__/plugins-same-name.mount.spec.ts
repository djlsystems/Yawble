// @vitest-environment happy-dom
//
// TWO INSTALLED PLUGINS WITH ONE NAME ARE TOLD APART, AND A RENAMED PLUGIN SAYS WHAT IT REPLACES
// (PLG 3, rename). Fixture Cloud 1.1.4 (`fixture-old`) and Fixture Cloud 3.0.0 (`fixture-new`) share a
// name; only the new one's manifest says it `replaces` the old id.
//
// - Add member and Member settings' Agent offer two choices that read differently, each naming its id
//   and version; a name nobody else has reads as before, `Name (plugin)`.
// - Admin -> Plugins shows one tile per installed id - also when `versions` does not carry one of them,
//   which is how a plugin Add member offers went missing from the screen where it is managed. SEEN TO
//   FAIL on 63506ba: the second id had no tile.
// - The new tile names the old id, its version and who is still on it; Move asks first, then moves
//   through the person's route; the old tile says what replaces it. The install answer says the same.
//
// THE MOCK IS OF `api/client`: what the routes do is pinned server-side (PluginReplacementTests).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const {
  listPlugins, listCatalog, addMember, getSecretKey, listConnections, listConnectionProviders, getMarketplace,
  moveReplacedMembers, getMember, getPluginSettings, savePluginSettings, updateMember, installPlugin, checkSolution,
} = vi.hoisted(() => ({
  listPlugins: vi.fn(),
  listCatalog: vi.fn(),
  addMember: vi.fn(),
  getSecretKey: vi.fn(),
  listConnections: vi.fn(),
  listConnectionProviders: vi.fn(),
  getMarketplace: vi.fn(),
  moveReplacedMembers: vi.fn(),
  getMember: vi.fn(),
  getPluginSettings: vi.fn(),
  savePluginSettings: vi.fn(),
  updateMember: vi.fn(),
  installPlugin: vi.fn(),
  checkSolution: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listPlugins, listCatalog, addMember, getSecretKey, listConnections, listConnectionProviders, getMarketplace,
  moveReplacedMembers, getMember, getPluginSettings, savePluginSettings, updateMember, installPlugin, checkSolution,
}));

vi.mock('../../api/documents', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listDocumentsRoot: vi.fn(async () => ({ folders: [], root: '/data/documents' })),
}));

import AddMemberDialog from '../AddMemberDialog.vue';
import MemberSettingsDialog from '../MemberSettingsDialog.vue';
import PluginsDialog from '../PluginsDialog.vue';
import PluginInstallDialog from '../PluginInstallDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { asMemberId, asTeamId, type ContainerSnapshot, type PluginList, type TeamId, type Team } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostList, hostPlugin, hostSettings } from '../../test/pluginFixtures';
import { settle } from '../../test/formProbe';

const builder = { team: 'alpha', member: 'Builder', teamName: 'Alpha Team', memberName: 'Builder' };
const deployer = { team: 'beta', member: 'Deployer', teamName: 'Beta Team', memberName: 'Deployer' };

const old = hostPlugin({
  id: 'fixture-old', name: 'Fixture Cloud', version: '1.1.4',
  members: [{ team: 'alpha', member: 'Builder' }, { team: 'beta', member: 'Deployer' }],
  replaces: [], replacedBy: [{ id: 'fixture-new', name: 'Fixture Cloud', version: '3.0.0' }],
});
const renamed = hostPlugin({
  id: 'fixture-new', name: 'Fixture Cloud', version: '3.0.0',
  replaces: [{ id: 'fixture-old', name: 'Fixture Cloud', version: '1.1.4', members: [builder, deployer] }], replacedBy: [],
});
const unique = hostPlugin({ id: 'fixture-mail', name: 'Fixture Mail', version: '2.0.0' });

beforeEach(() => {
  for (const mock of [listPlugins, listCatalog, addMember, getSecretKey, listConnections, listConnectionProviders, getMarketplace,
    moveReplacedMembers, getMember, getPluginSettings, savePluginSettings, updateMember, installPlugin, checkSolution]) mock.mockReset();
  listCatalog.mockResolvedValue({ agents: [{ name: 'claude', mode: 'Headless' }] });
  listPlugins.mockResolvedValue(hostList([old, renamed, unique]));
  listConnections.mockResolvedValue([]);
  listConnectionProviders.mockResolvedValue([]);
  getMarketplace.mockResolvedValue({ checked: false, reason: 'not read here', checkedAt: null, packages: [] });
});

afterEach(resetBody);

type Select = { props: (name: string) => unknown };
type Wrapper = { findAllComponents: (s: { name: string }) => unknown[]; unmount: () => void };

function agentSelect(wrapper: Wrapper): Select {
  const found = (wrapper.findAllComponents({ name: 'QSelect' }) as Select[]).find((s) => s.props('label') === 'Agent');
  if (!found) throw new Error('no Agent select');
  return found;
}

/** What each choice of the Agent select reads, as the select renders it. */
function choiceLabels(select: Select): string[] {
  const label = select.props('optionLabel');
  return (select.props('options') as unknown[]).map((option) =>
    typeof label === 'function' ? String(label(option)) : typeof option === 'object' && option ? String((option as { label: string }).label) : String(option));
}

describe('No two plugin choices read alike', () => {
  it('Add member lists the two same-named plugins by id and version, and a unique name as before', async () => {
    const wrapper = await mountDialog(AddMemberDialog, { team: 'alpha' as TeamId, teamName: 'Alpha', memberAgents: ['claude'] }) as unknown as Wrapper;
    await settle();

    const labels = choiceLabels(agentSelect(wrapper));
    expect(labels).toEqual([
      'claude',
      'Fixture Cloud (plugin, fixture-old 1.1.4)',
      'Fixture Cloud (plugin, fixture-new 3.0.0)',
      'Fixture Mail (plugin)',
    ]);
    expect(new Set(labels).size).toBe(labels.length);

    wrapper.unmount();
  });

  it("Member settings' Agent offers a plugin member the installed plugins by the same rule, and says a change moves it", async () => {
    const snapshot = {
      team: asTeamId('alpha'), id: asMemberId('Builder'), name: 'Builder', agent: 'plugin:fixture-old', kind: 'plugin', state: 'Idle',
      queueDepth: 0, ceiling: 16, subscribes: [], currentCorrelation: null, sinceSeq: 0,
    } as unknown as ContainerSnapshot;
    getPluginSettings.mockResolvedValue(hostSettings(old, { team: 'alpha', member: 'Builder', marketplaceUrl: null }));
    setActivePinia(createPinia());
    useConsoleStore().teams = [{ id: asTeamId('alpha'), name: 'Alpha', memberAgents: ['claude'], containers: [snapshot] } as unknown as Team];

    const wrapper = await mountDialog(MemberSettingsDialog, { snapshot }, { pinia: false }) as unknown as Wrapper & {
      findAllComponents: (s: { name: string }) => { props: (n: string) => unknown; vm: { $emit: (e: string, v: unknown) => void } }[];
    };
    await settle();

    const select = agentSelect(wrapper);
    expect(choiceLabels(select)).toEqual([
      'Fixture Cloud (plugin, fixture-old 1.1.4)',
      'Fixture Cloud (plugin, fixture-new 3.0.0)',
      'Fixture Mail (plugin)',
    ]);
    expect(bodyFind('[data-member-moving]')).toBeNull();

    (select as unknown as { vm: { $emit: (e: string, v: unknown) => void } }).vm.$emit('update:modelValue', 'plugin:fixture-new');
    await settle();
    expect(bodyFind('[data-member-moving]')?.textContent).toContain('Fixture Cloud (plugin, fixture-new 3.0.0)');

    wrapper.unmount();
  });
});

describe('Admin -> Plugins: one tile per installed id, and what a renamed plugin replaces', () => {
  async function mountPlugins(list: PluginList) {
    listPlugins.mockResolvedValue(list);
    setActivePinia(createPinia());
    const wrapper = await mountDialog(PluginsDialog, {}, { pinia: false });
    await settle();
    return wrapper;
  }

  const installedTiles = () =>
    [...document.body.querySelectorAll('[data-plugin][data-verdict="installed"]')].map((tile) => `${tile.getAttribute('data-plugin')}@${tile.getAttribute('data-plugin-version')}`);

  it('shows both same-named plugins even when the version list does not carry one of them', async () => {
    // `versions` as a Host read it without the new id's folder: Add member still offers it (it reads
    // `plugins`), and this screen must not drop it.
    const list = hostList([old, renamed, unique]);
    list.versions = list.versions.filter((entry) => entry.id !== 'fixture-new');

    await mountPlugins(list);

    expect(installedTiles()).toEqual(['fixture-mail@2.0.0', 'fixture-new@3.0.0', 'fixture-old@1.1.4']);
  });

  it('names the replaced id, its version and who is on it, moves only after the person confirms, and says what replaces the old one', async () => {
    moveReplacedMembers.mockResolvedValue({
      id: 'fixture-new', old: 'fixture-old',
      moves: [{ ...builder, outcome: 'moved', reason: null }, { ...deployer, outcome: 'failed', reason: 'the disk is full' }],
      remaining: [deployer],
    });

    await mountPlugins(hostList([old, renamed, unique]));

    const line = bodyFind('[data-plugin="fixture-new"] [data-replaced="fixture-old"] [data-replaced-line]')!.textContent!.replace(/\s+/g, ' ').trim();
    expect(line).toBe('Replaces Fixture Cloud 1.1.4 (fixture-old). Still on it: Alpha Team / Builder, Beta Team / Deployer.');
    expect(bodyFind('[data-plugin="fixture-old"] [data-plugin-replaced-by="fixture-new"]')!.textContent).toContain('Replaced by Fixture Cloud 3.0.0 (fixture-new)');
    expect(bodyFind('[data-plugin="fixture-mail"] [data-plugin-replaces]')).toBeNull();

    // NOTHING MOVES ON THE FIRST PRESS: it asks, naming the members.
    bodyFind('[data-plugin="fixture-new"] [data-replaced-move-ask]')!.click();
    await settle();
    expect(moveReplacedMembers).not.toHaveBeenCalled();
    expect(bodyFind('[data-replaced-question]')!.textContent).toContain('Alpha Team / Builder, Beta Team / Deployer');
    expect(bodyFind('[data-replaced-question]')!.textContent).toContain('keeps its settings and its own secret values');

    bodyFind('[data-replaced-move-confirm]')!.click();
    await settle();
    expect(moveReplacedMembers).toHaveBeenCalledWith('fixture-new', 'fixture-old');
    expect(bodyFind('[data-replaced-move="alpha/Builder"]')!.getAttribute('data-outcome')).toBe('moved');
    expect(bodyFind('[data-replaced-move="beta/Deployer"]')!.textContent).toContain('not moved - the disk is full');
    // The list is read again after a move.
    expect(listPlugins).toHaveBeenCalledTimes(2);
  });
});

describe('The install answer names what the installed plugin replaces', () => {
  it('lists the old id, its version and its members, and moves nothing by itself', async () => {
    checkSolution.mockResolvedValue({ ok: false, refusals: [{ file: 'solution.json', field: '(file)', message: 'There is no solution.json.' }] });
    installPlugin.mockResolvedValue({
      id: 'fixture-new', version: '3.0.0', name: 'Fixture Cloud', installed: true, replaced: false, reason: null, previous: null,
      moves: [], attention: [], replaces: [{ id: 'fixture-old', name: 'Fixture Cloud', version: '1.1.4', members: [builder] }],
    });

    const wrapper = await mountDialog(PluginInstallDialog, { modelValue: true, folder: '/data/documents/Marketplace/fixture-new-3.0.0' });
    await settle();
    // The person's Install press, as the folder dialog hands it on.
    (wrapper.findComponent({ name: 'InstallFromFolderDialog' }) as unknown as { vm: { $emit: (e: string, ...a: unknown[]) => void } })
      .vm.$emit('install', '/data/documents/Marketplace/fixture-new-3.0.0', false);
    await settle();
    expect(installPlugin).toHaveBeenCalledWith('/data/documents/Marketplace/fixture-new-3.0.0', false);

    expect(bodyText()).toContain('Replaces Fixture Cloud 1.1.4 (fixture-old).');
    expect(bodyFind('[data-install-replaces] [data-replaced-line]')!.textContent).toContain('Alpha Team / Builder');
    expect(moveReplacedMembers).not.toHaveBeenCalled();

    wrapper.unmount();
  });
});
