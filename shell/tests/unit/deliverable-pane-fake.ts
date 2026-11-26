// A fake core for the Deliverables page's pane (DeliverablePane) and the page itself (DeliverablesView): rows as
// deliverables.list gives them, a deliverables.get answer per row, and any other method a test scripts. Fakes only.
import { flushPromises, mount } from '@vue/test-utils';
import type { DeliverableDetail, DeliverableMessage, DeliverableRow, DeliverablesListAnswer, YawbleApi } from '../../src/shared/api';
import DeliverablePane from '../../src/renderer/src/deliverables/DeliverablePane.vue';

/** A deliverables.list row: open and idle in team payments unless said otherwise. */
export function paneRow(p: Partial<DeliverableRow> & Pick<DeliverableRow, 'id'>): DeliverableRow {
  const status = p.status ?? 'idle';
  const ended = status === 'completed' || status === 'closed' || status === 'expired';
  return {
    title: `Deliverable ${p.id}`, owner: 'Manager', ownerId: 1, state: ended ? status : 'open', undeclared: false, busy: [], ending: null,
    startedAt: '2026-11-16T10:00:00Z', lastAt: '2026-11-16T11:00:00Z', messages: 3, endedAt: ended ? '2026-11-16T11:00:00Z' : null,
    expiredAt: null, expiresAt: null, question: null, time: null, team: 'payments', teamId: 1, status, waitingFor: null,
    working: status === 'running', item: null, members: [], ...p,
  } as DeliverableRow;
}

export interface PaneCore { api: YawbleApi; calls: { method: string; params: any }[]; methods: () => string[] }
/**
 * rows: what deliverables.list and deliverables.get read (a function, so a test can change them); messages: each deliverable's
 * activity; backlog: whether the session may read the backlog (default yes); extra: any other method (an answer, or a throw). An unscripted method answers {}; `hold` delays the actions.
 */
export function paneCore(rows: () => DeliverableRow[], o: { messages?: Record<number, DeliverableMessage[]>; backlog?: boolean; extra?: Record<string, (p: any) => unknown>; hold?: Promise<void> } = {}): PaneCore {
  const calls: { method: string; params: any }[] = [];
  const api = {
    call: async (method: string, params?: any) => {
      calls.push({ method, params });
      if (method === 'deliverables.list') {
        const list = rows().filter((r) => params?.teamId === undefined || r.teamId === params.teamId);
        return { deliverables: list, counts: null as never, truncated: false, backlog: o.backlog ?? true, now: new Date().toISOString() } satisfies DeliverablesListAnswer;
      }
      if (method === 'deliverables.get') {
        const deliverable = rows().find((r) => r.id === params?.deliverable)!;
        return { deliverable, cards: [], messages: o.messages?.[params?.deliverable] ?? [], spec: null, backlog: o.backlog ?? true } satisfies DeliverableDetail;
      }
      if (o.extra?.[method]) return o.extra[method](params);
      if (o.hold) await o.hold;
      return {};
    },
    onEvent: () => () => {},
  } as unknown as YawbleApi;
  const reads = new Set(['deliverables.list', 'deliverables.get', 'goals.list']);
  return { api, calls, methods: () => calls.map((c) => c.method).filter((m) => !reads.has(m)) };
}

/** The pane for one row, its first read landed. */
export async function mountPane(row: DeliverableRow, api: YawbleApi, props: { readOnly?: boolean; phone?: boolean } = {}) {
  const w = mount(DeliverablePane, { props: { row, api, pollMs: 600_000, ...props }, attachTo: document.body });
  await flushPromises();
  return w;
}
