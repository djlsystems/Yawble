// @vitest-environment happy-dom
// Card 619 (B141 verify): the pending list with companion.list entries shaped exactly as the core sends them
// (CompanionService.ListForWindow: id, client, tool, plan, asked, expires, state; times in .NET "O" format,
// e.g. "2026-11-24T09:00:00.0000000+00:00", not JavaScript's toISOString()).
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import { askedAgo } from '../../src/renderer/src/companionAge';
import CompanionAsks from '../../src/renderer/src/components/CompanionAsks.vue';
import type { YawbleApi } from '../../src/shared/api';

/** A time as the core writes it: DateTimeOffset.ToUniversalTime().ToString("O"). */
const coreIso = (ms: number) => new Date(ms).toISOString().replace(/\.(\d{3})Z$/, '.$10000+00:00');
const DAY = 86_400_000;

function fakeApi(answers: Record<string, (params: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = answers[method];
      if (!answer) throw new Error(`unexpected ${method}`);
      return answer(params) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return { api, calls };
}

const coreAction = (id: string, askedMs: number, tool = 'team_delete') => ({
  id, client: 'claude-code', tool, plan: `DELETE team alpha (${id})`,
  asked: coreIso(askedMs), expires: coreIso(askedMs + 7 * DAY), state: 'pending',
});

describe('B141 verify: the pending list with core-shaped requests', () => {
  it('ReadsTheCoresTimeFormat', () => {
    const now = Date.parse('2026-11-24T12:00:00Z');
    expect(coreIso(now)).toBe('2026-11-24T12:00:00.0000000+00:00');
    expect(askedAgo(coreIso(now - 10 * 60_000), now)).toBe('asked 10 min ago');
    expect(askedAgo(coreIso(now - 5 * 3_600_000), now)).toBe('asked 5 h ago');
    expect(askedAgo(coreIso(now - 6 * DAY - 3_600_000), now)).toBe('asked 6 days ago');
  });

  it('AnOldAndAFreshRequestAreBothListedWithTheirAge_TheOldOneCanBeApproved', async () => {
    const old = coreAction('r000000000001', Date.now() - 6 * DAY - 60_000);
    const fresh = coreAction('r000000000002', Date.now() - 3 * 60_000, 'team_archive');
    const { api, calls } = fakeApi({
      'companion.list': () => ({ clients: [], pairings: [], actions: [old, fresh] }),
      'companion.decideAction': () => ({ id: old.id, state: 'running', plan: old.plan, result: null, nextId: null }),
    });
    const w = mount(CompanionAsks, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    const rows = w.findAll('[data-test=companion-action]');
    expect(rows).toHaveLength(2);
    expect(rows[0].get('[data-test=companion-asked]').text()).toBe('asked 6 days ago');
    expect(rows[0].get('[data-test=companion-asked]').attributes('title')).toBe(old.asked);
    expect(rows[1].get('[data-test=companion-asked]').text()).toBe('asked 3 min ago');
    expect(rows[0].text()).toContain(old.plan);
    const approve = rows[0].get('[data-test=companion-approve]');
    expect(approve.attributes('disabled')).toBeUndefined();
    await approve.trigger('click');
    await flushPromises();
    expect(calls.find(c => c.method === 'companion.decideAction')?.params).toEqual({ id: old.id, approve: true });
    w.unmount();
  });
});
