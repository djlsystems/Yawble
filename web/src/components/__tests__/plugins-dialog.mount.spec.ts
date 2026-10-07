// @vitest-environment happy-dom
//
// ADMIN > PLUGINS. Every plugin version and what the Host made of it - installed, or refused with
// its reason - with, for an installed one, its settings, secrets by name, events, skill and the
// members hired on it (each a link to that member's settings); Rescan; View manifest read-only and
// formatted; and Install from a folder, which shows the Host's verdict and is refused over an
// existing version without Replace; and Remove, asked first, which shows the Host's refusal while
// members are hired and removes otherwise.
//
// THE MOCK IS OF `api/client`: this file asks what the dialog does with what the routes answer,
// not what the Host does - that is pinned server-side.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const {
  listPlugins,
  rescanPlugins,
  getPluginManifest,
  installPlugin,
  removePlugin,
  getPluginSettings,
  getMember,
  listCatalog,
  fileSystemRoots,
  browseFileSystem,
  checkSolution,
} = vi.hoisted(() => ({
  listPlugins: vi.fn(),
  rescanPlugins: vi.fn(),
  getPluginManifest: vi.fn(),
  installPlugin: vi.fn(),
  removePlugin: vi.fn(),
  getPluginSettings: vi.fn(),
  getMember: vi.fn(),
  listCatalog: vi.fn(),
  fileSystemRoots: vi.fn(),
  browseFileSystem: vi.fn(),
  // Install from a folder checks for a solution package first; these folders hold none.
  checkSolution: vi.fn(async (folder: string) => ({
    ok: false, folder, plan: null, refusals: [{ file: 'solution.json', field: '(file)', reason: 'No solution.json.' }],
  })),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listPlugins,
  rescanPlugins,
  getPluginManifest,
  installPlugin,
  removePlugin,
  getPluginSettings,
  getMember,
  listCatalog,
  fileSystemRoots,
  browseFileSystem,
  checkSolution,
}));

// The install dialog's Upload a package lists the Documents folders on open; none here.
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
  type InstalledPlugin,
  type PluginList,
  type Team,
} from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { hostField, hostList, hostPlugin, hostSecret, hostSettings, hostSlot } from '../../test/pluginFixtures';
import { button, isDisabled, settle, type } from '../../test/formProbe';

const sampleEcho: InstalledPlugin = hostPlugin({
  id: 'sample-echo',
  name: 'Sample Echo',
  description: "Deterministic test plugin: transforms each instruction's text.",
  version: '0.2.0',
  config: {
    mode: hostField({ type: 'string', description: 'How to transform.', default: 'upper', enum: ['upper', 'reverse'] }),
    allow: hostField({ type: 'list', description: 'Who may be sent to.', required: true, setBy: 'person' }),
  },
  secrets: { token: hostSecret({ description: 'A demo credential.' }) },
  publishes: [{ type: 'plugin.sample-echo.echoed', summary: 'One echo done.' }],
  skills: ['skills/SKILL.md'],
  skill: 'plugin-sample-echo',
  members: [{ team: 'alpha', member: 'Echo' }],
});

/** The shape the settled contract answers: `versions` names every folder and the Host's verdict. */
const list: PluginList = {
  plugins: [sampleEcho],
  refused: [{ id: 'broken', reason: "config field 'x' has unknown type 'map'." }],
  versions: [
    { id: 'sample-echo', version: '0.1.0', name: 'Sample Echo', active: false, verdict: 'inactive', reason: null },
    { id: 'sample-echo', version: '0.2.0', name: 'Sample Echo', active: true, verdict: 'installed', reason: null },
    { id: 'broken', version: '1.0.0', name: 'Broken', active: true, verdict: 'refused', reason: "config field 'x' has unknown type 'map'." },
  ],
};

const echoMember = {
  team: asTeamId('alpha'),
  id: asMemberId('Echo'),
  name: 'Echo',
  agent: 'plugin:sample-echo',
  kind: 'plugin',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
} as unknown as ContainerSnapshot;

beforeEach(() => {
  for (const mock of [listPlugins, rescanPlugins, getPluginManifest, installPlugin, removePlugin, getPluginSettings, getMember, listCatalog, fileSystemRoots, browseFileSystem]) {
    mock.mockReset();
  }
  listPlugins.mockResolvedValue(list);
  listCatalog.mockResolvedValue({ agents: [] });
  getPluginSettings.mockResolvedValue(hostSettings(sampleEcho, { team: 'alpha', member: 'Echo' }));
});

afterEach(resetBody);

async function mountPlugins() {
  setActivePinia(createPinia());
  const board = useConsoleStore();
  board.teams = [
    { id: asTeamId('alpha'), name: 'Alpha Team', memberAgents: ['claude'], containers: [echoMember] } as unknown as Team,
  ];

  const wrapper = await mountDialog(PluginsDialog, {}, { pinia: false });
  await settle();

  return wrapper;
}

/** A plugin's row: the given version's, else its installed version's, else its only one. */
const row = (id: string, version?: string) =>
  version
    ? bodyFind(`[data-plugin="${id}"][data-plugin-version="${version}"]`)
    : bodyFind(`[data-plugin="${id}"][data-verdict="installed"]`) ?? bodyFind(`[data-plugin="${id}"]`);

/** Opens a tile's Details and answers the dialog, where everything the version declares is. */
async function details(id: string, version?: string): Promise<Element> {
  (row(id, version)!.querySelector('[data-plugin-details]') as HTMLElement).click();
  await settle();
  return bodyFind('[data-plugin-details-dialog]')!;
}

describe('PluginsDialog', () => {
  it('lists every version with id, name, version, active and the verdict: installed, refused, inactive', async () => {
    const wrapper = await mountPlugins();

    const inactive = row('sample-echo', '0.1.0')!;
    expect(inactive.getAttribute('data-verdict')).toBe('inactive');
    expect(inactive.textContent).toContain('Inactive');
    expect(inactive.querySelector('[data-active]')).toBeNull();

    const installed = row('sample-echo')!;
    expect(installed.getAttribute('data-verdict')).toBe('installed');
    expect(installed.textContent).toContain('Sample Echo');
    expect(installed.textContent).toContain('0.2.0');
    expect(installed.textContent).toContain('Installed');
    expect(installed.querySelector('[data-active]')).not.toBeNull();

    const refused = row('broken')!;
    expect(refused.getAttribute('data-verdict')).toBe('refused');
    expect(refused.textContent).toContain('Refused');
    expect(refused.querySelector('[data-reason]')?.textContent).toContain("config field 'x' has unknown type 'map'.");

    wrapper.unmount();
  });

  it('shows each connection slot in the Host\'s words, and None for a plugin without', async () => {
    const mailer = hostPlugin({
      id: 'mailer',
      connections: {
        mail: hostSlot({
          providers: ['google', 'microsoft'],
          required: true,
          description: 'The mailbox to read and send from.',
          summary: 'needs a Google or Microsoft connection',
        }),
      },
    });
    listPlugins.mockResolvedValue(hostList([mailer, sampleEcho]));
    const wrapper = await mountPlugins();

    expect(row('mailer')!.querySelector('[data-plugin-summary]')!.textContent).toContain('1 connection');
    const slot = (await details('mailer')).querySelector('[data-connection-slot="mail"]')!.textContent;
    expect(slot).toContain('mail');
    expect(slot).toContain('needs a Google or Microsoft connection');
    expect(slot).toContain('Required');
    expect(slot).toContain('The mailbox to read and send from.');
    expect(row('sample-echo')!.querySelector('[data-plugin-summary]')!.textContent).toContain('no connections');
    expect((await details('sample-echo')).querySelector('[data-connections]')!.textContent?.trim()).toBe('None');

    wrapper.unmount();
  });

  it('shows every refusal reason, including those only refused[] carries when versions exist', async () => {
    // `active` names a folder that is not there: the version folders read inactive, and the reason
    // is only in refused[]. A dot-named folder is left out of versions altogether.
    const missingActive = "`active` names '9.9', which is not a version directory.";
    const dotNamed = 'the directory name is not a plugin id.';
    listPlugins.mockResolvedValue({
      ...list,
      refused: [...list.refused, { id: 'relay', reason: missingActive }, { id: '.staging', reason: dotNamed }],
      versions: [
        ...list.versions,
        { id: 'relay', version: '1.0.0', name: 'Relay', active: false, verdict: 'inactive', reason: null },
      ],
    });
    const wrapper = await mountPlugins();

    const reasons = [...document.body.querySelectorAll('[data-verdict="refused"] [data-reason]')].map((el) => el.textContent?.trim());
    expect(reasons).toContain(missingActive);
    expect(reasons).toContain(dotNamed);
    expect(bodyFind('[data-plugin=".staging"][data-verdict="refused"]')).not.toBeNull();
    // The version folder is still listed as it is, and the Host's one reason for `broken` once.
    expect(row('relay', '1.0.0')?.getAttribute('data-verdict')).toBe('inactive');
    expect(reasons.filter((reason) => reason === "config field 'x' has unknown type 'map'.")).toHaveLength(1);

    wrapper.unmount();
  });

  it("shows an installed plugin's description, settings, secret names, events, skill and members", async () => {
    const wrapper = await mountPlugins();
    const tile = row('sample-echo')!;

    // THE TILE: the description, what it declares counted, and who is hired on it.
    expect(tile.querySelector('[data-description]')?.textContent).toContain('Deterministic test plugin');
    expect(tile.querySelector('[data-plugin-summary]')?.textContent).toMatch(/settings? · .*secrets? · .* · .*events? · a skill/);
    expect(tile.querySelector('[data-member-link="alpha/Echo"]')?.textContent).toContain('Alpha Team / Echo');
    expect(tile.querySelector('[data-config-field]')).toBeNull();

    // DETAILS: the whole of it.
    const installed = await details('sample-echo');

    const mode = installed.querySelector('[data-config-field="mode"]')?.textContent ?? '';
    expect(mode).toContain('string');
    expect(mode).toContain('Default: upper');

    const allow = installed.querySelector('[data-config-field="allow"]')?.textContent ?? '';
    expect(allow).toContain('list');
    expect(allow).toContain('Required');
    expect(allow).toContain('Set by a person only');

    expect(installed.querySelector('[data-secrets]')?.textContent).toContain('token');
    expect(installed.querySelector('[data-events]')?.textContent).toContain('plugin.sample-echo.echoed');
    expect(installed.querySelector('[data-skill]')?.textContent?.trim()).toBe('plugin-sample-echo');
    expect(installed.querySelector('[data-member-link="alpha/Echo"]')?.textContent).toContain('Alpha Team / Echo');

    wrapper.unmount();
  });

  it("opens a member's settings from its link", async () => {
    const wrapper = await mountPlugins();

    (row('sample-echo')!.querySelector('[data-member-link="alpha/Echo"]') as HTMLElement).click();
    await settle();
    await settle();

    expect(bodyText()).toContain('Member settings');
    expect(getPluginSettings).toHaveBeenCalledWith('alpha', 'Echo');
    expect(bodyFind('[data-member-plugin-settings]')).not.toBeNull();

    wrapper.unmount();
  });

  it('rescans and shows what the Host answers', async () => {
    const wrapper = await mountPlugins();

    rescanPlugins.mockResolvedValue({
      plugins: [{ ...sampleEcho, version: '0.3.0' }],
      refused: [],
      versions: [{ id: 'sample-echo', version: '0.3.0', name: 'Sample Echo', active: true, verdict: 'installed', reason: null }],
    });
    button('Rescan').click();
    await settle();

    expect(rescanPlugins).toHaveBeenCalledTimes(1);
    expect(row('sample-echo')?.getAttribute('data-plugin-version')).toBe('0.3.0');
    expect(row('broken')).toBeNull();

    wrapper.unmount();
  });

  it('shows each version as a tile and narrows the tiles by the filter', async () => {
    const wrapper = await mountPlugins();

    expect(document.body.querySelector('.plugin-tiles [data-plugin="sample-echo"]')).not.toBeNull();

    const filter = document.body.querySelector<HTMLInputElement>('input[data-plugin-filter], [data-plugin-filter] input')!;
    filter.value = 'BROKEN';
    filter.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
    expect(row('sample-echo')).toBeNull();
    expect(row('broken')).not.toBeNull();

    filter.value = 'nothing-is-called-this';
    filter.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
    expect(document.body.querySelector('[data-plugin-none-match]')).not.toBeNull();

    wrapper.unmount();
  });

  it('views a manifest read-only and formatted', async () => {
    const wrapper = await mountPlugins();

    getPluginManifest.mockResolvedValue('{"id":"sample-echo","version":"0.2.0","config":{"mode":{"type":"string"}}}');
    const view = row('sample-echo')!.querySelector('[data-view-manifest]') as HTMLElement;
    expect(view.getAttribute('aria-label')).toBe('View manifest sample-echo 0.2.0');
    view.click();
    await settle();

    expect(getPluginManifest).toHaveBeenCalledWith('sample-echo', '0.2.0');
    const manifest = bodyFind('[data-manifest]')!;
    expect(manifest.tagName).toBe('PRE');
    expect(manifest.textContent).toBe(
      JSON.stringify({ id: 'sample-echo', version: '0.2.0', config: { mode: { type: 'string' } } }, null, 2),
    );
    // Read-only: nothing in the manifest view can be typed into.
    expect(bodyFind('[data-manifest-dialog] input, [data-manifest-dialog] textarea')).toBeNull();

    wrapper.unmount();
  });
});

describe('PluginsDialog, installing from a folder', () => {
  it("shows the Host's verdict on an install and lists the new version", async () => {
    const wrapper = await mountPlugins();

    button('Install from a folder…').click();
    await settle();

    expect(isDisabled('Install')).toBe(true);
    await type('Folder', '/data/teams/alpha/repos/Echo/wt/bin/plugin');

    installPlugin.mockResolvedValue({ id: 'sample-echo', version: '0.2.0', installed: true, replaced: false, reason: null });
    button('Install').click();
    await settle();

    expect(installPlugin).toHaveBeenCalledWith('/data/teams/alpha/repos/Echo/wt/bin/plugin', false);
    expect(bodyFind('[data-install-verdict]')?.textContent).toContain('Installed sample-echo 0.2.0');
    expect(listPlugins).toHaveBeenCalledTimes(2);

    wrapper.unmount();
  });

  it('shows the refusal over an existing version without Replace, and sends replace once it is ticked', async () => {
    const wrapper = await mountPlugins();

    button('Install from a folder…').click();
    await settle();
    await type('Folder', '/data/work/sample-echo');

    // The route's 409 body, as `send` throws it: the sentence, and the id and version it read.
    const sentence = 'sample-echo 0.2.0 is already installed. Tick Replace to install over it.';
    installPlugin.mockRejectedValueOnce(
      Object.assign(
        new ActionRefused(sentence, { error: sentence, reason: sentence, installed: false, id: 'sample-echo', version: '0.2.0' }),
        { status: 409 },
      ),
    );
    button('Install').click();
    await settle();

    expect(installPlugin).toHaveBeenLastCalledWith('/data/work/sample-echo', false);
    const verdict = bodyFind('[data-install-verdict]')!.textContent ?? '';
    expect(verdict).toContain('Not installed (sample-echo 0.2.0)');
    expect(verdict).toContain('sample-echo 0.2.0 is already installed.');

    (bodyFind('[data-install-dialog] .q-checkbox') as HTMLElement).click();
    await settle();

    installPlugin.mockResolvedValueOnce({ id: 'sample-echo', version: '0.2.0', installed: true, replaced: true, reason: null });
    button('Install').click();
    await settle();

    expect(installPlugin).toHaveBeenLastCalledWith('/data/work/sample-echo', true);
    expect(bodyFind('[data-install-verdict]')?.textContent).toContain('Replaced sample-echo 0.2.0');

    wrapper.unmount();
  });

  it('shows a verdict the Host answered as not installed, naming its reason', async () => {
    const wrapper = await mountPlugins();

    button('Install from a folder…').click();
    await settle();
    await type('Folder', '/data/work/other');

    installPlugin.mockResolvedValue({ id: 'other', version: '1.0.0', installed: false, replaced: false, reason: 'The folder has no plugin.json.' });
    button('Install').click();
    await settle();

    expect(bodyFind('[data-install-verdict]')?.textContent).toContain('The folder has no plugin.json.');
    // The folder WAS copied and the catalog rescanned before it refused: the list is read again,
    // so the new version shows with its verdict without a Rescan.
    expect(listPlugins).toHaveBeenCalledTimes(2);

    wrapper.unmount();
  });

  it("browses with the install picker, which opens in the teams' Documents, and fills the folder", async () => {
    const wrapper = await mountPlugins();

    fileSystemRoots.mockResolvedValue({
      roots: [
        { name: 'Instance data', path: '/data', isInstance: true, allowCreate: false, allowUpdate: false },
        { name: 'Host home', path: '/home/op', isInstance: false, allowCreate: false, allowUpdate: false },
      ],
    });
    browseFileSystem.mockImplementation(async (path: string) => ({
      path,
      parent: null,
      permissions: { allowCreate: false, allowUpdate: false, allowDelete: false },
      entries: path === '/data/documents' ? [{ name: 'sample-echo', type: 'dir' }] : [],
    }));

    button('Install from a folder…').click();
    await settle();
    button('Browse…').click();
    await settle();
    await settle();

    expect(bodyText()).toContain('Choose the plugin folder');
    expect(browseFileSystem).toHaveBeenCalledWith('/data/documents');
    expect(bodyText()).not.toContain('Host home');

    [...document.body.querySelectorAll<HTMLElement>('.q-item__label')].find((label) => label.textContent?.trim() === 'sample-echo')!.click();
    await settle();
    button('Choose this folder').click();
    await settle();

    expect((bodyFind('[data-install-dialog] input') as HTMLInputElement).value).toBe('/data/documents/sample-echo');

    wrapper.unmount();
  });

  it('offers Remove plugin once per plugin and Remove version only where a plugin has more than one', async () => {
    const wrapper = await mountPlugins();

    expect(document.body.querySelectorAll('[data-plugin="sample-echo"] [data-remove-plugin]')).toHaveLength(1);
    expect(document.body.querySelectorAll('[data-plugin="sample-echo"] [data-remove-version]')).toHaveLength(2);
    expect(row('broken')!.querySelector('[data-remove-plugin]')).not.toBeNull();
    expect(row('broken')!.querySelector('[data-remove-version]')).toBeNull();

    wrapper.unmount();
  });

  it('asks before removing a plugin, and shows the Host refusing while members are hired, naming them', async () => {
    const wrapper = await mountPlugins();

    (row('sample-echo', '0.1.0')!.querySelector('[data-remove-plugin]') as HTMLElement).click();
    await settle();

    // Asked first: nothing is sent until Remove is pressed.
    expect(bodyFind('[data-remove-question]')?.textContent).toContain(
      'Remove the plugin Sample Echo (sample-echo), with all its versions 0.1.0, 0.2.0, from the instance?');
    expect(removePlugin).not.toHaveBeenCalled();

    const sentence = 'Plugin sample-echo is in use by Alpha Team / Echo; remove those members first.';
    removePlugin.mockRejectedValueOnce(new ActionRefused(sentence, {
      error: sentence,
      members: [{ team: 'alpha', member: 'Echo', teamName: 'Alpha Team', memberName: 'Echo' }],
    }));
    (bodyFind('[data-remove-confirm]') as HTMLElement).click();
    await settle();

    expect(removePlugin).toHaveBeenCalledWith('sample-echo', null);
    expect(bodyFind('[data-remove-refusal]')?.textContent).toContain(sentence);
    // Still asking, and the list was not re-read: nothing was removed.
    expect(bodyFind('[data-remove-dialog]')).not.toBeNull();
    expect(bodyFind('[data-remove-done]')).toBeNull();
    expect(listPlugins).toHaveBeenCalledTimes(1);

    wrapper.unmount();
  });

  it('cancelling the question removes nothing', async () => {
    const wrapper = await mountPlugins();

    (row('sample-echo', '0.1.0')!.querySelector('[data-remove-version]') as HTMLElement).click();
    await settle();
    button('Cancel').click();
    await settle();

    expect(removePlugin).not.toHaveBeenCalled();
    expect(bodyFind('[data-remove-dialog]')).toBeNull();

    wrapper.unmount();
  });

  it('removes one version once confirmed, says so, and reads the list again', async () => {
    const wrapper = await mountPlugins();

    (row('sample-echo', '0.1.0')!.querySelector('[data-remove-version]') as HTMLElement).click();
    await settle();
    expect(bodyFind('[data-remove-question]')?.textContent).toContain(
      'Remove version 0.1.0 of Sample Echo (sample-echo) from the instance?');

    removePlugin.mockResolvedValueOnce({ id: 'sample-echo', version: '0.1.0', whole: false, versions: ['0.1.0'] });
    listPlugins.mockResolvedValue({ ...list, versions: list.versions.filter((v) => v.version !== '0.1.0') });
    (bodyFind('[data-remove-confirm]') as HTMLElement).click();
    await settle();

    expect(removePlugin).toHaveBeenCalledWith('sample-echo', '0.1.0');
    expect(bodyFind('[data-remove-done]')?.textContent).toContain('Removed version 0.1.0 of sample-echo.');
    expect(bodyFind('[data-remove-dialog]')).toBeNull();
    expect(listPlugins).toHaveBeenCalledTimes(2);
    expect(row('sample-echo', '0.1.0')).toBeNull();

    wrapper.unmount();
  });

  it('removes a whole plugin nobody is hired on once confirmed', async () => {
    const wrapper = await mountPlugins();

    (row('broken')!.querySelector('[data-remove-plugin]') as HTMLElement).click();
    await settle();

    removePlugin.mockResolvedValueOnce({ id: 'broken', version: null, whole: true, versions: ['1.0.0'] });
    (bodyFind('[data-remove-confirm]') as HTMLElement).click();
    await settle();

    expect(removePlugin).toHaveBeenCalledWith('broken', null);
    expect(bodyFind('[data-remove-done]')?.textContent).toContain('Removed the plugin broken.');

    wrapper.unmount();
  });

  it('is empty-handed rather than blank when nothing is installed', async () => {
    listPlugins.mockResolvedValue({ plugins: [], refused: [], versions: [] });
    const wrapper = await mountPlugins();

    expect(bodyFind('[data-no-plugins]')).not.toBeNull();

    wrapper.unmount();
  });
});
