// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import LogsDialog from '../../src/renderer/src/components/LogsDialog.vue';
import type { LogSourceInfo, YawbleApi } from '../../src/shared/api';

// The run view itself (and its flowchart) is tested on its own; here it shows which run it was given.
vi.mock('../../src/renderer/src/components/workflows/WorkflowRunView.vue', async () => {
  const { defineComponent, h } = await import('vue');
  return { default: defineComponent({ props: ['run', 'api'], setup: (p: any) => () => h('div', { 'data-test': 'workflow-run-view', 'data-run': p.run.id }, p.run.reason) }) };
});

// The Workflow runs log as core/src/Yawble.Core/Logs/LogSources.cs gives it (id "workflows", its last column "run" the
// workflow run's id), and a Tool calls log without that column. A fake core only: the runs below are made up here.
const col = (key: string, label: string, kind: 'time' | 'text' | 'number' = 'text', filter = false) => ({ key, label, kind, filter });
const base = { prunable: false, loss: null, keptBecause: 'x', defaultDays: null, retentionDays: null, team: true, member: true };
const workflows: LogSourceInfo = {
  ...base, id: 'workflows', title: 'Workflow runs', group: 'Work history', about: 'Each run of a workflow member.',
  columns: [col('at', 'Started', 'time'), col('team', 'Team', 'text', true), col('member', 'Member'), col('workflow', 'Workflow', 'text', true),
    col('status', 'Status', 'text', true), col('reason', 'Reason'), col('run', 'Workflow run', 'number')],
};
const tools: LogSourceInfo = {
  ...base, id: 'tools', title: 'Tool calls', group: 'Accountability', about: 'Each tool call.',
  columns: [col('at', 'Time', 'time'), col('team', 'Team', 'text', true), col('member', 'Member'), col('run', 'Run', 'number')],
};
const runRow = { _id: '42', at: '2026-11-24T11:55:00Z', team: 'web', member: 'MailSorter', workflow: 'mail-sort@3', status: 'failed', reason: 'sign-in refused', run: 42 };
const runDetail = {
  id: 42, teamId: 1, team: 'web', memberId: 7, member: 'MailSorter', workflowId: 3, workflow: 'mail-sort', version: 3, trigger: 'trigger:9',
  status: 'failed', dryRun: false, test: false, input: null, outputs: null, failedStepId: 'fetch', reason: 'sign-in refused',
  startedAt: '2026-11-24T11:55:00Z', endedAt: '2026-11-24T11:55:02Z',
  steps: [], log: [{ at: '2026-11-24T11:55:01Z', line: 'fetch: sign-in refused' }],
};

function fakeApi(runFails = false) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      switch (method) {
        case 'logs.sources': return structuredClone([workflows, tools]) as any;
        case 'logs.who': return { teams: ['web'], members: [] } as any;
        case 'logs.values': return [] as any;
        case 'logs.query': return { rows: [{ ...runRow }], total: 1, asOf: '2026-11-24T12:00:00Z', next: null } as any;
        case 'workflows.run':
          if (runFails) throw new Error('No workflow run 42.');
          return structuredClone(runDetail) as any;
        case 'workflows.get': return { id: 3, key: 'mail-sort', name: 'Mail sort', version: 3, ok: true, document: { steps: [] } } as any;
        default: throw new Error(`unexpected ${method}`);
      }
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls };
}

afterEach(() => { document.body.innerHTML = ''; });

describe('Logs: Workflow runs', () => {
  it('ARowsWorkflowRun_OpensThatRunsView_AndBackShowsTheLogAgain', async () => {
    const { api, calls } = fakeApi();
    const w = mount(LogsDialog, { props: { api, source: 'workflows' }, attachTo: document.body });
    await flushPromises();
    const link = w.find('[data-test=logs-run-link-42]');
    expect(link.exists()).toBe(true);
    expect(link.text()).toBe('42');
    await link.trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'workflows.run').map((c) => c.params)).toEqual([{ id: 42 }]);
    expect(w.find('[data-test=logs-run]').exists()).toBe(true);
    expect(w.find('[data-test=workflow-run-view]').attributes('data-run')).toBe('42');
    // The link opens the run, not the row's own detail.
    expect(w.find('[data-test=logs-detail]').exists()).toBe(false);
    await w.find('[data-test=logs-run-back]').trigger('click');
    expect(w.find('[data-test=logs-run]').exists()).toBe(false);
    expect(w.find('[data-test=logs-row]').exists()).toBe(true);
    w.unmount();
  });

  it('ARunThatCannotBeRead_SaysWhy_AndTheLogStays', async () => {
    const { api } = fakeApi(true);
    const w = mount(LogsDialog, { props: { api, source: 'workflows' }, attachTo: document.body });
    await flushPromises();
    await w.find('[data-test=logs-run-link-42]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=logs-run-error]').text()).toContain('No workflow run 42.');
    expect(w.find('[data-test=logs-run]').exists()).toBe(false);
    expect(w.find('[data-test=logs-row]').exists()).toBe(true);
    w.unmount();
  });

  it('AnotherLogsRunColumn_IsPlainText', async () => {
    const { api } = fakeApi();
    const w = mount(LogsDialog, { props: { api, source: 'tools' }, attachTo: document.body });
    await flushPromises();
    expect(w.find('[data-test=logs-row]').exists()).toBe(true);
    expect(w.find('[data-test^=logs-run-link]').exists()).toBe(false);
    w.unmount();
  });
});
