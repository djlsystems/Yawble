import { computed, type CSSProperties } from 'vue';
import type { WindowGeometry } from './windowGeometry';

/**
 * MOVING AND RESIZING A WINDOW WITH THE POINTER, written once for every window that does it - the
 * Concierge terminal and the Documents explorer.
 *
 * A press on the handle (a title bar) moves the window; a press on an edge grip resizes it. The
 * pointer is CAPTURED on the window's shell, so a fast drag that leaves the handle keeps going, and
 * every move is applied as a delta from the last one through `apply`, which owns the clamping and
 * the remembering.
 *
 * The window's geometry and its rules stay the caller's: the Concierge keeps them in its display
 * store, the explorer in its own state. This is only the pointer half.
 */

export type WindowFrameMode = 'move' | 'resize-e' | 'resize-s' | 'resize-se' | 'resize-w' | 'resize-n';

/**
 * WHAT A PRESS ON THE HANDLE IGNORES. Buttons and icons in a title bar are controls, and a row or
 * crumb that can be dragged starts an HTML drag of its own - a window move starting under it
 * would carry the window off with the item.
 */
export const FrameIgnoredSelector = 'button, [role="button"], q-btn, .q-icon, [draggable="true"]';

export interface WindowFrameOptions {
  /** The window's geometry now, or null while it has never been placed (it then sits wherever its
   *  container put it, and the first press places it there). */
  geometry: () => WindowGeometry | null;

  /** Applies the next geometry: clamps it, stores it, remembers it. */
  apply: (next: WindowGeometry) => void;

  /** False while full screen or maximised, when the window does not move. */
  isWindowed: () => boolean;

  /** The window's own element, found from the pressed element with `closest`. */
  shellSelector: string;

  /** Presses on these never start a move. Defaults to {@link FrameIgnoredSelector}. */
  ignoreSelector?: string;

  /** The smallest the window may be, so a resize from the left or top edge stops instead of
   *  pushing the window across the screen once the minimum is reached. */
  minimum?: { width: number; height: number };
}

export interface WindowFrame {
  onHandlePointerDown: (event: PointerEvent) => void;
  onEdgePointerDown: (event: PointerEvent, mode: Exclude<WindowFrameMode, 'move'>) => void;

  /** Fixed at the window's geometry while windowed and placed, and empty otherwise - so a window
   *  nobody has moved has no size of its own. */
  style: Readonly<{ value: CSSProperties }>;
}

/** The geometry after a pointer moved by (dx, dy) in one mode. Pure, so it is tested on its own. */
export function framed(
  geometry: WindowGeometry,
  mode: WindowFrameMode,
  dx: number,
  dy: number,
  minimum: { width: number; height: number } = { width: 0, height: 0 },
): WindowGeometry {
  switch (mode) {
    case 'move':
      return { ...geometry, left: geometry.left + dx, top: geometry.top + dy };
    case 'resize-e':
      return { ...geometry, width: geometry.width + dx };
    case 'resize-s':
      return { ...geometry, height: geometry.height + dy };
    case 'resize-se':
      return { ...geometry, width: geometry.width + dx, height: geometry.height + dy };
    case 'resize-w': {
      const step = Math.min(dx, geometry.width - minimum.width);
      return { ...geometry, left: geometry.left + step, width: geometry.width - step };
    }
    case 'resize-n': {
      const step = Math.min(dy, geometry.height - minimum.height);
      return { ...geometry, top: geometry.top + step, height: geometry.height - step };
    }
  }
}

export function useWindowFrame(options: WindowFrameOptions): WindowFrame {
  const ignored = options.ignoreSelector ?? FrameIgnoredSelector;

  let last = { x: 0, y: 0 };
  let mode: WindowFrameMode | null = null;

  function begin(event: PointerEvent, next: WindowFrameMode) {
    const shell = (event.currentTarget as Element | null)?.closest(options.shellSelector);
    if (!shell) return;

    // An unplaced window is placed where it already is, so the first move starts from what the
    // person sees rather than from a default somewhere else.
    if (options.geometry() === null) {
      const rect = shell.getBoundingClientRect();
      options.apply({ left: rect.left, top: rect.top, width: rect.width, height: rect.height });
    }

    last = { x: event.clientX, y: event.clientY };
    mode = next;

    try {
      shell.setPointerCapture(event.pointerId);
    } catch {
      // A pointer that is already gone cannot be captured; the listeners below still end the drag.
    }
    shell.addEventListener('pointermove', onPointerMove);
    shell.addEventListener('pointerup', onPointerUp);
    shell.addEventListener('pointercancel', onPointerUp);
  }

  function onHandlePointerDown(event: PointerEvent) {
    if (!options.isWindowed()) return;
    if ((event.target as Element | null)?.closest?.(ignored)) return;

    begin(event, 'move');
  }

  function onEdgePointerDown(event: PointerEvent, edge: Exclude<WindowFrameMode, 'move'>) {
    event.preventDefault();
    event.stopPropagation();

    begin(event, edge);
  }

  function onPointerMove(event: Event) {
    if (!(event instanceof PointerEvent)) return;
    if (!mode || !options.isWindowed()) return;

    const geometry = options.geometry();
    if (geometry) {
      options.apply(framed(geometry, mode, event.clientX - last.x, event.clientY - last.y, options.minimum));
    }

    last = { x: event.clientX, y: event.clientY };
  }

  function onPointerUp(event: Event) {
    if (!(event instanceof PointerEvent)) return;

    mode = null;

    const shell = event.currentTarget as Element | null;
    if (!shell) return;

    shell.removeEventListener('pointermove', onPointerMove);
    shell.removeEventListener('pointerup', onPointerUp);
    // SAME HANDLER, BOTH EVENTS. A touch drag frequently ends in `pointercancel` rather than
    // `pointerup` - a second finger, a system edge gesture - and a drag that is never ended leaves
    // the mode set and the move listener attached, so the next stray pointermove resizes the
    // window without anybody touching a handle.
    shell.removeEventListener('pointercancel', onPointerUp);
    try {
      shell.releasePointerCapture(event.pointerId);
    } catch {
      // Ignore if capture was already released
    }
  }

  const style = computed<CSSProperties>(() => {
    const geometry = options.geometry();
    if (!geometry || !options.isWindowed()) return {};

    return {
      position: 'fixed',
      left: `${geometry.left}px`,
      top: `${geometry.top}px`,
      width: `${geometry.width}px`,
      height: `${geometry.height}px`,
      margin: '0',
    };
  });

  return { onHandlePointerDown, onEdgePointerDown, style };
}
