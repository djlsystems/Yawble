import { describe, expect, it } from 'vitest';
import { wheelZoom, zoom } from '../../src/main/zoom';

function fakeContents(level = 0) {
  const listeners: ((e: unknown, direction: string) => void)[] = [];
  return {
    level,
    getZoomLevel() { return this.level; },
    setZoomLevel(l: number) { this.level = l; },
    on(_event: 'zoom-changed', l: (e: unknown, direction: string) => void) { listeners.push(l); return this; },
    wheel(direction: string) { for (const l of listeners) l({}, direction); },
  };
}

describe('zoom', () => {
  it('steps half a level, stays between -3 and 5, and resets to actual size', () => {
    const wc = fakeContents();
    zoom(wc, 1); expect(wc.level).toBe(0.5);
    zoom(wc, -1); zoom(wc, -1); expect(wc.level).toBe(-0.5);
    for (let i = 0; i < 20; i++) zoom(wc, 1);
    expect(wc.level).toBe(5);
    for (let i = 0; i < 30; i++) zoom(wc, -1);
    expect(wc.level).toBe(-3);
    zoom(wc, null); expect(wc.level).toBe(0);
  });

  it('Ctrl + mouse wheel zooms in and out the same way as the View menu', () => {
    const wc = fakeContents();
    wheelZoom(wc);
    wc.wheel('in'); wc.wheel('in'); expect(wc.level).toBe(1);
    wc.wheel('out'); expect(wc.level).toBe(0.5);
    wc.wheel('sideways'); expect(wc.level).toBe(0.5);
  });
});
