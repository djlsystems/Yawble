import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { graphShape, MaxZoom, MinZoom, pinchCamera, teamAngle } from '../../src/renderer/src/monitor/scene';
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

describe('where the teams sit on the wall (scene.ts teamAngle)', () => {
  const at = (i: number, n: number) => {
    const a = teamAngle(i, n);
    return { x: Math.round(Math.cos(a) * 100) + 0, y: Math.round(Math.sin(a) * 100) + 0 }; // + 0: no -0
  };
  it('two teams sit side by side (left, right), not one above the other', () => {
    expect(at(0, 2)).toEqual({ x: -100, y: 0 });
    expect(at(1, 2)).toEqual({ x: 100, y: 0 });
  });
  it('the first team is on the left, and one team alone is at the middle', () => {
    expect(at(0, 4)).toEqual({ x: -100, y: 0 });
    expect(at(2, 4)).toEqual({ x: 100, y: 0 });
    expect(teamAngle(0, 1)).toBe(0);
  });
});

describe('a two-finger pinch on the wall (scene.ts pinchCamera)', () => {
  const start = { dist: 100, k: 1, wx: 50, wy: -20 };
  it('spreading the fingers to twice their distance doubles the zoom, keeping the world point between them', () => {
    const cam = pinchCamera(start, 200, [400, 300], 800, 600);
    expect(cam.k).toBe(2);
    // The world point under the middle of the fingers (the wall's centre here) is still the start's.
    expect(cam.x).toBe(50);
    expect(cam.y).toBe(-20);
  });
  it('moving both fingers together pans: the start point follows them', () => {
    const cam = pinchCamera(start, 100, [500, 300], 800, 600); // the fingers moved 100 px right
    expect(cam.k).toBe(1);
    expect(cam.x).toBe(50 - 100); // the camera's centre is 100 world units left of the point now under the fingers
  });
  it('stays within the zoom limits, and a zero spread is not a division by zero', () => {
    expect(pinchCamera(start, 10_000, [400, 300], 800, 600).k).toBe(MaxZoom);
    expect(pinchCamera(start, 0, [400, 300], 800, 600).k).toBe(MinZoom);
  });
});

describe('the default layout', () => {
  it('is the tuned set', () => {
    expect(LayoutDefaults).toEqual({ distance: 0.3, strength: 2.75, repulsion: 0.35, gravity: 2.4, friction: 0.45, spacing: 1 });
    for (const v of Object.values(LayoutDefaults)) expect(clampLayout(v)).toBe(v); // each on the sliders' range and step
  });
});

describe('a poll that changes nothing does not wake the forces (scene.ts graphShape)', () => {
  const n = (key: string, over: Partial<{ r: number; homeX: number; homeY: number }> = {}) => ({ key, r: 14, homeX: 10, homeY: -5, ...over });
  it('is the same for the same nodes, whatever they say', () => {
    expect(graphShape([n('a'), n('b')])).toBe(graphShape([{ ...n('a'), state: 'running' } as any, n('b')]));
  });
  it('changes when a node comes or goes, or its size or home moves', () => {
    const base = graphShape([n('a'), n('b')]);
    expect(graphShape([n('a')])).not.toBe(base);
    expect(graphShape([n('a'), n('b'), n('c')])).not.toBe(base);
    expect(graphShape([n('a'), n('b', { r: 17 })])).not.toBe(base);
    expect(graphShape([n('a'), n('b', { homeX: 40 })])).not.toBe(base);
  });
  it('the scene returns from a rebuild early when the shape is unchanged', () => {
    const src = readFileSync(resolve(__dirname, '../../src/renderer/src/monitor/scene.ts'), 'utf8');
    // Card 411: rebuild(wake = 0.3); settleSpacing's own moves wake it less.
    expect(src).toMatch(/rebuild\(wake = 0\.3\)[\s\S]*if \(shape === this\.shape\) return;[\s\S]*this\.sim\.alpha\(Math\.max\(this\.sim\.alpha\(\), wake\)\)/);
  });
});
