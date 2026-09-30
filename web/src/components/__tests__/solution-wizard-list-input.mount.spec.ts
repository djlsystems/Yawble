// @vitest-environment happy-dom
//
// A LIST SETTING IN THE WIZARD'S "YOUR PART" is the product's one list input: removable chips, and
// free text (positions, locations) added through its Add dialog, one item per line. A typed value is
// added or cancelled there - never left in a box that Next ignores, which once installed a team
// with no positions and nothing said so.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

import SolutionWizard from '../SolutionWizard.vue';
import type { SolutionPlan } from '../../api/types';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle, type } from '../../test/formProbe';
import { addChips, cancelAddDialog, chipsIn, removeChip, typeInAddDialog } from '../../test/chipList';
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

const list = () => bodyFind('[data-list-setting="Scout/positions"]')!;
const chips = () => chipsIn(list());

describe('Solution wizard - a list setting', () => {
  it('adds values through the dialog, one per line, without moving on, and installs with them', async () => {
    await yourPart();
    await type('Scout: region', 'Europe');

    await addChips(list(), 'Systems Analyst\nBusiness Analyst');
    expect(chips()).toEqual(['Systems Analyst', 'Business Analyst']);
    expect(bodyFind('[data-step="inputs"]')).not.toBeNull();

    const body = await install();
    expect((body.settings as Record<string, Record<string, unknown>>).Scout!.positions).toEqual(['Systems Analyst', 'Business Analyst']);
  });

  it('adds nothing when the dialog is cancelled', async () => {
    await yourPart();

    await typeInAddDialog(list(), 'Systems Analyst');
    await cancelAddDialog();

    expect(chips()).toEqual([]);
    expect(bodyFind('[data-step="inputs"]')).not.toBeNull();
  });

  it('removes a chip with its ×, and an untouched list stays empty and unsent', async () => {
    await yourPart();
    await type('Scout: region', 'Europe');

    await addChips(list(), 'Systems Analyst');
    await removeChip(list(), 'Systems Analyst');
    expect(chips()).toEqual([]);

    const body = await install();
    expect((body.settings as Record<string, Record<string, unknown>> | undefined)?.Scout?.positions).toBeUndefined();
  });
});
