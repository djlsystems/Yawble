// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import LogsDialog from '../../src/renderer/src/components/LogsDialog.vue';
import type { LogSourceInfo, YawbleApi } from '../../src/shared/api';

// Structured logs end to end: the shell against core-shaped answers. The Tool calls columns and the Diagnostics source are
// as core/src/Yawble.Core/Logs/LogSources.cs and FileLogs.cs give them; the Level values are the ones LogRpc.QueryLevel
// accepts; app.overview newest is as UiRpc builds it (kind question|approval|host|tool|folder, title cut at 120 + "…").
vi.mock('electron', () => ({ app: {}, Menu: {}, nativeImage: {}, Tray: class {} }));
const { needsYouToast } = await import('../../src/main/background');

const col = (key: string, label: string, kind: 'time' | 'text' | 'number' = 'text', filter = false) => ({ key, label, kind, filter });
const base = { prunable: true, loss: 'x.', keptBecause: null, defaultDays: 365, retentionDays: 365, team: true, member: true };
const tools: LogSourceInfo = {
  ...base, id: 'tools', title: 'Tool calls', group: 'Accountability', about: 'Each tool call.',
  columns: [col('at', 'Time', 'time'), col('team', 'Team', 'text', true), col('member', 'Member'), col('integration', 'Integration', 'text', true),
    col('args', 'Arguments'), col('decision', 'Decision', 'text', true), col('rule', 'Rule'), col('exit', 'Exit/HTTP', 'number'),
    col('duration', 'Ms', 'number'), col('bytes', 'Bytes', 'number'), col('via', 'Via', 'text', true), col('workflow', 'Workflow', 'number')],
};
const diagnostics: LogSourceInfo = {
  ...base, id: 'diagnostics', title: 'Diagnostics', group: 'Diagnostics', about: 'What the app itself reported.',
  columns: [col('at', 'Time', 'time'), col('origin', 'From', 'text', true), col('text', 'Message'), col('fields', 'Fields')], levels: true,
};

function fakeApi() {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      switch (method) {
        case 'logs.sources': return structuredClone([tools, diagnostics]) as any;
        case 'logs.who': return { teams: ['Alpha'], members: [] } as any;
        case 'logs.values': return [] as any;
        case 'logs.query':
          return (params?.source === 'tools'
            ? { rows: [{ _id: '0', at: '2026-11-24T10:05:00Z', team: 'Alpha', member: 'Reader', integration: 'email:Sales inbox', args: '["list","{}","script run 41","host 127.0.0.1","failed: the server refused the user name or password"]', decision: 'allow', rule: null, exit: 1, duration: 212, bytes: null, via: 'script', workflow: null }], total: 1, asOf: '2026-11-24T12:00:00Z', next: null }
            : { rows: [{ _id: '0', at: '2026-11-24T10:00:00Z', origin: 'core', text: 'script run 5 failed: Alpha/Poller', fields: 'level=warning area=scripts', level: 'warning' }], total: 1, asOf: '2026-11-24T12:00:00Z', next: null }) as any;
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

describe('Structured logs: Logs', () => {
  it('the Tool calls view shows the core columns, Exit/HTTP among them, and no Level filter', async () => {
    const { api } = fakeApi();
    const w = mount(LogsDialog, { props: { api, source: 'tools' }, attachTo: document.body });
    await flushPromises();
    expect(w.findAll('thead th').map((t) => t.text().replace('▼', ''))).toEqual(
      ['Time', 'Team', 'Member', 'Integration', 'Arguments', 'Decision', 'Rule', 'Exit/HTTP', 'Ms', 'Bytes', 'Via', 'Workflow']);
    expect(w.text()).toContain('212');
    expect(w.text()).toContain('failed: the server refused the user name or password');
    expect(w.find('[data-test=logs-level]').exists()).toBe(false);
    w.unmount();
  });

  it('the Diagnostics Level filter sends only values the core accepts, and none by default', async () => {
    const { api, calls } = fakeApi();
    const w = mount(LogsDialog, { props: { api, source: 'diagnostics' }, attachTo: document.body });
    await flushPromises();
    const options = w.findAll('[data-test=logs-level] option').map((o) => (o.element as HTMLOptionElement).value);
    expect(options).toEqual(['', 'error', 'warning', 'info', 'debug', 'all']); // LogRpc.QueryLevel: default/all/error/warning/info/debug
    expect(queries(calls).at(-1)).not.toHaveProperty('level');
    for (const v of ['error', 'warning', 'info', 'debug', 'all']) {
      await w.find('[data-test=logs-level]').setValue(v);
      await flushPromises();
      expect(queries(calls).at(-1)).toMatchObject({ source: 'diagnostics', level: v });
    }
    expect(w.text()).toContain('warning');
    w.unmount();
  });
});

describe('Structured logs: the needs-you notification with app.overview newest as the core builds it', () => {
  it('names the newest item and its team for each kind the core sends', () => {
    const at = '2026-11-24T10:00:00.0000000+00:00';
    expect(needsYouToast(4, { kind: 'question', title: 'Merge the release branch?', team: 'Yawble Engineering Team', at }))
      .toBe('4 things waiting for you. Newest: a question from Yawble Engineering Team: Merge the release branch?');
    expect(needsYouToast(1, { kind: 'approval', title: 'api:twilio CreateMessage', team: 'Alpha', at }))
      .toBe('1 thing waiting for you. Newest: a tool call to approve from Alpha: api:twilio CreateMessage');
    expect(needsYouToast(2, { kind: 'folder', title: 'D:\\data', team: null, at })).toBe('2 things waiting for you. Newest: a folder request: D:\\data');
  });

  it('a title the core already cut (120 characters and "…") is not cut twice', () => {
    const cut = `${'y'.repeat(120)}…`;
    expect(needsYouToast(2, { kind: 'question', title: cut, team: 'Alpha' })).toBe(`2 things waiting for you. Newest: a question from Alpha: ${'y'.repeat(120)}…`);
  });
});
