import { describe, expect, it } from 'vitest';
import { mapBeatsWorkflow, MinHitPx, nearestNodeAt } from '../../src/renderer/src/monitor/scene';

/**
 * A map node's tap reach has a floor of MinHitPx screen px at any zoom, so at the 375 px wall's zoom of
 * about 0.2 a team hub (14 world units, about 3 screen px) can still be tapped; the nearest node in reach wins.
 */
type N = { key: string; kind: string; r: number; x: number; y: number };
const hub: N = { key: 'team:payments', kind: 'team', r: 10, x: 0, y: 0 };
const member: N = { key: 'member:payments/developerines', kind: 'member', r: 12, x: 0, y: 0 };
/** The world distance that is `px` screen px at zoom k. */
const px = (p: number, k: number) => p / k;

describe('nearestNodeAt', () => {
  it('is a 22 screen px floor', () => expect(MinHitPx).toBe(22));

  it('at zoom 0.2: a tap 15 screen px from the hub hits it; 30 screen px does not', () => {
    const k = 0.2;
    expect(nearestNodeAt([hub], px(15, k), 0, k)).toBe(hub);
    expect(nearestNodeAt([hub], 0, px(21.5, k), k)).toBe(hub);
    expect(nearestNodeAt([hub], px(30, k), 0, k)).toBeNull();
    expect(nearestNodeAt([hub], px(400, k), px(400, k), k)).toBeNull();
  });

  it('at zoom 1: the floor is 22 world units, above the hub\'s 14; a far tap misses', () => {
    expect(nearestNodeAt([hub], 15, 0, 1)).toBe(hub);
    expect(nearestNodeAt([hub], 21, 0, 1)).toBe(hub);
    expect(nearestNodeAt([hub], 23, 0, 1)).toBeNull();
    expect(nearestNodeAt([hub], 200, 0, 1)).toBeNull();
  });

  it('keeps a node\'s own reach where it is larger than the floor (zoomed in)', () => {
    // zoom 4: the floor is 5.5 world units; the hub keeps 14, a member of r 12 keeps 20
    expect(nearestNodeAt([hub], 13, 0, 4)).toBe(hub);
    expect(nearestNodeAt([hub], 15, 0, 4)).toBeNull();
    expect(nearestNodeAt([{ ...member }], 19, 0, 4)).toBeTruthy();
  });

  it('the nearer of two nodes in reach wins, whichever comes first, at zoom 0.2 and 1', () => {
    for (const k of [0.2, 1]) {
      const a = { ...hub, x: 0 };
      const b = { ...member, x: px(20, k) }; // 20 screen px apart: both in reach of a tap between them
      const nearA = px(8, k), nearB = px(12, k);
      expect(nearestNodeAt([a, b], nearA, 0, k)).toBe(a);
      expect(nearestNodeAt([b, a], nearA, 0, k)).toBe(a);
      expect(nearestNodeAt([a, b], nearB, 0, k)).toBe(b);
      expect(nearestNodeAt([b, a], nearB, 0, k)).toBe(b);
    }
  });

  it('leaves workflow panels out (they are hit as rectangles)', () => {
    const panel: N = { key: 'workflows:payments', kind: 'workflows', r: 40, x: 0, y: 0 };
    expect(nearestNodeAt([panel], 0, 0, 0.2)).toBeNull();
    expect(nearestNodeAt([panel, hub], 0, 0, 0.2)).toBe(hub);
  });
});

describe('mapBeatsWorkflow', () => {
  const wf = { r: 10, x: 0, y: 0 };
  it('a workflow square under its map folder still takes the hover and the click on the square', () => {
    // The folder a few world px off the square's middle: a point on the square goes to the workflow, even nearer the folder.
    expect(mapBeatsWorkflow(0, wf, 4, 0)).toBe(false);
    expect(mapBeatsWorkflow(2, wf, 6, 0)).toBe(false);
    expect(mapBeatsWorkflow(1, wf, 0, 0)).toBe(false);
  });
  it('off the square, the nearer of the two wins; with no workflow in reach the map node does', () => {
    expect(mapBeatsWorkflow(3, wf, 20, 0)).toBe(true);
    expect(mapBeatsWorkflow(25, wf, 20, 0)).toBe(false);
    expect(mapBeatsWorkflow(5, null, 0, 0)).toBe(true);
  });
});
