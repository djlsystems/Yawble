/**
 * How the terminal displays, as a per-browser preference.
 *
 * A DISPLAY preference rather than team data: it describes this browser's view of the terminal,
 * never anything the server or another viewer needs, so it lives in `localStorage` beside
 * `harness.boardSize` and follows that key's wrapper discipline exactly - storage throws in a
 * private window with cookies blocked, and a display preference is not worth a crash.
 *
 * The rules live here rather than in the store or the component because mounting is possible now
 * and still the worse home for this rule: a spec that greps a component's own source is what let a
 * Critical through 116 green tests once already. Pure functions are what a test can actually
 * exercise.
 */

export interface TerminalDisplay {
  fontFamily: string;
  fontSize: number;
  maximised: boolean;
  width: number;
  height: number;
  left: number;
  top: number;
}

export const FontSizeBounds = { min: 10, max: 22 } as const;
export const WindowWidthBounds = { min: 360, max: 1920 } as const;
export const WindowHeightBounds = { min: 280, max: 1080 } as const;

/**
 * Cascadia-first monospace stack. The value is the full family string including the `monospace` fallback tail,
 * modelled on xterm's expectation.
 */
export const DefaultTerminalDisplay: TerminalDisplay = {
  fontFamily: '"Cascadia Mono", Consolas, monospace',
  fontSize: 13,
  maximised: false,
  width: 960,
  height: 560,
  left: 0,
  top: 0,
};

const StorageKey = 'harness.terminalDisplay';

/**
 * Compute initial window position/size from viewport.
 * Used when nothing is stored or the stored values are off-screen.
 * Bottom-right inset that misses the FAB, avoiding keyboard area.
 */
export function computeInitialWindowGeometry(viewportWidth: number, viewportHeight: number): Pick<TerminalDisplay, 'width' | 'height' | 'left' | 'top'> {
  // Clamp size to min/max bounds, but prefer ~960x560 if it fits
  let width = Math.min(960, viewportWidth - 40); // 20px margin on each side
  let height = Math.min(560, viewportHeight - 40);

  width = Math.max(WindowWidthBounds.min, Math.min(WindowWidthBounds.max, width));
  height = Math.max(WindowHeightBounds.min, Math.min(WindowHeightBounds.max, height));

  // Bottom-right inset (accounts for FAB in bottom-right)
  const left = Math.max(0, viewportWidth - width - 20); // 20px from right edge
  const top = Math.max(0, viewportHeight - height - 20); // 20px from bottom edge

  return { width, height, left, top };
}


function clamp(value: unknown, bounds: { min: number; max: number }, fallback: number): number {
  // NOT `typeof value === 'number'` alone. A number input yields NaN for an empty box, and NaN
  // survives every comparison silently - `Math.min(NaN, x)` is NaN - so an unguarded clamp writes
  // NaN into storage and the terminal takes `font-size: NaNpx`, which the browser DROPS. The
  // terminal then has no size at all, which looks like a layout bug rather than a bad value.
  if (typeof value !== 'number' || Number.isNaN(value)) {
    return fallback;
  }

  return Math.round(Math.min(bounds.max, Math.max(bounds.min, value)));
}

export function clampTerminalDisplay(partial: Partial<TerminalDisplay>): TerminalDisplay {
  const fontFamily =
    typeof partial.fontFamily === 'string' && isSupportedFontFamily(partial.fontFamily)
      ? partial.fontFamily
      : DefaultTerminalDisplay.fontFamily;

  return {
    fontFamily,
    fontSize: clamp(partial.fontSize, FontSizeBounds, DefaultTerminalDisplay.fontSize),
    maximised: typeof partial.maximised === 'boolean' ? partial.maximised : DefaultTerminalDisplay.maximised,
    width: clamp(partial.width, WindowWidthBounds, DefaultTerminalDisplay.width),
    height: clamp(partial.height, WindowHeightBounds, DefaultTerminalDisplay.height),
    left:
      typeof partial.left === 'number' && Number.isFinite(partial.left)
        ? Math.round(partial.left)
        : DefaultTerminalDisplay.left,
    top:
      typeof partial.top === 'number' && Number.isFinite(partial.top)
        ? Math.round(partial.top)
        : DefaultTerminalDisplay.top,
  };
}

/**
 * Clamp terminal display to fit within viewport, ensuring left/top don't push the window off-screen.
 */
export function clampTerminalDisplayToViewport(
  partial: Partial<TerminalDisplay>,
  viewportWidth: number,
  viewportHeight: number,
): TerminalDisplay {
  // First apply basic clamping
  const base = clampTerminalDisplay(partial);

  // Ensure width/height don't exceed viewport
  const width = Math.min(base.width, Math.max(WindowWidthBounds.min, viewportWidth - 20));
  const height = Math.min(base.height, Math.max(WindowHeightBounds.min, viewportHeight - 20));

  // Clamp left/top so the window stays on-screen
  const left = Math.max(0, Math.min(base.left, viewportWidth - width - 10));
  const top = Math.max(0, Math.min(base.top, viewportHeight - height - 10));

  return { ...base, width, height, left, top };
}

/**
 * List of supported font families. Each entry includes the monospace fallback.
 */
const SupportedFontFamilies = [
  '"Cascadia Mono", Consolas, monospace',
  '"IBM Plex Mono", Consolas, monospace',
  'Consolas, monospace',
  'ui-monospace, monospace',
] as const;

function isSupportedFontFamily(value: string): boolean {
  return SupportedFontFamilies.includes(value as any);
}

export function readTerminalDisplay(): TerminalDisplay {
  try {
    const stored = localStorage.getItem(StorageKey);

    if (!stored) return { ...DefaultTerminalDisplay };

    const parsed: unknown = JSON.parse(stored);

    // `typeof null === 'object'`, so the null check is not redundant - and an array passes
    // `typeof === 'object'` too, which is why the shape is probed by reading the fields rather
    // than by trusting the type.
    if (typeof parsed !== 'object' || parsed === null) return { ...DefaultTerminalDisplay };

    // CLAMPED ON READ, not only on write. Storage is editable by hand and shared with older
    // builds, so a value that was legal when it was written may not be now. Trusting it because it
    // came from us is how a 4000px window reaches the screen.
    return clampTerminalDisplay(parsed as Partial<TerminalDisplay>);
  } catch {
    // Unreadable storage, unparseable JSON, or access refused outright. A terminal that will not
    // render is a far worse outcome than a preference that quietly resets.
    return { ...DefaultTerminalDisplay };
  }
}

export function writeTerminalDisplay(display: Partial<TerminalDisplay>): void {
  try {
    localStorage.setItem(StorageKey, JSON.stringify(clampTerminalDisplay(display)));
  } catch {
    // Nothing to do and nothing worth saying: the display still applies for this session.
  }
}
