// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import LogsDialog from '../../src/renderer/src/components/LogsDialog.vue';
import type { LogSourceInfo, YawbleApi } from '../../src/shared/api';

// All activity with levels, as core/src/Yawble.Core/Logs/LogSources.cs gives it (levels: true, each row's level after
// its columns); Tool calls without. The Level values are the ones LogRpc.QueryLevel accepts.
const col = (key: string, label: string, kind: 'time' | 'text' | 'number' = 'text', filter = false) => ({ key, label, kind, filter });
const base = { prunable: false, loss: null, keptBecause: 'x', defaultDays: null, retentionDays: null, team: true, member: true };
const activity: LogSourceInfo = {
  ...base, id: 'activity', title: 'All activity', group: 'Activity', about: 'Everything that happened.', app: true, levels: true,
  columns: [col('at', 'Time', 'time'), col('source', 'Source', 'text', true), col('team', 'Team', 'text', true), col('member', 'Member'),
    col('event', 'Event'), col('details', 'Details')],
};
const tools: LogSourceInfo = {
  ...base, id: 'tools', title: 'Tool calls', group: 'Accountability', about: 'Each tool call.', levels: false,
  columns: [col('at', 'Time', 'time'), col('team', 'Team', 'text', true), col('member', 'Member'), col('integration', 'Tool', 'text', true)],
};

function fakeApi() {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      switch (method) {
        case 'logs.sources': return structuredClone([activity, tools]) as any;
        case 'logs.who': return { teams: ['web'], members: [] } as any;
        case 'logs.values': return [] as any;
        case 'logs.query':
          return (params?.source === 'activity'
            ? { rows: [{ _id: 'r1', at: '2026-11-24T11:55:00Z', source: 'Runs', team: 'web', member: 'DeveloperAda', event: 'run failed', details: 'claude headless exit 1', level: 'error' }], total: 1, asOf: '2026-11-24T12:00:00Z', next: null }
            : { rows: [], total: 0, asOf: '2026-11-24T12:00:00Z', next: null }) as any;
        default: throw new Error(`unexpected ${method}`);
      }
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls };
}

const queries = (calls: { method: string; params: any }[]) => calls.filter((c) => c.method === 'logs.query').map((c) => c.params);

afterEach(() => { document.body.innerHTML = ''; });

describe('Logs: the Level filter on All activity', () => {
  it('shows the Level select and each row\'s level, sends no level by default and each chosen one', async () => {
    const { api, calls } = fakeApi();
    const w = mount(LogsDialog, { props: { api }, attachTo: document.body });
    await flushPromises();
    expect(w.find('[data-test=logs-level]').exists()).toBe(true);
    expect(w.findAll('thead th').map((t) => t.text().replace('▼', ''))).toEqual(['Time', 'Level', 'Source', 'Team', 'Member', 'Event', 'Details']);
    expect(w.find('tbody').text()).toContain('error');
    expect(queries(calls).at(-1)).toMatchObject({ source: 'activity' });
    expect(queries(calls).at(-1)).not.toHaveProperty('level');
    for (const v of ['error', 'warning', 'info', 'debug', 'all']) {
      await w.find('[data-test=logs-level]').setValue(v);
      await flushPromises();
      expect(queries(calls).at(-1)).toMatchObject({ source: 'activity', level: v });
    }
    w.unmount();
  });

  it('a log without levels has no Level select and is sent no level, even with one chosen before', async () => {
    const { api, calls } = fakeApi();
    const w = mount(LogsDialog, { props: { api }, attachTo: document.body });
    await flushPromises();
    await w.find('[data-test=logs-level]').setValue('error');
    await flushPromises();
    await w.find('[data-test=logs-source]').setValue('tools');
    await flushPromises();
    expect(w.find('[data-test=logs-level]').exists()).toBe(false);
    expect(queries(calls).at(-1)).toMatchObject({ source: 'tools' });
    expect(queries(calls).at(-1)).not.toHaveProperty('level');
    w.unmount();
  });
});
