// @vitest-environment happy-dom
//
// INSTALL FROM A FOLDER: ONE DIALOG FOR SOLUTIONS AND FOR ADMIN > PLUGINS. A typed folder with
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
// THE MOCK IS OF `api/client`: what the dialog does with what the routes answer.
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

beforeEach(() => {
  vi.clearAllMocks();
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

afterEach(resetBody);

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

describe('Solutions and Plugins share the one install dialog', () => {
  it('Solutions opens the shared dialog, with no Replace, and its Install opens the wizard on the folder', async () => {
    const wrapper = await mountDialog(SolutionsLauncher, {});

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
