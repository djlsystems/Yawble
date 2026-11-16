// The window's zoom: View > Zoom in / Zoom out / Actual size, their keys, and Ctrl (Cmd on macOS) + mouse wheel all
// step the same way, half a level at a time, between -3 and 5.

/** What zoom needs of a window's web contents (Electron's WebContents has both). */
export interface Zoomable {
  getZoomLevel(): number;
  setZoomLevel(level: number): void;
}

export const ZoomStep = 0.5;
export const ZoomMin = -3;
export const ZoomMax = 5;

/** Zooms in (+1 step), out (-1 step), or back to actual size (null). */
export function zoom(wc: Zoomable, steps: 1 | -1 | null): void {
  wc.setZoomLevel(steps === null ? 0 : Math.max(ZoomMin, Math.min(ZoomMax, wc.getZoomLevel() + steps * ZoomStep)));
}

/** Ctrl/Cmd + mouse wheel: Electron reports it as "zoom-changed" with "in" or "out". */
export function wheelZoom(wc: Zoomable & { on(event: 'zoom-changed', listener: (e: unknown, direction: string) => void): unknown }): void {
  wc.on('zoom-changed', (_e, direction) => {
    if (direction === 'in') zoom(wc, 1);
    else if (direction === 'out') zoom(wc, -1);
  });
}
