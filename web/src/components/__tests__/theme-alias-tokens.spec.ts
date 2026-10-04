import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { compileString } from 'sass';
import { describe, expect, it } from 'vitest';

/**
 * AN ALIAS TOKEN FOLLOWS THE THEME ONLY WHEN THE DARK BLOCK SAYS IT AGAIN.
 *
 * A custom property whose value is `var(--x)` is resolved WHERE IT IS DECLARED and inherited as
 * that resolved value. An alias declared only in `:root` therefore keeps the LIGHT colour under
 * `body.body--dark`, even though the dark block redeclares `--x` - which is how `--os-muted` read
 * as the light muted ink on a dark page. Every alias of a token the dark block redeclares must be
 * redeclared there too.
 */
const cssDirectory = join(import.meta.dirname, '..', '..', 'css');

const css = compileString(
  readFileSync(join(cssDirectory, 'quasar.variables.scss'), 'utf8') +
    '\n' +
    readFileSync(join(cssDirectory, 'app.scss'), 'utf8'),
).css;

function block(selector: string): Record<string, string> {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = css.match(new RegExp(`(?:^|\\n)${escaped} \\{([^}]*)\\}`))!;

  return Object.fromEntries(
    [...match[1]!.matchAll(/(--[\w-]+):\s*([^;]+);/g)].map((m) => [m[1]!, m[2]!.trim()]),
  );
}

const root = block(':root');
const dark = block('body.body--dark');

describe('theme alias tokens', () => {
  it('every light alias of a token the dark theme changes is restated in the dark block', () => {
    const stale = Object.entries(root)
      .filter(([token, value]) => {
        const reference = value.match(/^var\((--[\w-]+)\)$/)?.[1];
        return reference !== undefined && reference in dark && !(token in dark);
      })
      .map(([token]) => token);

    expect(stale).toEqual([]);
  });

  it('--os-muted is the dark muted ink on a dark page', () => {
    expect(dark['--os-muted']).toBe('var(--os-ink-muted)');
  });
});
