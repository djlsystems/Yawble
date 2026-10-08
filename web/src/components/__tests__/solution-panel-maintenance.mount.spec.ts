// @vitest-environment happy-dom
//
// THE CONTROL PANEL'S MAINTENANCE SECTION: the version and source folder, and Update from a
// folder, which opens the install dialog (a typed folder, Browse, and Upload a package (.zip)) and
// then the install wizard on that folder - the wizard's own update path, not a second one.
//
// SEEN TO FAIL: with Update from a folder opening the bare picker as it did, the upload case finds no
// upload place and the typed case finds no Folder box; with the dialog's folder not handed to the
// wizard, the typed folder is never checked.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SolutionPanel from '../SolutionPanel.vue';
import HostPathPicker from '../HostPathPicker.vue';
import InstallFromFolderDialog from '../InstallFromFolderDialog.vue';
import SolutionWizard from '../SolutionWizard.vue';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, field, isDisabled, settle, type } from '../../test/formProbe';
import { fakeHost, reply, sent, wizardRoutes, type Call } from '../../test/solutionFixtures';
import { StandardFolders } from '../../test/documentsServer';
import { openSection, panelRead, panelRoutes } from '../../test/solutionPanelFixtures';

let calls: Call[] = [];

beforeEach(() => {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(fakeHost([
    ...panelRoutes(() => panelRead({ updatedAt: '2026-09-30T01:00:00Z' })),
    ...wizardRoutes(),
    (call) => (call.method === 'GET' && call.url === '/api/documents' ? reply(200, { folders: StandardFolders(), root: '/data/documents' }) : undefined),
    (call) =>
      call.method === 'POST' && call.url.startsWith('/api/teams/alpha/documents/upload-zip')
        ? reply(200, { path: 'job-tracker-1.2.0', name: 'job-tracker-1.2.0', isFolder: true, files: 3, size: 900 })
        : undefined,
  ], calls)));
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

describe('the control panel: Maintenance', () => {
  it('shows the version and the source folder', async () => {
    await mountDialog(SolutionPanel, { team: 'job-tracker' });
    await settle();
    await openSection('maintenance');

    expect(bodyFind('[data-maintenance-version]')?.textContent).toBe('1.1.0');
    expect(bodyFind('[data-maintenance-folder]')?.textContent).toBe('/data/documents/packages/job-tracker');
    expect(bodyFind('[data-maintenance-facts]')?.textContent).toContain('Updated');
  });

  async function openUpdate() {
    const wrapper = await mountDialog(SolutionPanel, { team: 'job-tracker' });
    await settle();
    await openSection('maintenance');
    bodyFind('[data-update-from-folder]')!.click();
    await settle();
    await settle();
    return wrapper;
  }

  it("updates from a folder through the picker and the install wizard's update path", async () => {
    const wrapper = await openUpdate();

    const dialog = wrapper.findComponent(InstallFromFolderDialog);
    expect(dialog.props('modelValue')).toBe(true);
    expect(dialog.props('title')).toBe('Update from a folder');

    const picker = dialog.findComponent(HostPathPicker);
    expect(picker.props('modelValue')).toBe(false);
    button('Browse…').click();
    await settle();
    expect(picker.props('modelValue')).toBe(true);
    expect(picker.props('instanceOnly')).toBe(true);

    picker.vm.$emit('chose', '/data/documents/packages/job-tracker-1.2.0');
    await settle();
    expect(field('Folder').value).toBe('/data/documents/packages/job-tracker-1.2.0');

    button('Update').click();
    await settle();

    const wizard = wrapper.findComponent(SolutionWizard);
    expect(wizard.props('modelValue')).toBe(true);
    expect(wizard.props('folder')).toBe('/data/documents/packages/job-tracker-1.2.0');
    expect(dialog.props('modelValue')).toBe(false);
  });

  it('uploads a package (.zip) into Documents and fills the folder with what it unpacked', async () => {
    const wrapper = await openUpdate();

    expect(bodyFind('[data-upload-package-button]')?.textContent).toContain('Upload a package (.zip)');
    const input = bodyFind('[data-upload-package-input]') as HTMLInputElement;
    Object.defineProperty(input, 'files', { value: [new File(['PK'], 'job-tracker-1.2.0.zip')], configurable: true });
    input.dispatchEvent(new Event('change'));
    await settle();
    await settle();

    expect(calls.filter((call) => call.method === 'POST' && call.url.startsWith('/api/teams/alpha/documents/upload-zip'))).toHaveLength(1);
    expect(field('Folder').value).toBe('/data/documents/alpha/job-tracker-1.2.0');
    expect(isDisabled('Update')).toBe(false);

    button('Update').click();
    await settle();
    expect(wrapper.findComponent(SolutionWizard).props('folder')).toBe('/data/documents/alpha/job-tracker-1.2.0');
  });

  it('checks a typed folder as it checks a picked one', async () => {
    const wrapper = await openUpdate();

    expect(isDisabled('Update')).toBe(true);
    await type('Folder', '  /data/documents/packages/job-tracker-1.2.0  ');
    button('Update').click();
    await settle();
    await settle();

    const wizard = wrapper.findComponent(SolutionWizard);
    expect(wizard.props('modelValue')).toBe(true);
    expect(wizard.props('folder')).toBe('/data/documents/packages/job-tracker-1.2.0');
    expect(sent(calls, 'POST', '/api/solutions/check').map((call) => (call.body as { folder: string }).folder)).toEqual([
      '/data/documents/packages/job-tracker-1.2.0',
    ]);
  });
});
