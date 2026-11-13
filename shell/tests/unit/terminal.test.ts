// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import type { CoreEvent, YawbleApi } from '../../src/shared/api';

// xterm.js does not lay out under happy-dom: a stand-in terminal that is fitted to 132 x 41.
vi.mock('@xterm/xterm', () => ({
  Terminal: class {
    cols = 132;
    rows = 41;
    options: Record<string, unknown> = {};
    open() {}
    write() {}
    reset() {}
    focus() {}
    dispose() {}
    loadAddon() {}
    onData() { return { dispose() {} }; }
    onResize() { return { dispose() {} }; }
  },
}));
vi.mock('@xterm/addon-fit', () => ({ FitAddon: class { fit() {} } }));
(globalThis as any).ResizeObserver ??= class { observe() {} disconnect() {} };

const TerminalView = (await import('../../src/renderer/src/components/TerminalView.vue')).default;

function fakeApi(session: unknown) {
  const calls: { method: string; params: any }[] = [];
  let push: ((e: CoreEvent) => void) | null = null;
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      if (method === 'terminal.attach') return { replay: '', end: 0, session } as any;
      return {} as any;
    },
    onEvent: (h) => { push = h; return () => {}; },
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls, push: (e: CoreEvent) => push!(e) };
}

const live = (startedAt: string) => ({ kind: 'interactive', inputEnabled: true, ended: false, startedAt, command: 'claude' });
const resizes = (calls: { method: string; params: any }[]) => calls.filter((c) => c.method === 'terminal.resize').map((c) => c.params);

describe('terminal view', () => {
  // The PTY starts at a default size; the window's terminal is already fitted when a session begins, so xterm
  // raises no resize of its own. Without telling the core, the CLI draws for the wrong size (garbled TUI).
  it('tells the core its size when a session on its channel goes live, once per session', async () => {
    const { api, calls, push } = fakeApi(null);
    mount(TerminalView, { props: { channel: 'member:2', api } });
    await flushPromises();
    expect(resizes(calls)).toEqual([]);

    push({ event: 'terminal.state', data: { channel: 'member:2', session: live('t1') } } as any);
    await flushPromises();
    expect(resizes(calls)).toEqual([{ channel: 'member:2', cols: 132, rows: 41 }]);

    push({ event: 'terminal.state', data: { channel: 'member:2', session: live('t1') } } as any);
    push({ event: 'terminal.state', data: { channel: 'member:9', session: live('t9') } } as any);
    await flushPromises();
    expect(resizes(calls)).toHaveLength(1);

    push({ event: 'terminal.state', data: { channel: 'member:2', session: live('t2') } } as any);
    await flushPromises();
    expect(resizes(calls)).toHaveLength(2);
  });

  it('tells the core its size when it attaches to a session that is already live', async () => {
    const { api, calls } = fakeApi(live('t1'));
    mount(TerminalView, { props: { channel: 'member:2', api } });
    await flushPromises();
    expect(resizes(calls)).toEqual([{ channel: 'member:2', cols: 132, rows: 41 }]);
  });

  it('exposes its size for the session start', async () => {
    const { api } = fakeApi(null);
    const w = mount(TerminalView, { props: { channel: 'member:2', api } });
    await flushPromises();
    expect((w.vm as any).size()).toEqual({ cols: 132, rows: 41 });
  });
});
