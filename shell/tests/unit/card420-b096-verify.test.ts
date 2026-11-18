// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it } from 'vitest';
import App from '../../src/renderer/src/App.vue';
import type { Conversation, YawbleApi } from '../../src/shared/api';

// Card 420 (verify B096): a new Concierge chat in the window should start on the saved default agent
// (Settings > Concierge, concierge.defaults). The core applies the saved default only when concierge.send has no cli;
// the window always sends one. A scripted fake core: no agent runs.
function fakeCore(defaultAgent: string | null) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      switch (method) {
        case 'app.overview': return { slots: { running: 0, limit: 4, queued: 0 }, teams: [] } as any;
        case 'link.status': return { state: 'off', viewers: [] } as any;
        case 'agents.detect': return [
          { cliId: 'claude', found: true, signIn: { status: 'signed-in', detail: null } },
          { cliId: 'grok', found: true, signIn: { status: 'signed-in', detail: null } },
        ] as any;
        case 'concierge.defaults': return { defaultAgent } as any;
        case 'concierge.list': return [] as any;
        case 'concierge.get': {
          const c: Conversation = {
            id: 'c1', title: 'x', cli: 'grok', teamId: null, team: null, createdAt: '', updatedAt: '', running: false, unread: 0, watching: 0, watches: [],
            messages: [], turn: null,
          } as any;
          return c as any;
        }
        case 'concierge.send': return { id: 'c1' } as any;
        default: throw new Error(`unexpected ${method}`);
      }
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls };
}

describe('card 420: a new chat and the saved Concierge default agent', () => {
  beforeEach(() => { try { localStorage.clear(); } catch { /* none */ } });

  // F1: the window's new chat starts on the first ready agent (App.vue defaultCli) and sends it as cli, so the
  // core never applies the saved default. Expected: the composer starts on grok (or the send leaves cli out).
  it.fails('F1: with "grok" saved and claude ready first, a new chat is sent on grok', async () => {
    const core = fakeCore('grok');
    const w = mount(App, { props: { api: core.api, pollMs: 60_000 } });
    await flushPromises();
    await flushPromises();
    await w.get('[data-test=composer-text]').setValue('hello');
    await w.get('[data-test=composer-text]').trigger('keydown', { key: 'Enter' });
    await flushPromises();
    const send = core.calls.find((c) => c.method === 'concierge.send')!;
    expect(send).toBeTruthy();
    expect(send.params.cli === 'grok' || send.params.cli == null).toBe(true);
    w.unmount();
  });

  it('(today) the new chat is sent on the first ready agent, whatever is saved', async () => {
    const core = fakeCore('grok');
    const w = mount(App, { props: { api: core.api, pollMs: 60_000 } });
    await flushPromises();
    await flushPromises();
    await w.get('[data-test=composer-text]').setValue('hello');
    await w.get('[data-test=composer-text]').trigger('keydown', { key: 'Enter' });
    await flushPromises();
    expect(core.calls.find((c) => c.method === 'concierge.send')!.params.cli).toBe('claude');
    expect(core.calls.some((c) => c.method === 'concierge.defaults')).toBe(false); // the window never reads it
    w.unmount();
  });
});
