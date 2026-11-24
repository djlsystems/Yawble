// placeBreakLabels never puts a break label over the start / end
// labels or over an earlier break label, keeps it on the axis, and hides it only when there is no room.
import { describe, expect, it } from 'vitest';
import { placeBreakLabels, TickGapPx } from '../../src/renderer/src/timeline/time';

// a small seeded generator, so a failure repeats
function rng(seed: number) {
  let s = seed >>> 0;
  return () => { s = (s * 1664525 + 1013904223) >>> 0; return s / 2 ** 32; };
}

describe('placeBreakLabels', () => {
  it('2000 seeded random axes: every shown label clears the start and end labels and the label before it', () => {
    const r = rng(434);
    for (let n = 0; n < 2000; n++) {
      const width = 120 + Math.floor(r() * 1100);
      const startRight = 20 + Math.floor(r() * 40);
      const endLeft = width - (20 + Math.floor(r() * 40));
      const count = 1 + Math.floor(r() * 4);
      const labels = Array.from({ length: count }, () => ({ x: r() * width, w: 30 + Math.floor(r() * 50) })).sort((a, b) => a.x - b.x);
      const out = placeBreakLabels(width, startRight, endLeft, labels);
      expect(out).toHaveLength(count);
      let prevRight = -Infinity;
      out.forEach((left, i) => {
        if (left == null) return;
        const right = left + labels[i].w;
        const say = `case ${n}: ${JSON.stringify({ width, startRight, endLeft, labels, out })}`;
        expect(left, say).toBeGreaterThanOrEqual(startRight + TickGapPx);
        expect(right, say).toBeLessThanOrEqual(endLeft - TickGapPx);
        expect(left, say).toBeGreaterThanOrEqual(prevRight + TickGapPx);
        expect(left, say).toBeGreaterThanOrEqual(0);
        expect(right, say).toBeLessThanOrEqual(width);
        prevRight = right;
      });
    }
  });

  it('a label is hidden only when it cannot fit: one alone in a room wider than it always shows', () => {
    const r = rng(7);
    for (let n = 0; n < 500; n++) {
      const w = 30 + Math.floor(r() * 50);
      const width = 400, startRight = 40, endLeft = 360;
      expect(placeBreakLabels(width, startRight, endLeft, [{ x: r() * width, w }])[0]).not.toBeNull();
    }
  });

  it('a hidden label does not push the next one: the next still gets its own place', () => {
    // the first (100 px) cannot fit in 40..124; the second (30 px) centred at 80 fits there
    expect(placeBreakLabels(170, 34, 130, [{ x: 60, w: 100 }, { x: 80, w: 30 }])).toEqual([null, 65]);
  });

  it('three labels close together line up left to right with the gap between them', () => {
    expect(placeBreakLabels(800, 40, 760, [{ x: 400, w: 50 }, { x: 405, w: 50 }, { x: 410, w: 50 }])).toEqual([375, 431, 487]);
  });

  it('an end label wider than its share (endLeft beyond width) is bounded by the width', () => {
    expect(placeBreakLabels(300, 40, 400, [{ x: 299, w: 50 }])).toEqual([244]);
  });
});
