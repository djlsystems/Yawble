// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import TriggersDialog from '../../src/renderer/src/components/TriggersDialog.vue';
import type { AgentCard, YawbleApi, Trigger } from '../../src/shared/api';

function fakeApi(answers: Record<string, (params: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = answers[method];
      if (!answer) throw new Error(`unexpected ${method}`);
      return answer(params) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls };
}

const card = (id: number, name: string, role: 'manager' | 'member'): AgentCard => ({
  member: { id, teamId: 1, team: 'ops', name, role, cli: 'claude', instructions: '', model: '', credential: 'default' },
  state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null, runStartedAt: null,
  lastRunState: null, lastRunReason: null, net: 'model + bus', wallTimeout: '2 h', loosened: [], policyProblems: [],
} as AgentCard);

const team = { id: 1, name: 'ops', repoPath: null, running: 0, queued: 0 } as any;
const nightly: Trigger = {
  id: 7, teamId: 1, memberId: 2, name: 'nightly', instruction: 'Check the build.', onlyWhenIdle: true, enabled: true,
  schedule: { kind: 'every', intervalSec: 86400, cron: null, timezone: null, fireAt: null },
  nextDueAt: '2026-11-07T12:00:00Z', lastFiredAt: null, lastOutcome: 'missed', lastReason: '2 fire(s) passed while the app was not running',
  lastSeq: null, missedCount: 2, wakeManager: 'onHandbackOrFailure',
};

describe('triggers dialog', () => {
  it('lists the team\'s triggers with their schedule, agent and missed fires', async () => {
    const { api } = fakeApi({ 'triggers.list': () => [nightly] });
    const w = mount(TriggersDialog, { props: { api, team, agents: [card(1, 'Manager', 'manager'), card(2, 'Ada', 'member')] } });
    await flushPromises();
    const row = w.find('[data-test=trigger-nightly]').text();
    expect(row).toContain('Ada');
    expect(row).toContain('every 1 day(s)');
    expect(row).toContain('2 missed');
    expect(row).toContain('Manager: only if it hands back or fails');
  });

  it('saves the Manager wake choice the person picks', async () => {
    const { api, calls } = fakeApi({
      'triggers.list': () => [nightly],
      'triggers.preview': () => ({ error: null, next: [] }),
      'triggers.save': (p) => ({ ...nightly, wakeManager: p.wakeManager }),
    });
    const w = mount(TriggersDialog, { props: { api, team, agents: [card(2, 'Ada', 'member')] } });
    await flushPromises();
    await w.find('[data-test=trigger-edit]').trigger('click');
    expect((w.find('[data-test=trigger-wake-manager]').element as HTMLSelectElement).value).toBe('onHandbackOrFailure');
    await w.find('[data-test=trigger-wake-manager]').setValue('never');
    await w.find('[data-test=trigger-save]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'triggers.save')!.params).toMatchObject({ id: 7, wakeManager: 'never' });
  });

  it('the instruction box spans the form, with its label above it', async () => {
    const { api } = fakeApi({ 'triggers.list': () => [], 'triggers.preview': () => ({ error: null, next: [] }) });
    const w = mount(TriggersDialog, { props: { api, team, agents: [card(2, 'Ada', 'member')] } });
    await flushPromises();
    await w.find('[data-test=trigger-new]').trigger('click');
    const box = w.find('[data-test=trigger-instruction]');
    expect(box.classes()).toContain('wide');
    const label = box.element.parentElement!;
    expect(label.tagName).toBe('LABEL');
    expect(label.parentElement!.classList.contains('form')).toBe(true); // a direct field of the form: stacked, full width
    expect(label.firstChild!.textContent!.trim()).toBe('Instruction'); // the label text comes before the box
  });

  it('saves a new every-N trigger in seconds, for the chosen agent', async () => {
    const { api, calls } = fakeApi({
      'triggers.list': () => [],
      'triggers.preview': () => ({ error: null, next: ['2026-11-07T13:00:00Z'] }),
      'triggers.save': (p) => ({ ...nightly, id: 8, name: p.name, memberId: p.memberId }),
    });
    const w = mount(TriggersDialog, { props: { api, team, agents: [card(1, 'Manager', 'manager'), card(2, 'Ada', 'member')] } });
    await flushPromises();
    await w.find('[data-test=trigger-new]').trigger('click');
    await w.find('[data-test=trigger-name]').setValue('hourly sweep');
    await w.find('[data-test=trigger-member]').findAll('option')[1].setSelected(); // Ada
    await w.find('[data-test=trigger-every]').setValue(2);
    await w.find('[data-test=trigger-instruction]').setValue('Sweep the queue.');
    await w.find('[data-test=trigger-save]').trigger('click');
    await flushPromises();
    const save = calls.find((c) => c.method === 'triggers.save')!.params;
    expect(save).toMatchObject({ name: 'hourly sweep', memberId: 2, kind: 'every', intervalSec: 7200, instruction: 'Sweep the queue.', onlyWhenIdle: true, wakeManager: 'onHandbackOrFailure' });
    expect(w.find('[data-test=triggers-note]').text()).toContain('Saved hourly sweep');
  });

  it('Run now says what happened', async () => {
    const { api } = fakeApi({ 'triggers.list': () => [nightly], 'triggers.runNow': () => ({ outcome: 'skipped', seq: null, reason: 'Ada was busy (only when idle)' }) });
    const w = mount(TriggersDialog, { props: { api, team, agents: [card(2, 'Ada', 'member')] } });
    await flushPromises();
    await w.find('[data-test=trigger-run-now]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=triggers-note]').text()).toContain('skipped (Ada was busy');
  });
});
