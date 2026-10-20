// THE BOARD'S ARRANGEMENT RULES, without a mount: the order the cards are laid in, where a moved
// card lands, and where a card let go at a point goes. The board wires these (board-arrange.mount.spec.ts).
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  arrange,
  boardOrderKey,
  dropBefore,
  movedBy,
  placed,
  readBoardOrder,
  storedBoardOrderKeys,
  writeBoardOrder,
  type CardBox,
} from '../boardOrder';

const m = (id: string) => ({ id });

describe('arrange', () => {
  it('lays the named members first in their order, then the rest in roster order', () => {
    const roster = [m('Manager'), m('Builder'), m('Tester'), m('Writer')];
    expect(arrange(roster, ['Tester', 'Manager']).map((x) => x.id)).toEqual(['Tester', 'Manager', 'Builder', 'Writer']);
  });

  it('passes over an id the roster lacks and counts a repeated id once', () => {
    const roster = [m('Manager'), m('Builder')];
    expect(arrange(roster, ['Gone', 'Builder', 'Builder']).map((x) => x.id)).toEqual(['Builder', 'Manager']);
  });

  it('with no order is the roster', () => {
    expect(arrange([m('a'), m('b')], []).map((x) => x.id)).toEqual(['a', 'b']);
  });
});

describe('placed', () => {
  const roster = ['a', 'b', 'c', 'd'];

  it('puts the moved card before the one named, over the current arrangement', () => {
    expect(placed(roster, [], 'd', 'b')).toEqual(['a', 'd', 'b', 'c']);
    expect(placed(roster, ['c', 'a'], 'b', 'a')).toEqual(['c', 'b', 'a', 'd']);
  });

  it('null puts it last, and the answer names every member so a removed one is dropped', () => {
    expect(placed(roster, ['gone', 'b'], 'a', null)).toEqual(['b', 'c', 'd', 'a']);
  });

  it('dropping a card on its own place, or a card not on the roster, changes nothing', () => {
    expect(placed(roster, [], 'b', 'b')).toEqual(roster);
    expect(placed(roster, [], 'zzz', 'b')).toEqual(roster);
    expect(placed(roster, [], 'a', 'nope')).toEqual(['b', 'c', 'd', 'a']);
  });
});

describe('movedBy', () => {
  const roster = ['a', 'b', 'c'];

  it('moves a card a number of places and clamps at the ends', () => {
    expect(movedBy(roster, [], 'a', 1)).toEqual(['b', 'a', 'c']);
    expect(movedBy(roster, [], 'c', -1)).toEqual(['a', 'c', 'b']);
    expect(movedBy(roster, [], 'a', Number.POSITIVE_INFINITY)).toEqual(['b', 'c', 'a']);
    expect(movedBy(roster, [], 'c', Number.NEGATIVE_INFINITY)).toEqual(['c', 'a', 'b']);
  });

  it('at an end already, or by zero, changes nothing', () => {
    expect(movedBy(roster, [], 'a', -1)).toEqual(roster);
    expect(movedBy(roster, [], 'b', 0)).toEqual(roster);
  });
});

describe('dropBefore', () => {
  // Two rows of two 100x100 cards with a 16px gutter: a (0..100), b (116..216); c, d below at y 116..216.
  const box = (id: string, left: number, top: number): CardBox => ({ id, left, top, right: left + 100, bottom: top + 100 });
  const boxes = [box('a', 0, 0), box('b', 116, 0), box('c', 0, 116), box('d', 116, 116)];

  it("goes before a card on its left half and after it (before the next) on its right half", () => {
    expect(dropBefore(boxes, 20, 50)).toBe('a');
    expect(dropBefore(boxes, 80, 50)).toBe('b');
    expect(dropBefore(boxes, 200, 50)).toBe('c'); // after b: before the card that begins the next row
    expect(dropBefore(boxes, 200, 150)).toBeNull(); // after the last card
  });

  it('in a gutter goes after the card to its left, and left of the row before its first card', () => {
    expect(dropBefore(boxes, 108, 50)).toBe('b');
    expect(dropBefore(boxes, -10, 150)).toBe('c');
    expect(dropBefore(boxes, 300, 50)).toBe('c'); // past the row's end: after b
  });

  it('above every row goes first, below every row last, and between rows to the nearer', () => {
    expect(dropBefore(boxes, 150, -20)).toBe('a');
    expect(dropBefore(boxes, 20, 400)).toBeNull();
    expect(dropBefore(boxes, 20, 103)).toBe('a'); // nearer the first row: on a's left half
    expect(dropBefore(boxes, 20, 113)).toBe('c'); // nearer the second row
  });

  it('with no other card is the end', () => {
    expect(dropBefore([], 10, 10)).toBeNull();
  });
});

describe('storage', () => {
  let stored: Record<string, string>;

  beforeEach(() => {
    stored = {};
    vi.stubGlobal('localStorage', {
      getItem: (k: string) => (k in stored ? stored[k] : null),
      setItem: (k: string, v: string) => { stored[k] = v; },
      removeItem: (k: string) => { delete stored[k]; },
      key: (i: number) => Object.keys(stored)[i] ?? null,
      get length() { return Object.keys(stored).length; },
    });
  });

  afterEach(() => vi.unstubAllGlobals());

  it('is one key per team, read back as written, and removed when the order is empty', () => {
    writeBoardOrder('alpha', ['b', 'a']);
    expect(stored[boardOrderKey('alpha')]).toBe('["b","a"]');
    expect(readBoardOrder('alpha')).toEqual(['b', 'a']);
    expect(readBoardOrder('beta')).toEqual([]);
    writeBoardOrder('alpha', []);
    expect(boardOrderKey('alpha') in stored).toBe(false);
  });

  it('reads a value that is not a list of strings as no order, and lists only its own keys', () => {
    stored[boardOrderKey('alpha')] = '{"a":1}';
    stored[boardOrderKey('beta')] = '["x", 3, "y"]';
    stored['harness.boardSize'] = '{}';
    expect(readBoardOrder('alpha')).toEqual([]);
    expect(readBoardOrder('beta')).toEqual(['x', 'y']);
    expect(storedBoardOrderKeys().sort()).toEqual([boardOrderKey('alpha'), boardOrderKey('beta')]);
  });

  it('a storage that throws reads as no order and writes nothing, with no crash', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => { throw new Error('blocked'); },
      setItem: () => { throw new Error('blocked'); },
      removeItem: () => { throw new Error('blocked'); },
      key: () => { throw new Error('blocked'); },
      get length(): number { throw new Error('blocked'); },
    });
    expect(readBoardOrder('alpha')).toEqual([]);
    expect(() => writeBoardOrder('alpha', ['a'])).not.toThrow();
    expect(storedBoardOrderKeys()).toEqual([]);
  });
});
