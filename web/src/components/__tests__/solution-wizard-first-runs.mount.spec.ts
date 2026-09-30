// @vitest-environment happy-dom
//
// THE SOLUTION WIZARD: A SCHEDULE'S FIRST RUN. The Review says a `runAtInstall` schedule "runs once
// now, then every ...". The result names each schedule's first run: "Scan for postings ran now", or
// "Morning summary first runs at 8:00 AM", and for a first run at install that did not happen, why
// and when it first runs instead.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

import SolutionWizard from '../SolutionWizard.vue';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle } from '../../test/formProbe';
import { firstRunLine, firstRunTime } from '../../lib/solutions';
import { Folder, fakeHost, hostPlan, reply, steps, wizardRoutes, type Call, type Route } from '../../test/solutionFixtures';

let calls: Call[] = [];

/** 8:51 PM and 8:00 AM tomorrow in whatever timezone the test runs in. */
const today = new Date();
const ranAt = new Date(today.getFullYear(), today.getMonth(), today.getDate(), 20, 51).toISOString();
const morningAt = new Date(today.getFullYear(), today.getMonth(), today.getDate() + 1, 8, 0).toISOString();

function planWithFirstRun() {
  const plan = hostPlan();
  plan.triggers = plan.triggers.map((trigger) =>
    trigger.name === 'Scan for postings'
      ? { ...trigger, runAtInstall: true }
      : trigger.name === 'Morning summary'
        ? { ...trigger, runAtInstall: true, schedule: 'runs once now, then At 08:00, Monday to Friday (Europe/London)' }
        : trigger,
  );
  return plan;
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
          { trigger: 'Scan for postings', member: 'Scout', runAtInstall: true, ranNow: true, outcome: 'fired', at: ranAt, next: null },
          { trigger: 'Morning summary', member: 'Coordinator', runAtInstall: false, ranNow: false, outcome: 'scheduled', at: morningAt, next: null },
          { trigger: 'Evening summary', member: 'Coordinator', runAtInstall: true, ranNow: false, outcome: 'skipped', at: morningAt, next: null },
        ],
      })
    : undefined;

function serve(extra: Route[] = []) {
  calls = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          ...extra,
          installed,
          (call) => (call.url === '/api/teams/job-tracker/solution' ? reply(200, { team: 'job-tracker', missing: [] }) : undefined),
          ...wizardRoutes({ plan: planWithFirstRun() }),
        ],
        calls,
      ),
    ),
  );
}

beforeEach(() => {
  setActivePinia(createPinia());
  serve();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

describe('Solution wizard - a schedule first run', () => {
  it('the Review says a runAtInstall schedule runs once now, then on its clock', async () => {
    await mountDialog(SolutionWizard, { folder: Folder }, { pinia: false });
    await settle();
    button('Next').click();
    await settle();

    const source = (name: string) => bodyFind(`[data-trigger="${name}"]`)!.querySelector('[data-source]')!.textContent;
    expect(source('Scan for postings')).toBe('runs once now, then every hour');
    expect(source('Morning summary')).toBe('runs once now, then At 08:00, Monday to Friday (Europe/London)');
    expect(source('Apply pressed')).toBe('on site.action where siteAction eq tracker/apply');
  });

  it('the result names each schedule first run: ran now, or when it first runs', async () => {
    await mountDialog(SolutionWizard, { folder: Folder }, { pinia: false });
    await settle();
    for (let index = 0; index < 3; index++) {
      button('Next').click();
      await settle();
    }
    button('Install').click();
    await settle();

    const line = (name: string) => bodyFind(`[data-first-run="${name}"]`)!.textContent!.trim();
    expect(line('Scan for postings')).toBe('Scan for postings ran now.');
    expect(bodyFind('[data-first-run="Scan for postings"]')!.getAttribute('data-ran-now')).toBe('true');
    expect(line('Morning summary')).toBe(`Morning summary first runs at ${firstRunTime(morningAt)}.`);
    expect(line('Evening summary')).toBe(`Evening summary did not run now (skipped); it first runs at ${firstRunTime(morningAt)}.`);
  });
});

describe('firstRunTime and firstRunLine', () => {
  it('says the time alone today and the weekday on another day', () => {
    const now = new Date(2026, 8, 30, 12, 0);
    expect(firstRunTime(new Date(2026, 8, 30, 20, 51).toISOString(), now)).toBe('8:51 PM');
    expect(firstRunTime(new Date(2026, 9, 1, 8, 0).toISOString(), now)).toBe('Thu 8:00 AM');
  });

  it('a first run at install that could not fire is the run outcome, with when it first runs', () => {
    const now = new Date(2026, 8, 30, 12, 0);
    const at = new Date(2026, 8, 30, 13, 0).toISOString();
    const run = { trigger: 'Fetch jobs', member: 'Scout', runAtInstall: true, ranNow: false, outcome: 'failed', at, next: null };
    expect(firstRunLine(run, now)).toBe('Fetch jobs could not run now; it first runs at 1:00 PM');
    expect(firstRunLine({ ...run, ranNow: true, outcome: 'fired' }, now)).toBe('Fetch jobs ran now');
    expect(firstRunLine({ ...run, runAtInstall: false, outcome: 'scheduled' }, now)).toBe('Fetch jobs first runs at 1:00 PM');
  });
});
