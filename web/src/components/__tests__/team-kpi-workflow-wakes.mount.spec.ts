// @vitest-environment happy-dom
//
// A WORKFLOW ROW WITH A PENDING WAKE SAYS SO in the Workflows dialog, as the member's card does:
// "wakes at 9:41 PM: <note>" on the reader's clock (the Host sends UTC), with a person's Cancel.
// Cancel calls the person's route under the team and the line goes; a refusal is said under it.
//
// The browser is in New York, on summer time (UTC-4) on these dates. The dialog TELEPORTS, so the
// assertions read `document.body`.
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';

const { cancelWake } = vi.hoisted(() => ({ cancelWake: vi.fn() }));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  // The Activity tile reads `/activity` when it mounts; nothing here is about it.
  getTeamActivity: () => new Promise(() => {}),
  cancelWake,
}));

import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

import TeamKpiStrip from '../TeamKpiStrip.vue';
import { useConsoleStore } from '../../stores/console';
import { asTeamId } from '../../api/types';
import type { PendingWake, TeamWorkflows, TeamWorkflowTiming } from '../../api/types';
import { localMinute } from '../../lib/localTime';
import { bodyFind, resetBody } from '../../test/mountQuasar';

const teamId = asTeamId('alpha');

/** 9:41 PM in New York on the 15th; 01:41 on the 16th in UTC. */
const Due = '2026-10-16T01:41:00Z';
const Now = '2026-10-16T01:30:00Z';

const wake = (over: Partial<PendingWake> = {}): PendingWake => ({
  id: 7,
  member: 'DeveloperIda',
  workflow: 4100,
  dueAt: Due,
  askedAt: '2026-10-16T01:31:00Z',
  note: 'check whether the certificate was issued',
  ...over,
});

function timing(over: Partial<TeamWorkflowTiming> = {}): TeamWorkflowTiming {
  return {
    available: true,
    correlation: 4100,
    state: 'Undeclared',
    startedAt: '2026-10-16T01:00:00Z',
    endedAt: null,
    serverNow: Now,
    executionSeconds: 0,
    partial: false,
    runsCounted: 0,
    runsUnfinished: 0,
    blockedBy: null,
    runsFailed: 0,
    failedMembers: [],
    missing: null,
    members: [],
    lastActivityAt: '2026-10-16T01:31:00Z',
    awaitingFrom: null,
    subject: 'Renew the certificate',
    ...over,
  } as TeamWorkflowTiming;
}

function payload(rows: TeamWorkflowTiming[]): TeamWorkflows {
  return {
    available: true,
    openCount: rows.length,
    totalCount: rows.length,
    earliestStartedAt: null,
    serverNow: Now,
    workflows: rows,
    missing: null,
  } as unknown as TeamWorkflows;
}

let zone: string | undefined;
let wrapper: VueWrapper | null = null;

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
  vi.setSystemTime(new Date(Now));
  cancelWake.mockReset();
});

afterEach(() => {
  vi.useRealTimers();
  wrapper?.unmount();
  wrapper = null;
  resetBody();
});

/** Mounted closed, then the Workflows dialog is opened by clicking the member line, as a person does. */
async function openDialog(rows: TeamWorkflowTiming[]) {
  setActivePinia(createPinia());
  const board = useConsoleStore();
  board.$patch({ teams: [{ id: teamId, name: 'Alpha', memberAgents: [], repos: [] }], activeTeamId: teamId, overviewLanded: true, workflowSpendLimit: null });
  // The refetch after a cancel is the store's own business; nothing here is about it.
  board.pullWorkflows = vi.fn(async () => {});

  wrapper = mount(TeamKpiStrip, {
    props: { teamId, containers: [], usage: null, timing: null, workflows: payload(rows), findings: null, clockOffset: 0 },
  });

  await flushPromises();
  await wrapper.find('.team-kpi-members').trigger('click');
  await flushPromises();
}

const line = () => bodyFind('[data-test="workflow-wakes"]');

describe('the Workflows dialog, a row with a pending wake', () => {
  it('says when it wakes on the reader\'s clock, with the note and Cancel', async () => {
    await openDialog([timing({ wakes: [wake()] })]);

    const text = line()?.textContent ?? '';
    expect(text).toContain(`wakes at ${localMinute(Date.parse(Due))}: check whether the certificate was issued`);
    expect(text).toContain('9:41');
    expect(text).not.toContain('1:41 AM');
    expect(line()?.closest('.workflow-row')?.textContent).toContain('Renew the certificate');
    expect(bodyFind('[data-action="cancel-wake"]')?.textContent).toContain('Cancel');
  });

  it('shows nothing on a row with no pending wake', async () => {
    await openDialog([timing()]);

    expect(bodyFind('.workflow-row')).not.toBeNull();
    expect(line()).toBeNull();
  });

  it('cancels that wake on the team, and the line goes', async () => {
    cancelWake.mockResolvedValue({ wake: wake(), cancelled: true });
    await openDialog([timing({ wakes: [wake()] })]);

    bodyFind('[data-action="cancel-wake"]')!.click();
    await flushPromises();

    expect(cancelWake).toHaveBeenCalledWith('alpha', 7);
    expect(line()).toBeNull();
  });

  it('says the Host\'s refusal under that row and keeps the line when the cancel is refused', async () => {
    cancelWake.mockRejectedValue(new Error('No pending wake 7 on this team. It has fired or been cancelled.'));
    await openDialog([timing({ wakes: [wake()] })]);

    bodyFind('[data-action="cancel-wake"]')!.click();
    await flushPromises();

    expect(bodyFind('[data-test="workflow-wake-error"]')?.textContent).toContain('It has fired or been cancelled.');
    expect(line()).not.toBeNull();
  });
});
