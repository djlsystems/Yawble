// @vitest-environment happy-dom
//
// THE SOLUTION WIZARD, STEP 4: INSTALL. Nothing is sent before Install is pressed. A success lists
// every step done, uploads the chosen documents, reads back what is still missing and offers to open
// the team. A failure names the failing step by its number and title with the Host's reason, and
// says nothing was left behind. A refusal (409) is the Host's sentence. An update sends
// `/api/solutions/update` with the team.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

import SolutionWizard from '../SolutionWizard.vue';
import { useConsoleStore } from '../../stores/console';
import type { Team } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle } from '../../test/formProbe';
import {
  Folder,
  fakeHost,
  installedRow,
  reply,
  sent,
  steps,
  updatePreview,
  wizardRoutes,
  type Call,
  type Route,
} from '../../test/solutionFixtures';

let calls: Call[] = [];

function serve(extra: Route[] = [], options: Parameters<typeof wizardRoutes>[0] = {}) {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(fakeHost([...extra, ...wizardRoutes(options)], calls)));
}

const Missing = [
  { kind: 'document', name: 'Resume', member: null, description: 'Your reference resume, .docx or PDF.' },
  { kind: 'connection', name: 'mail', member: 'Scout', description: 'Where postings are emailed from.' },
];

const success: Route = (call) =>
  call.url === '/api/solutions/install'
    ? reply(200, { ok: true, team: 'job-tracker', teamName: 'Job Tracker', version: '1.1.0', missing: Missing, steps: steps(8) })
    : undefined;

const record: Route = (call) =>
  call.url === '/api/teams/job-tracker/solution'
    ? reply(200, {
        team: 'job-tracker',
        id: 'job-tracker',
        name: 'Job Tracker',
        version: '1.1.0',
        installedAt: '2026-09-29T10:00:00Z',
        installedBy: 'dana@example.com',
        plugins: ['job-board 0.2.0'],
        missing: Missing,
      })
    : undefined;

beforeEach(() => {
  setActivePinia(createPinia());
  serve();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function toInstall() {
  const wrapper = await mountDialog(SolutionWizard, { folder: Folder }, { pinia: false });
  await settle();
  for (let index = 0; index < 3; index++) {
    button('Next').click();
    await settle();
  }
  expect(bodyFind('[data-step="install"]')).not.toBeNull();
  return wrapper;
}

const doneSteps = () =>
  [...document.body.querySelectorAll('[data-install-step][data-done="true"]')].map((item) => item.getAttribute('data-install-step'));

describe('Solution wizard - Install', () => {
  it('sends nothing until Install is pressed', async () => {
    serve([success, record]);
    await toInstall();

    expect(bodyText()).toContain('Install Job Tracker 1.1.0 as the team Job Tracker.');
    expect(sent(calls, 'POST', '/api/solutions/install')).toHaveLength(0);
    expect(sent(calls, 'POST', '/api/solutions/update')).toHaveLength(0);
  });

  it('on success lists every step done, reads back what is still missing, and offers to open the team', async () => {
    serve([success, record]);
    const wrapper = await toInstall();

    button('Install').click();
    await settle();

    expect(doneSteps()).toEqual(['plugins', 'team', 'members', 'skills', 'tools', 'sites', 'triggers', 'record']);
    expect(bodyText()).toContain('Install the plugins');
    expect(bodyText()).toContain('Record the package');
    expect(bodyText()).toContain('Installed Job Tracker (1.1.0).');

    expect(sent(calls, 'GET', '/api/teams/job-tracker/solution')).toHaveLength(1);
    const missing = [...document.body.querySelectorAll('[data-missing-item]')].map((item) => item.textContent);
    expect(missing).toEqual([
      'Waiting for Resume/ - Your reference resume, .docx or PDF.',
      'Waiting for Scout connection mail - Where postings are emailed from.',
    ]);

    // The Install button is gone: a second press cannot install twice.
    expect(bodyFind('[data-install-button]')).toBeNull();

    const board = useConsoleStore();
    board.teams = [{ id: 'job-tracker', name: 'Job Tracker', containers: [] } as unknown as Team];
    board.overviewLanded = true;
    const setActive = vi.spyOn(board, 'setActiveTeam');
    button('Open the team').click();
    await settle();

    expect(setActive).toHaveBeenCalledWith('job-tracker');
    expect(wrapper.emitted('opened')).toEqual([['job-tracker']]);
    expect(wrapper.emitted('update:modelValue')?.at(-1)).toEqual([false]);
  });

  it('says nothing is missing when the team is ready', async () => {
    serve([
      (call) =>
        call.url === '/api/solutions/install'
          ? reply(200, { ok: true, team: 'job-tracker', teamName: 'Job Tracker', version: '1.1.0', missing: [], steps: steps(8) })
          : undefined,
      (call) => (call.url === '/api/teams/job-tracker/solution' ? reply(200, { team: 'job-tracker', missing: [] }) : undefined),
    ]);
    await toInstall();
    button('Install').click();
    await settle();

    expect(bodyFind('[data-nothing-missing]')!.textContent).toContain('Nothing is missing');
  });

  it('on a failed step names it by number and title with the reason, and says nothing was left behind', async () => {
    serve([
      (call) =>
        call.url === '/api/solutions/install'
          ? reply(200, {
              ok: false,
              step: 'members',
              stepNumber: 3,
              reason: 'The preset Researcher is not in this instance.',
              steps: steps(2),
            })
          : undefined,
    ]);
    await toInstall();

    button('Install').click();
    await settle();

    const failure = bodyFind('[data-install-failure]')!.textContent ?? '';
    expect(failure).toContain('Failed at step 3 (Hire the members): The preset Researcher is not in this instance.');
    expect(failure).toContain('Nothing was left behind');
    expect(bodyFind('[data-install-step="members"]')!.textContent).toContain('failed, undone');
    expect(sent(calls, 'GET', '/api/teams/job-tracker/solution')).toHaveLength(0);
    expect(bodyFind('[data-open-team]')).toBeNull();
  });

  it("shows the Host's sentence for a refusal such as a name taken meanwhile (409)", async () => {
    serve([
      (call) => (call.url === '/api/solutions/install' ? reply(409, { error: 'A team called Job Tracker already exists.' }) : undefined),
    ]);
    await toInstall();

    button('Install').click();
    await settle();

    expect(bodyFind('[data-install-error]')!.textContent).toContain('A team called Job Tracker already exists.');
    expect(bodyFind('[data-install-button]')).not.toBeNull();
  });

  it('an update sends /api/solutions/update with the team, and says the versions', async () => {
    serve(
      [
        (call) =>
          call.url === '/api/solutions/preview' && (call.body as { team?: string }).team === 'job-tracker'
            ? reply(200, updatePreview('job-tracker'))
            : undefined,
        (call) =>
          call.url === '/api/solutions/update'
            ? reply(200, { ok: true, team: 'job-tracker', teamName: 'Job Tracker', version: '1.1.0', missing: [], steps: steps(8), from: '1.0.0', to: '1.1.0' })
            : undefined,
        (call) => (call.url === '/api/teams/job-tracker/solution' ? reply(200, { team: 'job-tracker', missing: [] }) : undefined),
      ],
      { installed: [installedRow('job-tracker', 'Job Tracker', '1.0.0')] },
    );
    await mountDialog(SolutionWizard, { folder: Folder }, { pinia: false });
    await settle();
    (bodyFind('[data-update-team="job-tracker"] .q-radio') as HTMLElement).click();
    await settle();
    for (let index = 0; index < 3; index++) {
      button('Next').click();
      await settle();
    }

    expect(bodyText()).toContain('Update Job Tracker from 1.0.0 to 1.1.0.');
    button('Update').click();
    await settle();

    expect(sent(calls, 'POST', '/api/solutions/install')).toHaveLength(0);
    expect(sent(calls, 'POST', '/api/solutions/update')[0]!.body).toMatchObject({ folder: Folder, team: 'job-tracker' });
    expect(bodyText()).toContain('Updated Job Tracker (1.1.0).');
  });
});
