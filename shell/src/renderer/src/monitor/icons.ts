import type { StepKind } from './steps';
import type { WorkflowIcon } from './workflows';

/**
 * THE STEP ICONS, drawn with canvas paths: a page for a read, a pencil for an edit, a page with a plus for a new
 * file, a prompt for a command, a lens for a search, a globe for the web, a speech bubble for the bus, three dots
 * for thinking... Each fits a box of `s` px centred on (x, y), in the current stroke and fill style.
 * Paths, not an icon font: they need no font file and stay sharp at every zoom.
 */
export function drawStepIcon(c: CanvasRenderingContext2D, kind: StepKind, x: number, y: number, s: number): void {
  const u = s / 16; // the paths are drawn on a 16-unit grid
  const P = (px: number, py: number): [number, number] => [x + (px - 8) * u, y + (py - 8) * u];
  const line = (...pts: [number, number][]) => {
    c.beginPath();
    pts.forEach(([px, py], i) => { const [a, b] = P(px, py); if (i) c.lineTo(a, b); else c.moveTo(a, b); });
    c.stroke();
  };
  const circle = (cx: number, cy: number, r: number, fill = false) => {
    const [a, b] = P(cx, cy);
    c.beginPath(); c.arc(a, b, r * u, 0, Math.PI * 2);
    if (fill) c.fill(); else c.stroke();
  };
  const page = () => line([4, 2], [10, 2], [12.5, 4.5], [12.5, 14], [4, 14], [4, 2]);
  c.lineWidth = Math.max(1, 1.4 * u);
  c.lineCap = 'round';
  c.lineJoin = 'round';
  switch (kind) {
    case 'read':
      page();
      line([6, 6.5], [10.5, 6.5]); line([6, 9], [10.5, 9]); line([6, 11.5], [9, 11.5]);
      break;
    case 'edit':
      line([3, 13], [3.6, 10.2], [10.8, 3], [13, 5.2], [5.8, 12.4], [3, 13]);
      line([9.4, 4.4], [11.6, 6.6]);
      break;
    case 'create':
      page();
      line([8.25, 6], [8.25, 11]); line([5.75, 8.5], [10.75, 8.5]);
      break;
    case 'run':
      line([2, 3], [14, 3], [14, 13], [2, 13], [2, 3]);
      line([4.5, 6], [7, 8], [4.5, 10]); line([8, 10.5], [11.5, 10.5]);
      break;
    case 'search':
      circle(6.8, 6.8, 4.2);
      line([9.8, 9.8], [13.5, 13.5]);
      break;
    case 'web':
      circle(8, 8, 6);
      line([2, 8], [14, 8]);
      c.beginPath(); { const [a, b] = P(8, 8); c.ellipse(a, b, 2.6 * u, 6 * u, 0, 0, Math.PI * 2); } c.stroke();
      break;
    case 'bus':
      line([2.5, 3], [13.5, 3], [13.5, 10.5], [7, 10.5], [4, 13.5], [4, 10.5], [2.5, 10.5], [2.5, 3]);
      line([5, 6], [11, 6]); line([5, 8], [9, 8]);
      break;
    case 'think':
      circle(4, 9, 1.4, true); circle(8, 9, 1.4, true); circle(12, 9, 1.4, true);
      break;
    case 'say':
      line([2.5, 3.5], [13.5, 3.5], [13.5, 10.5], [6, 10.5], [3.5, 13], [3.5, 10.5], [2.5, 10.5], [2.5, 3.5]);
      break;
    case 'agent':
      circle(8, 3.5, 1.8); circle(3.5, 12.5, 1.8); circle(12.5, 12.5, 1.8);
      line([8, 5.3], [8, 8], [3.5, 10.7]); line([8, 8], [12.5, 10.7]);
      break;
    case 'plan':
      line([2.5, 4], [3.8, 5.3], [6, 3]); line([8, 4.2], [13.5, 4.2]);
      line([2.5, 9], [3.8, 10.3], [6, 8]); line([8, 9.2], [13.5, 9.2]);
      line([8, 13.5], [13.5, 13.5]);
      break;
    case 'skill':
      line([2, 3.5], [7.5, 4.5], [7.5, 14], [2, 13], [2, 3.5]);
      line([14, 3.5], [8.5, 4.5], [8.5, 14], [14, 13], [14, 3.5]);
      break;
    default: {
      // A gear: a ring with six teeth.
      circle(8, 8, 2.2);
      for (let i = 0; i < 6; i++) {
        const a = (i * Math.PI) / 3;
        line([8 + Math.cos(a) * 4.2, 8 + Math.sin(a) * 4.2], [8 + Math.cos(a) * 6.2, 8 + Math.sin(a) * 6.2]);
      }
      circle(8, 8, 4.4);
    }
  }
}

/** A workflow's mark, in a box of `s` px centred on (x, y): a check, a play triangle, a pause, a cross. */
export function drawWorkflowIcon(c: CanvasRenderingContext2D, icon: WorkflowIcon, x: number, y: number, s: number): void {
  const u = s / 16;
  const P = (px: number, py: number): [number, number] => [x + (px - 8) * u, y + (py - 8) * u];
  const path = (close: boolean, ...pts: [number, number][]) => {
    c.beginPath();
    pts.forEach(([px, py], i) => { const [a, b] = P(px, py); if (i) c.lineTo(a, b); else c.moveTo(a, b); });
    if (close) { c.closePath(); c.fill(); } else c.stroke();
  };
  c.lineWidth = Math.max(1.2, 2 * u);
  c.lineCap = 'round';
  c.lineJoin = 'round';
  switch (icon) {
    case 'check': path(false, [3, 8.5], [6.5, 12], [13, 4.5]); break;
    case 'play': path(true, [5, 3], [13, 8], [5, 13]); break;
    case 'pause': path(false, [5.5, 3.5], [5.5, 12.5]); path(false, [10.5, 3.5], [10.5, 12.5]); break;
    case 'cross': path(false, [4, 4], [12, 12]); path(false, [12, 4], [4, 12]); break;
  }
}
