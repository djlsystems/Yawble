// While a dialog dims the page, the window controls the system draws in the title bar (Windows, Linux) dim with it.
// Counted, so one dialog closing over another does not light the controls up early.
let open = 0;

export function dimTitleBar(on: boolean): void {
  open = Math.max(0, open + (on ? 1 : -1));
  try { (window as { yawble?: { dimTitleBar?: (dim: boolean) => void } }).yawble?.dimTitleBar?.(open > 0); } catch { /* a browser view */ }
}
