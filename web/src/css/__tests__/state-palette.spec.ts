import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { compileString } from 'sass';
import { describe, expect, it } from 'vitest';
import { PRESETS, contrast, type PresetId } from '../../lib/appearance';
import { KanbanColourState, KanbanColours } from '../../lib/kanban';
import { States } from '../../lib/states';

/**
 * ONE PALETTE FOR STATE. Seven states, one `--os-state-*` token each, and every surface that draws a
 * state draws it through that token: the Kanban colour classes, the activity lanes, the Teams table's
 * and tab's chips, the member card's pill, the KPI tile's tone and the Monitor's legend and rings.
 * Measured from the compiled stylesheet (and the components' own styles), resolved per built-in
 * preset as `appearance-presets.spec.ts` does, which also holds each token to 3:1.
 */
const cssDirectory = join(import.meta.dirname, '..');
const components = join(import.meta.dirname, '..', '..', 'components');

const css = compileString(
  readFileSync(join(cssDirectory, 'quasar.variables.scss'), 'utf8') +
    '\n' +
    readFileSync(join(cssDirectory, 'app.scss'), 'utf8'),
).css;

function blockIn(source: string, selector: string): Record<string, string> {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const match = source.match(new RegExp(`(?:^|\\n)${escaped} \\{([^}]*)\\}`));
  if (!match) throw new Error(`no rule for ${selector}`);
  return Object.fromEntries(
    [...match[1]!.matchAll(/([\w-]+):\s*([^;]+);/g)].map((m) => [m[1]!, m[2]!.trim()]),
  );
}

const block = (selector: string) => blockIn(css, selector);
const root = block(':root');
const dark = block('body.body--dark');
const presetBlocks: Record<PresetId, Record<string, string>> = {
  light: root,
  dark,
  'high-contrast-light': block('body.os-theme-high-contrast-light'),
  'high-contrast-dark': block('body.body--dark.os-theme-high-contrast-dark'),
  'soft-dark': block('body.body--dark.os-theme-soft-dark'),
};
const chains: Record<PresetId, Record<string, string>> = {
  light: root,
  dark: { ...root, ...dark },
  'high-contrast-light': { ...root, ...presetBlocks['high-contrast-light'] },
  'high-contrast-dark': { ...root, ...dark, ...presetBlocks['high-contrast-dark'] },
  'soft-dark': { ...root, ...dark, ...presetBlocks['soft-dark'] },
};

function resolve(value: string, theme: Record<string, string>): string {
  const reference = value.match(/^var\((--[\w-]+)\)$/);
  if (!reference) return value;
  const next = theme[reference[1]!];
  if (!next) throw new Error(`${reference[1]} is not set`);
  return resolve(next, theme);
}

const hex = (h: string) => [1, 3, 5].map((i) => parseInt(h.slice(i, i + 2), 16));
/** `color-mix(in srgb, a p%, b)`, as the browser computes it. */
function mix(a: string, share: number, b: string): string {
  const [x, y] = [hex(a), hex(b)];
  return `#${x.map((v, i) => Math.round(v * share + y[i]! * (1 - share)).toString(16).padStart(2, '0')).join('')}`;
}

/** The Monitor's scoped style: the theme block and the wall block. */
const monitorSource = readFileSync(join(components, 'MonitorView.vue'), 'utf8');
const monitorStyle = monitorSource.slice(monitorSource.indexOf('<style scoped>'));
const monitorTheme = blockIn(monitorStyle, '.monitor');
/** The Monitor's member states and the state token each ring is. */
const MonitorRings: Record<string, string> = {
  running: 'running', waiting: 'waiting', decision: 'needs-you', blocked: 'blocked', failed: 'failed', idle: 'idle',
};

describe('every state is one token in every theme block', () => {
  for (const [id, own] of Object.entries(presetBlocks)) {
    it(`${id} sets all seven, and the activity lanes read them`, () => {
      for (const state of States) expect(own[`--os-state-${state}`], `${id} --os-state-${state}`).toBeTruthy();
      for (const lane of ['running', 'waiting', 'blocked', 'failed']) {
        expect(chains[id as PresetId][`--os-stat-${lane}`], `${id} --os-stat-${lane}`).toBe(`var(--os-state-${lane})`);
      }
    });
  }
});

describe('the Kanban colour classes are state tokens', () => {
  for (const word of KanbanColours) {
    it(`${word} is ${KanbanColourState[word]}, and holds 3:1 in every preset`, () => {
      const accent = block(`.os-kanban-${word}`)['--os-kanban-accent']!;
      expect(accent).toBe(`var(--os-state-${KanbanColourState[word]})`);
      for (const preset of PRESETS) {
        const theme = chains[preset.id];
        const colour = resolve(accent, theme);
        expect(contrast(colour, resolve('var(--os-chrome)', theme)), `${word} on chrome, ${preset.id}`).toBeGreaterThanOrEqual(3);
        expect(contrast(colour, resolve('var(--os-surface)', theme)), `${word} on surface, ${preset.id}`).toBeGreaterThanOrEqual(3);
      }
    });
  }
});

describe('the chips, the pill and the tile tone draw a state through its token', () => {
  it('the Teams table and the tab chip', () => {
    const chip = (word: string) => block(`.console-tab-status--${word}`).color;
    expect(chip('running')).toBe('var(--os-state-running)');
    expect(chip('queued')).toBe('var(--os-state-waiting)');
    expect(chip('awaiting')).toBe('var(--os-state-waiting)');
    expect(chip('blocked')).toBe('var(--os-state-blocked)');
    expect(chip('failed')).toBe('var(--os-state-failed)');
    expect(chip('idle')).toBe('var(--os-state-idle)');
    expect(chip('completed')).toBe('var(--os-state-done)');
    expect(css).toMatch(/\.console-tab-status--waiting,\s*\.console-tab-status--waiting-for-a-slot \{\s*color: var\(--os-state-waiting\);/);
  });

  it("the member card's pill", () => {
    for (const state of ['running', 'waiting', 'needs-you', 'blocked', 'failed', 'idle']) {
      expect(block(`.os-state-pill--${state}`)['--os-state-pill']).toBe(`var(--os-state-${state})`);
    }
  });

  it("the KPI tile's tone", () => {
    const strip = readFileSync(join(components, 'TeamKpiStrip.vue'), 'utf8');
    const style = strip.slice(strip.indexOf('<style scoped>'));
    for (const [tone, state] of [['live', 'running'], ['warn', 'blocked'], ['error', 'failed'], ['info', 'waiting']]) {
      expect(blockIn(style, `.team-kpi--${tone}`)['border-color']).toBe(`var(--os-state-${state})`);
      expect(blockIn(style, `.workflow-row--${tone}`)['border-left-color']).toBe(`var(--os-state-${state})`);
    }
  });
});

describe("the Monitor keeps its dark wall palette in both views", () => {
  it('rings each member state in its state token lifted toward white, with no second palette for full screen', () => {
    for (const [ring, state] of Object.entries(MonitorRings)) {
      expect(monitorTheme[`--os-monitor-state-${ring}`]).toBe(`color-mix(in srgb, var(--os-state-${state}) 60%, white)`);
      expect(monitorSource, `the legend names ${ring}`).toContain(`colour: 'var(--os-monitor-state-${ring})'`);
    }
    expect(monitorStyle).not.toContain('.monitor--wall');
    expect(monitorSource).not.toContain("'monitor--wall'");
  });

  it('holds every ring 3:1 on the wall ground, from every preset', () => {
    for (const preset of PRESETS) {
      const theme = chains[preset.id];
      const ground = resolve('var(--os-monitor-wall-ground)', theme);
      for (const state of Object.values(MonitorRings)) {
        const ring = mix(resolve(`var(--os-state-${state})`, theme), 0.6, '#ffffff');
        expect(contrast(ring, ground), `${state} on the wall, ${preset.id}`).toBeGreaterThanOrEqual(3);
      }
    }
  });

  it('paints the ground, the ink and every event hue from the wall palette and never the theme, each hue 3:1 on the wall', () => {
    const names = Object.keys(monitorTheme).filter((name) => name.startsWith('--os-monitor-') && !name.includes('state-'));
    expect(names.length).toBeGreaterThanOrEqual(22);
    for (const name of names) {
      expect(monitorTheme[name], name).toBe(`var(${name.replace('--os-monitor-', '--os-monitor-wall-')})`);
    }
    const theme = chains[PRESETS[0]!.id];
    const ground = resolve('var(--os-monitor-wall-ground)', theme);
    for (const name of names) {
      if (/-(ground|panel|rule)$/.test(name)) continue;
      const hue = resolve(`var(${name.replace('--os-monitor-', '--os-monitor-wall-')})`, theme);
      expect(contrast(hue, ground), `${name} on the wall`).toBeGreaterThanOrEqual(3);
    }
  });
});
