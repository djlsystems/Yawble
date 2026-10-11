import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * ONE DIALOG SCALE. Every dialog card is `os-dialog-sm`, `-md`, `-lg` or `-xl`, and
 * `css/dialog-scale.scss` alone gives it a width and the phone cap (`calc(100vw - 32px)`).
 *
 * What this replaces: 45 dialogs with their own numbers, inline `min-width: 760px` and a Skills
 * dialog with a 960px FLOOR - on a 390px phone that is a card two and a half screens wide. A width
 * set in a component's own style would also outrank the scale (a scoped selector carries the
 * component's attribute), so the check is on both halves: the class is there, and nothing local
 * sets a width on it.
 */
const componentsDirectory = join(import.meta.dirname, '..');

/**
 * EVERY `.vue` UNDER `components/`, subfolders included. The Documents explorer's own dialogs (the
 * clash question, Move to…) live in `components/documents/`; a scan of the top level only would
 * leave them unchecked while reading as green.
 */
function vueFiles(directory: string, prefix = ''): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    if (entry.isDirectory()) return entry.name === '__tests__' ? [] : vueFiles(join(directory, entry.name), `${prefix}${entry.name}/`);
    return entry.name.endsWith('.vue') ? [`${prefix}${entry.name}`] : [];
  });
}

const components = vueFiles(componentsDirectory)
  .map((name) => ({ name, source: readFileSync(join(componentsDirectory, name), 'utf8') }));

const withoutComments = (source: string) =>
  source.replace(/\/\*[\s\S]*?\*\//g, ' ').replace(/<!--[\s\S]*?-->/g, ' ');

/** The root `<q-card>` of each `<q-dialog>`: the first card tag after the dialog opens. */
function dialogCards(template: string): string[] {
  return [...template.matchAll(/<q-dialog\b[\s\S]*?(<q-card\b[^>]*>)/g)].map((m) => m[1]!);
}

const Scale = /\bos-dialog-(sm|md|lg|xl)\b/g;

describe('the dialog scale', () => {
  const withDialogs = components.filter(({ source }) => source.includes('<q-dialog'));

  it('reaches into components/documents/', () => {
    const documents = withDialogs.filter(({ name }) => name.startsWith('documents/')).map(({ name }) => name);

    expect(documents).toEqual(expect.arrayContaining(['documents/DocumentsClashDialog.vue', 'documents/DocumentsMoveDialog.vue']));
  });

  it('finds the dialogs it is meant to check', () => {
    // A regex that matches nothing passes every case below.
    expect(withDialogs.flatMap(({ source }) => dialogCards(withoutComments(source))).length)
      .toBeGreaterThan(40);
  });

  for (const { name, source } of withDialogs) {
    const template = withoutComments(source.slice(0, source.indexOf('<style')));
    const style = withoutComments(source.slice(source.indexOf('<style')));

    it(`${name}: every dialog card carries exactly one scale class`, () => {
      const cards = dialogCards(template);

      expect(cards.length).toBeGreaterThan(0);
      for (const card of cards) {
        expect([...card.matchAll(Scale)].length, card).toBe(1);
      }
    });

    it(`${name}: no dialog card sets its own width`, () => {
      for (const card of dialogCards(template)) {
        expect(card, card).not.toMatch(/style="[^"]*width/);

        for (const cls of card.match(/class="([^"]*)"/)?.[1]?.split(/\s+/) ?? []) {
          if (cls.startsWith('os-dialog-')) continue;

          const rules = [...style.matchAll(new RegExp(`\\.${cls}\\s*\\{([^}]*)\\}`, 'g'))];
          for (const rule of rules) {
            expect(rule[1], `.${cls} in ${name}`).not.toMatch(/(^|[\s;])(min-|max-)?width\s*:/);
          }
        }
      }
    });
  }

  it('no component in the tree keeps an inline min-width', () => {
    const offenders = components
      .filter(({ source }) => /style="[^"]*min-width/.test(withoutComments(source)))
      .map(({ name }) => name);

    expect(offenders).toEqual([]);
  });
});
