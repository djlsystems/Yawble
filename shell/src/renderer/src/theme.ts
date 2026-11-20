import { reactive } from 'vue';

/**
 * The window's light and dark themes. The mode is the person's choice: "system" (nothing chosen yet, or "Use system
 * setting" picked again) follows the OS (prefers-color-scheme) live; "light" or "dark" is kept in localStorage and the
 * OS no longer applies. The theme shown is set as data-theme="light"|"dark" on <html>, which styles.css keys its
 * colours on. The desktop window also tells Electron main (setTheme), which colours the title-bar overlay controls.
 * A remote view (Cloud Link) keeps its own choice under its own key.
 *
 * public/theme-boot.js (a classic script in each page's <head>) sets data-theme by the same rule before the stylesheet
 * paints; main.ts imports this module first, so it takes over before anything is mounted. A switch made in another
 * window of the app (same key in localStorage) arrives as a 'storage' event and is followed here.
 */
export type ThemeMode = 'system' | 'light' | 'dark';
export type Theme = 'light' | 'dark';

export const THEME_KEY = 'yawble.theme';
export const REMOTE_THEME_KEY = 'yawble.remoteTheme';
const DARK_QUERY = '(prefers-color-scheme: dark)';

export interface ThemeEnv {
  /** Where the choice is kept; may throw (storage blocked), which only means the choice is not kept. */
  storage?: () => Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;
  matchMedia?: (query: string) => Pick<MediaQueryList, 'matches'> & Partial<Pick<MediaQueryList, 'addEventListener' | 'removeEventListener'>>;
  root?: HTMLElement;
  key?: string;
  /** Told the theme each time it is applied (the desktop window's title-bar overlay). */
  notify?: (theme: Theme) => void;
  /** The page's window, for 'storage' events: a choice made in another window of the app (same key) is followed here. */
  events?: Pick<Window, 'addEventListener' | 'removeEventListener'>;
  /** The mode to start with when nothing is kept (no storage: the embedded remote view's ?theme=). Default system. */
  initialMode?: ThemeMode;
}

export interface ThemeState {
  mode: ThemeMode;
  current: Theme;
}

export interface ThemeController {
  readonly state: ThemeState;
  setMode(mode: ThemeMode): void;
  /** One click on the title-bar button: the other theme, kept. */
  toggle(): void;
  dispose(): void;
}

function readMode(env: ThemeEnv, key: string): ThemeMode {
  try {
    const v = env.storage?.().getItem(key);
    return v === 'light' || v === 'dark' ? v : 'system';
  } catch { return 'system'; }
}

function writeMode(env: ThemeEnv, key: string, mode: ThemeMode): void {
  try {
    const s = env.storage?.();
    if (!s) return;
    if (mode === 'system') s.removeItem(key);
    else s.setItem(key, mode);
  } catch { /* storage blocked: the choice holds for this page only */ }
}

export function createTheme(env: ThemeEnv): ThemeController {
  const key = env.key ?? THEME_KEY;
  let media: ReturnType<NonNullable<ThemeEnv['matchMedia']>> | null = null;
  try { media = env.matchMedia?.(DARK_QUERY) ?? null; } catch { media = null; }
  const osTheme = (): Theme => (media?.matches ? 'dark' : 'light');

  const state = reactive<ThemeState>({ mode: env.storage ? readMode(env, key) : env.initialMode ?? 'system', current: 'light' });

  const apply = () => {
    state.current = state.mode === 'system' ? osTheme() : state.mode;
    env.root?.setAttribute('data-theme', state.current);
    if (env.root) env.root.style.colorScheme = state.current;
    try { env.notify?.(state.current); } catch { /* a view without a title bar to colour */ }
  };
  const onOsChange = () => { if (state.mode === 'system') apply(); };
  try { media?.addEventListener?.('change', onOsChange); } catch { /* an old engine: no live follow */ }
  // Another window of the app (an agent's terminal window, the main window) changed the choice: show it here too.
  // key null is the whole storage cleared. Nothing is written back, so the windows never echo each other.
  const onStorage = (e: Event) => {
    const changed = (e as StorageEvent).key;
    if (changed !== null && changed !== key) return;
    state.mode = readMode(env, key);
    apply();
  };
  try { env.events?.addEventListener('storage', onStorage); } catch { /* no window */ }
  apply();

  const setMode = (mode: ThemeMode) => {
    state.mode = mode;
    writeMode(env, key, mode);
    apply();
  };
  return {
    state,
    setMode,
    toggle: () => setMode(state.current === 'dark' ? 'light' : 'dark'),
    dispose() {
      try { media?.removeEventListener?.('change', onOsChange); } catch { /* ignore */ }
      try { env.events?.removeEventListener('storage', onStorage); } catch { /* ignore */ }
    },
  };
}

/**
 * The page's theme set-up: the desktop window's own, or a remote view's (its own key). B115: the remote view embedded in
 * the cloud page (?embed=1, embed.ts) has no storage at all: the viewer's kept yawble.remoteTheme is neither read nor
 * written, nor followed from another tab; it starts at the cloud's ?theme=light|dark (else the OS) and the cloud's
 * yawble.theme messages set it after (App.vue, through setMode, which keeps nothing without storage).
 */
export function pageThemeEnv(w: (Window & typeof globalThis) | undefined = typeof window !== 'undefined' ? window : undefined): ThemeEnv {
  const yawble = w?.yawble as Window['yawble'] | undefined;
  const base: ThemeEnv = {
    matchMedia: w && typeof w.matchMedia === 'function' ? (q) => w.matchMedia(q) : undefined,
    root: w?.document?.documentElement,
    key: yawble?.remote ? REMOTE_THEME_KEY : THEME_KEY,
    notify: yawble?.remote ? undefined : (t) => yawble?.setTheme?.(t),
  };
  let q: URLSearchParams | null = null;
  try { q = new URLSearchParams(w?.location?.search ?? ''); } catch { q = null; }
  if (q?.get('embed') === '1') {
    const given = q.get('theme');
    return { ...base, initialMode: given === 'light' || given === 'dark' ? given : 'system' };
  }
  return { ...base, storage: () => localStorage, events: w };
}

export const theme = createTheme(pageThemeEnv());

/** The title-bar button's words: what one click does. */
export function toggleLabel(current: Theme): string {
  return current === 'dark' ? 'Switch to light theme' : 'Switch to dark theme';
}
