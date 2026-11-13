// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it } from 'vitest';
import App from '../../src/renderer/src/App.vue';
import type { YawbleApi } from '../../src/shared/api';

const conversation = {
  id: 'c1', title: 'old chat', cli: 'claude', teamId: null, team: null, createdAt: '', updatedAt: '', running: false, unread: 0, watching: 0, watches: [],
  messages: [{ id: 1, role: 'user', text: 'old chat', at: '', state: null, error: null, tools: [], cli: null }], turn: null,
};

function api(): YawbleApi {
  return {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string) => {
      switch (method) {
        case 'app.overview': return { slots: { running: 0, limit: 8, queued: 0 }, teams: [{ id: 1, name: 'payments', repoPath: null, members: 2, running: 0, queued: 0 }] } as any;
        case 'link.status': return { state: 'off', viewers: [] } as any;
        case 'concierge.list': return [{ ...conversation }] as any;
        case 'concierge.get': return JSON.parse(JSON.stringify(conversation));
        default: return [] as any;
      }
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
  } as unknown as YawbleApi;
}

describe('where the app opens', () => {
  beforeEach(() => { localStorage.clear(); sessionStorage.clear(); });

  it('a fresh start opens a new message to the Concierge, whatever was open last time', async () => {
    localStorage.setItem('yawble.place', JSON.stringify({ view: 'teams', conversationId: 'c1', teamId: 1, collapsed: true }));
    const w = mount(App, { props: { api: api(), pollMs: 60_000 } });
    await flushPromises();
    expect(w.find('[data-test=concierge-hero]').exists()).toBe(true);
    expect(w.find('[data-test=team-grid]').exists()).toBe(false);
    expect(w.find('.app').classes()).toContain('collapsed'); // the sidebar keeps how the person left it
  });

  it('a reload of the window keeps the screen the person was on', async () => {
    sessionStorage.setItem('yawble.place', JSON.stringify({ view: 'teams', conversationId: null, teamId: null }));
    const w = mount(App, { props: { api: api(), pollMs: 60_000 } });
    await flushPromises();
    expect(w.find('[data-test=team-grid]').exists()).toBe(true);
  });
});
