// @vitest-environment happy-dom
//
// ONE CLOCK ON THE SOLUTION PANEL: every time a person reads there is in the browser's zone and
// format. A status line the package fills ("last checked 2026-10-07T14:41:07Z") reads the instant
// as the browser's clock does, and so do a run's output, a blocked reason and the launcher's tile;
// a schedule asleep on its cap says when it resumes on the reader's clock, not the trigger's.
//
// The browser is in London, on summer time (UTC+1) on these dates.
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';

import SolutionPanel from '../SolutionPanel.vue';
import SolutionsLauncher from '../SolutionsLauncher.vue';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { fakeHost, reply, type Call } from '../../test/solutionFixtures';
import { launcherRow, panelRead, panelRoutes, panelTrigger, openSection } from '../../test/solutionPanelFixtures';
import { localTime } from '../../lib/localTime';

let calls: Call[] = [];
let zone: string | undefined;

const Checked = '2026-10-07T14:41:07Z';
/** What the browser's own clock says for it: "10/7/2026, 3:41:07 PM" on a London en-US browser. */
const local = (iso: string) => localTime(Date.parse(iso), { date: true });

function read() {
  const panel = panelRead({ status: `3 new jobs · last checked ${Checked}` });
  panel.recentRuns = panel.recentRuns.map((run, index) => (index === 0 ? { ...run, output: `Checked the board at ${Checked}.` } : run));
  panel.triggers = [
    panelTrigger({
      id: 'trg_scan',
      packageName: 'Scan for postings',
      packageKind: 'schedule',
      kind: 'cron',
      expression: '0 0 8 * * *',
      timezone: 'Asia/Tokyo',
      container: 'scout',
      dailyTokenCap: 1000,
      capReachedToday: true,
      // Midnight in Tokyo, 4 PM the day before in London.
      cappedUntil: '2026-10-07T15:00:00Z',
    }),
  ];
  return panel;
}

beforeAll(() => {
  zone = process.env.TZ;
  process.env.TZ = 'Europe/London';
});

afterAll(() => {
  if (zone === undefined) delete process.env.TZ;
  else process.env.TZ = zone;
});

beforeEach(() => {
  calls = [];
  vi.useFakeTimers({ toFake: ['Date'] });
  vi.setSystemTime(new Date('2026-10-07T09:00:00Z'));
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          (call) =>
            call.method === 'GET' && call.url === '/api/solutions/installed'
              ? reply(200, [launcherRow({ status: `3 new jobs · last checked ${Checked}` })])
              : undefined,
          ...panelRoutes(read),
        ],
        calls,
      ),
    ),
  );
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  resetBody();
});

describe('the solution panel: one clock', () => {
  it("reads an instant in the package's status line on the browser's clock, never as the raw ISO string", async () => {
    await mountDialog(SolutionPanel, { team: 'job-tracker' });
    await settle();

    expect(bodyFind('[data-panel-status]')!.textContent).toBe(`3 new jobs · last checked ${local(Checked)}`);
    expect(local(Checked)).toContain('3:41:07 PM');
    expect(bodyText()).not.toContain('T14:41:07Z');
  });

  it("reads a run's output and when a capped schedule resumes on the browser's clock", async () => {
    await mountDialog(SolutionPanel, { team: 'job-tracker' });
    await settle();

    expect(bodyFind('[data-capped]')!.textContent).toBe('Capped until 4:00 PM');

    await openSection('results');
    expect(bodyFind('[data-run-output]')!.textContent).toBe(`Checked the board at ${local(Checked)}.`);
    expect(bodyText()).not.toContain('T14:41:07Z');
  });

  it("the launcher's tile reads the status line's instant on the browser's clock too", async () => {
    await mountDialog(SolutionsLauncher, {});
    await settle();

    expect(bodyFind('[data-tile-status]')!.textContent).toBe(`3 new jobs · last checked ${local(Checked)}`);
  });

  it("a trigger's Details say its cron in words on both clocks, with the raw cron beside", async () => {
    await mountDialog(SolutionPanel, { team: 'job-tracker' });
    await settle();
    await openSection('controls');
    bodyFind('[data-trigger-details]')!.click();
    await settle();

    expect(bodyFind('[data-trigger-fact="Fires"]')!.textContent).toBe('every day at 8:00 AM Tokyo time, 12:00 AM yours');
    expect(bodyFind('[data-trigger-fact="Cron"]')!.textContent).toBe('cron 0 0 8 * * * (Asia/Tokyo)');
  });
});
