// @vitest-environment happy-dom
// A team whose member has an open needs_decision reads "Waiting on you" in the team header chip,
// the team list card and the sidebar dot, and the embed sends dot 'waiting'. The teams are fed in the shape the core's
// teams.list / app.overview really serializes TeamSummary (camelCase, every field: id, name, repoPath, members, running,
// queued, paused, archived, workflowExpiryDays, waiting), plus an older core's shape with no 'waiting' at all.
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import App from '../../src/renderer/src/App.vue';
import Sidebar from '../../src/renderer/src/components/Sidebar.vue';
import TeamStateChip from '../../src/renderer/src/components/TeamStateChip.vue';
import { buildNav, type EmbedNavState } from '../../src/renderer/src/embed';
import type { TeamSummary, YawbleApi } from '../../src/shared/api';

enableAutoUnmount(afterEach);

/** TeamSummary as System.Text.Json writes the core record (camelCase, all fields). */
const core = (id: number, name: string, o: { running?: number; queued?: number; paused?: boolean; waiting?: number } = {}) =>
  ({ id, name, repoPath: null, members: 2, running: 0, queued: 0, paused: false, archived: false, workflowExpiryDays: 3, waiting: 0, ...o });
/** An older core: no 'waiting' field. */
const older = (id: number, name: string) => {
  const { waiting: _w, ...rest } = core(id, name);
  return rest;
};

const teams = [
  core(1, 'asking', { waiting: 1 }),
  core(2, 'busy', { waiting: 1, running: 1 }),
  core(3, 'quiet', { waiting: 0 }),
  older(4, 'old'),
  core(5, 'held', { waiting: 2, paused: true }),
  core(6, 'lined', { waiting: 1, queued: 2 }),
  core(7, 'pausedOnly', { paused: true }),
];
const states = ['waiting', 'running', 'idle', 'idle', 'waiting', 'waiting', 'paused'];
const labels = ['Waiting on you', 'Running', 'Idle', 'Idle', 'Waiting on you', 'Waiting on you', 'Paused'];

const api = (): YawbleApi => ({
  getInfo: async () => { throw new Error('no'); },
  getAvailableBackends: async () => [],
  call: async (method: string) => {
    switch (method) {
      case 'app.overview': return { slots: { running: 1, limit: 8, queued: 2 }, teams } as any;
      case 'teams.list': return teams as any;
      case 'link.status': return { state: 'off', viewers: [] } as any;
      default: return [] as any;
    }
  },
  onEvent: () => () => {},
  openTerminal: async () => {},
} as unknown as YawbleApi);

describe('the app reads a waiting team as Waiting on you', () => {
  it('sidebar dots, team list cards and each team header chip', async () => {
    const w = mount(App, { props: { api: api(), pollMs: 60_000 } });
    await flushPromises();

    const dots = w.findAll('[data-test=team-item] .dot');
    expect(dots.map((d) => d.attributes('data-state'))).toEqual(states);
    expect(dots.map((d) => d.attributes('title'))).toEqual(labels);

    await w.get('[data-test=nav-teams]').trigger('click');
    await flushPromises();
    const cards = w.findAll('[data-test=team-card]');
    expect(cards.map((c) => c.attributes('data-state'))).toEqual(states);
    expect(cards.map((c) => c.classes().find((k) => k.startsWith('is-')))).toEqual(states.map((s) => `is-${s}`));
    expect(cards.map((c) => c.get('[data-test=team-state]').text())).toEqual(labels);

    for (let i = 0; i < teams.length; i++) {
      await w.findAll('[data-test=team-item]')[i].trigger('click');
      await flushPromises();
      const chip = w.get('[data-test=team-page-state]');
      expect([teams[i].name, chip.text(), chip.attributes('data-state')]).toEqual([teams[i].name, labels[i], states[i]]);
      expect(chip.classes()).toContain(`is-${states[i]}`);
    }
  });

  it('TeamStateChip: waiting 1 -> Waiting on you, no pulse; running wins; 0 or absent -> Idle', () => {
    const chip = (t: Partial<TeamSummary>) => mount(TeamStateChip, { props: { team: { running: 0, queued: 0, ...t } as any } });
    const a = chip(core(1, 'a', { waiting: 1 }));
    expect([a.text(), a.attributes('data-state'), a.classes().includes('is-waiting'), a.find('.pulse').exists()])
      .toEqual(['Waiting on you', 'waiting', true, false]);
    const b = chip(core(1, 'b', { waiting: 3, running: 1 }));
    expect([b.text(), b.attributes('data-state'), b.find('.pulse').exists()]).toEqual(['Running', 'running', true]);
    expect(chip(core(1, 'c')).text()).toBe('Idle');
    expect(chip(older(1, 'd')).text()).toBe('Idle');
    expect(chip(core(1, 'e', { waiting: 1, paused: true, queued: 4 })).text()).toBe('Waiting on you');
  });

  it('embed: buildNav sends dot waiting, the same as Sidebar.vue draws', () => {
    const s: EmbedNavState = {
      view: 'teams', conversations: [], teams: teams as unknown as TeamSummary[], conversationId: null, teamId: 1,
      slots: { running: 1, limit: 8, queued: 2 }, readOnly: false, removing: null, openWorkflows: 0,
    };
    const nav = buildNav(s);
    expect(nav.teams.map((t) => t.dot)).toEqual(states);
    // Waiting carries no running badge.
    expect(nav.teams[0].badge).toBeNull();
    const side = mount(Sidebar, { props: { ...s, remote: true } as any });
    expect(side.findAll('[data-test=team-item] .dot').map((d) => d.attributes('data-state'))).toEqual(nav.teams.map((t) => t.dot));
    // The wire form is JSON: 'waiting' survives it.
    expect(JSON.parse(JSON.stringify(nav)).teams[0].dot).toBe('waiting');
  });
});
