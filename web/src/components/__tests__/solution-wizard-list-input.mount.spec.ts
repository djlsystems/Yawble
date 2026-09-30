// @vitest-environment happy-dom
//
// A LIST SETTING IN THE WIZARD'S "YOUR PART" KEEPS WHAT IS TYPED. A free-text list (positions,
// locations) takes a value on Enter, on leaving its box, and when Next or Install is pressed with
// text still in it. It used to take one only on Enter, so a person who typed a position and pressed
// Next installed a team with no positions and nothing said so.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

import SolutionWizard from '../SolutionWizard.vue';
import type { SolutionPlan } from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, field, settle, type } from '../../test/formProbe';
import { Folder, fakeHost, hostPlan, reply, sent, steps, wizardRoutes, type Call } from '../../test/solutionFixtures';

let calls: Call[] = [];

function planWithPositions(): SolutionPlan {
  const plan = hostPlan();
  plan.personSettings = [
    ...plan.personSettings,
    {
      member: 'Scout',
      setting: 'positions',
      description: 'Job titles to search for.',
      required: false,
      type: 'list',
      default: [],
      choices: null,
    },
  ];
  return plan;
}

beforeEach(() => {
  setActivePinia(createPinia());
  calls = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          (call) =>
            call.url === '/api/solutions/install'
              ? reply(200, { ok: true, team: 'job-tracker', teamName: 'Job Tracker', version: '1.1.0', missing: [], steps: steps(8) })
              : undefined,
          (call) => (call.url === '/api/teams/job-tracker/documents/upload' ? reply(200, { name: 'cv.pdf', path: 'Resume/cv.pdf' }) : undefined),
          (call) => (call.url === '/api/teams/job-tracker/solution' ? reply(200, { team: 'job-tracker', missing: [] }) : undefined),
          ...wizardRoutes({ plan: planWithPositions() }),
        ],
        calls,
      ),
    ),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function yourPart() {
  const wrapper = await mountDialog(SolutionWizard, { folder: Folder }, { pinia: false });
  await settle();
  for (let index = 0; index < 2; index++) {
    button('Next').click();
    await settle();
  }
  expect(bodyFind('[data-step="inputs"]')).not.toBeNull();
  return wrapper;
}

async function install(): Promise<Record<string, unknown>> {
  button('Next').click();
  await settle();
  button('Install').click();
  await settle();
  return sent(calls, 'POST', '/api/solutions/install')[0]!.body as Record<string, unknown>;
}

const chips = () =>
  [...bodyFind('[data-list-setting="Scout/positions"]')!.querySelectorAll('[data-chip]')].map((chip) => chip.getAttribute('data-chip'));

describe('Solution wizard - a list setting', () => {
  it('takes a value typed and left in the box when Next is pressed, and installs with it', async () => {
    await yourPart();
    await type('Scout: region', 'Europe');

    await type('Scout: positions', 'Systems Analyst');
    const body = await install();

    expect((body.settings as Record<string, Record<string, unknown>>).Scout!.positions).toEqual(['Systems Analyst']);
  });

  it('adds a chip on Enter and keeps the box for the next value, without moving to the next step', async () => {
    await yourPart();

    await type('Scout: positions', 'Systems Analyst');
    field('Scout: positions').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
    await settle();
    await type('Scout: positions', 'Business Analyst');
    field('Scout: positions').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
    await settle();

    expect(chips()).toEqual(['Systems Analyst', 'Business Analyst']);
    expect(bodyFind('[data-step="inputs"]')).not.toBeNull();
    expect((field('Scout: positions') as HTMLInputElement).value).toBe('');
  });

  it('removes a chip with its ×, and an untouched list stays empty and unsent', async () => {
    await yourPart();
    await type('Scout: region', 'Europe');

    await type('Scout: positions', 'Systems Analyst');
    field('Scout: positions').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
    await settle();
    (bodyFind('[aria-label="Remove Systems Analyst"]') as HTMLElement).click();
    await settle();
    expect(chips()).toEqual([]);

    const body = await install();
    expect((body.settings as Record<string, Record<string, unknown>> | undefined)?.Scout?.positions).toBeUndefined();
  });
});
