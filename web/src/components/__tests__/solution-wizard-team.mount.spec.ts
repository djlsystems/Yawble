// @vitest-environment happy-dom
//
// THE SOLUTION WIZARD, STEP 1: TEAM. A new team named after the package by default, the name
// checked as New Team checks it (empty, too long) and by the Host's preview (`nameRefusal`, e.g.
// taken); a local repository ticked by default and sent; or an update of a team installed from an
// EARLIER version of the same package - a team on the same or a newer version is listed but cannot
// be chosen, and says why. Choosing one previews the update with the team.
//
// THE MOCK IS OF `fetch`: what is pinned is each request the step sends as well as what it shows.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises } from '@vue/test-utils';

import SolutionWizard from '../SolutionWizard.vue';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, field, hasError, isDisabled, settle, type } from '../../test/formProbe';
import {
  Folder,
  fakeHost,
  installPreview,
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

beforeEach(() => {
  setActivePinia(createPinia());
  serve();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function open() {
  const wrapper = await mountDialog(SolutionWizard, { folder: Folder }, { pinia: false });
  await settle();
  return wrapper;
}

const previews = () => sent(calls, 'POST', '/api/solutions/preview');

describe('Solution wizard - Team', () => {
  it('names the new team after the package, previews that name, and moves on to the review', async () => {
    await open();

    expect(sent(calls, 'POST', '/api/solutions/check')[0]!.body).toEqual({ folder: Folder });
    expect((field('Team name') as HTMLInputElement).value).toBe('Job Tracker');
    expect(previews()[0]!.body).toEqual({ folder: Folder, team: 'Job Tracker' });

    button('Next').click();
    await settle();

    expect(bodyFind('[data-step="review"]')).not.toBeNull();
  });

  it('refuses an empty name and one longer than 60 characters, as New Team does', async () => {
    await open();

    await type('Team name', '   ');
    expect(hasError('Team name')).toBe(true);
    expect(bodyText()).toContain('A team needs a name.');
    expect(isDisabled('Next')).toBe(true);

    await type('Team name', 'x'.repeat(61));
    expect(hasError('Team name')).toBe(true);
    expect(bodyText()).toContain('A team name cannot be longer than 60 characters.');
    expect(isDisabled('Next')).toBe(true);

    await type('Team name', 'My search');
    expect(hasError('Team name')).toBe(false);
  });

  it("shows the Host's nameRefusal for a taken name, re-previewed on change, and stays on Team", async () => {
    const Taken = 'A team called Job Tracker already exists. Choose another name.';
    serve([
      (call) =>
        call.url === '/api/solutions/preview' && (call.body as { team?: string }).team === 'Job Tracker'
          ? reply(200, installPreview('Job Tracker', Taken))
          : undefined,
    ]);

    await open();

    expect(bodyText()).toContain(Taken);
    expect(hasError('Team name')).toBe(true);
    expect(isDisabled('Next')).toBe(true);
    expect(bodyFind('[data-step="team"]')).not.toBeNull();

    // A new name is asked about after a short pause, and the refusal goes.
    await type('Team name', 'My job search');
    await new Promise((resolve) => setTimeout(resolve, 450));
    await settle();

    expect(previews().at(-1)!.body).toEqual({ folder: Folder, team: 'My job search' });
    expect(bodyText()).not.toContain(Taken);
    expect(isDisabled('Next')).toBe(false);
  });

  it('Next re-asks about a name typed since the last preview, without waiting for the pause', async () => {
    const Taken = 'A team called Busy already exists. Choose another name.';
    serve([
      (call) =>
        call.url === '/api/solutions/preview' && (call.body as { team?: string }).team === 'Busy'
          ? reply(200, installPreview('Busy', Taken))
          : undefined,
    ]);
    await open();

    await type('Team name', 'Busy');
    button('Next').click();
    await settle();

    expect(previews().at(-1)!.body).toEqual({ folder: Folder, team: 'Busy' });
    expect(bodyText()).toContain(Taken);
    expect(bodyFind('[data-step="team"]')).not.toBeNull();
  });

  it('ticks "Create a local repository" by default and sends it; unticked it sends false', async () => {
    serve([
      (call) =>
        call.url === '/api/solutions/install'
          ? reply(200, { ok: true, team: 'job-tracker', teamName: 'Job Tracker', version: '1.1.0', missing: [], steps: steps(8) })
          : undefined,
      (call) => (call.url === '/api/teams/job-tracker/solution' ? reply(200, { team: 'job-tracker', missing: [] }) : undefined),
    ]);
    await open();

    const box = bodyFind('[data-local-repository]')!;
    expect(box.getAttribute('aria-checked')).toBe('true');
    box.click();
    await settle();
    expect(box.getAttribute('aria-checked')).toBe('false');

    for (let index = 0; index < 3; index++) {
      button('Next').click();
      await settle();
    }
    button('Install').click();
    await settle();

    expect(sent(calls, 'POST', '/api/solutions/install')[0]!.body).toMatchObject({ teamName: 'Job Tracker', localRepository: false });
  });

  it('offers to update only a team on an older version; the same or a newer one is shown disabled, saying why', async () => {
    serve([], {
      installed: [
        installedRow('job-tracker', 'Job Tracker', '1.0.0'),
        installedRow('job-tracker-2', 'Second search', '1.1.0'),
        installedRow('job-tracker-3', 'Future search', '2.0.0'),
        installedRow('other', 'Other team', '0.1.0', 'another-package'),
      ],
    });
    await open();

    expect(bodyFind('[data-mode-update]')).not.toBeNull();
    const older = bodyFind('[data-update-team="job-tracker"]')!;
    const same = bodyFind('[data-update-team="job-tracker-2"]')!;
    const newer = bodyFind('[data-update-team="job-tracker-3"]')!;

    expect(older.getAttribute('data-selectable')).toBe('true');
    expect(older.textContent).toContain('Job Tracker (runs 1.0.0)');

    expect(same.getAttribute('data-selectable')).toBe('false');
    expect(same.querySelector('.q-radio')!.getAttribute('aria-disabled')).toBe('true');
    expect(same.textContent).toContain('already on 1.1.0');

    expect(newer.getAttribute('data-selectable')).toBe('false');
    expect(newer.textContent).toContain('already on 2.0.0');

    // A team from another package is not offered at all.
    expect(bodyFind('[data-update-team="other"]')).toBeNull();
  });

  it('choosing an older team previews the update with that team and reviews it', async () => {
    serve(
      [
        (call) =>
          call.url === '/api/solutions/preview' && (call.body as { team?: string }).team === 'job-tracker'
            ? reply(200, updatePreview('job-tracker'))
            : undefined,
      ],
      { installed: [installedRow('job-tracker', 'Job Tracker', '1.0.0')] },
    );
    await open();

    (bodyFind('[data-update-team="job-tracker"] .q-radio') as HTMLElement).click();
    await settle();

    expect(previews().at(-1)!.body).toEqual({ folder: Folder, team: 'job-tracker' });

    button('Next').click();
    await flushPromises();
    await settle();

    expect(bodyFind('[data-version-line]')!.textContent).toContain('1.0.0 -> 1.1.0');
  });

  it('no team from this package: no update option', async () => {
    await open();
    expect(bodyFind('[data-mode-update]')).toBeNull();
  });
});
