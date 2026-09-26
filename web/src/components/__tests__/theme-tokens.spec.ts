import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * EVERY COLOUR IS A TOKEN, so the theme changes in one file.
 *
 * A literal colour in a component is a colour the dark theme cannot reach: `body--dark` redefines
 * the `--os-*` tokens in `app.scss` and nothing else. Quasar's palette classes (`text-grey-7`,
 * `bg-red-1`) are the same defect spelled as a class - fixed light-theme values that do not invert.
 * This keeps both out.
 */
const src = join(import.meta.dirname, '..', '..');

function files(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);

    if (statSync(path).isDirectory()) return name === '__tests__' || name === 'presentation' ? [] : files(path);

    return /\.(vue|ts)$/.test(name) ? [path] : [];
  });
}

const sources = files(src).map((path) => ({ name: relative(src, path).replace(/\\/g, '/'), text: readFileSync(path, 'utf8') }));

/**
 * STILL CARRYING LITERALS ON ANOTHER BRANCH OF THE SAME ITEM, not exemptions. `lib/kanban.ts`'s hex
 * table moves into `app.scss`, `RepoCard.vue` drops its `var()` fallbacks. The Concierge panel
 * keeps literals on purpose: its terminal and frame use xterm's own dark colours, not the theme.
 * Delete an entry when that changes; the test then holds the file to the rule.
 */
const pending = new Set(['lib/kanban.ts', 'components/ConciergePanel.vue', 'components/RepoCard.vue']);

describe('no literal colours outside the theme', () => {
  for (const { name, text } of sources) {
    if (pending.has(name)) continue;

    it(`${name} has no six-digit hex colour`, () => {
      expect(text.match(/#[0-9a-fA-F]{6}\b/g) ?? []).toEqual([]);
    });
  }
});

describe('no Quasar palette classes that ignore the theme', () => {
  for (const { name, text } of sources.filter((s) => s.name.endsWith('.vue'))) {
    it(`${name} uses no text-grey-* or bg-*-1`, () => {
      expect(text.match(/\b(text-grey-\d+|bg-[a-z]+-1)\b/g) ?? []).toEqual([]);
    });
  }
});

/**
 * THE UTILITY CLASSES A TEMPLATE USES ARE CLASSES THE GLOBAL STYLESHEETS DEFINE: `type.scss`, and
 * `dialog-scale.scss` for the `os-dialog-*` widths. The global twin of
 * `styles-match-templates.spec.ts`: a misspelt `os-text-mute` is a class that matches nothing and
 * renders in full ink with nothing failing. A class may sit anywhere in a selector: the dialog scale
 * is `.q-dialog__inner > .os-dialog-sm`.
 */
describe('every os- utility class a template uses is defined', () => {
  const defined = new Set(
    ['type.scss', 'dialog-scale.scss'].flatMap((file) =>
      [...readFileSync(join(src, 'css', file), 'utf8').matchAll(/^[^\n{}]*?\.(os-[\w-]+)[^\n]*[{,]$/gm)].map((m) => m[1]!),
    ),
  );
  const used = new Set(
    sources.flatMap(({ text }) => [...text.matchAll(/\b(os-(?:text|bg|dialog|body)[\w-]*)\b/g)].map((m) => m[1]!)),
  );

  for (const cls of used) {
    it(`.${cls} is defined`, () => {
      expect(defined.has(cls)).toBe(true);
    });
  }
});
