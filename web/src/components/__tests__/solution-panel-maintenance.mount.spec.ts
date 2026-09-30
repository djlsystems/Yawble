// @vitest-environment happy-dom
//
// THE CONTROL PANEL'S MAINTENANCE SECTION (B001J): the version and source folder, and Update from a
// folder, which opens the folder picker and then the install wizard on that folder - the wizard's own
// update path, not a second one.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SolutionPanel from '../SolutionPanel.vue';
import HostPathPicker from '../HostPathPicker.vue';
import SolutionWizard from '../SolutionWizard.vue';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { fakeHost, type Call } from '../../test/solutionFixtures';
import { openSection, panelRead, panelRoutes } from '../../test/solutionPanelFixtures';

let calls: Call[] = [];

beforeEach(() => {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(fakeHost(panelRoutes(() => panelRead({ updatedAt: '2026-09-30T01:00:00Z' })), calls)));
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

  it("updates from a folder through the picker and the install wizard's update path", async () => {
    const wrapper = await mountDialog(SolutionPanel, { team: 'job-tracker' });
    await settle();
    await openSection('maintenance');

    const picker = wrapper.findComponent(HostPathPicker);
    expect(picker.props('modelValue')).toBe(false);
    bodyFind('[data-update-from-folder]')!.click();
    await settle();
    expect(picker.props('modelValue')).toBe(true);
    expect(picker.props('instanceOnly')).toBe(true);

    picker.vm.$emit('chose', '/data/documents/packages/job-tracker-1.2.0');
    await settle();

    const wizard = wrapper.findComponent(SolutionWizard);
    expect(wizard.props('modelValue')).toBe(true);
    expect(wizard.props('folder')).toBe('/data/documents/packages/job-tracker-1.2.0');
  });
});
