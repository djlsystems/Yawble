// @vitest-environment happy-dom
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, h } from 'vue';
import FolderAskCard from '../../src/renderer/src/components/FolderAskCard.vue';
import FolderAsks from '../../src/renderer/src/components/FolderAsks.vue';
import MemberPane from '../../src/renderer/src/components/MemberPane.vue';
import BoardView from '../../src/renderer/src/board/BoardView.vue';
import { folderAnswerText, folderAskAskedBy, folderAskChoices, folderAskHeadText, type FolderAsk } from '../../src/renderer/src/folderAsks';
import { MonitorModel, needYouSummary } from '../../src/renderer/src/monitor/model';
import type { AgentCard, YawbleApi } from '../../src/shared/api';

/**
 * Card 497 (B111, shell): folder asks - an agent asks for a folder outside its clone - shown wherever network and tool
 * asks show: Needs you (the window's banner, the Board's lane), the member's pane, and the Monitor's You node. Fakes
 * only: no core. The FolderAsk shape is shared/api.ts's (card 496), re-exported by folderAsks.ts (card 507).
 */

vi.mock('../../src/renderer/src/monitor/scene', async (orig) => {
  const real = await orig<typeof import('../../src/renderer/src/monitor/scene')>();
  class FakeScene {
    model: unknown; onHover?: unknown; onPick?: unknown; onOpenWorkflow?: unknown; effects: unknown[] = [];
    setPinned() {} setLayout() {} setTeams() {} setPicked() {} destroy() {} fit() {} clearFiles() {}
  }
  return { ...real, MonitorScene: FakeScene };
});

// The full shared/api.ts shape (card 507: folderAsks.ts re-exports it). reason is null in one case: older/odd data.
const ask = (o: Partial<Omit<FolderAsk, 'reason' | 'choices'>> & { reason?: string | null; choices?: FolderAsk['choices'] | null } = {}): FolderAsk => ({
  id: 9, at: '2026-11-19T10:00:00Z', teamId: 1, team: 'Smoke-sessions', memberId: 7, member: 'Smoke-sessions/OperatorClifford', memberName: 'OperatorClifford',
  askedBy: 'Smoke-sessions/OperatorClifford', path: 'Z:\\smokesessions', access: 'readwrite', reason: 'write the smoke results next to the recordings',
  exists: true, existedWhenAsked: true, state: 'pending', grantedAccess: null, decidedBy: null, decidedVia: null, decidedAt: null, note: null,
  workflow: 40, choices: [], ...o,
} as FolderAsk);

type Call = { method: string; params: any };
function fakeApi(list: () => FolderAsk[], opts: { fail?: Error; remote?: boolean } = {}) {
  const calls: Call[] = [];
  const listeners: ((e: { event: string; data?: unknown }) => void)[] = [];
  const api = {
    ...(opts.remote ? { remote: true } : {}),
    call: async (method: string, params?: any) => {
      calls.push({ method, params });
      if (method === 'folder.asks') return list();
      if (method === 'folder.answer') { if (opts.fail) throw opts.fail; return { id: params.id, state: params.choice === 'deny' ? 'denied' : 'approved' }; }
      throw new Error(`not faked: ${method}`);
    },
    onEvent: (l: (e: { event: string; data?: unknown }) => void) => { listeners.push(l); return () => { listeners.splice(listeners.indexOf(l), 1); }; },
  } as unknown as YawbleApi;
  const emit = (event: string) => { for (const l of [...listeners]) l({ event }); };
  return { api, calls, emit };
}

const mounted: VueWrapper[] = [];
const keep = <T extends VueWrapper>(w: T): T => { mounted.push(w); return w; };
afterEach(() => { while (mounted.length) { try { mounted.pop()!.unmount(); } catch { /* already */ } } });
const buttons = (w: VueWrapper) => w.findAll('.buttons button').map((b) => b.text());

describe('FolderAskCard: what it says', () => {
  it('names team / member, the access, the path and the reason, and says the folder exists', () => {
    const { api } = fakeApi(() => []);
    const w = keep(mount(FolderAskCard, { props: { ask: ask(), api } }));
    expect(w.get('[data-test=folder-ask-head]').text().replace(/\s+/g, ' '))
      .toBe('Smoke-sessions / OperatorClifford asks read-write on Z:\\smokesessions - write the smoke results next to the recordings');
    expect(w.get('[data-test=folder-ask-exists]').attributes('data-exists')).toBe('true');
    expect(w.get('[data-test=folder-ask-exists]').text()).toBe('The folder exists.');
  });

  it('says when the folder does not exist; a read-only ask says read-only; no reason, no dash', () => {
    const { api } = fakeApi(() => []);
    const w = keep(mount(FolderAskCard, { props: { ask: ask({ exists: false, access: 'read', reason: null }), api } }));
    expect(w.get('[data-test=folder-ask-exists]').attributes('data-exists')).toBe('false');
    expect(w.get('[data-test=folder-ask-exists]').text()).toContain('does not exist');
    expect(w.get('[data-test=folder-ask-head]').text().replace(/\s+/g, ' ')).toBe('Smoke-sessions / OperatorClifford asks read-only on Z:\\smokesessions');
  });

  it('shows agent text as plain text, never HTML', () => {
    const { api } = fakeApi(() => []);
    const w = keep(mount(FolderAskCard, { props: { ask: ask({ reason: '<img src=x onerror=alert(1)>', path: 'C:\\<b>x</b>' }), api } }));
    expect(w.find('img').exists()).toBe(false);
    expect(w.find('b[data-test] b').exists()).toBe(false);
    expect(w.get('[data-test=folder-ask-reason]').text()).toBe('<img src=x onerror=alert(1)>');
    expect(w.get('[data-test=folder-ask-path]').text()).toBe('C:\\<b>x</b>');
  });
});

describe('FolderAskCard: when the Manager asked for the member (card 507)', () => {
  const managerAsk = (o: Parameters<typeof ask>[0] = {}) => ask({ askedBy: 'Smoke-sessions/Manager', ...o });

  it('says the Manager asks, for the member, the access, the path and the reason', () => {
    const { api } = fakeApi(() => []);
    const w = keep(mount(FolderAskCard, { props: { ask: managerAsk(), api } }));
    expect(w.get('[data-test=folder-ask-head]').text().replace(/\s+/g, ' '))
      .toBe('Smoke-sessions / Manager asks, for OperatorClifford, read-write on Z:\\smokesessions - write the smoke results next to the recordings');
    expect(w.get('[data-test=folder-ask-asked-by]').attributes('data-asked-by')).toBe('Manager');
    expect(w.get('[data-test=folder-ask-member]').text()).toBe('OperatorClifford');
    expect(folderAskHeadText(managerAsk())).toBe(w.get('[data-test=folder-ask-head]').text().replace(/\s+/g, ' '));
  });

  it('a Manager with its own name is named, with (Manager); read-only and no reason read the same way', () => {
    const { api } = fakeApi(() => []);
    const w = keep(mount(FolderAskCard, { props: { ask: managerAsk({ askedBy: 'Smoke-sessions/Ada', access: 'read', reason: null }), api } }));
    expect(w.get('[data-test=folder-ask-head]').text().replace(/\s+/g, ' '))
      .toBe('Smoke-sessions / Ada (Manager) asks, for OperatorClifford, read-only on Z:\\smokesessions');
  });

  it('the member\'s own ask is unchanged, whether askedBy is "Team/Name", a bare name, another case or missing', () => {
    const { api } = fakeApi(() => []);
    const own = 'Smoke-sessions / OperatorClifford asks read-write on Z:\\smokesessions - write the smoke results next to the recordings';
    for (const askedBy of ['Smoke-sessions/OperatorClifford', 'OperatorClifford', 'smoke-sessions/operatorclifford', '', undefined as unknown as string]) {
      const w = keep(mount(FolderAskCard, { props: { ask: ask({ askedBy }), api } }));
      expect(w.get('[data-test=folder-ask-head]').text().replace(/\s+/g, ' ')).toBe(own);
      expect(w.find('[data-test=folder-ask-asked-by]').exists()).toBe(false);
      expect(folderAskAskedBy(ask({ askedBy }))).toBeNull();
    }
  });

  it('the Manager\'s name is plain text, never HTML', () => {
    const { api } = fakeApi(() => []);
    const w = keep(mount(FolderAskCard, { props: { ask: managerAsk({ askedBy: 'Smoke-sessions/<img src=x onerror=alert(1)>' }), api } }));
    expect(w.find('img').exists()).toBe(false);
    expect(w.get('[data-test=folder-ask-asked-by]').text()).toBe('<img src=x onerror=alert(1)> (Manager)');
  });

  it('Deny on a Manager\'s ask says the Manager is told; Approve reads as for the member\'s own', async () => {
    expect(folderAnswerText(managerAsk(), 'deny')).toBe('Denied OperatorClifford read-write on Z:\\smokesessions; the Manager is told.');
    expect(folderAnswerText(ask(), 'deny')).toBe('Denied OperatorClifford read-write on Z:\\smokesessions; the agent is told.');
    expect(folderAnswerText(managerAsk(), 'read')).toBe('Approved read-only for OperatorClifford on Z:\\smokesessions: applies from its next run.');
    const { api, calls } = fakeApi(() => []);
    const w = keep(mount(FolderAskCard, { props: { ask: managerAsk(), api } }));
    await w.get('[data-test=folder-ask-deny]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'folder.answer')!.params).toEqual({ id: 9, choice: 'deny' });
    expect(w.get('[data-test=folder-ask-done]').text()).toBe('Denied OperatorClifford read-write on Z:\\smokesessions; the Manager is told.');
  });
});

describe('folderAsks.ts re-exports the shared/api.ts types (card 507)', () => {
  it('declares no FolderAsk / FolderAskChoice of its own', () => {
    const src = readFileSync(resolve(__dirname, '../../src/renderer/src/folderAsks.ts'), 'utf8');
    expect(src).toContain("export type { FolderAsk, FolderAskChoice } from '../../shared/api';");
    expect(src).not.toMatch(/export (interface|type) FolderAsk\b\s*[={]/);
    expect(src).not.toMatch(/export type FolderAskChoice\s*=/);
  });
});
describe('FolderAskCard: the answers', () => {
  it('a read-write ask offers Approve, Approve read-only and Deny', () => {
    const { api } = fakeApi(() => []);
    const w = keep(mount(FolderAskCard, { props: { ask: ask(), api } }));
    expect(buttons(w)).toEqual(['Approve', 'Approve read-only', 'Deny']);
  });

  it('a read-only ask offers no read-write, even when the core lists it', () => {
    const { api } = fakeApi(() => []);
    const w = keep(mount(FolderAskCard, { props: { ask: ask({ access: 'read', choices: ['readwrite', 'read', 'deny'] }), api } }));
    expect(buttons(w)).toEqual(['Approve', 'Deny']);
    expect(w.find('[data-test=folder-ask-readwrite]').exists()).toBe(false);
    expect(folderAskChoices({ access: 'read', choices: null })).toEqual(['read', 'deny']);
    expect(folderAskChoices({ access: 'read', choices: ['readwrite'] })).toEqual([]);
    // The core's choices can only narrow it.
    expect(folderAskChoices({ access: 'readwrite', choices: ['read', 'deny'] })).toEqual(['read', 'deny']);
  });

  for (const [testId, choice] of [['readwrite', 'readwrite'], ['read', 'read'], ['deny', 'deny']] as const) {
    it(`${testId === 'readwrite' ? 'Approve' : testId === 'read' ? 'Approve read-only' : 'Deny'} calls folder.answer with choice ${choice}`, async () => {
      const { api, calls } = fakeApi(() => []);
      const w = keep(mount(FolderAskCard, { props: { ask: ask(), api } }));
      await w.get(`[data-test=folder-ask-${testId}]`).trigger('click');
      await flushPromises();
      expect(calls.filter((c) => c.method === 'folder.answer').map((c) => c.params)).toEqual([{ id: 9, choice }]);
      expect(w.emitted('answered')?.[0]?.[1]).toBe(choice);
      const done = w.get('[data-test=folder-ask-done]').text();
      if (choice === 'deny') expect(done).toContain('Denied');
      else expect(done).toBe(`Approved ${choice === 'read' ? 'read-only' : 'read-write'} for OperatorClifford on Z:\\smokesessions: applies from its next run.`);
      expect(w.find('.buttons').exists()).toBe(false);
    });
  }

  it('a read-only ask: Approve calls folder.answer with read', async () => {
    const { api, calls } = fakeApi(() => []);
    const w = keep(mount(FolderAskCard, { props: { ask: ask({ access: 'read' }), api } }));
    await w.get('[data-test=folder-ask-read]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'folder.answer')!.params).toEqual({ id: 9, choice: 'read' });
  });

  it('a refused answer shows the core\'s words and keeps the buttons', async () => {
    const { api } = fakeApi(() => [], { fail: new Error('the ask is already closed') });
    const w = keep(mount(FolderAskCard, { props: { ask: ask(), api } }));
    await w.get('[data-test=folder-ask-deny]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=folder-ask-error]').text()).toContain('the ask is already closed');
    expect(buttons(w)).toEqual(['Approve', 'Approve read-only', 'Deny']);
  });

  it('an approved ask (a list with all) says what it got and that it applies from the next run', () => {
    const { api } = fakeApi(() => []);
    const w = keep(mount(FolderAskCard, { props: { ask: ask({ state: 'approved', grantedAccess: 'read' }), api } }));
    expect(w.get('[data-test=folder-ask-done]').text()).toBe('Approved read-only: applies from its next run.');
    expect(w.find('.buttons').exists()).toBe(false);
  });
});

describe('FolderAsks: Needs you', () => {
  it('lists the pending ones only, counts them, follows team and member, reloads on folder.asksChanged', async () => {
    let list = [ask(), ask({ id: 10, state: 'approved' }), ask({ id: 11, team: 'payments', member: 'payments/DeveloperAda' })];
    const { api, emit } = fakeApi(() => list);
    const w = keep(mount(FolderAsks, { props: { api, pollMs: 60_000, banner: true } }));
    await flushPromises();
    expect(w.findAll('[data-test=folder-ask]')).toHaveLength(2);
    expect(w.emitted('count')!.at(-1)).toEqual([2]);
    expect(w.get('.title').text()).toBe('Needs you: 2 agents ask for a folder outside its clone');

    await w.setProps({ team: 'payments' });
    expect(w.findAll('[data-test=folder-ask]').map((a) => a.attributes('data-id'))).toEqual(['11']);
    await w.setProps({ team: undefined, member: 'OperatorClifford' });
    expect(w.findAll('[data-test=folder-ask]').map((a) => a.attributes('data-id'))).toEqual(['9']);
    expect(w.emitted('count')!.at(-1)).toEqual([1]);

    list = [];
    emit('folder.asksChanged');
    await flushPromises();
    expect(w.find('[data-test=folder-asks]').exists()).toBe(false);
    expect(w.emitted('count')!.at(-1)).toEqual([0]);
  });

  it('after an answer the item goes and a line says it applies from the next run, until closed', async () => {
    let list = [ask()];
    const { api } = fakeApi(() => list);
    const w = keep(mount(FolderAsks, { props: { api, pollMs: 60_000 } }));
    await flushPromises();
    list = [];
    await w.get('[data-test=folder-ask-read]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=folder-ask]').exists()).toBe(false);
    expect(w.get('[data-test=folder-ask-answered]').text()).toContain('Approved read-only for OperatorClifford on Z:\\smokesessions: applies from its next run.');
    await w.get('[data-test=folder-ask-answered-close]').trigger('click');
    expect(w.find('[data-test=folder-asks]').exists()).toBe(false);
  });

  it('an older core without folder.asks (it throws, or answers no list) shows nothing', async () => {
    for (const api of [
      { call: async () => { throw new Error('method_not_found'); }, onEvent: () => () => {} },
      { call: async () => ({}), onEvent: () => () => {} },
    ] as unknown as YawbleApi[]) {
      const w = keep(mount(FolderAsks, { props: { api, pollMs: 60_000, banner: true } }));
      await flushPromises();
      expect(w.find('[data-test=folder-asks]').exists()).toBe(false);
    }
  });

  it('App shows the banner over every view but the Board (whose Needs You lane lists them)', () => {
    const app = readFileSync(resolve(__dirname, '../../src/renderer/src/App.vue'), 'utf8');
    expect(app).toMatch(/<FolderAsks v-if="view !== 'board'" banner :api="api" :poll-ms="pollMs" \/>/);
    // Inside the one banners box (the phone's 40% cap, B037 F1), after the tool asks.
    const box = app.slice(app.indexOf('<div class="banners"'), app.indexOf('</div>', app.indexOf('<div class="banners"')));
    expect(box).toContain('<FolderAsks');
  });
});

describe('the Board\'s Needs You lane counts folder asks', () => {
  it('lists one, counts it, and follows the team filter', async () => {
    let pending = [ask({ team: 'payments', member: 'payments/DeveloperPriya' })];
    const calls: Call[] = [];
    const api = {
      call: async (method: string, params?: any) => {
        calls.push({ method, params });
        if (method === 'board.list') return { lanes: [{ id: 'todo', label: 'To Do' }, { id: 'blocked', label: 'Needs You' }, { id: 'done', label: 'Done' }], cards: [], teams: [{ id: 1, name: 'payments' }, { id: 2, name: 'i18n' }] };
        if (method === 'folder.asks') return pending;
        if (method === 'folder.answer') { pending = []; return { id: params.id, state: 'approved' }; }
        throw new Error(method);
      },
      onEvent: () => () => {},
    } as unknown as YawbleApi;
    const w = keep(mount(BoardView, { props: { api, pollMs: 60_000 } }));
    await flushPromises();
    const lane = w.get('[data-test=lane-blocked]');
    expect(lane.get('[data-test=folder-ask]').text()).toContain('DeveloperPriya');
    expect(lane.get('.count').text()).toBe('1');
    expect(w.find('[data-test=folder-asks].banner').exists()).toBe(false);

    await w.get('[data-test=board-team]').setValue('i18n');
    expect(w.find('[data-test=folder-ask]').exists()).toBe(false);
    expect(w.get('[data-test=lane-blocked] .count').text()).toBe('0');
    expect(w.get('[data-test=lane-blocked]').text()).toContain('Nothing needs you.');
    await w.get('[data-test=board-team]').setValue('payments');

    await w.get('[data-test=folder-ask-readwrite]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'folder.answer')!.params).toEqual({ id: 9, choice: 'readwrite' });
    expect(w.find('[data-test=folder-ask]').exists()).toBe(false);
    expect(w.get('[data-test=lane-blocked] .count').text()).toBe('0');
  });
});

describe('the member\'s pane shows its own folder asks', () => {
  const member = { id: 7, teamId: 1, team: 'Smoke-sessions', name: 'OperatorClifford', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' } as const;
  const card = (name = member.name): AgentCard => ({
    member: { ...member, name }, state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null,
    runStartedAt: null, lastRunState: null, lastRunReason: null, net: 'model + bus', wallTimeout: '2 h', loosened: [], policyProblems: [],
  } as AgentCard);
  const FakeScreen = defineComponent({ props: { channel: String }, setup: () => () => h('div') });

  it('its ask is in its pane with Approve; another member\'s pane has none', async () => {
    const { api, calls } = fakeApi(() => [ask()]);
    const mine = keep(mount(MemberPane, { props: { card: card(), api } as any, global: { stubs: { TerminalScreen: FakeScreen } } }));
    const other = keep(mount(MemberPane, { props: { card: card('TesterWren'), api } as any, global: { stubs: { TerminalScreen: FakeScreen } } }));
    await flushPromises();
    expect(mine.findAll('[data-test=folder-ask]')).toHaveLength(1);
    expect(other.find('[data-test=folder-ask]').exists()).toBe(false);
    await mine.get('[data-test=folder-ask-deny]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'folder.answer')!.params).toEqual({ id: 9, choice: 'deny' });
  });
});

describe('the Monitor\'s You node counts folder asks', () => {
  it('the model counts a folder ask as its own kind, in the You node\'s words', () => {
    const m = new MonitorModel();
    m.setAsks([{ key: 'f:9', kind: 'folder', team: 'Smoke-sessions', member: 'OperatorClifford', label: 'Z:\\smokesessions' },
      { key: 't:4', kind: 'tool', team: 'payments', member: 'Ana', label: '.NET SDK' }], 0);
    const items = m.needYouItems();
    expect(items.map((i) => i.kind)).toEqual(['tool', 'folder']);
    expect(needYouSummary(items)).toBe('1 tool ask, 1 folder ask');
    expect(needYouSummary([{ kind: 'folder' }, { kind: 'folder' }])).toBe('2 folder asks');
  });

  it('MonitorView reads folder.asks (pending only) into setAsks, and reloads on folder.asksChanged', async () => {
    const { default: MonitorView } = await import('../../src/renderer/src/monitor/MonitorView.vue');
    const spy = vi.spyOn(MonitorModel.prototype, 'setAsks');
    let folders: FolderAsk[] = [ask(), ask({ id: 10, state: 'denied' })];
    const listeners: ((e: { event: string }) => void)[] = [];
    const api = {
      call: async (method: string) => {
        switch (method) {
          case 'app.overview': return { slots: { running: 0, limit: 8, queued: 0 }, teams: [{ id: 1, name: 'Smoke-sessions', repoPath: null, members: 1, running: 0, queued: 0 }] };
          case 'monitor.messages': return { end: 0, messages: [] };
          case 'monitor.steps': return { end: 0, pending: false, steps: [] };
          case 'folder.asks': return folders;
          default: return [];
        }
      },
      onEvent: (fn: (e: { event: string }) => void) => { listeners.push(fn); return () => {}; },
    } as unknown as YawbleApi;
    const w = keep(mount(MonitorView, { props: { api, pollMs: 60_000 }, attachTo: document.body }));
    for (let i = 0; i < 8; i++) await flushPromises();
    const last = () => spy.mock.calls.at(-1)![0].map((a) => ({ ...a }));
    expect(last()).toEqual([{ key: 'f:9', kind: 'folder', team: 'Smoke-sessions', member: 'OperatorClifford', label: 'Z:\\smokesessions' }]);

    folders = [];
    for (const l of [...listeners]) l({ event: 'folder.asksChanged' });
    for (let i = 0; i < 8; i++) await flushPromises();
    expect(last()).toEqual([]);
    spy.mockRestore();
    w.unmount();
    mounted.pop();
  });
});

describe('on a phone', () => {
  it('the card takes the full width and its buttons stack, as the other ask cards', () => {
    const css = readFileSync(resolve(__dirname, '../../src/renderer/src/components/FolderAskCard.vue'), 'utf8');
    expect(css).toMatch(/\.phone \.buttons \{ flex-direction: column; align-items: stretch;/);
    expect(css).toMatch(/\.phone[^{]*\{[^}]*width: 100%/);
    expect(readFileSync(resolve(__dirname, '../../src/renderer/src/components/FolderAsks.vue'), 'utf8')).toContain('.banner.phone { padding: 8px 8px; }');
  });
});
