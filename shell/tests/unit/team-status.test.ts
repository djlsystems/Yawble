// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import App from '../../src/renderer/src/App.vue';
import type { YawbleApi } from '../../src/shared/api';

const teams = [
  { id: 1, name: 'busy', repoPath: null, members: 3, running: 2, queued: 1 },
  { id: 2, name: 'waiting', repoPath: null, members: 2, running: 0, queued: 2 },
  { id: 3, name: 'held', repoPath: null, members: 2, running: 0, queued: 0, paused: true },
  { id: 4, name: 'quiet', repoPath: null, members: 2, running: 0, queued: 0 },
];

function api(): YawbleApi {
  return {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string) => {
      switch (method) {
        case 'app.overview': return { slots: { running: 2, limit: 8, queued: 3 }, teams } as any;
        case 'link.status': return { state: 'off', viewers: [] } as any;
        case 'agents.detect': return [] as any;
        default: return [] as any;
      }
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
  } as unknown as YawbleApi;
}

async function teamsPage() {
  const w = mount(App, { props: { api: api(), pollMs: 60_000 } });
  await flushPromises();
  await w.get('[data-test=nav-teams]').trigger('click');
  await flushPromises();
  return w;
}

describe('a team shows its status in the list', () => {
  it('outlines each team card in its state colour and says the state in words', async () => {
    const w = await teamsPage();
    const cards = w.findAll('[data-test=team-card]');
    expect(cards.map((c) => c.attributes('data-state'))).toEqual(['running', 'queued', 'paused', 'idle']);
    expect(cards[0].classes()).toContain('is-running');
    expect(cards[1].classes()).toContain('is-queued');
    expect(cards[2].classes()).toContain('is-paused');
    expect(cards[3].classes()).toContain('is-idle');
    expect(cards.map((c) => c.get('[data-test=team-state]').text())).toEqual(['Running', 'Queued', 'Paused', 'Idle']);
  });

  it('gives the sidebar dot the same state, animated only while running', async () => {
    const w = await teamsPage();
    const dots = w.findAll('[data-test=team-item] .dot');
    expect(dots.map((d) => d.attributes('data-state'))).toEqual(['running', 'queued', 'paused', 'idle']);
    expect(dots[0].classes()).toContain('busy');
    expect(dots.slice(1).some((d) => d.classes().includes('busy'))).toBe(false);
  });
  it("shows the same state chip in the team's own page title", async () => {
    const w = mount(App, { props: { api: api(), pollMs: 60_000 } });
    await flushPromises();
    const items = w.findAll('[data-test=team-item]');
    for (const [i, word] of [[0, 'Running'], [1, 'Queued'], [2, 'Paused'], [3, 'Idle']] as const) {
      await items[i].trigger('click');
      await flushPromises();
      const chip = w.get('[data-test=team-page-state]');
      expect(chip.text()).toBe(word);
      expect(chip.attributes('data-state')).toBe(word.toLowerCase());
    }
  });
});
