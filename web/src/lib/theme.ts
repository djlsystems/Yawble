/**
 * Light, dark, or whatever the operating system says - as a per-viewer preference.
 *
 * A DISPLAY preference, like the board size beside it: it describes this browser's view and never
 * leaves `localStorage`. Two people on one instance see whatever each chose. Same wrapper
 * discipline as `lib/boardSize` - storage throws in a private window with cookies blocked, and a
 * colour scheme is not worth a crash.
 */
export type Theme = 'auto' | 'light' | 'dark';

export const Themes: readonly Theme[] = ['auto', 'light', 'dark'];

/** Following the system is the default, so a person who never opens the menu gets what they asked
 *  their OS for rather than what somebody here guessed. */
export const DefaultTheme: Theme = 'auto';

const StorageKey = 'harness.theme';

export function isTheme(value: unknown): value is Theme {
  return typeof value === 'string' && (Themes as readonly string[]).includes(value);
}

export function readTheme(): Theme {
  try {
    const stored = localStorage.getItem(StorageKey);

    // Checked on read, not trusted: storage is editable by hand, and a word Quasar does not know
    // would reach `Dark.set` as a truthy string and force dark on somebody who chose light.
    return isTheme(stored) ? stored : DefaultTheme;
  } catch {
    return DefaultTheme;
  }
}

export function writeTheme(theme: Theme): void {
  try {
    localStorage.setItem(StorageKey, theme);
  } catch {
    // The theme still applies for this session.
  }
}

/**
 * What `Dark.set` takes for a theme. Quasar spells "follow the system" as the string `'auto'` and
 * the two fixed choices as booleans, so this is the one place that translation lives.
 */
export function darkSetting(theme: Theme): boolean | 'auto' {
  return theme === 'auto' ? 'auto' : theme === 'dark';
}

/** The account menu's label and icon for each choice, in the order it offers them. */
export const ThemeChoices: readonly { value: Theme; label: string; icon: string }[] = [
  { value: 'auto', label: 'Match system', icon: 'brightness_auto' },
  { value: 'light', label: 'Light', icon: 'light_mode' },
  { value: 'dark', label: 'Dark', icon: 'dark_mode' },
];
