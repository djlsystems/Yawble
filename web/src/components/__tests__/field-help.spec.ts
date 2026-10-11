import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * A FIELD'S HELP IS BEHIND ITS INFO ICON, never printed under it: no `q-input` or `q-select` carries
 * Quasar's `hint`, which prints small text below the field. Its explanation goes in a `FieldHelp`
 * (`#append`), whose tooltip shows it on hover and whose label reads it to a screen reader. What a
 * person must see now - an error, Required, a warning - stays in words on the form.
 */
const src = join(import.meta.dirname, '..', '..');

function files(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return name === '__tests__' ? [] : files(path);
    return name.endsWith('.vue') ? [path] : [];
  });
}

/** The start tag of every q-input and q-select, attributes and all. */
function fieldTags(text: string): string[] {
  return [...text.matchAll(/<q-(?:input|select)\b[^>]*?(?:\/>|>)/gs)].map((m) => m[0]);
}

describe('a field\'s help', () => {
  for (const path of files(src)) {
    const name = relative(src, path).replace(/\\/g, '/');
    it(`${name} prints no hint under a field`, () => {
      expect(fieldTags(readFileSync(path, 'utf8')).filter((tag) => /\s:?hint=/.test(tag))).toEqual([]);
    });
  }

  it('finds a hint where one is written', () => {
    expect(fieldTags('<q-input v-model="x"\n  hint="Some help"\n/>').filter((tag) => /\s:?hint=/.test(tag))).toHaveLength(1);
  });
});
