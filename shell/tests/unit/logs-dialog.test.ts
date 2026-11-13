// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import LogsDialog from '../../src/renderer/src/components/LogsDialog.vue';
import AgentCard from '../../src/renderer/src/components/AgentCard.vue';
import { diagnosticLog } from '../../src/main/diagnostic-log';
import type { LogSourceInfo, YawbleApi } from '../../src/shared/api';

// A fake core only: the logs below are made up here, and nothing is pruned anywhere.
const col = (key: string, label: string, kind: 'time' | 'text' | 'number' = 'text', filter = false) => ({ key, label, kind, filter });
const sources: LogSourceInfo[] = [
  { id: 'activity', title: 'All activity', group: 'Activity', about: 'Everything a member did.', columns: [col('at', 'Time', 'time'), col('source', 'Source', 'text', true), col('team', 'Team', 'text', true), col('member', 'Member'), col('event', 'Event'), col('details', 'Details')], prunable: false, loss: null, keptBecause: 'it is read from the other logs', defaultDays: null, retentionDays: null, team: true, member: true },
  { id: 'audit', title: 'Audit', group: 'Accountability', about: 'Every change.', columns: [col('at', 'Time', 'time'), col('via', 'Via', 'text', true), col('method', 'Call', 'text', true), col('outcome', 'Outcome')], prunable: true, loss: 'who changed what.', keptBecause: null, defaultDays: 365, retentionDays: 365, team: true, member: true },
  { id: 'runs', title: 'Runs', group: 'Security', about: 'Each run.', columns: [col('at', 'Started', 'time'), col('duration', 'Seconds', 'number')], prunable: true, loss: 'how runs ended.', keptBecause: null, defaultDays: 365, retentionDays: 90, team: true, member: true },
];

function rowsOf(n: number, from = 0) {
  return Array.from({ length: n }, (_, i) => ({ _id: String(from + i), at: new Date(Date.UTC(2026, 10, 13, 12, 0, 0) - (from + i) * 60000).toISOString(), source: 'Runs', team: 'web', member: 'DeveloperAda', event: `run ${from + i}`, details: 'claude' }));
}

function fakeApi(remote = false) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      switch (method) {
        case 'logs.sources': return structuredClone(sources) as any;
        case 'logs.who': return { teams: ['web', 'docs'], members: [{ team: 'web', name: 'Manager', key: 'web/Manager' }, { team: 'web', name: 'DeveloperAda', key: 'web/DeveloperAda' }] } as any;
        case 'logs.values': return (params?.column === 'via' ? ['app', 'cloud'] : ['Runs', 'Network']) as any;
        case 'logs.query': {
          const offset = (params?.offset as number) ?? 0;
          // 250 entries in pages of 100.
          const n = Math.min(100, 250 - offset);
          return { rows: rowsOf(n, offset), total: offset === 0 ? 250 : null, asOf: '2026-11-13T12:00:00Z', next: offset + n < 250 ? offset + n : null } as any;
        }
        case 'logs.prunePreview': return { source: 'audit', title: 'Audit', cutoff: '2025-11-13T12:00:00Z', count: 1234, oldest: '2025-01-01T00:00:00Z', newest: '2025-11-12T00:00:00Z', loss: 'who changed what.', files: 0 } as any;
        case 'logs.prune': return { deleted: 1234 } as any;
        case 'logs.setRetention': return { retentionDays: params?.days } as any;
        default: throw new Error(`unexpected ${method}`);
      }
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
    ...(remote ? { remote: true } : {}),
  };
  return { api, calls };
}

const queries = (calls: { method: string; params: any }[]) => calls.filter((c) => c.method === 'logs.query').map((c) => c.params);
// The first-page queries (the tests' DOM has no layout, so the dialog also loads the later pages at once).
const firsts = (calls: { method: string; params: any }[]) => queries(calls).filter((q) => q.offset === 0);

afterEach(() => { vi.useRealTimers(); document.body.innerHTML = ''; });

describe('LogsDialog', () => {
  it('ShowsTheChosenLogAsATable_NewestFirst_WithItsCount', async () => {
    const { api, calls } = fakeApi();
    const w = mount(LogsDialog, { props: { api }, attachTo: document.body });
    await flushPromises();
    expect(w.find('[data-test=logs-source]').element).toHaveProperty('value', 'activity');
    expect(w.findAll('thead th').map((t) => t.text())).toEqual(['Time▼', 'Source', 'Team', 'Member', 'Event', 'Details']);
    expect(w.findAll('[data-test=logs-row]').length).toBeGreaterThanOrEqual(100);
    expect(w.find('[data-test=logs-count]').text()).toContain('250 entries');
    expect(queries(calls)[0]).toMatchObject({ source: 'activity', offset: 0, limit: 100, sort: 'at', descending: true });
    w.unmount();
  });

  it('LoadsTheNextPageWhenAskedForMore_UntilTheEnd_OnTheSameSnapshot', async () => {
    const { api, calls } = fakeApi();
    const w = mount(LogsDialog, { props: { api }, attachTo: document.body });
    await flushPromises();
    while (w.find('[data-test=logs-more]').exists()) { await w.find('[data-test=logs-more]').trigger('click'); await flushPromises(); }
    expect(w.findAll('[data-test=logs-row]')).toHaveLength(250);
    const later = queries(calls).filter((q) => q.offset > 0);
    expect(later.map((q) => q.offset)).toEqual([100, 200]);
    expect(later.every((q) => q.asOf === '2026-11-13T12:00:00Z')).toBe(true);
    w.unmount();
  });

  it('SortsByAColumn_ThenTheOtherWay', async () => {
    const { api, calls } = fakeApi();
    const w = mount(LogsDialog, { props: { api, source: 'runs' }, attachTo: document.body });
    await flushPromises();
    await w.find('[data-test=logs-sort-duration]').trigger('click');
    await flushPromises();
    expect(firsts(calls).at(-1)).toMatchObject({ sort: 'duration', descending: true, offset: 0 });
    await w.find('[data-test=logs-sort-duration]').trigger('click');
    await flushPromises();
    expect(firsts(calls).at(-1)).toMatchObject({ sort: 'duration', descending: false });
    expect(w.find('th[aria-sort=ascending]').text()).toContain('Seconds');
    w.unmount();
  });

  it('FiltersByTeamMemberColumnsTimeAndText', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { api, calls } = fakeApi();
    const w = mount(LogsDialog, { props: { api, source: 'audit' }, attachTo: document.body });
    await flushPromises();
    await w.find('[data-test=logs-team]').setValue('web');
    await flushPromises();
    expect(queries(calls).at(-1)).toMatchObject({ team: 'web' });
    await w.find('[data-test=logs-member]').setValue('web/DeveloperAda');
    await flushPromises();
    expect(queries(calls).at(-1)).toMatchObject({ member: 'web/DeveloperAda' });
    expect(queries(calls).at(-1).team).toBeUndefined();
    await w.find('[data-test=logs-filter-via]').setValue('cloud');
    await flushPromises();
    expect(queries(calls).at(-1).filters).toEqual({ via: 'cloud' });
    await w.find('[data-test=logs-from]').setValue('2026-11-01');
    await flushPromises();
    expect(queries(calls).at(-1).from).toBe('2026-11-01T00:00:00');
    await w.find('[data-test=logs-text]').setValue('pause');
    vi.advanceTimersByTime(350);
    await flushPromises();
    expect(queries(calls).at(-1).text).toBe('pause');
    await w.find('[data-test=logs-clear]').trigger('click');
    await flushPromises();
    vi.advanceTimersByTime(350);
    await flushPromises();
    const last = queries(calls).at(-1);
    expect([last.team, last.member, last.from, last.text]).toEqual([undefined, undefined, undefined, undefined]);
    w.unmount();
  });

  it('OpenedForAMember_ShowsItsActivity_AndTheFiltersStillChange', async () => {
    const { api, calls } = fakeApi();
    const w = mount(LogsDialog, { props: { api, source: 'activity', member: 'web/DeveloperAda' }, attachTo: document.body });
    await flushPromises();
    expect(queries(calls)[0]).toMatchObject({ source: 'activity', member: 'web/DeveloperAda' });
    expect(w.find('[data-test=logs-team]').element).toHaveProperty('value', 'web');
    expect(w.find('[data-test=logs-member]').element).toHaveProperty('value', 'web/DeveloperAda');
    await w.find('[data-test=logs-member]').setValue('');
    await flushPromises();
    expect(queries(calls).at(-1)).toMatchObject({ team: 'web', member: undefined });
    await w.find('[data-test=logs-source]').setValue('audit');
    await flushPromises();
    expect(queries(calls).at(-1)).toMatchObject({ source: 'audit', team: 'web' });
    w.unmount();
  });

  it('PruneSaysExactlyWhatGoes_AndDeletesOnlyAfterTheYes', async () => {
    const { api, calls } = fakeApi();
    const w = mount(LogsDialog, { props: { api, source: 'audit' }, attachTo: document.body });
    await flushPromises();
    await w.find('[data-test=logs-prune-open]').trigger('click');
    await w.find('[data-test=logs-prune-age]').setValue('365');
    await w.find('[data-test=logs-prune-preview]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'logs.prunePreview')?.params).toEqual({ source: 'audit', olderThanDays: 365 });
    const confirm = w.find('[data-test=logs-prune-confirm]').text();
    expect(confirm).toContain('This deletes 1,234 Audit entries');
    expect(confirm).toContain('You lose who changed what.');
    expect(confirm).toContain('cannot be undone');
    expect(calls.some((c) => c.method === 'logs.prune')).toBe(false);
    await w.find('[data-test=logs-prune-yes]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'logs.prune')?.params).toEqual({ source: 'audit', before: '2025-11-13T12:00:00Z', confirm: true });
    expect(w.find('[data-test=logs-pruned]').text()).toContain('Deleted 1,234 entries from Audit');
    w.unmount();
  });

  it('AKeptLogOffersNoPrune_AndRetentionSetsEachLogsDays', async () => {
    const { api, calls } = fakeApi();
    const w = mount(LogsDialog, { props: { api }, attachTo: document.body });
    await flushPromises();
    expect(w.find('[data-test=logs-prune-open]').exists()).toBe(false);
    await w.find('[data-test=logs-retention-open]').trigger('click');
    expect(w.find('[data-test=logs-retention]').text()).toContain('All activity (it is read from the other logs)');
    await w.find('[data-test=logs-keep-select-runs]').setValue('30');
    await flushPromises();
    expect(calls.find((c) => c.method === 'logs.setRetention')?.params).toEqual({ source: 'runs', days: 30 });
    w.unmount();
  });

  it('ACloudViewerReadsOnly', async () => {
    const { api } = fakeApi(true);
    const w = mount(LogsDialog, { props: { api, source: 'audit' }, attachTo: document.body });
    await flushPromises();
    expect(w.findAll('[data-test=logs-row]').length).toBeGreaterThan(0);
    expect(w.find('[data-test=logs-prune-open]').exists()).toBe(false);
    expect(w.find('[data-test=logs-retention-open]').exists()).toBe(false);
    w.unmount();
  });

  it('ARowOpensToShowEveryField', async () => {
    const { api } = fakeApi();
    const w = mount(LogsDialog, { props: { api }, attachTo: document.body });
    await flushPromises();
    await w.find('[data-test=logs-row]').trigger('click');
    expect(w.find('[data-test=logs-detail]').text()).toContain('claude');
    w.unmount();
  });
});

describe('the member card', () => {
  it('HasALogsButton', async () => {
    const card = { member: { id: 1, team: 'web', name: 'DeveloperAda', role: 'member', cli: 'claude' }, state: 'idle', hasClone: false } as any;
    const w = mount(AgentCard, { props: { card } });
    await w.find('[data-test=open-logs]').trigger('click');
    expect(w.emitted('logs')).toHaveLength(1);
  });
});

describe('the window process diagnostic log', () => {
  it('AppendsEachLineWithItsTimeAndOrigin_ToTheDaysFile', () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'diag-'));
    const err = vi.spyOn(console, 'error').mockImplementation(() => {});
    try {
      const report = diagnosticLog(dir, () => new Date('2026-11-13T12:00:00Z'));
      report('the core exited (1)');
      report('two\nlines');
      const text = fs.readFileSync(path.join(dir, 'logs', 'shell-2026-11-13.log'), 'utf8');
      expect(text).toBe('2026-11-13T12:00:00.000Z\tshell\tthe core exited (1)\n2026-11-13T12:00:00.000Z\tshell\ttwo\n2026-11-13T12:00:00.000Z\tshell\tlines\n');
      expect(err).toHaveBeenCalledWith('[shell] the core exited (1)');
    } finally {
      err.mockRestore();
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });
});
