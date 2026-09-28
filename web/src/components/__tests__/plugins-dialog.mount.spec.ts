// @vitest-environment happy-dom
//
// ADMIN > PLUGINS. Every plugin version and what the Host made of it - installed, or refused with
// its reason - with, for an installed one, its settings, secrets by name, events, skill and the
// members hired on it (each a link to that member's settings); Rescan; View manifest read-only and
// formatted; and Install from a folder, which shows the Host's verdict and is refused over an
// existing version without Replace.
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
  getPluginSettings,
  getMember,
  listCatalog,
  fileSystemRoots,
  browseFileSystem,
} = vi.hoisted(() => ({
  listPlugins: vi.fn(),
  rescanPlugins: vi.fn(),
  getPluginManifest: vi.fn(),
  installPlugin: vi.fn(),
  getPluginSettings: vi.fn(),
  getMember: vi.fn(),
  listCatalog: vi.fn(),
  fileSystemRoots: vi.fn(),
  browseFileSystem: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listPlugins,
  rescanPlugins,
  getPluginManifest,
  installPlugin,
  getPluginSettings,
  getMember,
  listCatalog,
  fileSystemRoots,
  browseFileSystem,
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
import { hostField, hostPlugin, hostSecret, hostSettings } from '../../test/pluginFixtures';
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
  for (const mock of [listPlugins, rescanPlugins, getPluginManifest, installPlugin, getPluginSettings, getMember, listCatalog, fileSystemRoots, browseFileSystem]) {
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
    const installed = row('sample-echo')!;

    expect(installed.querySelector('[data-description]')?.textContent).toContain('Deterministic test plugin');

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

  it('views a manifest read-only and formatted', async () => {
    const wrapper = await mountPlugins();

    getPluginManifest.mockResolvedValue('{"id":"sample-echo","version":"0.2.0","config":{"mode":{"type":"string"}}}');
    const view = [...row('sample-echo')!.querySelectorAll('button')].find((b) => b.textContent?.includes('View manifest'))!;
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

  it('browses with the folder picker, offering the data root only, and fills the folder', async () => {
    const wrapper = await mountPlugins();

    fileSystemRoots.mockResolvedValue({
      roots: [
        { name: 'Instance data', path: '/data', isInstance: true, allowCreate: false, allowUpdate: false },
        { name: 'Host home', path: '/home/op', isInstance: false, allowCreate: false, allowUpdate: false },
      ],
    });
    browseFileSystem.mockResolvedValue({ path: '/data', parent: null, entries: [] });

    button('Install from a folder…').click();
    await settle();
    button('Browse…').click();
    await settle();
    await settle();

    expect(bodyText()).toContain('Choose the plugin folder');
    expect(bodyText()).toContain('Instance data');
    expect(bodyText()).not.toContain('Host home');

    wrapper.unmount();
  });

  it('is empty-handed rather than blank when nothing is installed', async () => {
    listPlugins.mockResolvedValue({ plugins: [], refused: [], versions: [] });
    const wrapper = await mountPlugins();

    expect(bodyFind('[data-no-plugins]')).not.toBeNull();

    wrapper.unmount();
  });
});
