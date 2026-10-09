// @vitest-environment happy-dom
//
// INSTALL FROM A FOLDER: ONE DIALOG FOR MARKETPLACE > ADVANCED AND FOR ADMIN > PLUGINS. A typed folder with
// Browse, the screen's own options (Replace, for a plugin), and a place for uploading a package.
//
// ITS PICKER OPENS IN THE TEAMS' DOCUMENTS, where a package is uploaded, and goes no higher: the
// data root's own folders (agent-credentials, backups, bin, ...) are never listed, nor is a
// dot-entry anywhere (`.harness-team`). "Choose this folder" stays disabled until a folder below
// Documents is open - Documents itself is nobody's package.
//
// SEEN TO FAIL: before this dialog, Solutions opened the bare host picker at the roots level and
// Plugins kept its own typed-path dialog. Each case below was also reddened on its own: starting
// the picker at the roots (start), dropping the entry filter (hidden), choosing Documents itself
// (Choose), and either screen going back to its own dialog (shared).
//
// UPLOAD A PACKAGE (.zip) sits in the upload place: the zip goes to the Documents zip route, into
// the active team's folder unless the person picks another, and the folder it made fills Folder.
// SEEN TO FAIL: with the slot's old sentence back in place of the action, every case in that
// describe fails (no button); with the dialog not taking the uploaded folder, the Folder cases fail.
//
// THE MOCK IS OF `api/client`: what the dialog does with what the routes answer. The Documents
// routes the upload uses are answered at `fetch` by the fake Documents host.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const { fileSystemRoots, browseFileSystem, solutionsInstalled, listPlugins, listCatalog, checkSolution } = vi.hoisted(() => ({
  fileSystemRoots: vi.fn(),
  browseFileSystem: vi.fn(),
  solutionsInstalled: vi.fn(),
  listPlugins: vi.fn(),
  listCatalog: vi.fn(),
  checkSolution: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  fileSystemRoots,
  browseFileSystem,
  solutionsInstalled,
  listPlugins,
  listCatalog,
  checkSolution,
}));

import InstallFromFolderDialog from '../InstallFromFolderDialog.vue';
import SolutionsLauncher from '../SolutionsLauncher.vue';
import PluginsDialog from '../PluginsDialog.vue';
import type { DirectoryListing, HostEntry } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, field, isDisabled, settle } from '../../test/formProbe';
import { documentsServer } from '../../test/documentsServer';
import { useConsoleStore } from '../../stores/console';
import { asTeamId } from '../../api/types';
import UploadPackageAction from '../UploadPackageAction.vue';

const DataRootFolders = ['agent-credentials', 'agent-home', 'backups', 'bin', 'connections', 'go'];

const dir = (name: string): HostEntry => ({ name, type: 'dir' });
const file = (name: string): HostEntry => ({ name, type: 'file' });

/** What the instance looks like: the data root's own folders, Documents with a team marker and a
 *  dot-folder, and the uploaded package two levels down. */
const tree: Record<string, HostEntry[]> = {
  '/data': [...DataRootFolders.map(dir), dir('documents'), dir('teams'), file('messages.db')],
  '/data/documents': [file('.harness-team'), dir('.cache'), dir('QuickNotes'), dir('b0042-packages')],
  '/data/documents/QuickNotes': [file('.harness-team'), dir('job-tracker')],
  '/data/documents/QuickNotes/job-tracker': [file('solution.json'), dir('plugins')],
};

const listing = (path: string): DirectoryListing => ({
  path,
  parent: null,
  permissions: { allowCreate: false, allowUpdate: false, allowDelete: false },
  entries: tree[path] ?? [],
  truncated: false,
  total: (tree[path] ?? []).length,
});

let server: ReturnType<typeof documentsServer>;

beforeEach(() => {
  vi.clearAllMocks();
  // Every open of the dialog lists the Documents folders the upload can go to.
  server = documentsServer();
  fileSystemRoots.mockResolvedValue({
    roots: [
      { name: 'Instance data', path: '/data', isInstance: true, allowCreate: false, allowUpdate: false, allowDelete: false },
      { name: 'Host home', path: '/home/op', isInstance: false, allowCreate: false, allowUpdate: false, allowDelete: false },
    ],
  });
  browseFileSystem.mockImplementation((path: string) => Promise.resolve(listing(path)));
  solutionsInstalled.mockResolvedValue([]);
  listPlugins.mockResolvedValue({ plugins: [], refused: [], versions: [] });
  listCatalog.mockResolvedValue({ agents: [] });
  checkSolution.mockImplementation(async (folder: string) => ({
    ok: false, folder, plan: null, refusals: [{ file: 'solution.json', field: '(file)', reason: 'No solution.json.' }],
  }));
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

/** A row is opened by clicking its name, where the shared browser hangs the handler. */
function rowNamed(name: string): HTMLElement | undefined {
  return [...document.body.querySelectorAll('.q-item__label')]
    .find((candidate) => candidate.textContent?.trim() === name) as HTMLElement | undefined;
}

const rowNames = () => [...document.body.querySelectorAll('.q-item__label:not(.q-item__label--caption)')]
  .map((label) => label.textContent?.trim());

async function openPicker() {
  const wrapper = await mountDialog(InstallFromFolderDialog, { title: 'Install from a folder' });
  button('Browse…').click();
  await settle();
  await settle();
  return wrapper;
}

describe('The install picker', () => {
  it('starts in the teams Documents, not at the roots or the data root', async () => {
    const wrapper = await openPicker();

    expect(browseFileSystem).toHaveBeenCalledTimes(1);
    expect(browseFileSystem).toHaveBeenCalledWith('/data/documents');
    expect(rowNames()).toContain('QuickNotes');
    expect(rowNames()).not.toContain('Instance data');
    expect(bodyText()).not.toContain('Host home');

    wrapper.unmount();
  });

  it('hides dot-entries and never lists the data root or its own folders', async () => {
    const wrapper = await openPicker();

    expect(rowNames()).not.toContain('.harness-team');
    expect(rowNames()).not.toContain('.cache');
    // Nothing above Documents: no `..` row and no crumb that climbs to the data root.
    expect(rowNames()).not.toContain('..');
    expect(bodyText()).not.toContain('..');

    rowNamed('QuickNotes')!.click();
    await settle();

    expect(rowNames()).toContain('job-tracker');
    expect(rowNames()).not.toContain('.harness-team');

    expect(browseFileSystem).not.toHaveBeenCalledWith('/data');
    for (const name of DataRootFolders) expect(bodyText()).not.toContain(name);

    wrapper.unmount();
  });

  it('keeps Choose this folder disabled until a folder below Documents is open, then fills the folder', async () => {
    const wrapper = await openPicker();

    expect(isDisabled('Choose this folder')).toBe(true);

    rowNamed('QuickNotes')!.click();
    await settle();
    rowNamed('job-tracker')!.click();
    await settle();

    expect(isDisabled('Choose this folder')).toBe(false);
    button('Choose this folder').click();
    await settle();

    expect(field('Folder').value).toBe('/data/documents/QuickNotes/job-tracker');

    wrapper.unmount();
  });
});

describe('Marketplace and Plugins share the one install dialog', () => {
  it('Marketplace > Advanced opens the shared dialog, with no Replace, and its Install opens the wizard on the folder', async () => {
    const wrapper = await mountDialog(SolutionsLauncher, {});

    bodyFind('[data-solutions-tab="advanced"]')!.click();
    await settle();
    bodyFind('[data-install-from-folder]')!.click();
    await settle();

    const dialog = wrapper.findComponent(InstallFromFolderDialog);
    expect(dialog.exists()).toBe(true);
    expect(dialog.props('modelValue')).toBe(true);
    expect(bodyFind('[data-install-dialog]')).not.toBeNull();
    expect(bodyFind('[data-upload-package]')).not.toBeNull();
    expect(bodyFind('[data-install-dialog] .q-checkbox')).toBeNull();

    (field('Folder') as HTMLInputElement).value = '/data/documents/QuickNotes/job-tracker';
    field('Folder').dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
    button('Install').click();
    await settle();

    expect(wrapper.findComponent({ name: 'SolutionWizard' }).props('folder')).toBe('/data/documents/QuickNotes/job-tracker');

    wrapper.unmount();
  });

  it('Plugins opens the same dialog, with its Replace option and the upload place', async () => {
    setActivePinia(createPinia());
    const wrapper = await mountDialog(PluginsDialog, {}, { pinia: false });
    await settle();

    button('Install from a folder…').click();
    await settle();

    const dialog = wrapper.findComponent(InstallFromFolderDialog);
    expect(dialog.exists()).toBe(true);
    expect(dialog.props('modelValue')).toBe(true);
    expect(bodyFind('[data-install-dialog] .q-checkbox')?.textContent).toContain('Replace');
    expect(bodyFind('[data-upload-package]')).not.toBeNull();

    wrapper.unmount();
  });
});

describe('Upload a package (.zip)', () => {

  async function openWithActiveTeam(team: string) {
    setActivePinia(createPinia());
    useConsoleStore().activeTeamId = asTeamId(team);
    const wrapper = await mountDialog(InstallFromFolderDialog, {}, { pinia: false });
    await settle();
    return wrapper;
  }

  async function chooseZip(name: string) {
    const input = bodyFind('[data-upload-package-input]') as HTMLInputElement;
    Object.defineProperty(input, 'files', { value: [new File(['PK'], name)], configurable: true });
    input.dispatchEvent(new Event('change'));
    await settle();
    await settle();
  }

  it('says where the zip lands: the active team\'s Documents', async () => {
    const wrapper = await openWithActiveTeam('beta');

    expect(bodyFind('[data-upload-package-button]')?.textContent).toContain('Upload a package (.zip)');
    expect(bodyFind('[data-upload-package-action]')?.textContent).toContain('top of Documents › Beta');

    wrapper.unmount();
  });

  it('uploads into the active team\'s folder and fills Folder with the unpacked folder', async () => {
    const wrapper = await openWithActiveTeam('alpha');
    await chooseZip('job-tracker.zip');

    expect(server.callsTo('upload-zip').map((call) => [call.folder, call.body])).toEqual([
      ['alpha', { file: 'job-tracker.zip', path: '', onClash: 'ask' }],
    ]);
    expect(field('Folder').value).toBe('/data/documents/alpha/job-tracker');
    expect(bodyFind('[data-upload-package-landed]')?.textContent).toContain('Unpacked into Documents › Alpha › job-tracker');
    expect(isDisabled('Install')).toBe(false);

    wrapper.unmount();
  });

  it('uploads into the team the person picked', async () => {
    const wrapper = await openWithActiveTeam('alpha');
    wrapper.findComponent(UploadPackageAction).findComponent({ name: 'QSelect' }).vm.$emit('update:modelValue', 'beta');
    await settle();
    await chooseZip('job-tracker.zip');

    expect(server.callsTo('upload-zip').map((call) => call.folder)).toEqual(['beta']);
    expect(field('Folder').value).toBe('/data/documents/beta/job-tracker');

    wrapper.unmount();
  });

  it('a keep-both clash fills Folder with the copy it made', async () => {
    const wrapper = await openWithActiveTeam('alpha');
    server.reply('upload-zip', 409, {
      error: 'job-tracker is already in alpha. Choose Keep both, Replace or Skip.',
      clashes: [{ from: 'job-tracker', to: 'job-tracker', isFolder: true }],
    });
    server.reply('upload-zip', 200, {
      path: 'job-tracker (copy)', name: 'job-tracker (copy)', isFolder: true, files: ['job-tracker (copy)/solution.json'], size: 2,
    });
    await chooseZip('job-tracker.zip');

    (bodyFind('.documents-clash [data-clash="keep-both"]') as HTMLElement).click();
    await settle();
    await settle();

    expect(server.callsTo('upload-zip').map((call) => (call.body as { onClash: string }).onClash)).toEqual(['ask', 'keep-both']);
    expect(field('Folder').value).toBe('/data/documents/alpha/job-tracker (copy)');

    wrapper.unmount();
  });

  it('shows an unsafe zip\'s refusal and leaves Folder empty', async () => {
    const wrapper = await openWithActiveTeam('alpha');
    server.reply('upload-zip', 400, { error: 'The zip was not unpacked: ../outside.md leaves the folder.' });
    await chooseZip('evil.zip');

    expect(bodyFind('[data-upload-package-error]')?.textContent).toContain('The zip was not unpacked: ../outside.md leaves the folder.');
    expect(field('Folder').value).toBe('');

    wrapper.unmount();
  });

  it('says how large a zip may be when the upload answers 413, and leaves Folder empty', async () => {
    const wrapper = await openWithActiveTeam('alpha');
    server.reply('upload-zip', 413);
    await chooseZip('big.zip');

    expect(bodyFind('[data-upload-package-error]')?.textContent).toContain('That zip is larger than 25 MB.');
    expect(bodyFind('[data-upload-package-error]')?.textContent).not.toContain('413');
    expect(field('Folder').value).toBe('');

    wrapper.unmount();
  });

  it.each([
    ['Marketplace', () => mountDialog(SolutionsLauncher, {}, { pinia: false }), '[data-install-from-folder]'],
    ['Plugins', () => mountDialog(PluginsDialog, {}, { pinia: false }), null],
  ] as const)('works from %s', async (_screen, mountScreen, opener) => {
    setActivePinia(createPinia());
    useConsoleStore().activeTeamId = asTeamId('alpha');
    const wrapper = await mountScreen();
    await settle();
    if (opener) {
      bodyFind('[data-solutions-tab="advanced"]')!.click();
      await settle();
      bodyFind(opener)!.click();
    }
    else button('Install from a folder…').click();
    await settle();
    await settle();

    await chooseZip('job-tracker.zip');

    expect(field('Folder').value).toBe('/data/documents/alpha/job-tracker');

    wrapper.unmount();
  });
});
