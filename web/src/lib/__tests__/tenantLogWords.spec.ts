import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { detailWords, didWords } from '../tenantLogWords';

/**
 * EVERY ACTION THE HOST WRITES HAS WORDS. The list is not copied here: it is read from the Host's
 * own source - the `TenantActions` constants, and every `TenantActions.X` the Host code names - so
 * an action added there fails this spec until the Admin Log can say it, rather than reaching a
 * person as raw JSON.
 */
const source = join(import.meta.dirname, '../../../../src');
const declarations = join(source, 'Harness.Contracts/ITenantLog.cs');

function csFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    if (entry.name === 'bin' || entry.name === 'obj') return [];
    const path = join(directory, entry.name);
    if (entry.isDirectory()) return csFiles(path);
    return entry.name.endsWith('.cs') ? [path] : [];
  });
}

/** The `TenantActions` constants, by name. */
function constants(): Map<string, string> {
  const text = readFileSync(declarations, 'utf8');
  const body = text.slice(text.indexOf('public static class TenantActions'));
  const found = new Map<string, string>();
  for (const match of body.matchAll(/public const string (\w+) = "([^"]+)";/g)) found.set(match[1]!, match[2]!);
  return found;
}

/** Every action the Host code writes: each `TenantActions.X` named outside the declarations. */
function writtenActions(): string[] {
  const values = constants();
  const names = new Set<string>();
  for (const file of csFiles(source)) {
    if (file === declarations) continue;
    for (const match of readFileSync(file, 'utf8').matchAll(/\bTenantActions\.(\w+)/g)) names.add(match[1]!);
  }
  return [...names].sort().map((name) => {
    const value = values.get(name);
    if (!value) throw new Error(`TenantActions.${name} is named in the Host but not declared`);
    return value;
  });
}

/** A detail the words cannot have been shaped for: what a row would show if they fell back to JSON. */
const looksLikeJson = (text: string) => /^[[{]|"\s*:/.test(text.trim());

const samples = ['{}', '{"team":"job-tracker","count":3,"names":["a","b"],"nested":{"x":1}}'];

describe('every action the Host writes reads in words', () => {
  const actions = writtenActions();

  it('finds the Host vocabulary in its source', () => {
    expect(actions.length).toBeGreaterThan(100);
    expect(actions).toContain('solution.updated');
    expect(actions).toContain('member.added');
  });

  it.each(actions)('%s has a Did phrase', (action) => {
    const did = didWords({ action, detail: null });
    expect(did, action).not.toBeNull();
    expect(did, action).not.toBe(action);
  });

  it.each(actions)('%s has a Detail sentence, never its JSON', (action) => {
    for (const detail of samples) {
      const words = detailWords({ action, detail });
      expect(words, `${action} ${detail}`).not.toBeNull();
      expect(looksLikeJson(words!), `${action}: ${words}`).toBe(false);
    }
  });
});

describe("a member's rate or billing account row", () => {
  const detail = (rate: [string | null, string | null], account: [string | null, string | null]) =>
    JSON.stringify({ team: 'alpha', rate: { old: rate[0], new: rate[1] }, billingAccount: { old: account[0], new: account[1] } });

  it('names the rate set, from its stored text, and leaves an unchanged account out', () => {
    const acct = '3f2b8c1e-9a4d-4e2f-b6a1-0c5d7e8f9a10';
    expect(detailWords({ action: 'member.billing-changed', detail: detail([null, '62.5'], [acct, acct]) }))
      .toBe('Hourly rate set from nothing to 62.5.');
  });

  it('says a clear is a clear', () => {
    expect(detailWords({ action: 'member.billing-changed', detail: detail(['60', null], ['3f2b8c1e-9a4d-4e2f-b6a1-0c5d7e8f9a10', null]) }))
      .toBe('Hourly rate 60 cleared; billing account 3f2b8c1e-9a4d-4e2f-b6a1-0c5d7e8f9a10 cleared.');
  });
});

describe('a billing account row', () => {
  const id = '3f2b8c1e-9a4d-4e2f-b6a1-0c5d7e8f9a10';
  const detail = (changes: Record<string, [unknown, unknown]>) =>
    JSON.stringify({ account: id, changes: Object.fromEntries(Object.entries(changes).map(([k, [o, n]]) => [k, { old: o, new: n }])) });

  it('names a created account, its kind and its own currency, and says its recurring amount is not an estimate', () => {
    const words = detailWords({
      action: 'billing.account-created',
      subjectName: 'Claude Team',
      detail: detail({ name: [null, 'Claude Team'], vendor: [null, 'Anthropic'], billingType: [null, 'subscription'], billingPeriod: [null, 'monthly'], currency: [null, 'EUR'], recurringAmount: [null, '30'] }),
    });
    expect(words).toBe('Created Claude Team (Anthropic subscription); billed monthly in EUR; recurring amount 30 recorded, not added to any estimate.');
  });

  it('names each changed field as from and to, and a clear as a clear', () => {
    expect(detailWords({ action: 'billing.account-changed', detail: detail({ vendor: ['Anthropic', 'Anthropic PBC'], description: ['Seats', null] }) }))
      .toBe('Vendor set from Anthropic to Anthropic PBC; description Seats cleared.');
  });

  it('never calls an account Spend or Actual Spend', () => {
    const actions = ['billing.account-created', 'billing.account-changed', 'billing.account-deactivated', 'billing.account-reactivated'];
    for (const action of actions) {
      const words = `${didWords({ action, detail: null })} ${detailWords({ action, detail: detail({ name: [null, 'X'] }) })}`;
      expect(words, action).not.toMatch(/spen[dt]/i);
    }
  });
});

describe('an expense row', () => {
  const account = '3f2b8c1e-9a4d-4e2f-b6a1-0c5d7e8f9a10';
  const detail = (extra: Record<string, unknown> = {}) =>
    JSON.stringify({
      expense: 'b7d4e2a1-0c3f-4e5d-9a8b-1c2d3e4f5a6b',
      account,
      supersedes: null,
      status: 'confirmed',
      kind: 'invoice',
      amount: '20.5',
      currency: 'USD',
      servicePeriodStart: '2026-11-01T00:00:00.0000000+00:00',
      servicePeriodEnd: '2026-12-01T00:00:00.0000000+00:00',
      ...extra,
    });

  it('names a recorded expense, its amount in its currency and the account', () => {
    const words = detailWords({ action: 'billing.expense-recorded', subjectName: 'Claude Team', detail: detail() });
    expect(words).toMatch(/^Recorded an invoice of 20\.5 USD on Claude Team; for .+ up to .+\.$/);
  });

  it('says a void is a new row and keeps the row it voids', () => {
    const words = detailWords({ action: 'billing.expense-voided', subjectName: 'Claude Team', detail: detail({ status: 'void', supersedes: 'old-row' }) });
    expect(words).toBe('Voided an invoice of 20.5 USD on Claude Team; the void is a new row; the expense it voids, old-row, is kept.');
  });

  it('names the replaced row', () => {
    const words = detailWords({ action: 'billing.expense-replaced', subjectName: 'Claude Team', detail: detail({ kind: 'subscription-charge', supersedes: 'old-row' }) });
    expect(words).toMatch(/^Replaced expense old-row on Claude Team with a subscription-charge of 20\.5 USD; for /);
  });

  it('never calls an expense Spend or Actual Spend', () => {
    for (const action of ['billing.expense-recorded', 'billing.expense-voided', 'billing.expense-replaced']) {
      const words = `${didWords({ action, detail: null })} ${detailWords({ action, detail: detail({ supersedes: 'x' }) })}`;
      expect(words, action).not.toMatch(/spen[dt]/i);
    }
  });
});

describe('a deleted outcome', () => {
  const detail = (cascade: { id: string; name: string } | null) =>
    JSON.stringify({
      status: 'Merged',
      mergedInto: 'o-2',
      mergedIntoName: 'Faster onboarding',
      cascadedFrom: cascade?.id ?? null,
      cascadedFromName: cascade?.name ?? null,
      workflowsUnlinked: 3,
      backlogItems: 1,
      triggers: 2,
    });

  it('names the deleted outcome from the row, with what it cleared', () => {
    const words = detailWords({ action: 'outcome.deleted', detail: detail(null), subjectName: 'Ship weekly' });
    expect(words).toBe('Deleted Ship weekly; 3 workflows now under No outcome; 1 backlog item lost its outcome; 2 triggers lost their outcome.');
  });

  it('names the outcome it went with when it was deleted along with the one it was merged into', () => {
    const words = detailWords({ action: 'outcome.deleted', detail: detail({ id: 'o-2', name: 'Faster onboarding' }), subjectName: 'Ship weekly' });
    expect(words).toContain('Ship weekly');
    expect(words).toContain('along with Faster onboarding, which it had been merged into');
  });

  it('reads without a name, never as JSON, when the row has no subject name', () => {
    const words = detailWords({ action: 'outcome.deleted', detail: detail(null) });
    expect(words).toMatch(/^Deleted the outcome; /);
    expect(looksLikeJson(words!)).toBe(false);
  });
});

describe("a named secret's row", () => {
  const detail = JSON.stringify({ team: 'Ops', member: 'Azure', plugin: 'azure-plugin', name: 'DEPLOY_KEY' });

  it.each([
    ['member.named-secret-set', 'DEPLOY_KEY set for Azure'],
    ['member.named-secret-replaced', 'DEPLOY_KEY replaced for Azure'],
    ['member.named-secret-removed', 'DEPLOY_KEY removed for Azure'],
  ])('%s names the member and the secret', (action, words) => {
    const sentence = detailWords({ action, detail });
    expect(sentence).toContain(words);
    expect(sentence).toContain('azure-plugin');
  });
});
