import { describe, expect, it } from 'vitest';
import {
  clampLaneWidth,
  DefaultLaneWidth,
  LaneWidthStorageKey,
  loadLaneWidth,
  MaxLaneWidth,
  MinLaneWidth,
  saveLaneWidth,
} from '../kanbanLaneWidth';

function memory(initial: Record<string, string> = {}) {
  const data = new Map(Object.entries(initial));
  return {
    data,
    getItem: (key: string) => data.get(key) ?? null,
    setItem: (key: string, value: string) => void data.set(key, value),
  };
}

describe('the Kanban lane width', () => {
  it('starts at 300px', () => {
    expect(DefaultLaneWidth).toBe(300);
    expect(loadLaneWidth(memory())).toBe(300);
  });

  it('keeps a width inside its limits and rounds it', () => {
    expect(clampLaneWidth(50)).toBe(MinLaneWidth);
    expect(clampLaneWidth(5000)).toBe(MaxLaneWidth);
    expect(clampLaneWidth(333.6)).toBe(334);
    expect(clampLaneWidth(Number.NaN)).toBe(DefaultLaneWidth);
  });

  it('remembers a width and reads it back', () => {
    const storage = memory();
    saveLaneWidth(420, storage);
    expect(storage.data.get(LaneWidthStorageKey)).toBe('420');
    expect(loadLaneWidth(storage)).toBe(420);
  });

  it('reads the default for something it did not write, and a stored width outside the limits clamped', () => {
    expect(loadLaneWidth(memory({ [LaneWidthStorageKey]: 'wide' }))).toBe(DefaultLaneWidth);
    expect(loadLaneWidth(memory({ [LaneWidthStorageKey]: '9000' }))).toBe(MaxLaneWidth);
  });

  it('never throws when storage does', () => {
    const throwing = {
      getItem: () => { throw new Error('blocked'); },
      setItem: () => { throw new Error('blocked'); },
    };
    expect(loadLaneWidth(throwing)).toBe(DefaultLaneWidth);
    expect(() => saveLaneWidth(400, throwing)).not.toThrow();
  });
});
