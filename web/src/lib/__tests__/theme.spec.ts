import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DefaultTheme, darkSetting, readTheme, writeTheme } from '../theme';

function useStorage(initial: Record<string, string> = {}) {
  const store = { ...initial };

  vi.stubGlobal('localStorage', {
    getItem: (k: string) => (k in store ? store[k] : null),
    setItem: (k: string, v: string) => {
      store[k] = v;
    },
  });

  return store;
}

beforeEach(() => {
  vi.unstubAllGlobals();
});

describe('the theme preference', () => {
  it('follows the system until somebody chooses', () => {
    useStorage();

    expect(readTheme()).toBe('auto');
    expect(DefaultTheme).toBe('auto');
  });

  it('reads back what was written', () => {
    const store = useStorage();

    writeTheme('dark');

    expect(store['harness.theme']).toBe('dark');
    expect(readTheme()).toBe('dark');
  });

  /** A word Quasar does not know would reach `Dark.set` as a truthy string and force dark. */
  it('refuses a stored word that is not a theme', () => {
    useStorage({ 'harness.theme': 'midnight' });

    expect(readTheme()).toBe('auto');
  });

  it('survives storage that throws', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => {
        throw new Error('denied');
      },
      setItem: () => {
        throw new Error('denied');
      },
    });

    expect(readTheme()).toBe('auto');
    expect(() => writeTheme('light')).not.toThrow();
  });

  it('speaks Quasar: auto as the string, the fixed choices as booleans', () => {
    expect(darkSetting('auto')).toBe('auto');
    expect(darkSetting('dark')).toBe(true);
    expect(darkSetting('light')).toBe(false);
  });
});
