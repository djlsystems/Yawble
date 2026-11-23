// @vitest-environment happy-dom
// B130: a team whose member has an open needs_decision says "Waiting on you" (the member card's words), not Idle.
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import App from '../../src/renderer/src/App.vue';
import TeamStateChip from '../../src/renderer/src/components/TeamStateChip.vue';
import { WaitingLabel } from '../../src/renderer/src/reportFormat';
import { teamState, teamStateLabel } from '../../src/renderer/src/teamState';
import type { YawbleApi } from '../../src/shared/api';

const t = (o: { running?: number; queued?: number; paused?: boolean; waiting?: number }) =>
  ({ running: 0, queued: 0, paused: false, ...o });

describe('teamState: running > waiting > paused > queued > idle', () => {
  it('waiting alone is waiting', () => expect(teamState(t({ waiting: 1 }))).toBe('waiting'));
  it('waiting and running is running', () => expect(teamState(t({ waiting: 2, running: 1 }))).toBe('running'));
  it('waiting and paused is waiting', () => expect(teamState(t({ waiting: 1, paused: true }))).toBe('waiting'));
  it('waiting and queued is waiting', () => expect(teamState(t({ waiting: 1, queued: 3 }))).toBe('waiting'));
  it('paused beats queued', () => expect(teamState(t({ paused: true, queued: 1 }))).toBe('paused'));
  it('none is idle; an older core with no waiting field is idle too', () => {
    expect(teamState(t({ waiting: 0 }))).toBe('idle');
    expect(teamState({ running: 0, queued: 0 })).toBe('idle');
  });
  it('the label is the member card\'s words', () => {
    expect(teamStateLabel.waiting).toBe(WaitingLabel);
    expect(WaitingLabel).toBe('Waiting on you');
  });
});

describe('TeamStateChip', () => {
  it('shows "Waiting on you" with is-waiting and data-state=waiting', () => {
    const w = mount(TeamStateChip, { props: { team: t({ waiting: 1 }) } });
    expect(w.text()).toBe('Waiting on you');
    expect(w.classes()).toContain('is-waiting');
    expect(w.attributes('data-state')).toBe('waiting');
    expect(w.find('.pulse').exists()).toBe(false);
  });
  it('says Running while a member runs, whatever waits', () => {
    const w = mount(TeamStateChip, { props: { team: t({ waiting: 1, running: 1 }) } });
    expect(w.text()).toBe('Running');
  });
});

describe('the app: team list, page header and sidebar dot', () => {
  const teams = [
    { id: 1, name: 'asking', repoPath: null, members: 2, running: 0, queued: 0, waiting: 1 },
    { id: 2, name: 'quiet', repoPath: null, members: 2, running: 0, queued: 0, waiting: 0 },
  ];
  const api = (): YawbleApi => ({
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string) => {
      switch (method) {
        case 'app.overview': return { slots: { running: 0, limit: 8, queued: 0 }, teams } as any;
        case 'link.status': return { state: 'off', viewers: [] } as any;
        default: return [] as any;
      }
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
  } as unknown as YawbleApi);

  it('a team with an open decision reads Waiting on you everywhere', async () => {
    const w = mount(App, { props: { api: api(), pollMs: 60_000 } });
    await flushPromises();
    expect(w.findAll('[data-test=team-item] .dot').map((d) => d.attributes('data-state'))).toEqual(['waiting', 'idle']);
    await w.get('[data-test=nav-teams]').trigger('click');
    await flushPromises();
    const cards = w.findAll('[data-test=team-card]');
    expect(cards.map((c) => c.attributes('data-state'))).toEqual(['waiting', 'idle']);
    expect(cards[0].classes()).toContain('is-waiting');
    expect(cards.map((c) => c.get('[data-test=team-state]').text())).toEqual(['Waiting on you', 'Idle']);
    await w.findAll('[data-test=team-item]')[0].trigger('click');
    await flushPromises();
    const chip = w.get('[data-test=team-page-state]');
    expect(chip.text()).toBe('Waiting on you');
    expect(chip.attributes('data-state')).toBe('waiting');
  });
});
