/**
 * HOW THE APP LOOKS TO ONE PERSON: a theme (Match system, a built-in preset, or one of their own),
 * the text size, the font and the line spacing.
 *
 * The record is the PERSON'S, kept on the server (`/api/me/appearance`) so it follows them to every
 * browser; someone who never chose sees the instance default (`theme.default`). This browser keeps a
 * copy (`readCached`) only so the page opens in the person's theme before the server has answered.
 *
 * THE HOST CHECKS NAMES AND BOUNDS, THIS FILE DRAWS. The three lists below are the Host's
 * (`Appearance.cs`), held to it by `AppearanceTests.The_webs_presets_and_fonts_are_the_hosts`.
 *
 * WHAT IS APPLIED WHERE:
 * - the preset is a class on <body> (`os-theme-<id>`) over Quasar's light or dark mode, read by the
 *   blocks in `app.scss`;
 * - a person's own theme sets its colours, and the tokens that follow from them, inline on <body>,
 *   which wins over every block;
 * - the text size is the root font size on <html>: every size in the app is in rem;
 * - the font and the line spacing are `--os-font` and `--os-line-height` on <body>.
 *
 * The Concierge's terminal is not touched: it keeps its own font, size and colours
 * (`lib/terminalDisplay.ts`).
 */

export const PRESET_IDS = ['light', 'dark', 'high-contrast-light', 'high-contrast-dark', 'soft-dark'] as const;

export const FONT_IDS = ['plex', 'atkinson', 'system'] as const;

export const COLOUR_KEYS = [
  'background',
  'panels',
  'text',
  'accent',
  'running',
  'waiting',
  'blocked',
  'failed',
] as const;

export type PresetId = (typeof PRESET_IDS)[number];
export type FontId = (typeof FONT_IDS)[number];
export type ColourKey = (typeof COLOUR_KEYS)[number];
export type LineSpacing = 'normal' | 'relaxed';
export type Colours = Record<ColourKey, string>;

export const MATCH_SYSTEM = 'auto';
export const CUSTOM_PREFIX = 'custom:';

export const MIN_TEXT_SIZE = 90;
export const MAX_TEXT_SIZE = 160;
export const TEXT_SIZE_STEP = 10;
export const MAX_THEMES = 20;
export const MAX_NAME_LENGTH = 40;

/** One of the person's own themes: a preset with the colours they changed. */
export interface CustomTheme {
  id: string;
  name: string;
  basedOn: PresetId;
  colours?: Partial<Colours>;
}

/** What `/api/me/appearance` keeps. Every field optional: one left out is the default. */
export interface AppearanceRecord {
  selected?: string;
  textSize?: number;
  font?: FontId;
  lineSpacing?: LineSpacing;
  themes?: CustomTheme[];
}

export interface Preset {
  id: PresetId;
  label: string;
  dark: boolean;
  /** The preset's own values for the colours a person's theme sets: the swatch in the dialog, and
   *  where a new theme starts. The same values `app.scss` paints (`appearance-presets.spec.ts`). */
  colours: Colours;
}

export const PRESETS: readonly Preset[] = [
  {
    id: 'light',
    label: 'Light',
    dark: false,
    colours: {
      background: '#faf8f5', panels: '#ffffff', text: '#20211c', accent: '#e83b00',
      running: '#2e7d4f', waiting: '#5f7385', blocked: '#b26a00', failed: '#c62828',
    },
  },
  {
    id: 'dark',
    label: 'Dark',
    dark: true,
    colours: {
      background: '#14110f', panels: '#1b1613', text: '#ece7e3', accent: '#e83b00',
      running: '#5cb884', waiting: '#8fa1b3', blocked: '#e0a83a', failed: '#ec5a7f',
    },
  },
  {
    id: 'high-contrast-light',
    label: 'High contrast light',
    dark: false,
    colours: {
      background: '#ffffff', panels: '#ffffff', text: '#000000', accent: '#b32d00',
      running: '#0b6b2f', waiting: '#3c4f60', blocked: '#7a4c00', failed: '#a1102e',
    },
  },
  {
    id: 'high-contrast-dark',
    label: 'High contrast dark',
    dark: true,
    colours: {
      background: '#000000', panels: '#0a0a0a', text: '#ffffff', accent: '#ff7a45',
      running: '#6fe39b', waiting: '#b9c7d4', blocked: '#ffc24d', failed: '#ff7a9a',
    },
  },
  {
    id: 'soft-dark',
    label: 'Soft dark',
    dark: true,
    colours: {
      background: '#22201d', panels: '#2a2724', text: '#e6dfd6', accent: '#f2703c',
      running: '#7cbf8e', waiting: '#98a6b3', blocked: '#d9ad5c', failed: '#e07a8f',
    },
  },
];

export const FONTS: readonly { id: FontId; label: string; stack: string | null }[] = [
  // null: the app's own face, `$typography-font-family`, which `app.scss` falls back to.
  { id: 'plex', label: 'IBM Plex Sans (the app’s own)', stack: null },
  {
    id: 'atkinson',
    label: 'Atkinson Hyperlegible (made for low vision)',
    stack: "'Atkinson Hyperlegible', ui-sans-serif, system-ui, sans-serif",
  },
  { id: 'system', label: 'This computer’s own font', stack: "system-ui, -apple-system, 'Segoe UI', sans-serif" },
];

export const COLOUR_LABELS: Record<ColourKey, string> = {
  background: 'Background',
  panels: 'Panels',
  text: 'Text',
  accent: 'Accent',
  running: 'Running',
  waiting: 'Waiting',
  blocked: 'Blocked',
  failed: 'Failed',
};

export const LINE_HEIGHTS: Record<LineSpacing, string> = { normal: '1.5', relaxed: '1.75' };

export function preset(id: string): Preset | undefined {
  return PRESETS.find((p) => p.id === id);
}

/** What is on screen, decided from the record, the instance default and the computer's setting. */
export interface Resolved {
  /** What the person chose, or the instance default: auto, a preset id, or custom:<id>. */
  selected: string;
  preset: PresetId;
  dark: boolean;
  custom: CustomTheme | null;
  textSize: number;
  font: FontId;
  lineSpacing: LineSpacing;
}

/**
 * Turns a record into what to paint. Nothing here trusts its input: the record may be an old copy
 * from this browser, so a name that is no longer known falls back rather than throwing.
 */
export function resolve(record: AppearanceRecord | null, instanceDefault: string, systemDark: boolean): Resolved {
  const themes = record?.themes ?? [];
  const defaulted = instanceDefault === MATCH_SYSTEM || preset(instanceDefault) ? instanceDefault : MATCH_SYSTEM;
  let selected = record?.selected ?? defaulted;

  let custom: CustomTheme | null = null;
  if (selected.startsWith(CUSTOM_PREFIX)) {
    custom = themes.find((t) => CUSTOM_PREFIX + t.id === selected) ?? null;
    if (!custom) selected = defaulted;
  } else if (selected !== MATCH_SYSTEM && !preset(selected)) {
    selected = defaulted;
  }

  const presetId: PresetId = custom
    ? custom.basedOn
    : selected === MATCH_SYSTEM
      ? systemDark ? 'dark' : 'light'
      : (selected as PresetId);

  const size = record?.textSize ?? 100;

  return {
    selected,
    preset: presetId,
    dark: preset(presetId)?.dark ?? false,
    custom,
    textSize: Math.min(MAX_TEXT_SIZE, Math.max(MIN_TEXT_SIZE, size)),
    font: FONT_IDS.includes(record?.font as FontId) ? (record!.font as FontId) : 'plex',
    lineSpacing: record?.lineSpacing === 'relaxed' ? 'relaxed' : 'normal',
  };
}

/** A theme's colours: its preset's, with the ones the person changed. */
export function coloursOf(theme: CustomTheme): Colours {
  return { ...(preset(theme.basedOn) ?? PRESETS[0]!).colours, ...theme.colours };
}

function channels(hex: string): [number, number, number] {
  return [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16)) as [number, number, number];
}

/** `a` with `amount` (0 to 1) of `b` mixed in, as #rrggbb. */
export function mix(a: string, b: string, amount: number): string {
  const [x, y] = [channels(a), channels(b)];
  return (
    '#' +
    x.map((c, i) => Math.round(c + (y[i]! - c) * amount).toString(16).padStart(2, '0')).join('')
  );
}

export function luminance(hex: string): number {
  const [r, g, b] = channels(hex).map((c) => {
    const v = c / 255;
    return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  }) as [number, number, number];
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

export function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number];
  return (hi + 0.05) / (lo + 0.05);
}

/**
 * The tokens a person's own theme sets inline on <body>: each of its colours, and every token that
 * follows from one, so nothing on screen keeps the preset's colour beside the person's. Concrete
 * values, never `var()`: an alias on <html> would resolve to the preset's colour, not this one.
 */
export function customTokens(theme: CustomTheme): Record<string, string> {
  const c = coloursOf(theme);
  const dark = preset(theme.basedOn)?.dark ?? false;
  const rule = mix(c.panels, c.text, 0.14);
  const ruleStrong = mix(c.panels, c.text, 0.24);
  const muted = mix(c.panels, c.text, 0.75);
  const tint = (colour: string) => mix(c.panels, colour, dark ? 0.16 : 0.1);

  return {
    '--q-primary': c.accent,
    '--q-positive': c.running,
    '--q-warning': c.blocked,
    '--q-negative': c.failed,
    ...(dark ? { '--q-dark': c.panels, '--q-dark-page': c.background } : {}),
    '--os-primary': c.accent,
    '--os-primary-hover': mix(c.accent, dark ? '#ffffff' : '#000000', 0.15),
    // A solution's card wears the accent; a plugin's keeps the preset's violet.
    '--os-kind-solution': c.accent,
    '--os-ink': c.text,
    '--os-ink-muted': muted,
    '--os-ink-faint': mix(c.panels, c.text, 0.55),
    '--os-muted': muted,
    '--os-paper': c.background,
    '--os-surface': c.panels,
    '--os-chrome': mix(mix(c.panels, c.background, 0.5), c.text, 0.04),
    '--os-rule': rule,
    '--os-rule-strong': ruleStrong,
    '--os-ok': c.running,
    '--os-warn': c.blocked,
    '--os-warn-ink': c.blocked,
    '--os-error': c.failed,
    // The four states a person's theme chooses, said on the state tokens every chip and pill reads
    // and on the activity lanes; needs you, idle and done keep the preset's.
    '--os-state-running': c.running,
    '--os-state-waiting': c.waiting,
    '--os-state-blocked': c.blocked,
    '--os-state-failed': c.failed,
    '--os-stat-running': c.running,
    '--os-stat-waiting': c.waiting,
    '--os-stat-blocked': c.blocked,
    '--os-stat-failed': c.failed,
    '--os-stat-idle': ruleStrong,
    '--os-tint-ok': tint(c.running),
    '--os-tint-warn': tint(c.blocked),
    '--os-tint-error': tint(c.failed),
  };
}

/**
 * The sentences the Themes dialog shows under a theme's colours: text below 4.5:1 on what it is read
 * against, a state colour below 3:1 on the panels it marks. A warning, never a refusal.
 */
export function contrastWarnings(colours: Colours): string[] {
  const warnings: string[] = [];
  const ratio = (r: number) => `${(Math.floor(r * 10) / 10).toFixed(1)}:1`;

  for (const ground of ['background', 'panels'] as const) {
    const r = contrast(colours.text, colours[ground]);
    if (r < 4.5) {
      warnings.push(`Text on ${COLOUR_LABELS[ground]} is ${ratio(r)}; 4.5:1 is recommended for reading.`);
    }
  }

  for (const state of ['accent', 'running', 'waiting', 'blocked', 'failed'] as const) {
    const r = contrast(colours[state], colours.panels);
    if (r < 3) {
      warnings.push(`${COLOUR_LABELS[state]} on Panels is ${ratio(r)}; 3:1 is recommended for a mark.`);
    }
  }

  return warnings;
}

const THEME_CLASSES = PRESET_IDS.map((id) => `os-theme-${id}`);
let customKeys: string[] = [];

/**
 * Paints a resolved appearance onto the page. Quasar's dark mode is the caller's (App.vue), because
 * importing `quasar` here would pull its client bundle into every node-environment spec.
 */
export function apply(resolved: Resolved, doc: Document = document): void {
  const body = doc.body;
  const root = doc.documentElement;

  body.classList.remove(...THEME_CLASSES);
  body.classList.add(`os-theme-${resolved.preset}`);

  for (const key of customKeys) body.style.removeProperty(key);
  customKeys = [];
  if (resolved.custom) {
    for (const [key, value] of Object.entries(customTokens(resolved.custom))) {
      body.style.setProperty(key, value);
      customKeys.push(key);
    }
  }

  root.style.fontSize = resolved.textSize === 100 ? '' : `${resolved.textSize}%`;

  const stack = FONTS.find((f) => f.id === resolved.font)?.stack;
  if (stack) body.style.setProperty('--os-font', stack);
  else body.style.removeProperty('--os-font');

  body.style.setProperty('--os-line-height', LINE_HEIGHTS[resolved.lineSpacing]);
}

/** A rem in pixels at a text size, for text drawn on a canvas (the charts), which rem cannot reach. */
export function textPx(rem: number, textSize: number): number {
  return Math.round((rem * 16 * textSize) / 100);
}

/** The person's own label for what is selected: a preset's name, a theme's name, or Match system. */
export function selectedLabel(selected: string, themes: readonly CustomTheme[]): string {
  if (selected === MATCH_SYSTEM) return 'Match system';
  if (selected.startsWith(CUSTOM_PREFIX)) {
    return themes.find((t) => CUSTOM_PREFIX + t.id === selected)?.name ?? 'Match system';
  }
  return preset(selected)?.label ?? 'Match system';
}

/** A new theme's id: its name in lowercase letters, digits and hyphens, unique among the person's. */
export function newThemeId(name: string, themes: readonly CustomTheme[]): string {
  const base =
    name
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, '-')
      .replace(/^-+|-+$/g, '')
      .slice(0, 32) || 'theme';
  let id = base;
  for (let n = 2; themes.some((t) => t.id === id); n++) id = `${base}-${n}`;
  return id;
}

const CacheKey = 'harness.appearance';
const OldThemeKey = 'harness.theme';

export interface Cached {
  record: AppearanceRecord | null;
  instanceDefault: string;
}

/** This browser's copy of the last answer, for the first paint. Never trusted beyond that. */
export function readCached(): Cached {
  try {
    const parsed = JSON.parse(localStorage.getItem(CacheKey) ?? 'null') as Cached | null;
    if (parsed && typeof parsed.instanceDefault === 'string') return parsed;
  } catch {
    // Storage blocked or edited by hand: the server's answer replaces it in a moment.
  }
  return { record: null, instanceDefault: MATCH_SYSTEM };
}

export function writeCached(cached: Cached): void {
  try {
    localStorage.setItem(CacheKey, JSON.stringify(cached));
  } catch {
    // Still applies for this session.
  }
}

/**
 * The light or dark choice this browser kept before appearances were the person's, once: a person
 * who chose Dark here and has no record yet keeps Dark. Removed when read.
 */
export function takeOldTheme(): PresetId | null {
  try {
    const old = localStorage.getItem(OldThemeKey);
    localStorage.removeItem(OldThemeKey);
    return old === 'light' || old === 'dark' ? old : null;
  } catch {
    return null;
  }
}
