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
import {
  clampGeometry,
  clampNumber,
  clampToViewport,
  initialGeometry,
  readStored,
  writeStored,
  type WindowBounds,
} from './windowGeometry';

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

export const TerminalDisplayStorageKey = 'harness.terminalDisplay';
const StorageKey = TerminalDisplayStorageKey;

/** The terminal's bounds in the shape the shared window rules take. */
export const TerminalWindowBounds: WindowBounds = { width: WindowWidthBounds, height: WindowHeightBounds };

/**
 * Compute initial window position/size from viewport.
 * Used when nothing is stored or the stored values are off-screen.
 * Bottom-right inset that misses the FAB, avoiding keyboard area.
 */
export function computeInitialWindowGeometry(viewportWidth: number, viewportHeight: number): Pick<TerminalDisplay, 'width' | 'height' | 'left' | 'top'> {
  return initialGeometry(viewportWidth, viewportHeight, TerminalWindowBounds, { width: 960, height: 560 }, 'bottom-right');
}

export function clampTerminalDisplay(partial: Partial<TerminalDisplay>): TerminalDisplay {
  const fontFamily =
    typeof partial.fontFamily === 'string' && isSupportedFontFamily(partial.fontFamily)
      ? partial.fontFamily
      : DefaultTerminalDisplay.fontFamily;

  return {
    fontFamily,
    fontSize: clampNumber(partial.fontSize, FontSizeBounds, DefaultTerminalDisplay.fontSize),
    maximised: typeof partial.maximised === 'boolean' ? partial.maximised : DefaultTerminalDisplay.maximised,
    ...clampGeometry(partial, TerminalWindowBounds, DefaultTerminalDisplay),
  };
}

/**
 * Clamp terminal display to fit within viewport, ensuring left/top don't push the window off-screen.
 * The window rules are `lib/windowGeometry`'s, shared with every other movable window.
 */
export function clampTerminalDisplayToViewport(
  partial: Partial<TerminalDisplay>,
  viewportWidth: number,
  viewportHeight: number,
): TerminalDisplay {
  return clampToViewport(clampTerminalDisplay(partial), TerminalWindowBounds, viewportWidth, viewportHeight);
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
  // Unreadable storage, unparseable JSON, or access refused outright all read as nothing stored. A
  // terminal that will not render is a far worse outcome than a preference that quietly resets.
  const parsed = readStored(StorageKey);

  // `typeof null === 'object'`, so the null check is not redundant - and an array passes
  // `typeof === 'object'` too, which is why the shape is probed by reading the fields rather
  // than by trusting the type.
  if (typeof parsed !== 'object' || parsed === null) return { ...DefaultTerminalDisplay };

  // CLAMPED ON READ, not only on write. Storage is editable by hand and shared with older
  // builds, so a value that was legal when it was written may not be now. Trusting it because it
  // came from us is how a 4000px window reaches the screen.
  return clampTerminalDisplay(parsed as Partial<TerminalDisplay>);
}

export function writeTerminalDisplay(display: Partial<TerminalDisplay>): void {
  writeStored(StorageKey, clampTerminalDisplay(display));
}
