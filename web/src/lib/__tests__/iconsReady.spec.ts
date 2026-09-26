// @vitest-environment happy-dom
//
// ON A SLOW LINK AN ICON FONT CAN TAKE SECONDS. Until it arrives its names render as words -
// "groupsarrow_drop_down" across the ribbon. Icons stay hidden until the font
// is ready, or until a timeout, so a page never shows the names and never stays blank for good.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { IconsReadyClass, markIconsReadyWhenLoaded } from '../iconsReady';

afterEach(() => {
  document.documentElement.classList.remove(IconsReadyClass);
  vi.useRealTimers();
});

describe('markIconsReadyWhenLoaded', () => {
  it('marks the page ready once the icon font has loaded, and not before', async () => {
    let loaded: () => void = () => {};
    const fonts = { load: vi.fn(() => new Promise<unknown[]>((resolve) => { loaded = () => resolve([]); })) };

    const done = markIconsReadyWhenLoaded(fonts, 15_000);
    expect(document.documentElement.classList.contains(IconsReadyClass)).toBe(false);
    expect(fonts.load).toHaveBeenCalledWith(expect.stringContaining('Material Symbols Outlined'), expect.any(String));

    loaded();
    await done;

    expect(document.documentElement.classList.contains(IconsReadyClass)).toBe(true);
  });

  it('marks the page ready after the timeout even if the font never arrives', async () => {
    vi.useFakeTimers();
    const fonts = { load: vi.fn(() => new Promise<unknown[]>(() => {})) };

    const done = markIconsReadyWhenLoaded(fonts, 15_000);
    await vi.advanceTimersByTimeAsync(15_000);
    await done;

    expect(document.documentElement.classList.contains(IconsReadyClass)).toBe(true);
  });

  it('marks the page ready at once where the browser cannot report fonts', async () => {
    await markIconsReadyWhenLoaded(undefined, 15_000);

    expect(document.documentElement.classList.contains(IconsReadyClass)).toBe(true);
  });
});
