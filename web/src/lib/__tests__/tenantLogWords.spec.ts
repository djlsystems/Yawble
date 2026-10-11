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
