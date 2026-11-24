// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it } from 'vitest';
import App from '../../src/renderer/src/App.vue';
import { savedConciergeAgent } from '../../src/renderer/src/conciergeDefault';
import type { Conversation, YawbleApi } from '../../src/shared/api';

// Card 420 (verify B096): a new Concierge chat in the window should start on the saved default agent
// (Settings > Concierge, concierge.defaults). The core applies the saved default only when concierge.send has no cli;
// the window always sends one, so the window reads the saved default itself (card 419 fix round). A scripted fake
// core: no agent runs. `ready`: the agents signed in; `noDefaults`: an older core without concierge.defaults.
function fakeCore(defaultAgent: string | null, ready: string[] = ['claude', 'grok'], noDefaults = false) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      switch (method) {
        case 'app.overview': return { slots: { running: 0, limit: 4, queued: 0 }, teams: [] } as any;
        case 'link.status': return { state: 'off', viewers: [] } as any;
        case 'agents.detect': return ['claude', 'grok'].map((id) => (
          { cliId: id, found: true, signIn: { status: ready.includes(id) ? 'signed-in' : 'signed-out', detail: null } }
        )) as any;
        case 'concierge.defaults':
          if (noDefaults) throw new Error('unknown method concierge.defaults');
          return { defaultAgent } as any;
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

/** Mounts the window, sends "hello" from a new chat and returns the cli the send carried. */
async function sentCli(core: ReturnType<typeof fakeCore>) {
  const w = mount(App, { props: { api: core.api, pollMs: 60_000 } });
  await flushPromises();
  await flushPromises();
  await w.get('[data-test=composer-text]').setValue('hello');
  await w.get('[data-test=composer-text]').trigger('keydown', { key: 'Enter' });
  await flushPromises();
  const send = core.calls.find((c) => c.method === 'concierge.send');
  w.unmount();
  expect(send).toBeTruthy();
  return send!.params.cli;
}

describe('card 420: a new chat and the saved Concierge default agent', () => {
  beforeEach(() => {
    try { localStorage.clear(); } catch { /* none */ }
    savedConciergeAgent.value = null;
  });

  // F1 (fixed, card 419 fix round): the window used to send the first ready agent whatever was saved.
  it('F1: with "grok" saved and claude ready first, a new chat is sent on grok', async () => {
    const core = fakeCore('grok');
    const cli = await sentCli(core);
    expect(cli === 'grok' || cli == null).toBe(true);
    expect(core.calls.some((c) => c.method === 'concierge.defaults')).toBe(true);
  });

  it('fallback: no default saved, a new chat starts on the first ready agent', async () => {
    expect(await sentCli(fakeCore(null))).toBe('claude');
  });

  it('fallback: the saved default is not ready (signed out), the first ready agent', async () => {
    expect(await sentCli(fakeCore('grok', ['claude']))).toBe('claude');
    savedConciergeAgent.value = null;
    expect(await sentCli(fakeCore('claude', ['grok']))).toBe('grok');
  });

  it('fallback: the saved default is not installed on this computer, the first ready agent', async () => {
    expect(await sentCli(fakeCore('codex'))).toBe('claude');
  });

  it('fallback: an older core without concierge.defaults, the first ready agent', async () => {
    expect(await sentCli(fakeCore(null, ['claude', 'grok'], true))).toBe('claude');
  });
});
