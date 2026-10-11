import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { compileString } from 'sass';
import { describe, expect, it } from 'vitest';

/**
 * THE IDLE TRACK FOLLOWS THE THEME, as the browser resolves it.
 *
 * A custom property whose value is `var(--x)` is resolved WHERE IT IS DECLARED and then inherited
 * as that resolved value. A token declared only in `:root` as `var(--os-rule-strong)` therefore
 * keeps the LIGHT rule colour under `body.body--dark`, even though the dark block redeclares
 * `--os-rule-strong`. Merging the two blocks and then resolving would miss that, so this resolves a
 * token in the block that declares it.
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

/** A token's value on `body.body--dark`, resolved the way the browser does. */
function onDarkBody(token: string): string {
  const declaredOnBody = token in dark;
  const value = declaredOnBody ? dark[token]! : root[token];
  if (!value) throw new Error(`${token} is not set`);

  const reference = value.match(/^var\((--[\w-]+)\)$/);
  if (!reference) return value;

  // Declared on the body: its var() sees the dark block. Declared only in :root: it was resolved there.
  return declaredOnBody ? onDarkBody(reference[1]!) : onRoot(reference[1]!);
}

function onRoot(token: string): string {
  const value = root[token];
  if (!value) throw new Error(`${token} is not set`);

  const reference = value.match(/^var\((--[\w-]+)\)$/);

  return reference ? onRoot(reference[1]!) : value;
}

describe('the Activity tile idle track', () => {
  it('is the light rule colour in light', () => {
    expect(onRoot('--os-stat-idle')).toBe(onRoot('--os-rule-strong'));
  });

  it('is the dark rule colour in dark, not the light one carried over', () => {
    expect(onDarkBody('--os-stat-idle')).toBe(onDarkBody('--os-rule-strong'));
  });
});
