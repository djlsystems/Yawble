import { describe, expect, it } from 'vitest';
import {
  clampLayout, isDefaultLayout, LayoutDefaults, LayoutMax, LayoutMin, LayoutStorageKey, readLayout, writeLayout,
} from '../../src/renderer/src/monitor/layout';

/** A storage that holds one map, or refuses every call. */
function store(refuse = false) {
  const map = new Map<string, string>();
  const no = () => { throw new Error('refused'); };
  return {
    map,
    getItem: refuse ? no : (k: string) => map.get(k) ?? null,
    setItem: refuse ? no : (k: string, v: string) => void map.set(k, v),
    removeItem: refuse ? no : (k: string) => void map.delete(k),
  };
}

describe('the Monitor layout forces (monitor/layout.ts)', () => {
  it('clamps a value into the range, to the step, and falls back for what is not a number', () => {
    expect(clampLayout(1.83)).toBe(1.85);
    expect(clampLayout('0.5')).toBe(0.5);
    expect(clampLayout(99)).toBe(LayoutMax);
    expect(clampLayout(0)).toBe(LayoutMin);
    expect(clampLayout('abc', 1)).toBe(1);
    expect(clampLayout('', 1)).toBe(1);
    expect(clampLayout(Number.NaN, 2)).toBe(2);
  });

  it('reads what was written, checks each value, and keeps the defaults for anything missing or broken', () => {
    const s = store();
    writeLayout({ ...LayoutDefaults, gravity: 2.5, friction: 0.4 }, s);
    expect(readLayout(s)).toEqual({ ...LayoutDefaults, gravity: 2.5, friction: 0.4 });
    s.map.set(LayoutStorageKey, JSON.stringify({ distance: 50, repulsion: 'x', unknown: 3 }));
    expect(readLayout(s)).toEqual({ ...LayoutDefaults, distance: LayoutMax });
    s.map.set(LayoutStorageKey, '{not json');
    expect(readLayout(s)).toEqual(LayoutDefaults);
  });

  it('writing the defaults forgets the setting; a storage that refuses changes nothing and throws nothing', () => {
    const s = store();
    writeLayout({ ...LayoutDefaults, strength: 2 }, s);
    expect(s.map.has(LayoutStorageKey)).toBe(true);
    writeLayout({ ...LayoutDefaults }, s);
    expect(s.map.has(LayoutStorageKey)).toBe(false);
    const refusing = store(true);
    expect(() => writeLayout({ ...LayoutDefaults, strength: 2 }, refusing)).not.toThrow();
    expect(readLayout(refusing)).toEqual(LayoutDefaults);
    expect(readLayout(null)).toEqual(LayoutDefaults);
    expect(isDefaultLayout(LayoutDefaults)).toBe(true);
    expect(isDefaultLayout({ ...LayoutDefaults, friction: 1.05 })).toBe(false);
  });
});
