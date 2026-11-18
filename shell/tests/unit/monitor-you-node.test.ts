import { describe, expect, it } from 'vitest';
import { onYouNode, youNodeAt, YouR, type KeepOut } from '../../src/renderer/src/monitor/rhythm';

// Card 416 (B093 part 2): the You node stays inside the wall and clear of the Legend button (a phone's controls row
// along the bottom, the desktop's button at the bottom left) whatever the wall's height.

/** A phone's controls row as the real page measures it (card 417): bottom 8px, left/right 8px, 48 px tall. */
const phoneRow = (w: number, h: number): KeepOut => ({ left: 8, top: h - 56, right: w - 8, bottom: h - 8 });
/** The phone's Legend button, 78 x 48 at the row's right end. */
const phoneLegend = (w: number, h: number): KeepOut => ({ left: w - 8 - 78, top: h - 56, right: w - 8, bottom: h - 8 });
/** What MonitorView gives the scene on a phone, most telling first: Legend, then the row. */
const phoneKeep = (w: number, h: number): KeepOut[] => [phoneLegend(w, h), phoneRow(w, h)];
const onRect = (x: number, y: number, k: KeepOut) => x >= k.left && x <= k.right && y >= k.top && y <= k.bottom;
/** The desktop's Legend button (left 12px, bottom 8px). */
const desktopLegend = (h: number): KeepOut => ({ left: 12, top: h - 28, right: 82, bottom: h - 8 });

function overlaps(n: { x: number; y: number; r: number }, k: KeepOut): boolean {
  const dx = Math.max(k.left - n.x, 0, n.x - k.right), dy = Math.max(k.top - n.y, 0, n.y - k.bottom);
  return Math.hypot(dx, dy) < n.r;
}
function inside(n: { x: number; y: number; r: number }, w: number, h: number): boolean {
  return n.x - n.r >= 0 && n.x + n.r <= w && n.y - n.r >= 0 && n.y + n.r <= h;
}

describe('the You node on a short wall (card 416)', () => {
  it('a normal wall keeps its place: right edge, half way down', () => {
    expect(youNodeAt(1280, 800, [desktopLegend(800)])).toEqual({ x: 1280 - YouR - 22, y: 400, r: YouR });
    expect(youNodeAt(375, 500, [phoneRow(375, 500)])).toEqual({ x: 375 - YouR - 22, y: 250, r: YouR });
    expect(youNodeAt(400, 150).y).toBe(120);
  });

  it('a 110 px phone wall (375x640, 12 asks): inside the wall and above the controls row', () => {
    const k = phoneRow(375, 110);
    const n = youNodeAt(375, 110, [k]);
    expect(inside(n, 375, 110)).toBe(true);
    expect(overlaps(n, k)).toBe(false);
    expect(n.y + n.r).toBeLessThanOrEqual(k.top);
    expect(n.x).toBe(375 - YouR - 22); // still at the right edge
    // and a tap on it hits it, while a tap on the Legend button (the row's right end) does not
    expect(onYouNode(n.x, n.y, 375, 110, [k])).toBe(true);
    expect(onYouNode(340, 80, 375, 110, [k])).toBe(false);
  });

  it('without its keep-outs the old place was off a 110 px wall', () => {
    expect(youNodeAt(375, 110).y + YouR).toBeLessThanOrEqual(110);
  });

  it('a 272 px phone wall: half way would touch nothing, so it stays at 136', () => {
    const k = phoneRow(375, 272);
    const n = youNodeAt(375, 272, [k]);
    expect(n.y).toBe(136);
    expect(inside(n, 375, 272)).toBe(true);
    expect(overlaps(n, k)).toBe(false);
  });

  it('a 160 px phone wall: 120 would sit on the row, so it moves up just clear of it', () => {
    const k = phoneRow(375, 160);
    const n = youNodeAt(375, 160, [k]);
    expect(overlaps(n, k)).toBe(false);
    expect(n.y).toBeLessThan(120);
    expect(n.y).toBeGreaterThan(k.top - n.r - 10);
  });

  it('every wall height from 60 to 900 px: inside, and clear of the row whenever there is room above it', () => {
    for (let h = 60; h <= 900; h += 2) {
      const k = phoneRow(375, h);
      const n = youNodeAt(375, h, [k]);
      expect(inside(n, 375, h), `h=${h}`).toBe(true);
      if (k.top >= 2 * YouR + 10) expect(overlaps(n, k), `h=${h}`).toBe(false);
      const d = youNodeAt(1280, h, [desktopLegend(h)]);
      expect(inside(d, 1280, h), `desktop h=${h}`).toBe(true);
      expect(overlaps(d, desktopLegend(h)), `desktop h=${h}`).toBe(false);
    }
  });

  it('a Legend button where the node would be: it moves sideways when it cannot go up or down', () => {
    const legend: KeepOut = { left: 300, top: 0, right: 375, bottom: 110 }; // covers the right edge, top to bottom
    const n = youNodeAt(375, 110, [legend]);
    expect(overlaps(n, legend)).toBe(false);
    expect(n.x + n.r).toBeLessThanOrEqual(300);
    expect(inside(n, 375, 110)).toBe(true);
  });

  it('a wall covered all over: the least telling rect is given up first', () => {
    const row = phoneRow(375, 110);
    const openLegend: KeepOut = { left: 0, top: 0, right: 375, bottom: 60 };
    const n = youNodeAt(375, 110, [row, openLegend]);
    expect(overlaps(n, row)).toBe(false);
    expect(inside(n, 375, 110)).toBe(true);
  });

  // Card 416 round 2 (card 417's fault): under ~114.5 px no spot clears the row with the margins; the node used to go
  // back to its old place, on Legend. 375x546 gives 113 px, 375x530 103 px.
  for (const h of [113, 110, 103, 100]) {
    it(`a ${h} px phone wall with the real row and Legend: inside, clear of Legend, its centre on the wall`, () => {
      const keep = phoneKeep(375, h);
      const [legend, row] = keep as [KeepOut, KeepOut];
      const n = youNodeAt(375, h, keep);
      expect(inside(n, 375, h), JSON.stringify(n)).toBe(true);
      expect(overlaps(n, legend), JSON.stringify(n)).toBe(false);
      expect(onRect(n.x, n.y, row), 'the centre is off the controls row').toBe(false);
      expect(n.y).toBeLessThan(row.top);
      // where there is room (113, 110) it clears the whole row too, at the right edge
      if (h >= 110) {
        expect(overlaps(n, row), JSON.stringify(n)).toBe(false);
        expect(n.x).toBe(375 - YouR - 22);
      } else {
        expect(n.y).toBe(YouR); // as high as it can go: it overlaps the row the least
      }
      expect(onYouNode(n.x, n.y, 375, h, keep)).toBe(true);
      expect(onYouNode(legend.left + 39, legend.top + 24, 375, h, keep), 'a tap on Legend is not on the node').toBe(false);
    });
  }

  it('every phone wall from 100 to 300 px with the real row: inside, never on Legend, centre never on the row', () => {
    for (let h = 100; h <= 300; h += 1) {
      const keep = phoneKeep(375, h);
      const n = youNodeAt(375, h, keep);
      expect(inside(n, 375, h), `h=${h}`).toBe(true);
      expect(overlaps(n, keep[0]!), `h=${h}`).toBe(false);
      expect(onRect(n.x, n.y, keep[1]!), `h=${h}`).toBe(false);
      if (h >= 105) expect(overlaps(n, keep[1]!), `h=${h}`).toBe(false);
    }
  });

  it('with the legend open over the top as well: the open legend is given up before Legend or the row', () => {
    const h = 103;
    const openLegend: KeepOut = { left: 8, top: 0, right: 300, bottom: h - 60 };
    const keep = [...phoneKeep(375, h), openLegend];
    const n = youNodeAt(375, h, keep);
    expect(inside(n, 375, h)).toBe(true);
    expect(overlaps(n, keep[0]!)).toBe(false);
    expect(onRect(n.x, n.y, keep[1]!)).toBe(false);
  });

  it('a wall shorter than the node: centred, never off it', () => {
    const n = youNodeAt(375, 40, [phoneRow(375, 40)]);
    expect(n.y).toBe(20);
    expect(n.x).toBeLessThanOrEqual(375 - YouR);
  });
});
