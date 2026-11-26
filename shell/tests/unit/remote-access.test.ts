// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { CoreEvent, CoreInfo, YawbleApi } from '../../src/shared/api';

// A remote view's access. Fakes only: no core, no link, no network.
vi.mock('@xterm/xterm', () => ({
  Terminal: class {
    cols = 80;
    rows = 24;
    options: Record<string, unknown> = {};
    open() {}
    write() {}
    reset() {}
    focus() {}
    dispose() {}
    loadAddon() {}
    onData(h: (data: string) => void) { typed = h; return { dispose() {} }; }
    onResize() { return { dispose() {} }; }
  },
}));
vi.mock('@xterm/addon-fit', () => ({ FitAddon: class { fit() {} } }));
(globalThis as any).ResizeObserver ??= class { observe() {} disconnect() {} };
let typed: ((data: string) => void) | null = null;

const { accessOf, applyRemoteAccess, loadRemoteAccess, readOnly, READ_ONLY_LABEL, READ_ONLY_TITLE } = await import('../../src/renderer/src/remoteAccess');
const { createRemoteApi } = await import('../../src/renderer/src/remoteApi');
const { errorText } = await import('../../src/renderer/src/api');
const TaskAskCard = (await import('../../src/renderer/src/components/TaskAskCard.vue')).default;
const Composer = (await import('../../src/renderer/src/components/Composer.vue')).default;
const NetworkAskCard = (await import('../../src/renderer/src/components/NetworkAskCard.vue')).default;
const TerminalView = (await import('../../src/renderer/src/components/TerminalView.vue')).default;

const chip = () => document.querySelector('[data-test="remote-access-chip"]');
const info = (over: Partial<CoreInfo> = {}) => ({ product: 'Yawble', version: '1', platform: 'win32', os: 'w', dotnet: '10', pid: 1, sandbox: {} as any, schema: [], ...over }) as CoreInfo;
const taskAsk = { id: 1, member: 'Alpha/DeveloperInes', taskName: 'build', reason: 'to build', commit: 'abcdef1234' } as any;
const composerProps = { teams: [], agents: [], cli: 'claude', teamId: null, running: false };
const networkAsk = {
  id: 7, at: '2026-11-13T21:00:00Z', teamId: 1, team: 'Alpha', member: 'Alpha/DeveloperInes', memberName: 'DeveloperInes',
  host: 'api.nuget.org', reason: null, method: 'CONNECT', pathSample: null, count: 1, lastSeen: '2026-11-13T21:00:00Z',
  hint: null, deliverable: 12, state: 'pending', scope: null, decidedBy: null, decidedAt: null, plainHttp: false, choices: ['agent', 'deny'],
} as any;

function fakeApi(call: (method: string, params?: any) => unknown = () => ({})): { api: YawbleApi; calls: { method: string; params: any }[] } {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    remote: true,
    getInfo: async () => info(),
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => { calls.push({ method, params }); return call(method, params) as any; },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls };
}

afterEach(() => applyRemoteAccess('full'));

describe('remote access from core.getInfo', () => {
  it('is read only when the core says exactly "read"; missing or anything else is full', () => {
    expect(accessOf(info({ remoteAccess: 'read' }))).toBe('read');
    expect(accessOf(info({ remoteAccess: 'full' }))).toBe('full');
    expect(accessOf(info())).toBe('full');
    expect(accessOf({ remoteAccess: 'READ' as any })).toBe('full');
    expect(accessOf(null)).toBe('full');
  });

  it('shows the "Read-only (Visitor)" chip for read, and none for full or a missing value', async () => {
    await loadRemoteAccess(async () => info({ remoteAccess: 'read' }));
    expect(chip()?.textContent).toBe(READ_ONLY_LABEL);
    expect(chip()?.textContent).toBe('Read-only (Visitor)');
    expect(document.documentElement.dataset.remoteAccess).toBe('read');

    await loadRemoteAccess(async () => info());
    expect(chip()).toBeNull();
    expect(readOnly.value).toBe(false);

    await loadRemoteAccess(async () => info({ remoteAccess: 'full' }));
    expect(chip()).toBeNull();
  });

  it('adds one chip only, however often it is applied', () => {
    applyRemoteAccess('read');
    applyRemoteAccess('read');
    expect(document.querySelectorAll('[data-test="remote-access-chip"]')).toHaveLength(1);
  });

  it('keeps what it has when core.getInfo fails', async () => {
    applyRemoteAccess('read');
    expect(await loadRemoteAccess(async () => { throw new Error('closed'); })).toBe('read');
    expect(chip()).not.toBeNull();
  });
});

describe('change buttons in a read-only view', () => {
  it('disables the approval buttons, with the reason as their title', () => {
    applyRemoteAccess('read');
    const w = mount(TaskAskCard, { props: { ask: taskAsk } });
    for (const id of ['task-ask-once', 'task-ask-always', 'task-ask-decline']) {
      const b = w.get(`[data-test="${id}"]`).element as HTMLButtonElement;
      expect(b.disabled).toBe(true);
      expect(b.title).toBe(READ_ONLY_TITLE);
    }
  });

  it('leaves them enabled with full access', () => {
    const w = mount(TaskAskCard, { props: { ask: taskAsk } });
    for (const id of ['task-ask-once', 'task-ask-always', 'task-ask-decline'])
      expect((w.get(`[data-test="${id}"]`).element as HTMLButtonElement).disabled).toBe(false);
    expect(w.get('[data-test="task-ask-always"]').attributes('title')).toContain('Run now');
    expect(chip()).toBeNull();
  });

  it('follows a change of access after the screen is up, and gives the button its own title back', async () => {
    const w = mount(TaskAskCard, { props: { ask: taskAsk } });
    const always = () => w.get('[data-test="task-ask-always"]').element as HTMLButtonElement;
    applyRemoteAccess('read');
    expect(always().disabled).toBe(true);
    applyRemoteAccess('full');
    expect(always().disabled).toBe(false);
    expect(always().title).toContain('Run now');
  });

  it('stays disabled when the screen re-renders its own :disabled', async () => {
    applyRemoteAccess('read');
    const w = mount(TaskAskCard, { props: { ask: taskAsk, busy: true } });
    await w.setProps({ busy: false });
    expect((w.get('[data-test="task-ask-once"]').element as HTMLButtonElement).disabled).toBe(true);
  });

  it('keeps the Concierge send button disabled even with text typed', async () => {
    applyRemoteAccess('read');
    const w = mount(Composer, { props: composerProps });
    await w.get('[data-test="composer-text"]').setValue('hello');
    expect((w.get('[data-test="composer-send"]').element as HTMLButtonElement).disabled).toBe(true);
  });

  it('enables the Concierge send button for full access once there is text', async () => {
    const w = mount(Composer, { props: composerProps });
    await w.get('[data-test="composer-text"]').setValue('hello');
    expect((w.get('[data-test="composer-send"]').element as HTMLButtonElement).disabled).toBe(false);
  });

  it('disables the network answers and sends nothing', async () => {
    applyRemoteAccess('read');
    const { api, calls } = fakeApi();
    const w = mount(NetworkAskCard, { props: { ask: networkAsk, api } });
    const b = w.get('[data-test="network-ask-agent"]');
    expect((b.element as HTMLButtonElement).disabled).toBe(true);
    await b.trigger('click');
    expect(calls).toEqual([]);
  });

  it('shows a read-only refusal of a network answer as the core words it', async () => {
    let refuse: (e: Error) => void = () => {};
    const { api } = fakeApi(() => new Promise((_, reject) => { refuse = reject; }));
    const w = mount(NetworkAskCard, { props: { ask: networkAsk, api } });
    await w.get('[data-test="network-ask-agent"]').trigger('click');
    applyRemoteAccess('read'); // the access arrived while the call was out
    refuse(Object.assign(new Error(READ_ONLY_TITLE), { code: 'remote_refused' }));
    await flushPromises();
    expect(w.text()).toContain(READ_ONLY_TITLE);
    expect(w.text()).not.toContain('answer it in the window');
  });

  it('sends no keys from a terminal to the core', async () => {
    applyRemoteAccess('read');
    const session = { kind: 'interactive', inputEnabled: true, ended: false, startedAt: 't1', command: 'claude' };
    const { api, calls } = fakeApi((m) => (m === 'terminal.attach' ? { replay: '', end: 0, session } : {}));
    mount(TerminalView, { props: { channel: 'member:2', api } });
    await flushPromises();
    typed!('ls\r');
    expect(calls.filter((c) => c.method === 'terminal.input')).toEqual([]);
  });

  it('sends the keys with full access', async () => {
    const session = { kind: 'interactive', inputEnabled: true, ended: false, startedAt: 't1', command: 'claude' };
    const { api, calls } = fakeApi((m) => (m === 'terminal.attach' ? { replay: '', end: 0, session } : {}));
    mount(TerminalView, { props: { channel: 'member:2', api } });
    await flushPromises();
    typed!('ls\r');
    expect(calls.filter((c) => c.method === 'terminal.input')).toHaveLength(1);
  });
});

describe('over the remote socket', () => {
  class FakeSocket {
    static OPEN = 1;
    static last: FakeSocket | null = null;
    readyState = 0;
    sent: any[] = [];
    onopen: (() => void) | null = null;
    onmessage: ((m: { data: string }) => void) | null = null;
    onclose: ((e: { reason: string }) => void) | null = null;
    constructor(public url: string) { FakeSocket.last = this; }
    send(text: string) { this.sent.push(JSON.parse(text)); }
    open() { this.readyState = 1; this.onopen?.(); }
    answer(message: unknown) { this.onmessage?.({ data: JSON.stringify(message) }); }
  }

  it('asks core.getInfo on connect and shows the chip for a Visitor', async () => {
    vi.stubGlobal('WebSocket', FakeSocket);
    try {
      const api = createRemoteApi('ws://example.invalid/rpc');
      const events: CoreEvent[] = [];
      api.onEvent((e) => { events.push(e); if (e.event === 'remote.connected') void loadRemoteAccess(api.getInfo); });
      const ws = FakeSocket.last!;
      ws.open();
      const ask = ws.sent.find((m) => m.method === 'core.getInfo');
      expect(ask).toBeTruthy();
      ws.answer({ id: ask.id, result: info({ remoteAccess: 'read' }) });
      await flushPromises();
      expect(chip()?.textContent).toBe('Read-only (Visitor)');
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('gives a refusal\'s text plainly, with its code', async () => {
    vi.stubGlobal('WebSocket', FakeSocket);
    try {
      const api = createRemoteApi('ws://example.invalid/rpc');
      const ws = FakeSocket.last!;
      ws.open();
      const p = api.call('board.move', { id: 1, lane: 'done' });
      const sent = ws.sent.find((m) => m.method === 'board.move');
      ws.answer({ id: sent.id, error: { code: 'remote_refused', message: READ_ONLY_TITLE } });
      const e = await p.catch((x) => x);
      expect(errorText(e)).toBe(READ_ONLY_TITLE);
      expect((e as { code?: string }).code).toBe('remote_refused');
    } finally {
      vi.unstubAllGlobals();
    }
  });
});
