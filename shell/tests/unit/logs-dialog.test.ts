// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import LogsDialog from '../../src/renderer/src/components/LogsDialog.vue';
import AgentCard from '../../src/renderer/src/components/MemberPane.vue';
import { diagnosticLog } from '../../src/main/diagnostic-log';
import type { LogSourceInfo, YawbleApi } from '../../src/shared/api';

// A fake core only: the logs below are made up here, and nothing is pruned anywhere.
const col = (key: string, label: string, kind: 'time' | 'text' | 'number' = 'text', filter = false) => ({ key, label, kind, filter });
const sources: LogSourceInfo[] = [
  { id: 'activity', title: 'All activity', group: 'Activity', about: 'Everything that happened, by members, teams and the app.', columns: [col('at', 'Time', 'time'), col('source', 'Source', 'text', true), col('team', 'Team', 'text', true), col('member', 'Member'), col('event', 'Event'), col('details', 'Details')], prunable: false, loss: null, keptBecause: 'it is read from the other logs', defaultDays: null, retentionDays: null, team: true, member: true },
  { id: 'audit', title: 'Audit', group: 'Accountability', about: 'Every change.', columns: [col('at', 'Time', 'time'), col('via', 'Via', 'text', true), col('method', 'Call', 'text', true), col('outcome', 'Outcome')], prunable: true, loss: 'who changed what.', keptBecause: null, defaultDays: 365, retentionDays: 365, team: true, member: true },
  { id: 'runs', title: 'Runs', group: 'Security', about: 'Each run.', columns: [col('at', 'Started', 'time'), col('duration', 'Seconds', 'number')], prunable: true, loss: 'how runs ended.', keptBecause: null, defaultDays: 365, retentionDays: 90, team: true, member: true },
];

function rowsOf(n: number, from = 0) {
  return Array.from({ length: n }, (_, i) => ({ _id: String(from + i), at: new Date(Date.UTC(2026, 10, 13, 12, 0, 0) - (from + i) * 60000).toISOString(), source: 'Runs', team: 'web', member: 'DeveloperAda', event: `run ${from + i}`, details: 'claude' }));
}

function fakeApi(remote = false, extra: LogSourceInfo[] = []) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      switch (method) {
        case 'logs.sources': return structuredClone([...sources, ...extra]) as any;
        case 'logs.who': return { teams: ['web', 'docs'], members: [{ team: 'web', name: 'Manager', key: 'web/Manager' }, { team: 'web', name: 'DeveloperAda', key: 'web/DeveloperAda' }] } as any;
        case 'logs.values': return (params?.column === 'via' ? ['app', 'cloud'] : params?.column === 'origin' ? ['core', 'shell'] : ['Runs', 'Network']) as any;
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

  it('ListsDiagnostics_WithTheShellsLogBesideTheCores_AndFiltersByFrom', async () => {
    // As the core offers it (FileLogs.Diagnostics): every .log in <data>/logs, core-<day>.log and shell-<day>.log alike.
    const diagnostics: LogSourceInfo = {
      id: 'diagnostics', title: 'Diagnostics', group: 'Diagnostics',
      about: 'What the app itself reported while it ran, from its core and its window (From: core or shell): start-up and shutdown, cloning and syncing, background work, and errors.',
      columns: [col('at', 'Time', 'time'), col('origin', 'From', 'text', true), col('text', 'Message')],
      prunable: true, loss: 'the app\'s own messages from those days.', keptBecause: null, defaultDays: 14, retentionDays: 14, team: false, member: false,
    };
    const { api, calls } = fakeApi(false, [diagnostics]);
    const w = mount(LogsDialog, { props: { api, source: 'diagnostics' }, attachTo: document.body });
    await flushPromises();
    expect(w.find('[data-test=logs-source]').element).toHaveProperty('value', 'diagnostics');
    expect(w.find('[data-test=logs-source]').text()).toContain('Diagnostics');
    expect(w.find('[data-test=logs-about]').text()).toContain('core or shell');
    expect(firsts(calls).at(-1)).toMatchObject({ source: 'diagnostics' });
    await w.find('[data-test=logs-filter-origin]').setValue('shell');
    await flushPromises();
    expect(queries(calls).at(-1)).toMatchObject({ source: 'diagnostics', filters: { origin: 'shell' } });
    expect(w.find('[data-test=logs-prune-open]').exists()).toBe(true); // kept to its retention, like the core's
    w.unmount();
  });

  it('ALogWithLevels_ShowsItsLevelColumnAndTheLevelFilter_DebugHiddenUntilAsked', async () => {
    // As the core offers Diagnostics now (FileLogs.Diagnostics, LogService.Sources): a Fields column and levels.
    const diagnostics: LogSourceInfo = {
      id: 'diagnostics', title: 'Diagnostics', group: 'Diagnostics', about: 'What the app itself reported.',
      columns: [col('at', 'Time', 'time'), col('origin', 'From', 'text', true), col('text', 'Message'), col('fields', 'Fields')],
      prunable: true, loss: 'the app\'s own messages from those days.', keptBecause: null, defaultDays: 14, retentionDays: 14, team: true, member: true,
      levels: true,
    };
    const { api, calls } = fakeApi(false, [diagnostics]);
    const w = mount(LogsDialog, { props: { api, source: 'diagnostics' }, attachTo: document.body });
    await flushPromises();
    expect(w.findAll('thead th').map((t) => t.text().replace('▼', ''))).toEqual(['Time', 'Level', 'From', 'Message', 'Fields']);
    // The default asks for no level: the core leaves debug out.
    expect(firsts(calls).at(-1)).not.toHaveProperty('level');
    expect(w.find('[data-test=logs-level]').element).toHaveProperty('value', '');
    await w.find('[data-test=logs-level]').setValue('debug');
    await flushPromises();
    expect(queries(calls).at(-1)).toMatchObject({ source: 'diagnostics', level: 'debug' });
    await w.find('[data-test=logs-clear]').trigger('click');
    await flushPromises();
    expect(queries(calls).at(-1)).not.toHaveProperty('level');
    // A log without levels offers no Level filter.
    await w.find('[data-test=logs-source]').setValue('audit');
    await flushPromises();
    expect(w.find('[data-test=logs-level]').exists()).toBe(false);
    w.unmount();
  });
});

// The Member filter picks a member, a team as a whole or the app. The core answers
// logs.query source=activity on this contract: member="team/Name" -> that member only; team="X" and no member -> X's
// members plus X's team rows (member column "<X>"); app=true -> the app's own rows only (team empty, member "app").
describe('LogsDialog: the Member filter on a member, a team or the app', () => {
  const opts = (w: ReturnType<typeof mount>) => w.findAll('[data-test=logs-member] option').map((o) => [(o.element as HTMLOptionElement).value, o.text()]);

  it('OffersEachMember_EachTeamAsAWhole_AndTheApp', async () => {
    const { api } = fakeApi();
    const w = mount(LogsDialog, { props: { api }, attachTo: document.body });
    await flushPromises();
    expect(opts(w)).toEqual([
      ['', 'Everyone'],
      ['web/Manager', 'web/Manager'], ['web/DeveloperAda', 'web/DeveloperAda'],
      ['team:web', 'web (whole team)'], ['team:docs', 'docs (whole team)'],
      ['app', 'app (the app itself)'],
    ]);
    expect(w.find('[data-test=logs-about]').text()).not.toContain('Everything a member did');
    w.unmount();
  });

  it('AMember_SendsMemberOnly', async () => {
    const { api, calls } = fakeApi();
    const w = mount(LogsDialog, { props: { api }, attachTo: document.body });
    await flushPromises();
    await w.find('[data-test=logs-member]').setValue('web/DeveloperAda');
    await flushPromises();
    const q = firsts(calls).at(-1);
    expect(q).toMatchObject({ source: 'activity', member: 'web/DeveloperAda', team: undefined });
    expect(q.app).toBeUndefined();
    expect(w.find('[data-test=logs-team]').element).toHaveProperty('value', 'web');
    w.unmount();
  });

  it('AWholeTeam_SendsTeamWithNoMember', async () => {
    const { api, calls } = fakeApi();
    const w = mount(LogsDialog, { props: { api }, attachTo: document.body });
    await flushPromises();
    await w.find('[data-test=logs-member]').setValue('team:docs');
    await flushPromises();
    const q = firsts(calls).at(-1);
    expect(q).toMatchObject({ source: 'activity', team: 'docs', member: undefined });
    expect(q.app).toBeUndefined();
    expect(w.find('[data-test=logs-team]').element).toHaveProperty('value', 'docs');
    expect(w.find('[data-test=logs-member]').element).toHaveProperty('value', 'team:docs');
    // With a team chosen, its own whole-team option is the one offered.
    expect(opts(w).filter(([v]) => v.startsWith('team:'))).toEqual([['team:docs', 'docs (whole team)']]);
    w.unmount();
  });

  it('TheApp_SendsAppTrue_WithNoTeamOrMember', async () => {
    const { api, calls } = fakeApi();
    const w = mount(LogsDialog, { props: { api }, attachTo: document.body });
    await flushPromises();
    await w.find('[data-test=logs-member]').setValue('app');
    await flushPromises();
    const q = firsts(calls).at(-1);
    expect(q).toMatchObject({ source: 'activity', app: true, team: undefined, member: undefined });
    expect(w.find('[data-test=logs-team]').element).toHaveProperty('value', '');
    expect(w.find('[data-test=logs-clear]').exists()).toBe(true);
    w.unmount();
  });

  it('PickingOneClearsTheOthers', async () => {
    const { api, calls } = fakeApi();
    const w = mount(LogsDialog, { props: { api, member: 'web/DeveloperAda' }, attachTo: document.body });
    await flushPromises();
    expect(firsts(calls)[0]).toMatchObject({ source: 'activity', member: 'web/DeveloperAda' });
    // member -> app: the member and its team go.
    await w.find('[data-test=logs-member]').setValue('app');
    await flushPromises();
    expect(firsts(calls).at(-1)).toMatchObject({ app: true, team: undefined, member: undefined });
    // app -> a whole team: app goes.
    await w.find('[data-test=logs-member]').setValue('team:web');
    await flushPromises();
    let q = firsts(calls).at(-1);
    expect(q).toMatchObject({ team: 'web', member: undefined });
    expect(q.app).toBeUndefined();
    // whole team -> a member: the team filter becomes the member's, sent as the member alone.
    await w.find('[data-test=logs-member]').setValue('web/Manager');
    await flushPromises();
    expect(firsts(calls).at(-1)).toMatchObject({ team: undefined, member: 'web/Manager' });
    // app, then a team in the Team select: app goes.
    await w.find('[data-test=logs-member]').setValue('app');
    await flushPromises();
    await w.find('[data-test=logs-team]').setValue('docs');
    await flushPromises();
    q = firsts(calls).at(-1);
    expect(q).toMatchObject({ team: 'docs', member: undefined });
    expect(q.app).toBeUndefined();
    // Clear filters clears app too.
    await w.find('[data-test=logs-member]').setValue('app');
    await flushPromises();
    await w.find('[data-test=logs-clear]').trigger('click');
    await flushPromises();
    q = firsts(calls).at(-1);
    expect([q.team, q.member, q.app]).toEqual([undefined, undefined, undefined]);
    w.unmount();
  });

  it('TheApp_NarrowsTheSourceValues_AndClearingItWidensThemAgain', async () => {
    const { api, calls } = fakeApi();
    // The app's own rows: no Runs and no team; the fake core answers app:true with only those.
    const inner = api.call;
    api.call = (async (method: string, params?: Record<string, unknown>) => {
      if (method === 'logs.values' && params?.app) { calls.push({ method, params }); return (params.column === 'source' ? ['Audit'] : []) as any; }
      return inner(method, params);
    }) as any;
    const w = mount(LogsDialog, { props: { api }, attachTo: document.body });
    await flushPromises();
    const valueCalls = () => calls.filter((c) => c.method === 'logs.values').map((c) => c.params);
    const listed = (key: string) => w.findAll(`[data-test=logs-filter-${key}] option`).map((o) => o.text()).slice(1);
    // All activity's value list is Source (its Team column is the Team select, filled from logs.who).
    expect(listed('source')).toEqual(['Runs', 'Network']);
    expect(valueCalls().every((p) => p.app === undefined)).toBe(true);

    let before = valueCalls().length;
    await w.find('[data-test=logs-member]').setValue('app');
    await flushPromises();
    const picked = valueCalls().slice(before);
    expect(picked.length).toBeGreaterThan(0);
    for (const p of picked) expect(p).toMatchObject({ source: 'activity', column: 'source', app: true, team: undefined, member: undefined });
    expect(listed('source')).toEqual(['Audit']);

    before = valueCalls().length;
    await w.find('[data-test=logs-member]').setValue('');
    await flushPromises();
    const cleared = valueCalls().slice(before);
    expect(cleared.length).toBeGreaterThan(0);
    for (const p of cleared) expect(p.app).toBeUndefined();
    expect(listed('source')).toEqual(['Runs', 'Network']);
    w.unmount();
  });

  it('PickingATeamOrClearFilters_AfterApp_WidensTheSourceValuesAgain', async () => {
    // The other two ways app goes away.
    const { api, calls } = fakeApi();
    const inner = api.call;
    api.call = (async (method: string, params?: Record<string, unknown>) => {
      if (method === 'logs.values' && params?.app) { calls.push({ method, params }); return (params.column === 'source' ? ['Audit'] : []) as any; }
      return inner(method, params);
    }) as any;
    const w = mount(LogsDialog, { props: { api }, attachTo: document.body });
    await flushPromises();
    const valueCalls = () => calls.filter((c) => c.method === 'logs.values').map((c) => c.params);
    const listed = (key: string) => w.findAll(`[data-test=logs-filter-${key}] option`).map((o) => o.text()).slice(1);
    // Team select options come from logs.who, not logs.values: unchanged by app.
    const teams = () => w.findAll('[data-test=logs-team] option').map((o) => o.text());
    const teamsBefore = teams();

    await w.find('[data-test=logs-member]').setValue('app');
    await flushPromises();
    expect(listed('source')).toEqual(['Audit']);
    expect(teams()).toEqual(teamsBefore);

    // app -> a team in the Team select: app goes, values asked without app and for that team.
    let before = valueCalls().length;
    await w.find('[data-test=logs-team]').setValue('docs');
    await flushPromises();
    let after = valueCalls().slice(before);
    expect(after.length).toBeGreaterThan(0);
    expect(after.at(-1)).toMatchObject({ source: 'activity', column: 'source', team: 'docs' });
    expect(after.at(-1).app).toBeUndefined();
    expect(listed('source')).toEqual(['Runs', 'Network']);

    // app again, then Clear filters: app goes, the full list is back.
    await w.find('[data-test=logs-member]').setValue('app');
    await flushPromises();
    expect(listed('source')).toEqual(['Audit']);
    before = valueCalls().length;
    await w.find('[data-test=logs-clear]').trigger('click');
    await flushPromises();
    after = valueCalls().slice(before);
    expect(after.length).toBeGreaterThan(0);
    expect([after.at(-1).team, after.at(-1).member, after.at(-1).app]).toEqual([undefined, undefined, undefined]);
    expect(listed('source')).toEqual(['Runs', 'Network']);
    w.unmount();
  });

  it('AppIsAllActivityOnly_AndLeavingAllActivityDropsIt', async () => {
    const { api, calls } = fakeApi();
    const w = mount(LogsDialog, { props: { api }, attachTo: document.body });
    await flushPromises();
    await w.find('[data-test=logs-member]').setValue('app');
    await flushPromises();
    await w.find('[data-test=logs-source]').setValue('audit');
    await flushPromises();
    const q = firsts(calls).at(-1);
    expect(q.source).toBe('audit');
    expect(q.app).toBeUndefined();
    expect(w.find('[data-test=logs-member-app]').exists()).toBe(false);
    expect(opts(w)[0]).toEqual(['', 'All members']);
    w.unmount();
  });
});
describe('the member card', () => {
  it('HasALogsButton', async () => {
    const card = { member: { id: 1, team: 'web', name: 'DeveloperAda', role: 'member', cli: 'claude' }, state: 'idle', hasClone: false } as any;
    const w = mount(AgentCard, { props: { card } });
    await w.get('[data-test=member-more]').trigger('click');
    await w.get('[data-test=open-logs]').trigger('click');
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
