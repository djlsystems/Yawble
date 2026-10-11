// @vitest-environment happy-dom
import { beforeEach, describe, expect, it } from 'vitest';
import {
  PRESETS,
  apply,
  contrastWarnings,
  customTokens,
  newThemeId,
  readCached,
  resolve,
  selectedLabel,
  takeOldTheme,
  textPx,
  writeCached,
  type CustomTheme,
} from '../appearance';

const evening: CustomTheme = {
  id: 'evening',
  name: 'Evening',
  basedOn: 'soft-dark',
  colours: { text: '#ffffff', accent: '#00a0ff' },
};

describe('what is on screen', () => {
  it('is the instance default for a person who never chose', () => {
    expect(resolve(null, 'high-contrast-dark', false)).toMatchObject({
      selected: 'high-contrast-dark',
      preset: 'high-contrast-dark',
      dark: true,
      custom: null,
      textSize: 100,
      font: 'plex',
      lineSpacing: 'normal',
    });
  });

  it('follows the computer for Match system', () => {
    expect(resolve({ selected: 'auto' }, 'light', true).preset).toBe('dark');
    expect(resolve({ selected: 'auto' }, 'light', false).preset).toBe('light');
  });

  it('paints a person\'s own theme over the preset it started from', () => {
    const resolved = resolve({ selected: 'custom:evening', themes: [evening] }, 'auto', false);
    expect(resolved).toMatchObject({ preset: 'soft-dark', dark: true, custom: evening });
  });

  it('falls back to the default for a name it no longer knows, never throwing', () => {
    expect(resolve({ selected: 'custom:gone', themes: [] }, 'dark', false).selected).toBe('dark');
    expect(resolve({ selected: 'neon' }, 'purple', false).selected).toBe('auto');
    expect(resolve({ textSize: 400, font: 'comic' as never }, 'auto', false)).toMatchObject({
      textSize: 160,
      font: 'plex',
    });
  });
});

describe("a person's own theme", () => {
  it('sets every token that follows from its colours, as concrete colours', () => {
    const tokens = customTokens(evening);

    expect(tokens['--os-ink']).toBe('#ffffff');
    expect(tokens['--os-primary']).toBe('#00a0ff');
    expect(tokens['--q-primary']).toBe('#00a0ff');
    expect(tokens['--os-paper']).toBe(PRESETS.find((p) => p.id === 'soft-dark')!.colours.background);
    for (const value of Object.values(tokens)) expect(value).toMatch(/^#[0-9a-f]{6}$/);
  });

  it('warns, in a sentence, about text and marks that will be hard to read', () => {
    const colours = { ...PRESETS[0]!.colours, text: '#bbbbbb', waiting: '#eeeeee' };

    expect(contrastWarnings(colours)).toEqual([
      'Text on Background is 1.8:1; 4.5:1 is recommended for reading.',
      'Text on Panels is 1.9:1; 4.5:1 is recommended for reading.',
      'Waiting on Panels is 1.1:1; 3:1 is recommended for a mark.',
    ]);
    expect(contrastWarnings(PRESETS[0]!.colours)).toEqual([]);
  });

  it('gets an id from its name, unique among the person\'s themes', () => {
    expect(newThemeId('My Big Screen!', [])).toBe('my-big-screen');
    expect(newThemeId('Evening', [evening])).toBe('evening-2');
    expect(newThemeId('!!!', [])).toBe('theme');
  });

  it('is named by its own name, a preset by its label', () => {
    expect(selectedLabel('custom:evening', [evening])).toBe('Evening');
    expect(selectedLabel('soft-dark', [])).toBe('Soft dark');
    expect(selectedLabel('auto', [])).toBe('Match system');
  });
});

describe('painting it', () => {
  beforeEach(() => {
    document.body.className = '';
    document.body.removeAttribute('style');
    document.documentElement.removeAttribute('style');
  });

  it('puts the preset on body, the size on the root and the face and spacing on body', () => {
    apply(resolve({ selected: 'high-contrast-light', textSize: 130, font: 'atkinson', lineSpacing: 'relaxed' }, 'auto', false));

    expect(document.body.classList.contains('os-theme-high-contrast-light')).toBe(true);
    expect(document.documentElement.style.fontSize).toBe('130%');
    expect(document.body.style.getPropertyValue('--os-font')).toContain('Atkinson Hyperlegible');
    expect(document.body.style.getPropertyValue('--os-line-height')).toBe('1.75');
  });

  it('takes a person\'s colours back off when another theme is chosen', () => {
    apply(resolve({ selected: 'custom:evening', themes: [evening] }, 'auto', false));
    expect(document.body.style.getPropertyValue('--os-ink')).toBe('#ffffff');

    apply(resolve({ selected: 'light' }, 'auto', false));
    expect(document.body.style.getPropertyValue('--os-ink')).toBe('');
    expect(document.body.classList.contains('os-theme-soft-dark')).toBe(false);
    expect(document.body.classList.contains('os-theme-light')).toBe(true);
    expect(document.documentElement.style.fontSize).toBe('');
  });
});

describe("this browser's copy", () => {
  beforeEach(() => localStorage.clear());

  it('reads back what was written, and the default when there is none', () => {
    expect(readCached()).toEqual({ record: null, instanceDefault: 'auto' });
    writeCached({ record: { selected: 'dark' }, instanceDefault: 'light' });
    expect(readCached()).toEqual({ record: { selected: 'dark' }, instanceDefault: 'light' });
  });

  it('hands on the light or dark this browser kept before, once', () => {
    localStorage.setItem('harness.theme', 'dark');
    expect(takeOldTheme()).toBe('dark');
    expect(takeOldTheme()).toBeNull();
  });
});

describe('text drawn on a canvas', () => {
  it('is a rem at the person\'s text size, in whole pixels', () => {
    expect(textPx(0.75, 100)).toBe(12);
    expect(textPx(0.75, 150)).toBe(18);
    expect(textPx(0.6875, 130)).toBe(14);
  });
});
