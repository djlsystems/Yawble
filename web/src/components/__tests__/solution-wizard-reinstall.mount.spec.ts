// @vitest-environment happy-dom
//
// THE SOLUTION WIZARD, REINSTALL. A team that kept an uninstalled install of this package (its sites
// offline with their data) is offered: named by the package's own team name it previews as a
// reinstall straight away, saying what comes back; under any other name the kept team is offered by
// a button that previews it. The person's earlier answers that still apply are filled in, each still
// asked, and the install is sent onto that team with them.
//
// THE MOCK IS OF `fetch`: what is pinned is each request the wizard sends as well as what it shows.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

import SolutionWizard from '../SolutionWizard.vue';
import type { SolutionPreview } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, field, settle, type } from '../../test/formProbe';
import {
  Folder,
  fakeHost,
  hostPlan,
  installPreview,
  reply,
  sent,
  steps,
  wizardRoutes,
  type Call,
  type Route,
} from '../../test/solutionFixtures';

let calls: Call[] = [];

const Kept = { team: 'job-tracker', teamName: 'Job Tracker', version: '1.0.0', uninstalledAt: '2026-10-01T10:00:00Z' };

function reinstallPreview(): SolutionPreview {
  return {
    ...(installPreview('Job Tracker') as Extract<SolutionPreview, { mode: 'install' }>),
    reinstallable: [Kept],
    reinstall: { team: 'job-tracker', teamName: 'Job Tracker', from: '1.0.0' },
    previous: {
      settings: [
        { member: 'Scout', setting: 'sources', value: ['sample'] },
        { member: 'Scout', setting: 'region', value: 'Berlin' },
      ],
      connections: [{ member: 'Scout', slot: 'mail', connection: 'conn-1' }],
    },
  };
}

const preview: Route = (call) => {
  if (call.method !== 'POST' || call.url !== '/api/solutions/preview') return undefined;
  const team = (call.body as { team?: string }).team;
  return team === 'Job Tracker'
    ? reply(200, reinstallPreview())
    : reply(200, { ...installPreview(team ?? 'Job Tracker'), reinstallable: [Kept] });
};

const install: Route = (call) =>
  call.url === '/api/solutions/install'
    ? reply(200, { ok: true, team: 'job-tracker', teamName: 'Job Tracker', version: '1.1.0', missing: [], steps: steps(8), reinstalledFrom: '1.0.0' })
    : undefined;

const record: Route = (call) =>
  call.url === '/api/teams/job-tracker/solution' ? reply(200, { team: 'job-tracker', missing: [] }) : undefined;

beforeEach(() => {
  setActivePinia(createPinia());
  calls = [];
  vi.stubGlobal('fetch', vi.fn(fakeHost([preview, install, record, ...wizardRoutes({ plan: hostPlan() })], calls)));
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

const words = (selector: string) => (bodyFind(selector)?.textContent ?? '').replace(/\s+/g, ' ').trim();

describe('Solution wizard - reinstall onto a kept team', () => {
  it('previews the kept team as a reinstall and says its sites come back with their data', async () => {
    await open();

    expect(words('[data-reinstall-line]')).toContain(
      'Reinstalls onto the team Job Tracker, which kept Job Tracker 1.0.0 when it was uninstalled: its sites come back with their data',
    );
    expect(bodyFind('[data-local-repository]')).toBeNull();
    expect(bodyText()).not.toContain('already exists');

    button('Next').click();
    await settle();
    expect(words('[data-reinstall-team-line]')).toBe(
      'Reinstall onto Job Tracker, which had 1.0.0: its kept sites come back with their data.',
    );
    expect(bodyFind('[data-new-team-line]')).toBeNull();
  });

  it('offers the kept team under another name, and choosing it previews that team', async () => {
    await open();

    await type('Team name', 'My search');
    await new Promise((resolve) => setTimeout(resolve, 450));
    await settle();

    expect(bodyFind('[data-reinstall-line]')).toBeNull();
    const offer = bodyFind('[data-reinstall-team="job-tracker"]')!;
    expect(offer.textContent).toContain('Reinstall onto Job Tracker (had 1.0.0)');

    offer.click();
    await settle();

    expect(sent(calls, 'POST', '/api/solutions/preview').at(-1)!.body).toEqual({ folder: Folder, team: 'Job Tracker' });
    expect((field('Team name') as HTMLInputElement).value).toBe('Job Tracker');
    expect(bodyFind('[data-reinstall-line]')).not.toBeNull();
  });

  it('fills in the earlier answers, still asked, and installs onto the kept team with them', async () => {
    await open();
    button('Next').click();
    await settle();
    button('Next').click();
    await settle();

    // Still asked, starting from the answer before.
    expect(bodyFind('[data-step="inputs"]')).not.toBeNull();
    expect((field('Scout: region') as HTMLInputElement).value).toBe('Berlin');
    expect(bodyFind('[data-person-setting="Scout/region"] [data-previous-setting]')).not.toBeNull();
    expect(bodyFind('[data-kept-setting]')).toBeNull();

    button('Next').click();
    await settle();
    expect(words('[data-step="install"]')).toContain(
      'Reinstall Job Tracker 1.1.0 onto the team Job Tracker, bringing back its kept sites with their data.',
    );
    button('Install').click();
    await settle();

    const body = sent(calls, 'POST', '/api/solutions/install')[0]!.body as Record<string, unknown>;
    expect(body.teamName).toBe('Job Tracker');
    expect(body.settings).toEqual({ Scout: { sources: ['sample'], region: 'Berlin' } });
    expect(body.connections).toEqual({ Scout: { mail: 'conn-1' } });
  });
});
