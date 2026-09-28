// @vitest-environment happy-dom
//
// `TriggersDialog` and what a trigger costs: the member's measured cost in the editor (with runs,
// and with none measured), the confirmation a sub-5-minute schedule on an agent member asks for
// (and a plugin member does not), the wake choice and the daily cap, and spent today on the row.
// Nothing here is estimated - the lines say what runs reported, and runs that reported nothing are
// said as such, never as zero.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  createSchedule: vi.fn(),
  getMemberMeasuredCost: vi.fn(),
  listContainerTriggers: vi.fn(),
  listEvents: vi.fn(),
  listWatchRoots: vi.fn(),
  updateSchedule: vi.fn(),
}));

import TriggersDialog from '../TriggersDialog.vue';
import {
  createSchedule,
  getMemberMeasuredCost,
  listContainerTriggers,
  listEvents,
  listWatchRoots,
  updateSchedule,
} from '../../api/client';
import { asTeamId, type MemberMeasuredCost, type TeamTrigger } from '../../api/types';
import { useConsoleStore } from '../../stores/console';
import { bodyText, mountDialog as mountQuasarDialog, resetBody } from '../../test/mountQuasar';
import * as probe from '../../test/formProbe';

const measured: MemberMeasuredCost = {
  lastRuns: 10, measuredRuns: 8, unmeasuredRuns: 2, medianBillableTokens: 12400, kind: 'agent',
};

const nothingMeasured: MemberMeasuredCost = {
  lastRuns: 0, measuredRuns: 0, unmeasuredRuns: 0, medianBillableTokens: null, kind: 'agent',
};

function everyRow(over: Partial<TeamTrigger> = {}): TeamTrigger {
  return {
    id: 't1',
    team: asTeamId('Alpha'),
    container: 'Dev',
    name: 'Sweep',
    instruction: 'Sweep the board.',
    kind: 'every',
    expression: null,
    timezone: null,
    intervalSeconds: 3600,
    fireAt: null,
    idleOnly: true,
    enabled: true,
    nextDueAt: null,
    lastFiredAt: null,
    lastOutcome: null,
    lastSeq: null,
    missedCount: 0,
    createdAt: '2026-09-28T10:00:00Z',
    createdBy: 'u1',
    eventType: null,
    filter: null,
    ...over,
  };
}

const props = { team: asTeamId('Alpha'), container: 'Dev', subscribes: [] };

beforeEach(() => {
  vi.mocked(listEvents).mockResolvedValue([]);
  vi.mocked(listWatchRoots).mockResolvedValue([]);
  vi.mocked(listContainerTriggers).mockResolvedValue([]);
  vi.mocked(createSchedule).mockResolvedValue(everyRow());
});

afterEach(() => {
  vi.mocked(createSchedule).mockReset();
  vi.mocked(updateSchedule).mockReset();
  vi.mocked(getMemberMeasuredCost).mockReset();
  resetBody();
});

/** Mounts the dialog with the board's refresh after a save stubbed: that is a network read this
 *  spec is not about. */
async function mountDialog(...args: Parameters<typeof mountQuasarDialog>) {
  const wrapper = await mountQuasarDialog(...args);
  vi.spyOn(useConsoleStore(), 'refresh').mockResolvedValue(undefined);
  return wrapper;
}

/** The instruction box has a hint and no label; it is the only textarea in the editor. */
function theOnlyTextarea(): HTMLTextAreaElement {
  const found = [...document.body.querySelectorAll('textarea')];
  if (found.length !== 1) throw new Error(`expected exactly one textarea, found ${found.length}`);
  return found[0]!;
}

async function openNewTrigger() {
  probe.button('Add trigger').click();
  await probe.settle();
}

/** A new trigger, named and instructed, firing every `count` minutes. */
async function fillEveryMinutes(count: number) {
  await probe.type('Name', 'Sweep');
  const instruction = theOnlyTextarea();
  instruction.value = 'Sweep the board.';
  instruction.dispatchEvent(new Event('input', { bubbles: true }));
  await probe.type('Every', String(count));
}

function confirmation(): HTMLElement | null {
  return document.body.querySelector('.short-schedule-confirm');
}

describe('TriggersDialog, the measured cost line', () => {
  it('says the median billable tokens of the recent runs and how many were not measured', async () => {
    vi.mocked(getMemberMeasuredCost).mockResolvedValue(measured);
    const wrapper = await mountDialog(TriggersDialog, props);
    await openNewTrigger();

    expect(getMemberMeasuredCost).toHaveBeenCalledWith(asTeamId('Alpha'), 'Dev');
    const line = document.body.querySelector('.measured-cost')?.textContent ?? '';
    expect(line).toContain(`Median ${(12400).toLocaleString()} billable tokens per run`);
    expect(line).toContain('over its last 10 runs (8 measured, 2 not measured)');
    // No projection: nothing per day, nothing estimated.
    expect(line).not.toMatch(/day|estimat/i);

    wrapper.unmount();
  });

  it('says no runs were measured yet, rather than a zero', async () => {
    vi.mocked(getMemberMeasuredCost).mockResolvedValue(nothingMeasured);
    const wrapper = await mountDialog(TriggersDialog, props);
    await openNewTrigger();

    const line = document.body.querySelector('.measured-cost')?.textContent ?? '';
    expect(line).toContain('No runs measured yet.');
    expect(line).not.toMatch(/\b0 billable/);

    wrapper.unmount();
  });

  it('says so when the cost could not be read, and guesses nothing', async () => {
    vi.mocked(getMemberMeasuredCost).mockRejectedValue(new Error('No such member.'));
    const wrapper = await mountDialog(TriggersDialog, props);
    await openNewTrigger();

    expect(document.body.querySelector('.measured-cost')?.textContent)
      .toContain('Measured cost not available: No such member.');

    wrapper.unmount();
  });
});

describe('TriggersDialog, a short schedule', () => {
  it('asks an agent member to confirm a schedule more frequent than every 5 minutes', async () => {
    vi.mocked(getMemberMeasuredCost).mockResolvedValue(measured);
    const wrapper = await mountDialog(TriggersDialog, { ...props, memberKind: 'agent' });
    await openNewTrigger();
    await fillEveryMinutes(1);

    probe.button('Save').click();
    await probe.settle();

    expect(createSchedule).not.toHaveBeenCalled();
    const text = confirmation()?.textContent ?? '';
    expect(text).toContain('every 1 minute');
    expect(text).toContain(`median of ${(12400).toLocaleString()} billable tokens`);
    expect(text).toMatch(/plugin/);
    expect(text).toMatch(/event trigger/);

    probe.button('Save anyway').click();
    await probe.settle();

    expect(createSchedule).toHaveBeenCalledTimes(1);
    expect(vi.mocked(createSchedule).mock.calls[0]![1]).toMatchObject({ intervalSeconds: 60 });

    wrapper.unmount();
  });

  it('says the cost per run is not known when no run was measured', async () => {
    vi.mocked(getMemberMeasuredCost).mockResolvedValue(nothingMeasured);
    const wrapper = await mountDialog(TriggersDialog, { ...props, memberKind: 'agent' });
    await openNewTrigger();
    await fillEveryMinutes(2);

    probe.button('Save').click();
    await probe.settle();

    expect(confirmation()?.textContent).toContain('cost per run is not known');
    probe.button('Go back').click();
    await probe.settle();
    expect(createSchedule).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  it('does not ask at every 5 minutes', async () => {
    vi.mocked(getMemberMeasuredCost).mockResolvedValue(measured);
    const wrapper = await mountDialog(TriggersDialog, { ...props, memberKind: 'agent' });
    await openNewTrigger();
    await fillEveryMinutes(5);

    probe.button('Save').click();
    await probe.settle();

    expect(confirmation()).toBeNull();
    expect(createSchedule).toHaveBeenCalledTimes(1);

    wrapper.unmount();
  });

  it('never asks a plugin member, whose runs spend no model tokens', async () => {
    vi.mocked(getMemberMeasuredCost).mockResolvedValue({ ...nothingMeasured, kind: 'plugin' });
    const wrapper = await mountDialog(TriggersDialog, { ...props, memberKind: 'plugin' });
    await openNewTrigger();
    await fillEveryMinutes(1);

    probe.button('Save').click();
    await probe.settle();

    expect(confirmation()).toBeNull();
    expect(createSchedule).toHaveBeenCalledTimes(1);

    wrapper.unmount();
  });
});

describe('TriggersDialog, the wake choice and the daily cap', () => {
  it('offers the wake choice, defaulting a new trigger to hand-back or failure, with a plain hint', async () => {
    vi.mocked(getMemberMeasuredCost).mockResolvedValue(measured);
    const wrapper = await mountDialog(TriggersDialog, props);
    await openNewTrigger();

    const wake = probe.fieldWrapper('Wake the Manager when a run ends');
    expect(wake.textContent).toContain('Only if it hands back or fails');
    expect(bodyText()).toContain('The Manager is woken only when the run hands something back or fails.');

    wrapper.unmount();
  });

  it('sends the wake choice and the daily cap with a new trigger', async () => {
    vi.mocked(getMemberMeasuredCost).mockResolvedValue(measured);
    const wrapper = await mountDialog(TriggersDialog, props);
    await openNewTrigger();
    await fillEveryMinutes(10);

    expect(bodyText()).toContain('Blank is no cap.');
    await probe.type('Daily token cap (optional)', '50000');

    probe.button('Save').click();
    await probe.settle();

    expect(createSchedule).toHaveBeenCalledWith(asTeamId('Alpha'), expect.objectContaining({
      wakeManager: 'onHandbackOrFailure',
      dailyTokenCap: 50000,
    }));

    wrapper.unmount();
  });

  it('refuses a cap that is not a whole number of 1 or more, under the field', async () => {
    vi.mocked(getMemberMeasuredCost).mockResolvedValue(measured);
    const wrapper = await mountDialog(TriggersDialog, props);
    await openNewTrigger();
    await fillEveryMinutes(10);

    await probe.type('Daily token cap (optional)', '0');
    await probe.blur('Daily token cap (optional)');

    expect(probe.hasError('Daily token cap (optional)')).toBe(true);
    expect(probe.isDisabled('Save')).toBe(true);

    wrapper.unmount();
  });

  it('reads a trigger from before the setting as Always, and patches only what changed', async () => {
    vi.mocked(getMemberMeasuredCost).mockResolvedValue(measured);
    vi.mocked(listContainerTriggers).mockResolvedValue([everyRow()]);
    vi.mocked(updateSchedule).mockResolvedValue(everyRow());
    const wrapper = await mountDialog(TriggersDialog, props);

    (document.body.querySelector('[aria-label="Edit this trigger"]') as HTMLButtonElement).click();
    await probe.settle();

    expect(probe.fieldWrapper('Wake the Manager when a run ends').textContent).toContain('Always');

    await probe.type('Daily token cap (optional)', '20000');
    probe.button('Save').click();
    await probe.settle();

    expect(updateSchedule).toHaveBeenCalledWith(asTeamId('Alpha'), 't1', { dailyTokenCap: 20000 });

    wrapper.unmount();
  });

  it('shows spent today against the cap, and unmeasured runs as such, never as zero', async () => {
    vi.mocked(getMemberMeasuredCost).mockResolvedValue(measured);
    vi.mocked(listContainerTriggers).mockResolvedValue([
      everyRow({
        id: 'a',
        dailyTokenCap: 50000,
        spentToday: { billableTokens: 3000, measuredRuns: 2, unmeasuredRuns: 1 },
        capReachedToday: false,
      }),
      everyRow({
        id: 'b',
        name: 'Other',
        dailyTokenCap: 10000,
        spentToday: { billableTokens: 0, measuredRuns: 0, unmeasuredRuns: 2 },
        capReachedToday: true,
      }),
    ]);
    const wrapper = await mountDialog(TriggersDialog, props);

    const lines = [...document.body.querySelectorAll('.trigger-spend')].map((el) => el.textContent?.trim());
    expect(lines[0]).toBe(
      `spent today ${(3000).toLocaleString()} tokens + 1 run not measured / cap ${(50000).toLocaleString()} tokens`,
    );
    expect(lines[1]).toContain('spent today nothing measured + 2 runs not measured');
    expect(lines[1]).not.toMatch(/spent today 0 tokens/);
    expect(lines[1]).toContain('cap reached, fires again tomorrow');

    wrapper.unmount();
  });
});
