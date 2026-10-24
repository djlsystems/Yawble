import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { compileString } from 'sass';
import { describe, expect, it } from 'vitest';
import { PRESETS, contrast, type PresetId } from '../../lib/appearance';
import { States } from '../../lib/states';

/**
 * EVERY BUILT-IN THEME IS READABLE, MEASURED FROM THE COMPILED STYLESHEET. Each preset is the
 * `:root` block, then Dark's block for a dark preset, then its own `os-theme-<id>` block, resolved
 * through every `var()` the way the browser would. Text holds 4.5:1 on every ground it is read
 * against (7:1, WCAG AAA, for the high-contrast pair); muted text 4.5:1 on panels; every state mark
 * 3:1 on `--os-chrome`, where the activity tile draws it; the solution and plugin kind colours
 * 3:1 on `--os-surface`, where a Marketplace card draws them. The swatches `lib/appearance.ts` shows,
 * and starts a person's theme from, are what the stylesheet paints.
 */
const cssDirectory = join(import.meta.dirname, '..');

const css = compileString(
  readFileSync(join(cssDirectory, 'quasar.variables.scss'), 'utf8') +
    '\n' +
    readFileSync(join(cssDirectory, 'app.scss'), 'utf8'),
).css;

function block(selector: string): Record<string, string> {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = css.match(new RegExp(`(?:^|\\n)${escaped} \\{([^}]*)\\}`));
  if (!match) throw new Error(`no rule for ${selector}`);
  return Object.fromEntries(
    [...match[1]!.matchAll(/(--[\w-]+):\s*([^;]+);/g)].map((m) => [m[1]!, m[2]!.trim()]),
  );
}

const root = block(':root');
const dark = block('body.body--dark');

const chains: Record<PresetId, Record<string, string>> = {
  light: root,
  dark: { ...root, ...dark },
  'high-contrast-light': { ...root, ...block('body.os-theme-high-contrast-light') },
  'high-contrast-dark': { ...root, ...dark, ...block('body.body--dark.os-theme-high-contrast-dark') },
  'soft-dark': { ...root, ...dark, ...block('body.body--dark.os-theme-soft-dark') },
};

function resolve(token: string, theme: Record<string, string>): string {
  const value = theme[token];
  if (!value) throw new Error(`${token} is not set`);
  const reference = value.match(/^var\((--[\w-]+)\)$/);
  return reference ? resolve(reference[1]!, theme) : value;
}

describe('every built-in theme', () => {
  for (const preset of PRESETS) {
    const theme = chains[preset.id];
    const t = (token: string) => resolve(token, theme);
    const textFloor = preset.id.startsWith('high-contrast') ? 7 : 4.5;

    it(`${preset.label}: the swatches are what the stylesheet paints`, () => {
      expect({
        background: t('--os-paper'),
        panels: t('--os-surface'),
        text: t('--os-ink'),
        accent: t('--os-primary'),
        running: t('--os-stat-running'),
        waiting: t('--os-stat-waiting'),
        blocked: t('--os-stat-blocked'),
        failed: t('--os-stat-failed'),
      }).toEqual(preset.colours);
    });

    it(`${preset.label}: text holds ${textFloor}:1 on every ground and muted text 4.5:1`, () => {
      for (const ground of ['--os-paper', '--os-surface', '--os-chrome']) {
        expect(contrast(t('--os-ink'), t(ground)), `text on ${ground}`).toBeGreaterThanOrEqual(textFloor);
      }
      expect(contrast(t('--os-ink-muted'), t('--os-surface')), 'muted text on panels').toBeGreaterThanOrEqual(4.5);
    });

    it(`${preset.label}: every state token holds 3:1 on --os-chrome and on --os-surface`, () => {
      for (const state of States) {
        for (const ground of ['--os-chrome', '--os-surface']) {
          expect(
            contrast(t(`--os-state-${state}`), t(ground)),
            `${state} on ${ground}`,
          ).toBeGreaterThanOrEqual(3);
        }
      }
    });

    it(`${preset.label}: the solution and plugin kind colours each hold 3:1 on --os-surface and differ`, () => {
      for (const kind of ['solution', 'plugin']) {
        expect(t(`--os-kind-${kind}`), kind).toMatch(/^#[0-9a-f]{6}$/);
        expect(contrast(t(`--os-kind-${kind}`), t('--os-surface')), `${kind} on --os-surface`).toBeGreaterThanOrEqual(3);
      }
      expect(t('--os-kind-solution'), 'a solution wears the accent').toBe(preset.colours.accent);
      expect(t('--os-kind-plugin')).not.toBe(t('--os-kind-solution'));
    });

    it(`${preset.label}: every state mark holds 3:1 on the tile`, () => {
      for (const state of ['running', 'waiting', 'held', 'blocked', 'failed']) {
        expect(
          contrast(t(`--os-stat-${state}`), t('--os-chrome')),
          `${state} on --os-chrome`,
        ).toBeGreaterThanOrEqual(3);
      }
    });
  }
});
