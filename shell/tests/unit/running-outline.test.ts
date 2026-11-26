// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import App from '../../src/renderer/src/App.vue';
import MemberPane from '../../src/renderer/src/components/MemberPane.vue';
import BoardCardItem from '../../src/renderer/src/board/BoardCardItem.vue';
import RunningOutline from '../../src/renderer/src/components/RunningOutline.vue';
import type { AgentCard, BoardCard, YawbleApi } from '../../src/shared/api';

const teams = [
  { id: 1, name: 'busy', repoPath: null, members: 3, running: 2, queued: 1 },
  { id: 2, name: 'waiting', repoPath: null, members: 2, running: 0, queued: 2 },
  { id: 3, name: 'quiet', repoPath: null, members: 2, running: 0, queued: 0 },
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

const member = { id: 1, teamId: 3, team: 'hello-world', name: 'DeveloperAda', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' } as const;
const card = (over: Partial<AgentCard> = {}): AgentCard => ({
  member: { ...member }, state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null,
  runStartedAt: null, lastRunState: 'completed', lastRunReason: null, ...over,
});

describe('a running card has a dotted outline whose dots move clockwise', () => {
  it('draws one rounded rect with round dots that march forward along its path, and stands still with reduced motion', () => {
    const w = mount(RunningOutline, { props: { radius: 12, inset: '-1px' } });
    expect(w.attributes('aria-hidden')).toBe('true');
    expect(w.attributes('style')).toContain('inset: -1px');
    const rect = w.get('rect');
    expect(rect.attributes('rx')).toBe('12');
  });

  it('on the Teams list, only a running team card has it, and its border gives way to it', async () => {
    const w = mount(App, { props: { api: api(), pollMs: 60_000 } });
    await flushPromises();
    await w.get('[data-test=nav-teams]').trigger('click');
    await flushPromises();
    const cards = w.findAll('[data-test=team-card]');
    expect(cards.map((c) => c.attributes('data-state'))).toEqual(['running', 'queued', 'idle']);
    expect(cards.map((c) => c.find('[data-test=running-outline]').exists())).toEqual([true, false, false]);
  });

  it('on the team page, only a running member card has it, and the old breathing is gone', async () => {
    const w = mount(MemberPane, { props: { card: card({ state: 'running', runId: 7, runStartedAt: '2026-11-25T10:00:00Z' }) }, global: { stubs: { FolderAsks: true } } });
    expect(w.find('[data-test=running-outline]').exists()).toBe(true);
    await w.setProps({ card: card() });
    expect(w.find('[data-test=running-outline]').exists()).toBe(false);
    await w.setProps({ card: card({ state: 'queued', queuePosition: 1, queueLength: 2, queuedInstructions: 1 }) });
    expect(w.find('[data-test=running-outline]').exists()).toBe(false);
  });

  it('on the board, only a running card has it, and its border gives way to it', async () => {
    const boardCard = (over: Partial<BoardCard> = {}): BoardCard => ({
      id: 7, team: 'payments', teamId: 1, member: 'DeveloperPriya', item: null, title: 'Run the suite', body: '', status: 'running',
      lane: 'in-progress', placed: false, awaitingManager: false, deliverable: 12, createdAt: '2026-11-08T10:00:00Z', updatedAt: '2026-11-08T10:00:00Z',
      trailCount: 0, ...over,
    });
    const w = mount(BoardCardItem, { props: { card: boardCard() } });
    expect(w.classes()).toContain('running');
    expect(w.find('[data-test=running-outline]').exists()).toBe(true);
    for (const status of ['queued', 'blocked', 'handback', 'done'] as const) {
      await w.setProps({ card: boardCard({ status, lane: status === 'done' ? 'done' : 'in-progress' }) });
      expect(w.find('[data-test=running-outline]').exists()).toBe(false);
      expect(w.classes()).not.toContain('running');
    }
  });
});
