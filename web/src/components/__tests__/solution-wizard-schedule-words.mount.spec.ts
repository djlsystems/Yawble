// @vitest-environment happy-dom
//
// THE SOLUTION WIZARD SAYS A SCHEDULE IN WORDS, IN THE PERSON'S OWN ZONE. Review reads "weekdays at
// 8:00 AM London time, 3:00 AM yours" for a London cron on a New York browser, with the raw cron
// under Advanced, never "cron 0 0 8 * * 1-5 (Europe/London)" on its own; the install's "first runs
// at" says the same 3:00 AM; and an update that moves a schedule shows its old words beside its new.
//
// The browser is in New York and the day is Wednesday 7 October 2026, when London is five hours
// ahead of New York.
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

import SolutionWizard from '../SolutionWizard.vue';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle } from '../../test/formProbe';
import { Folder, UpdateDiff, fakeHost, hostPlan, installedRow, reply, steps, updatePreview, wizardRoutes, type Call, type Route } from '../../test/solutionFixtures';
import { panelRead, panelTrigger } from '../../test/solutionPanelFixtures';

let calls: Call[] = [];
let zone: string | undefined;

/** The plan as the Host sends it: its own words for the schedule are the raw cron. */
function plan() {
  const read = hostPlan();
  read.triggers = read.triggers.map((trigger) =>
    trigger.name === 'Morning summary' ? { ...trigger, schedule: 'cron 0 0 8 * * 1-5 (Europe/London)' } : trigger,
  );
  return read;
}

const installed: Route = (call) =>
  call.url === '/api/solutions/install'
    ? reply(200, {
        ok: true,
        team: 'job-tracker',
        teamName: 'Job Tracker',
        version: '1.1.0',
        missing: [],
        steps: steps(8),
        firstRuns: [
          // Thursday 8:00 London time, the next weekday morning.
          { trigger: 'Morning summary', member: 'Coordinator', runAtInstall: false, ranNow: false, outcome: 'scheduled', at: '2026-10-08T07:00:00Z', next: null },
        ],
      })
    : undefined;

function serve(extra: Route[] = [], installedTeams = [] as ReturnType<typeof installedRow>[]) {
  calls = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          ...extra,
          installed,
          (call) => (call.url === '/api/teams/job-tracker/solution' ? reply(200, { team: 'job-tracker', missing: [] }) : undefined),
          ...wizardRoutes({ plan: plan(), installed: installedTeams }),
        ],
        calls,
      ),
    ),
  );
}

beforeAll(() => {
  zone = process.env.TZ;
  process.env.TZ = 'America/New_York';
});

afterAll(() => {
  if (zone === undefined) delete process.env.TZ;
  else process.env.TZ = zone;
});

beforeEach(() => {
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(new Date('2026-10-07T16:00:00Z'));
  setActivePinia(createPinia());
  serve();
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  resetBody();
});

const morning = () => bodyFind('[data-trigger="Morning summary"]')!;

describe('Solution wizard - a schedule in words', () => {
  it("Review says a cron in words in London's clock and the reader's, with the raw cron under Advanced", async () => {
    await mountDialog(SolutionWizard, { folder: Folder }, { pinia: false });
    await settle();
    button('Next').click();
    await settle();

    expect(morning().querySelector('[data-source]')!.textContent).toBe('weekdays at 8:00 AM London time, 3:00 AM yours');
    const advanced = morning().querySelector('[data-cron]')!;
    expect(advanced.querySelector('summary')!.textContent).toBe('Advanced');
    expect(advanced.textContent).toContain('cron 0 0 8 * * 1-5 (Europe/London)');
  });

  it('the install says it first runs at the same 3:00 AM Review promised', async () => {
    await mountDialog(SolutionWizard, { folder: Folder }, { pinia: false });
    await settle();
    button('Next').click();
    await settle();
    const promised = morning().querySelector('[data-source]')!.textContent!;

    for (let index = 0; index < 2; index++) {
      button('Next').click();
      await settle();
    }
    button('Install').click();
    await settle();

    const line = bodyFind('[data-first-run="Morning summary"]')!.textContent!.trim();
    expect(line).toBe('Morning summary first runs at Thu 3:00 AM (8:00 AM London time).');
    expect(promised).toContain('3:00 AM yours');
  });

  it("an update that moves a schedule shows its old words beside its new", async () => {
    serve(
      [
        (call) =>
          call.url === '/api/solutions/preview' && (call.body as { team?: string }).team === 'job-tracker'
            ? reply(200, { ...updatePreview('job-tracker', plan()), diff: { ...UpdateDiff, triggers: { added: [], changed: ['Morning summary'], removed: [] } } })
            : undefined,
        (call) =>
          call.url === '/api/teams/job-tracker/solution/panel'
            ? reply(
                200,
                panelRead({
                  members: [{ packageName: 'Coordinator', member: 'Manager', kind: 'agent', role: 'manager', state: 'idle', lastRun: null }],
                  triggers: [
                    panelTrigger({
                      id: 'trg_morning',
                      packageName: 'Morning summary',
                      packageKind: 'schedule',
                      kind: 'cron',
                      expression: '0 0 7 * * 1-5',
                      timezone: 'Europe/London',
                      container: 'Manager',
                      instruction: plan().triggers.find((trigger) => trigger.name === 'Morning summary')!.instruction,
                      wakeManager: 'never',
                      idleOnly: true,
                      dailyTokenCap: 100000,
                    }),
                  ],
                }),
              )
            : undefined,
      ],
      [installedRow('job-tracker', 'Job Tracker', '1.0.0')],
    );
    await mountDialog(SolutionWizard, { folder: Folder, team: 'job-tracker' }, { pinia: false });
    await settle();
    button('Next').click();
    await settle();

    const when = morning().querySelector('[data-change="When"]')!;
    expect(when.querySelector('[data-was]')!.textContent).toContain('weekdays at 7:00 AM London time, 2:00 AM yours');
    expect(when.querySelector('[data-now]')!.textContent).toContain('weekdays at 8:00 AM London time, 3:00 AM yours');
    expect(morning().querySelector('[data-change="Daily cap"]')).toBeNull();
  });
});
