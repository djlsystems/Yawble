// The phone-remote fake core (tests/e2e/phone/fake-core.ts) against the real UiRpc's
// deliverable expiry shapes, as the core's expiry contract test reads them over the wire (same key list). The
// faults F1-F4 are fixed in the fake, so every test is a plain `it`. Test-only.
import { describe, expect, it } from 'vitest';
import { createFakeCore } from '../e2e/phone/fake-core';
import { DELIVERABLE_EXTEND, TEAM_DELIVERABLE_EXPIRY } from '../../src/shared/api';

/** bus.deliverables item keys from core (the expiry contract test's DeliverableItemKeys). */
const ItemKeys = ['id', 'title', 'owner', 'state', 'undeclared', 'busy', 'ending', 'startedAt', 'lastAt', 'messages',
  'endedAt', 'expiredAt', 'expiresAt', 'expiryWarning', 'question',
  // The deliverable item's time.
  'time'].sort();

function core(access: 'full' | 'read' = 'full') {
  const c = createFakeCore();
  c.setExpiry(true);
  c.setAccess(access);
  let id = 0;
  const call = (method: string, params?: unknown): { result?: any; error?: { code: string; message: string } } =>
    JSON.parse(c.answer(JSON.stringify({ id: ++id, method, params }))!);
  return { c, call };
}

describe('the fake core serves the real UiRpc deliverable expiry shapes', () => {
  it('bus.deliverables lists an expiring open #12 on question 3 and an expired #9 with endedAt before expiredAt', () => {
    const { call } = core();
    const list = call('bus.deliverables', { teamId: 1, limit: 50 }).result as any[];
    const w12 = list.find((w) => w.id === 12), w9 = list.find((w) => w.id === 9);
    expect(w12.state).toBe('open');
    expect(w12.question).toBe(3);
    expect(Date.parse(w12.expiresAt) - Date.now()).toBeLessThan(24 * 3600_000);
    expect(w9.state).toBe('expired');
    expect(Date.parse(w9.endedAt)).toBeLessThan(Date.parse(w9.expiredAt));
    expect(w9.expiresAt).toBeNull();
    expect(w9.question).toBeNull();
  });

  it('every bus.deliverables item carries the real keys, expiryWarning included', () => {
    const { call } = core();
    for (const w of call('bus.deliverables', { teamId: 1 }).result as any[]) expect(Object.keys(w).sort()).toEqual(ItemKeys);
  });

  it('an expired item\'s ending is the core\'s detail "no activity for 3 days"', () => {
    const { call } = core();
    expect((call('bus.deliverables', { teamId: 1 }).result as any[]).find((w) => w.id === 9).ending).toBe('no activity for 3 days');
  });

  it('bus.deliverableExtend returns the list item with the clock reset to now + deliverableExpiryDays', () => {
    const { call } = core();
    const r = call(DELIVERABLE_EXTEND, { teamId: 1, deliverable: 12 }).result;
    expect(Object.keys(r).sort()).toEqual(ItemKeys);
    expect(Math.abs(Date.parse(r.expiresAt) - (Date.now() + 3 * 86400_000))).toBeLessThan(60_000);
  });

  it('bus.deliverableExtend on the expired #9 is refused with already_ended; a Visitor\'s is refused like any change', () => {
    expect(core().call(DELIVERABLE_EXTEND, { teamId: 1, deliverable: 9 }).error?.code).toBe('already_ended');
    expect(core('read').call(DELIVERABLE_EXTEND, { teamId: 1, deliverable: 12 }).error?.code).toBe('remote_refused');
  });

  it('teams.deliverableExpiry: { teamId } reads 3, { teamId, days } sets 1..365, the rest bad_request; a Visitor reads but cannot set', () => {
    const { call } = core();
    expect(call(TEAM_DELIVERABLE_EXPIRY, { teamId: 1 }).result).toEqual({ days: 3 });
    expect(call(TEAM_DELIVERABLE_EXPIRY, { teamId: 1, days: 1 }).result).toEqual({ days: 1 });
    expect(call(TEAM_DELIVERABLE_EXPIRY, { teamId: 1, days: 365 }).result).toEqual({ days: 365 });
    for (const bad of [0, 366, -1, 2.5, '7', true]) expect(call(TEAM_DELIVERABLE_EXPIRY, { teamId: 1, days: bad }).error?.code).toBe('bad_request');
    expect(call(TEAM_DELIVERABLE_EXPIRY, { teamId: 1 }).result).toEqual({ days: 365 });
    const visitor = core('read');
    expect(visitor.call(TEAM_DELIVERABLE_EXPIRY, { teamId: 1 }).result).toEqual({ days: 3 });
    expect(visitor.call(TEAM_DELIVERABLE_EXPIRY, { teamId: 1, days: 5 }).error?.code).toBe('remote_refused');
  });

  it('teams.setDeliverableExpiry is not served (the real core: unknown_method)', () => {
    const { c, call } = core();
    expect(call('teams.setDeliverableExpiry', { teamId: 1, days: 5 }).error).toBeTruthy();
    expect(c.unknown.has('teams.setDeliverableExpiry')).toBe(true);
  });

  it('teams.list items carry deliverableExpiryDays, and it follows teams.deliverableExpiry', () => {
    const { call } = core();
    expect((call('teams.list').result as any[]).every((t) => t.deliverableExpiryDays === 3)).toBe(true);
    call(TEAM_DELIVERABLE_EXPIRY, { teamId: 1, days: 9 });
    expect((call('teams.list').result as any[]).find((t) => t.id === 1).deliverableExpiryDays).toBe(9);
  });

  // The values behind F1 and F3, not only the keys.
  it('F1: expiryWarning is the core\'s WarningText on the warned #12, null on #10 and #9, and null again after Extend', () => {
    const { call } = core();
    const list = () => call('bus.deliverables', { teamId: 1 }).result as any[];
    const by = (id: number) => list().find((w) => w.id === id);
    expect(by(12).expiryWarning).toBe('#12 will expire tomorrow; it waits on your answer to question 3');
    expect(by(10).expiryWarning).toBeNull();
    expect(by(9).expiryWarning).toBeNull();
    expect(call(DELIVERABLE_EXTEND, { teamId: 1, deliverable: 12 }).result.expiryWarning).toBeNull();
    expect(by(12).expiryWarning).toBeNull();
  });

  it('F3: with deliverableExpiryDays 9, Extend sets expiresAt = now + 9 days, and the list item shows the same expiresAt', () => {
    const { call } = core();
    call(TEAM_DELIVERABLE_EXPIRY, { teamId: 1, days: 9 });
    const r = call(DELIVERABLE_EXTEND, { teamId: 1, deliverable: 12 }).result;
    expect(Math.abs(Date.parse(r.expiresAt) - (Date.now() + 9 * 86400_000))).toBeLessThan(60_000);
    expect((call('bus.deliverables', { teamId: 1 }).result as any[]).find((w) => w.id === 12).expiresAt).toBe(r.expiresAt);
  });
});
