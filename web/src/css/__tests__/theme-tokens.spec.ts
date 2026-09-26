import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { compileString } from 'sass';
import { describe, expect, it } from 'vitest';
import { KanbanColours } from '../../lib/kanban';

/**
 * THE TOKENS, READ FROM THE COMPILED STYLESHEET rather than from the source text.
 *
 * Compiling is the point: a token whose value is `#{$primary}` is only the measured colour once
 * Sass has run, and a stylesheet that does not compile is a console with no styles at all - both
 * are things a grep over `app.scss` would pass. The variables file is prepended the way Quasar's
 * build prepends it.
 *
 * Components are built against these NAMES, so the names are the
 * contract this pins: a rename here fails in this file before it fails silently in a component, as an
 * unset custom property that paints nothing.
 */
const cssDirectory = join(import.meta.dirname, '..');

const css = compileString(
  readFileSync(join(cssDirectory, 'quasar.variables.scss'), 'utf8') +
    '\n' +
    readFileSync(join(cssDirectory, 'app.scss'), 'utf8'),
).css;

/** The declarations of the ONE rule whose selector is exactly this - not a nested descendant. */
function block(selector: string): Record<string, string> {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = css.match(new RegExp(`(?:^|\\n)${escaped} \\{([^}]*)\\}`));

  if (!match) throw new Error(`no rule for ${selector}`);

  return Object.fromEntries(
    [...match[1]!.matchAll(/(--[\w-]+|[a-z-]+):\s*([^;]+);/g)].map((m) => [m[1]!, m[2]!.trim()]),
  );
}

const light = block(':root');
const dark = block('body.body--dark');

describe('the theme tokens', () => {
  it('carries the measured brand values in light', () => {
    expect(light).toMatchObject({
      '--os-primary': '#e83b00',
      '--os-primary-hover': '#ff5a1f',
      '--os-ink': '#20211c',
      '--os-paper': '#faf8f5',
      '--os-surface': '#ffffff',
      '--os-rule': '#e4dfd8',
    });
  });

  it('carries the measured brand values in dark', () => {
    expect(dark).toMatchObject({
      '--os-ink': '#ece7e3',
      '--os-paper': '#14110f',
      '--os-surface': '#1b1613',
      '--os-rule': '#2a231e',
    });
  });

  /** The names components build against. */
  it('defines every name components reach for', () => {
    for (const name of [
      '--os-primary',
      '--os-primary-hover',
      '--os-ink',
      '--os-ink-muted',
      '--os-ink-faint',
      '--os-paper',
      '--os-surface',
      '--os-chrome',
      '--os-rule',
      '--os-rule-strong',
      '--os-mono',
      '--os-muted',
      '--os-ok',
      '--os-warn',
      '--os-tint-ok',
      '--os-tint-warn',
      '--os-tint-error',
      '--os-tint-info',
    ]) {
      expect(light[name], name).toBeTruthy();
    }
  });

  /**
   * A token the dark block forgets is a light value on a dark page - pale text on pale ground in
   * one place, and nothing fails. Every per-theme token is overridden, by name.
   */
  it('overrides every per-theme token in dark', () => {
    for (const name of [
      '--os-ink',
      '--os-ink-muted',
      '--os-ink-faint',
      '--os-paper',
      '--os-surface',
      '--os-chrome',
      '--os-rule',
      '--os-rule-strong',
      '--os-ok',
      '--os-warn',
      '--os-tint-ok',
      '--os-tint-warn',
      '--os-tint-error',
      '--os-tint-info',
    ]) {
      expect(dark[name], name).toBeTruthy();
      expect(dark[name], name).not.toBe(light[name]);
    }
  });

  it('sets the dialog scale once, for both themes', () => {
    expect(light).toMatchObject({
      '--os-dialog-sm': '26rem',
      '--os-dialog-md': '34rem',
      '--os-dialog-lg': '46rem',
      '--os-dialog-xl': '72rem',
    });
  });

  it('sets the Quasar semantic colours to the derived values', () => {
    const variables = readFileSync(join(cssDirectory, 'quasar.variables.scss'), 'utf8');

    expect(variables).toMatch(/^\$primary\s*:\s*#e83b00;/m);
    expect(variables).toMatch(/^\$negative\s*:\s*#b8123f;/m);
    expect(variables).toMatch(/^\$positive\s*:\s*#2e8b57;/m);
    expect(variables).toMatch(/^\$warning\s*:\s*#c98a12;/m);
    expect(variables).toMatch(/^\$info\s*:\s*#3b6ea5;/m);
  });
});

/**
 * THE KANBAN PALETTE, WORD TO COLOUR. `lib/kanban.ts` keeps the words and `app.scss` owns what they
 * look like, so this is the join: a word the lib can emit with no class behind it is a colourless
 * card and nothing failing.
 */
describe('the kanban palette', () => {
  for (const word of KanbanColours) {
    it(`paints ${word} from the tokens`, () => {
      expect(block(`.os-kanban-${word}`)).toEqual({
        '--os-kanban-accent': `var(--os-kanban-${word})`,
        '--os-kanban-tint': `var(--os-kanban-${word}-tint)`,
      });
      expect(light[`--os-kanban-${word}`]).toMatch(/^#[0-9a-f]{6}$/);
      expect(light[`--os-kanban-${word}-tint`]).toMatch(/^#[0-9a-f]{6}$/);
      expect(dark[`--os-kanban-${word}-tint`]).toMatch(/^#[0-9a-f]{6}$/);
    });
  }
});
