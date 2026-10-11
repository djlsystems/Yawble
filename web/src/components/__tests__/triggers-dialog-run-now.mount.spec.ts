// @vitest-environment happy-dom
//
// `TriggersDialog`'s Run now: a schedule row has the button and an event row does not; pressing it
// calls the run route and says what happened - a capped Run now is said as skipped, not as a run.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const notify = vi.fn();

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getMemberMeasuredCost: vi.fn(),
  listContainerTriggers: vi.fn(),
  listEvents: vi.fn(),
  listWatchRoots: vi.fn(),
  runScheduleNow: vi.fn(),
}));

import { flushPromises } from '@vue/test-utils';
import TriggersDialog from '../TriggersDialog.vue';
import { getMemberMeasuredCost, listContainerTriggers, listEvents, listWatchRoots, runScheduleNow } from '../../api/client';
import { asTeamId, type TeamTrigger } from '../../api/types';
import { mountDialog, resetBody } from '../../test/mountQuasar';

function row(over: Partial<TeamTrigger> = {}): TeamTrigger {
  return {
    id: 't1',
    team: asTeamId('Alpha'),
    container: 'Dev',
    name: 'Nightly',
    instruction: 'Look.',
    kind: 'cron',
    expression: '0 0 3 * * *',
    timezone: 'UTC',
    intervalSeconds: null,
    fireAt: null,
    idleOnly: false,
    enabled: true,
    nextDueAt: '2026-10-01T03:00:00Z',
    lastFiredAt: null,
    lastOutcome: null,
    lastSeq: null,
    missedCount: 0,
    createdAt: '2026-09-28T10:00:00Z',
    createdBy: 'u1',
    eventType: null,
    filter: null,
    dailyTokenCap: 1000,
    spentToday: { billableTokens: 0, measuredRuns: 0, unmeasuredRuns: 0 },
    capReachedToday: false,
    ...over,
  };
}

const props = { team: asTeamId('Alpha'), container: 'Dev', subscribes: [] };

beforeEach(() => {
  vi.mocked(listEvents).mockResolvedValue([]);
  vi.mocked(listWatchRoots).mockResolvedValue([]);
  vi.mocked(getMemberMeasuredCost).mockResolvedValue({
    lastRuns: 0, measuredRuns: 0, unmeasuredRuns: 0, medianBillableTokens: null, kind: 'agent',
  });
  vi.mocked(listContainerTriggers).mockResolvedValue([
    row(),
    row({ id: 't2', name: 'On echo', kind: 'event', eventType: 'plugin.sample-echo.done', expression: null, nextDueAt: null }),
  ]);
});

afterEach(() => {
  vi.mocked(listContainerTriggers).mockReset();
  vi.mocked(runScheduleNow).mockReset();
  notify.mockReset();
  resetBody();
});

function runButtons(): HTMLButtonElement[] {
  return [...document.body.querySelectorAll<HTMLButtonElement>('button[aria-label="Run this schedule now"]')];
}

describe('TriggersDialog: Run now', () => {
  it('offers Run now on a schedule only, and a fire is said as running', async () => {
    vi.mocked(runScheduleNow).mockResolvedValue({
      outcome: 'fired', reason: null, seq: 42, trigger: row({ lastOutcome: 'fired', lastSeq: 42 }),
    });
    const wrapper = await mountDialog(TriggersDialog, props);

    expect(runButtons()).toHaveLength(1);
    runButtons()[0]!.click();
    await flushPromises();

    expect(runScheduleNow).toHaveBeenCalledWith('Alpha', 't1');
    expect(notify).toHaveBeenCalledWith(expect.objectContaining({ type: 'positive', message: 'Nightly is running now.' }));

    wrapper.unmount();
  });

  it('says a capped Run now was skipped, with the reason', async () => {
    vi.mocked(runScheduleNow).mockResolvedValue({
      outcome: 'capped', reason: 'daily token cap reached', seq: null,
      trigger: row({ lastOutcome: 'capped', capReachedToday: true }),
    });
    const wrapper = await mountDialog(TriggersDialog, props);

    runButtons()[0]!.click();
    await flushPromises();

    expect(notify).toHaveBeenCalledWith(expect.objectContaining({
      type: 'warning', message: 'Nightly was skipped: daily token cap reached.',
    }));

    wrapper.unmount();
  });
});
