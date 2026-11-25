// A fake core for the Workflows page's pane (WorkflowPane) and the page itself (WorkflowsView): rows as
// workflows.list gives them, a workflows.get answer per row, and any other method a test scripts. Fakes only.
import { flushPromises, mount } from '@vue/test-utils';
import type { WorkflowDetail, WorkflowMessage, WorkflowRow, WorkflowsListAnswer, YawbleApi } from '../../src/shared/api';
import WorkflowPane from '../../src/renderer/src/workflows/WorkflowPane.vue';

/** A workflows.list row: open and idle in team payments unless said otherwise. */
export function paneRow(p: Partial<WorkflowRow> & Pick<WorkflowRow, 'id'>): WorkflowRow {
  const status = p.status ?? 'idle';
  const ended = status === 'completed' || status === 'closed' || status === 'expired';
  return {
    title: `Workflow ${p.id}`, owner: 'Manager', ownerId: 1, state: ended ? status : 'open', undeclared: false, busy: [], ending: null,
    startedAt: '2026-11-16T10:00:00Z', lastAt: '2026-11-16T11:00:00Z', messages: 3, endedAt: ended ? '2026-11-16T11:00:00Z' : null,
    expiredAt: null, expiresAt: null, question: null, time: null, team: 'payments', teamId: 1, status, waitingFor: null,
    working: status === 'running', item: null, members: [], ...p,
  } as WorkflowRow;
}

export interface PaneCore { api: YawbleApi; calls: { method: string; params: any }[]; methods: () => string[] }
/**
 * rows: what workflows.list and workflows.get read (a function, so a test can change them); messages: each workflow's
 * activity; backlog: whether the session may read the backlog (default yes); extra: any other method (an answer, or a throw). An unscripted method answers {}; `hold` delays the actions.
 */
export function paneCore(rows: () => WorkflowRow[], o: { messages?: Record<number, WorkflowMessage[]>; backlog?: boolean; extra?: Record<string, (p: any) => unknown>; hold?: Promise<void> } = {}): PaneCore {
  const calls: { method: string; params: any }[] = [];
  const api = {
    call: async (method: string, params?: any) => {
      calls.push({ method, params });
      if (method === 'workflows.list') {
        const list = rows().filter((r) => params?.teamId === undefined || r.teamId === params.teamId);
        return { workflows: list, counts: null as never, truncated: false, backlog: o.backlog ?? true, now: new Date().toISOString() } satisfies WorkflowsListAnswer;
      }
      if (method === 'workflows.get') {
        const workflow = rows().find((r) => r.id === params?.workflow)!;
        return { workflow, cards: [], messages: o.messages?.[params?.workflow] ?? [], spec: null, backlog: o.backlog ?? true } satisfies WorkflowDetail;
      }
      if (o.extra?.[method]) return o.extra[method](params);
      if (o.hold) await o.hold;
      return {};
    },
    onEvent: () => () => {},
  } as unknown as YawbleApi;
  const reads = new Set(['workflows.list', 'workflows.get']);
  return { api, calls, methods: () => calls.map((c) => c.method).filter((m) => !reads.has(m)) };
}

/** The pane for one row, its first read landed. */
export async function mountPane(row: WorkflowRow, api: YawbleApi, props: { readOnly?: boolean; phone?: boolean } = {}) {
  const w = mount(WorkflowPane, { props: { row, api, pollMs: 600_000, ...props }, attachTo: document.body });
  await flushPromises();
  return w;
}
