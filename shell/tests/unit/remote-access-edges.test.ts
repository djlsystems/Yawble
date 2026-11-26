// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { CoreInfo, YawbleApi } from '../../src/shared/api';

// Edge cases of the remote view's access. Fakes only: no core, no link, no network.
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

const { accessOf, applyRemoteAccess, loadRemoteAccess, readOnly, READ_ONLY_TITLE } = await import('../../src/renderer/src/remoteAccess');
const { createRemoteApi } = await import('../../src/renderer/src/remoteApi');
const Composer = (await import('../../src/renderer/src/components/Composer.vue')).default;
const TaskAskCard = (await import('../../src/renderer/src/components/TaskAskCard.vue')).default;
const TerminalView = (await import('../../src/renderer/src/components/TerminalView.vue')).default;

// The core's sentence (core/src/Yawble.Core/Link/RemoteAccess.cs ReadOnlyRefusal), copied here on purpose.
const CORE_REFUSAL = 'You have read-only access to this computer (Visitor). Ask a workgroup Member or an Owner for more.';
const chip = () => document.querySelector('[data-test="remote-access-chip"]') as HTMLElement | null;
const composerProps = { teams: [], agents: [], cli: 'claude', teamId: null, running: false };

afterEach(() => applyRemoteAccess('full'));

describe('accessOf: only exact "read" is read', () => {
  it.each([
    ['READ'], ['Read'], [''], [' read'], ['read '], ['readonly'], ['visitor'], ['full'], [null], [undefined], [0], [1], [true], [['read']], [{ access: 'read' }],
  ])('%j is full', (v) => {
    expect(accessOf({ remoteAccess: v as any })).toBe('full');
  });

  it('exact "read" is read; an empty answer or none is full', () => {
    expect(accessOf({ remoteAccess: 'read' })).toBe('read');
    expect(accessOf({})).toBe('full');
    expect(accessOf(undefined)).toBe('full');
    expect(accessOf(null)).toBe('full');
  });

  it('the chip\'s title is the core\'s refusal sentence, word for word', () => {
    expect(READ_ONLY_TITLE).toBe(CORE_REFUSAL);
    applyRemoteAccess('read');
    expect(chip()!.title).toBe(CORE_REFUSAL);
    expect(chip()!.getAttribute('role')).toBe('status');
    expect(chip()!.textContent).toBe('Read-only (Visitor)');
  });
});

describe('core.getInfo over the remote socket, as the core writes it', () => {
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
    raw(text: string) { this.onmessage?.({ data: text }); }
  }

  // The core's getInfo answer with remoteAccess added (CloudLink.AnswerAsync: info["remoteAccess"] = access).
  const coreInfoText = (tail: string) =>
    `{"product":"Yawble","version":"t","platform":"win32","os":"w","dotnet":"10","pid":1,"sandbox":{},"schema":[]${tail}}`;

  async function connectWith(tail: string) {
    const api = createRemoteApi('ws://example.invalid/rpc');
    api.onEvent((e) => { if (e.event === 'remote.connected') void loadRemoteAccess(api.getInfo); });
    const ws = FakeSocket.last!;
    ws.open();
    const ask = ws.sent.find((m) => m.method === 'core.getInfo');
    expect(ask).toBeTruthy();
    ws.raw(`{"id":${JSON.stringify(ask.id)},"result":${coreInfoText(tail)}}`);
    await flushPromises();
    return ws;
  }

  it.each([
    [',"remoteAccess":"read"', 'read'],
    [',"remoteAccess":"full"', 'full'],
    ['', 'full'],
    [',"remoteAccess":null', 'full'],
    [',"remoteAccess":"READ"', 'full'],
  ])('getInfo%s gives %s', async (tail, expected) => {
    vi.stubGlobal('WebSocket', FakeSocket);
    try {
      await connectWith(tail);
      expect(readOnly.value).toBe(expected === 'read');
      expect(document.documentElement.dataset.remoteAccess).toBe(expected);
      expect(chip() !== null).toBe(expected === 'read');
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('a later connect that says full takes the chip away again', async () => {
    vi.stubGlobal('WebSocket', FakeSocket);
    try {
      await connectWith(',"remoteAccess":"read"');
      expect(chip()).not.toBeNull();
      await connectWith(',"remoteAccess":"full"');
      expect(chip()).toBeNull();
      expect(readOnly.value).toBe(false);
    } finally {
      vi.unstubAllGlobals();
    }
  });
});

describe('change buttons when access changes after the screen is up', () => {
  it('a button the screen itself disables stays disabled when access goes from read back to full', async () => {
    // Composer's send is :disabled="!canSend": with no text it must stay disabled whatever the access does.
    const w = mount(Composer, { props: composerProps });
    const send = () => w.get('[data-test="composer-send"]').element as HTMLButtonElement;
    expect(send().disabled).toBe(true);
    applyRemoteAccess('read');
    expect(send().disabled).toBe(true);
    applyRemoteAccess('full');
    await flushPromises();
    expect(send().disabled).toBe(true);
  });

  it('a busy approval stays disabled when access goes from read back to full', async () => {
    const ask = { id: 1, member: 'Alpha/DeveloperInes', taskName: 'build', reason: 'to build', commit: 'abcdef1234' } as any;
    const w = mount(TaskAskCard, { props: { ask, busy: true } });
    const once = () => w.get('[data-test="task-ask-once"]').element as HTMLButtonElement;
    expect(once().disabled).toBe(true);
    applyRemoteAccess('read');
    applyRemoteAccess('full');
    await flushPromises();
    expect(once().disabled).toBe(true);
  });
});

describe('terminals', () => {
  function fakeApi(): { api: YawbleApi; calls: string[] } {
    const calls: string[] = [];
    const session = { kind: 'interactive', inputEnabled: true, ended: false, startedAt: 't1', command: 'claude' };
    const api: YawbleApi = {
      remote: true,
      getInfo: async () => ({}) as CoreInfo,
      getAvailableBackends: async () => [],
      call: async (method: string) => { calls.push(method); return (method === 'terminal.attach' ? { replay: '', end: 0, session } : {}) as any; },
      onEvent: () => () => {},
      openTerminal: async () => {},
      pickFolder: async () => null,
    };
    return { api, calls };
  }

  it('sends no keys once the access turns read after the terminal is attached', async () => {
    const { api, calls } = fakeApi();
    mount(TerminalView, { props: { channel: 'member:2', api } });
    await flushPromises();
    applyRemoteAccess('read');
    typed!('rm -rf /\r');
    expect(calls.filter((m) => m === 'terminal.input')).toEqual([]);
  });
});
