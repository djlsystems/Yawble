// Card 390 (B085 verify), gap tests: the phone-remote fake core (tests/e2e/phone/fake-core.ts) against the real UiRpc's
// B085 shapes, as core's Card390ExpiryContractTests reads them over the wire (same key list). What matches is a plain
// `it`; a known mismatch is `it.fails` (it turns red once the fake is fixed: then make it a plain `it`). Test-only.
import { describe, expect, it } from 'vitest';
import { createFakeCore } from '../e2e/phone/fake-core';
import { WORKFLOW_EXTEND, TEAM_WORKFLOW_EXPIRY } from '../../src/shared/api';

/** bus.workflows item keys from core (Card390ExpiryContractTests.WorkflowItemKeys). */
const ItemKeys = ['id', 'title', 'owner', 'state', 'undeclared', 'busy', 'ending', 'startedAt', 'lastAt', 'messages',
  'endedAt', 'expiredAt', 'expiresAt', 'expiryWarning', 'question'].sort();

function core(access: 'full' | 'read' = 'full') {
  const c = createFakeCore();
  c.setExpiry(true);
  c.setAccess(access);
  let id = 0;
  const call = (method: string, params?: unknown): { result?: any; error?: { code: string; message: string } } =>
    JSON.parse(c.answer(JSON.stringify({ id: ++id, method, params }))!);
  return { c, call };
}

describe('card 390: the fake core serves the real UiRpc B085 shapes', () => {
  it('bus.workflows lists an expiring open #12 on question 3 and an expired #9 with endedAt before expiredAt', () => {
    const { call } = core();
    const list = call('bus.workflows', { teamId: 1, limit: 50 }).result as any[];
    const w12 = list.find((w) => w.id === 12), w9 = list.find((w) => w.id === 9);
    expect(w12.state).toBe('open');
    expect(w12.question).toBe(3);
    expect(Date.parse(w12.expiresAt) - Date.now()).toBeLessThan(24 * 3600_000);
    expect(w9.state).toBe('expired');
    expect(Date.parse(w9.endedAt)).toBeLessThan(Date.parse(w9.expiredAt));
    expect(w9.expiresAt).toBeNull();
    expect(w9.question).toBeNull();
  });

  it.fails('every bus.workflows item carries the real keys, expiryWarning included (fake: no expiryWarning)', () => {
    const { call } = core();
    for (const w of call('bus.workflows', { teamId: 1 }).result as any[]) expect(Object.keys(w).sort()).toEqual(ItemKeys);
  });

  it.fails('an expired item\'s ending is the core\'s detail "no activity for 3 days" (fake: "expired: no activity…", shown "Expired: expired: …")', () => {
    const { call } = core();
    expect((call('bus.workflows', { teamId: 1 }).result as any[]).find((w) => w.id === 9).ending).toBe('no activity for 3 days');
  });

  it.fails('bus.workflowExtend returns the list item with the clock reset to now + workflowExpiryDays (fake: expiresAt null)', () => {
    const { call } = core();
    const r = call(WORKFLOW_EXTEND, { teamId: 1, workflow: 12 }).result;
    expect(Object.keys(r).sort()).toEqual(ItemKeys);
    expect(Math.abs(Date.parse(r.expiresAt) - (Date.now() + 3 * 86400_000))).toBeLessThan(60_000);
  });

  it('bus.workflowExtend on the expired #9 is refused with already_ended; a Visitor\'s is refused like any change', () => {
    expect(core().call(WORKFLOW_EXTEND, { teamId: 1, workflow: 9 }).error?.code).toBe('already_ended');
    expect(core('read').call(WORKFLOW_EXTEND, { teamId: 1, workflow: 12 }).error?.code).toBe('remote_refused');
  });

  it('teams.workflowExpiry: { teamId } reads 3, { teamId, days } sets 1..365, the rest bad_request; a Visitor reads but cannot set', () => {
    const { call } = core();
    expect(call(TEAM_WORKFLOW_EXPIRY, { teamId: 1 }).result).toEqual({ days: 3 });
    expect(call(TEAM_WORKFLOW_EXPIRY, { teamId: 1, days: 1 }).result).toEqual({ days: 1 });
    expect(call(TEAM_WORKFLOW_EXPIRY, { teamId: 1, days: 365 }).result).toEqual({ days: 365 });
    for (const bad of [0, 366, -1, 2.5, '7', true]) expect(call(TEAM_WORKFLOW_EXPIRY, { teamId: 1, days: bad }).error?.code).toBe('bad_request');
    expect(call(TEAM_WORKFLOW_EXPIRY, { teamId: 1 }).result).toEqual({ days: 365 });
    const visitor = core('read');
    expect(visitor.call(TEAM_WORKFLOW_EXPIRY, { teamId: 1 }).result).toEqual({ days: 3 });
    expect(visitor.call(TEAM_WORKFLOW_EXPIRY, { teamId: 1, days: 5 }).error?.code).toBe('remote_refused');
  });

  it('teams.setWorkflowExpiry is not served (the real core: unknown_method)', () => {
    const { c, call } = core();
    expect(call('teams.setWorkflowExpiry', { teamId: 1, days: 5 }).error).toBeTruthy();
    expect(c.unknown.has('teams.setWorkflowExpiry')).toBe(true);
  });

  it.fails('teams.list items carry workflowExpiryDays, and it follows teams.workflowExpiry (fake: absent)', () => {
    const { call } = core();
    expect((call('teams.list').result as any[]).every((t) => t.workflowExpiryDays === 3)).toBe(true);
    call(TEAM_WORKFLOW_EXPIRY, { teamId: 1, days: 9 });
    expect((call('teams.list').result as any[]).find((t) => t.id === 1).workflowExpiryDays).toBe(9);
  });
});
