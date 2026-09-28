// @vitest-environment happy-dom
//
// `TriggersDialog` and a trigger its daily cap is holding: a schedule asleep until the next day says
// "Capped until <time>", and an event trigger says how many fires the cap skipped today.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getMemberMeasuredCost: vi.fn(),
  listContainerTriggers: vi.fn(),
  listEvents: vi.fn(),
  listWatchRoots: vi.fn(),
}));

import TriggersDialog from '../TriggersDialog.vue';
import { getMemberMeasuredCost, listContainerTriggers, listEvents, listWatchRoots } from '../../api/client';
import { asTeamId, type TeamTrigger } from '../../api/types';
import { mountDialog, resetBody } from '../../test/mountQuasar';

function row(over: Partial<TeamTrigger> = {}): TeamTrigger {
  return {
    id: 't1',
    team: asTeamId('Alpha'),
    container: 'Dev',
    name: 'Sweep',
    instruction: 'Sweep the board.',
    kind: 'every',
    expression: null,
    timezone: null,
    intervalSeconds: 60,
    fireAt: null,
    idleOnly: false,
    enabled: true,
    nextDueAt: '2026-09-29T00:00:00Z',
    lastFiredAt: null,
    lastOutcome: 'capped',
    lastSeq: 10,
    missedCount: 0,
    createdAt: '2026-09-28T10:00:00Z',
    createdBy: 'u1',
    eventType: null,
    filter: null,
    dailyTokenCap: 4000,
    spentToday: { billableTokens: 4500, measuredRuns: 3, unmeasuredRuns: 0 },
    capReachedToday: true,
    ...over,
  };
}

const props = { team: asTeamId('Alpha'), container: 'Dev', subscribes: [] };

beforeEach(() => {
  vi.mocked(listEvents).mockResolvedValue([]);
  vi.mocked(listWatchRoots).mockResolvedValue([]);
  vi.mocked(getMemberMeasuredCost).mockResolvedValue({
    lastRuns: 3, measuredRuns: 3, unmeasuredRuns: 0, medianBillableTokens: 1500, kind: 'agent',
  });
});

afterEach(() => {
  vi.mocked(listContainerTriggers).mockReset();
  resetBody();
});

describe('TriggersDialog: what the daily cap is holding', () => {
  it('shows a schedule asleep on its cap as capped until it resumes, and an event trigger its skips today', async () => {
    vi.mocked(listContainerTriggers).mockResolvedValue([
      row({ cappedUntil: '2026-09-28T15:00:00Z', skippedToday: 1, kind: 'cron', expression: '0 * * * * *', timezone: 'Asia/Tokyo' }),
      row({
        id: 't2', name: 'On echo', kind: 'event', eventType: 'plugin.sample-echo.done', intervalSeconds: null,
        nextDueAt: null, cappedUntil: null, skippedToday: 3,
      }),
      row({ id: 't3', name: 'Free', lastOutcome: 'fired', capReachedToday: false, cappedUntil: null, skippedToday: 0 }),
    ]);
    const wrapper = await mountDialog(TriggersDialog, props);

    const lines = [...document.body.querySelectorAll('.trigger-capped')].map((el) => el.textContent?.trim());
    expect(lines).toEqual(['Capped until 2026-09-29 00:00 Asia/Tokyo', 'skipped today: 3']);

    wrapper.unmount();
  });
});
