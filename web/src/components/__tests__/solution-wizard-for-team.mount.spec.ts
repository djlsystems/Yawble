// @vitest-environment happy-dom
//
// THE SOLUTION WIZARD OPENED FOR A TEAM. From a solution's own panel (Maintenance, Update from a
// folder) the wizard starts on "Update an existing team" with that team chosen, and asks the Host
// for that team's update - never "Install as a new team" under the package's name, which would make
// a second team of the same name. Opened anywhere else it starts as before.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

import SolutionPanel from '../SolutionPanel.vue';
import HostPathPicker from '../HostPathPicker.vue';
import SolutionWizard from '../SolutionWizard.vue';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle } from '../../test/formProbe';
import {
  Folder,
  fakeHost,
  installedRow,
  reply,
  sent,
  updatePreview,
  wizardRoutes,
  type Call,
  type Route,
} from '../../test/solutionFixtures';
import { openSection, panelRoutes } from '../../test/solutionPanelFixtures';

let calls: Call[] = [];

const updateOfJobTracker: Route = (call) =>
  call.url === '/api/solutions/preview' && (call.body as { team?: string }).team === 'job-tracker'
    ? reply(200, updatePreview('job-tracker'))
    : undefined;

function serve(installed = [installedRow('job-tracker', 'Job Tracker', '1.0.0')]) {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(fakeHost([updateOfJobTracker, ...panelRoutes(), ...wizardRoutes({ installed })], calls)));
}

beforeEach(() => {
  setActivePinia(createPinia());
  serve();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

const checked = (selector: string) => bodyFind(selector)?.getAttribute('aria-checked');

describe('Solution wizard - opened for a team', () => {
  it("from the team's panel, Update from a folder starts on updating that team, not a new one", async () => {
    const wrapper = await mountDialog(SolutionPanel, { team: 'job-tracker' }, { pinia: false });
    await settle();
    await openSection('maintenance');
    bodyFind('[data-update-from-folder]')!.click();
    await settle();
    wrapper.findComponent(HostPathPicker).vm.$emit('chose', Folder);
    await settle();
    await settle();

    expect(wrapper.findComponent(SolutionWizard).props('team')).toBe('job-tracker');
    expect(checked('[data-mode-update]')).toBe('true');
    expect(checked('[data-mode-install]')).toBe('false');
    expect(checked('[data-update-team="job-tracker"] .q-radio')).toBe('true');
    expect(sent(calls, 'POST', '/api/solutions/preview').at(-1)!.body).toMatchObject({ team: 'job-tracker' });

    button('Next').click();
    await settle();
    expect(bodyFind('[data-version-line]')!.textContent).toContain('Job Tracker: 1.0.0 -> 1.1.0');
  });

  it('opened anywhere else, it starts on a new team as before', async () => {
    await mountDialog(SolutionWizard, { folder: Folder }, { pinia: false });
    await settle();

    expect(checked('[data-mode-install]')).toBe('true');
    expect(checked('[data-mode-update]')).toBe('false');
    expect(sent(calls, 'POST', '/api/solutions/preview').at(-1)!.body).toMatchObject({ team: 'Job Tracker' });
  });

  it('for a team this package cannot update (already on this version), it starts as before', async () => {
    serve([installedRow('job-tracker', 'Job Tracker', '1.1.0')]);
    await mountDialog(SolutionWizard, { folder: Folder, team: 'job-tracker' }, { pinia: false });
    await settle();

    expect(checked('[data-mode-install]')).toBe('true');
    expect(bodyFind('[data-update-team="job-tracker"]')!.getAttribute('data-selectable')).toBe('false');
  });
});
