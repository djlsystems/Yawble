import { describe, expect, it } from 'vitest';
import { flipShifts, flipTransform } from '../flip';

const boxes = (entries: Record<string, [number, number]>) =>
  new Map(Object.entries(entries).map(([id, [left, top]]) => [id, { left, top }]));

describe('the invert step', () => {
  /**
   * OLD MINUS NEW. The card is already laid out where it ends up, so the transform has to carry it
   * BACK to where it was before the transition returns it. The opposite sign is still an animation
   * and is exactly backwards - a card that moved right appears to fly left - which is why this
   * arithmetic is a function with a test rather than two lines in a template.
   */
  it('shifts a card back to where it was', () => {
    const shifts = flipShifts(boxes({ a: [100, 200] }), boxes({ a: [300, 260] }));

    expect(shifts.get('a')).toEqual({ dx: -200, dy: -60 });
  });

  /**
   * A card that did not move is ABSENT rather than present with a zero shift: the caller uses
   * membership to decide what to touch, and touching an unmoved card costs a style write and a
   * forced reflow on every refetch - which on a live board is every few seconds.
   */
  it('leaves out a card that did not move', () => {
    const shifts = flipShifts(boxes({ a: [10, 10] }), boxes({ a: [10, 10] }));

    expect(shifts.size).toBe(0);
  });

  /** A card that has only just appeared has nowhere to come from; TransitionGroup fades it in. */
  it('leaves out a card that was not there before', () => {
    const shifts = flipShifts(boxes({ a: [0, 0] }), boxes({ a: [0, 0], b: [0, 40] }));

    expect(shifts.has('b')).toBe(false);
  });

  /** A card that has gone has no node left to transform. */
  it('leaves out a card that is no longer there', () => {
    const shifts = flipShifts(boxes({ a: [0, 0], b: [0, 40] }), boxes({ a: [0, 0] }));

    expect(shifts.has('b')).toBe(false);
  });

  it('carries every card that moved, not just the first', () => {
    const shifts = flipShifts(
      boxes({ a: [0, 0], b: [0, 40], c: [0, 80] }),
      boxes({ a: [0, 40], b: [0, 40], c: [200, 0] }),
    );

    expect([...shifts.keys()].sort()).toEqual(['a', 'c']);
  });
});

describe('the transform', () => {
  it('is a two-axis translate, in pixels', () => {
    expect(flipTransform({ dx: -200, dy: 12 })).toBe('translate(-200px, 12px)');
  });
});
