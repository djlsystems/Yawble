import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { placeWindow, WindowStateFile, type Display, type SavedWindow } from '../../src/main/window-state';

// Two monitors side by side: the main one (1920x1080, taskbar 40 px) and a second to its right (2560x1440).
const main: Display = { id: 1, workArea: { x: 0, y: 0, width: 1920, height: 1040 } };
const right: Display = { id: 2, workArea: { x: 1920, y: 0, width: 2560, height: 1400 } };
const defaults = { width: 1200, height: 800 };
const onRight: SavedWindow = { bounds: { x: 2100, y: 100, width: 1600, height: 1000 }, maximized: false, displayId: 2, displayWorkArea: right.workArea };

describe('the main window opens where it was', () => {
  it('opens at its default size, centred on the main monitor, the first time', () => {
    expect(placeWindow(null, [main, right], main, defaults)).toEqual({ bounds: { x: 360, y: 120, width: 1200, height: 800 }, maximized: false, how: 'default' });
  });

  it('opens on the same monitor, place and size when nothing changed', () => {
    expect(placeWindow(onRight, [main, right], main, defaults)).toEqual({ bounds: onRight.bounds, maximized: false, how: 'saved' });
  });

  it('opens maximized again on the monitor it was maximized on', () => {
    const p = placeWindow({ ...onRight, maximized: true }, [main, right], main, defaults);
    expect(p.maximized).toBe(true);
    expect(p.bounds).toEqual(onRight.bounds); // the size it returns to when un-maximized
  });

  it('falls back to the default size on the main monitor when its monitor was unplugged', () => {
    expect(placeWindow(onRight, [main], main, defaults)).toEqual({ bounds: { x: 360, y: 120, width: 1200, height: 800 }, maximized: false, how: 'default' });
  });

  it('stays maximized, on the main monitor, when a maximized window lost its monitor', () => {
    const p = placeWindow({ ...onRight, maximized: true }, [main], main, defaults);
    expect(p).toMatchObject({ maximized: true, how: 'default' });
    expect(p.bounds.x).toBeLessThan(1920);
  });

  it('shrinks and moves inside its monitor when that monitor now has a lower resolution', () => {
    const smaller: Display = { id: 2, workArea: { x: 1920, y: 0, width: 1600, height: 860 } };
    const p = placeWindow(onRight, [main, smaller], main, defaults);
    expect(p.how).toBe('fitted');
    expect(p.bounds).toEqual({ x: 1920, y: 0, width: 1600, height: 860 });
  });

  it('pulls a window that hung partly off the edge back on screen', () => {
    const half: SavedWindow = { bounds: { x: 1500, y: 200, width: 1000, height: 700 }, maximized: false, displayId: 1, displayWorkArea: main.workArea };
    const p = placeWindow(half, [main], main, defaults);
    expect(p).toEqual({ bounds: { x: 920, y: 200, width: 1000, height: 700 }, maximized: false, how: 'fitted' });
  });

  it('finds the window on another monitor when the monitor ids were renumbered', () => {
    const renumbered: Display = { id: 7, workArea: right.workArea };
    expect(placeWindow(onRight, [main, renumbered], main, defaults)).toMatchObject({ bounds: onRight.bounds, how: 'fitted' });
  });

  it('uses the default when the saved place is off every screen (title bar out of reach)', () => {
    const lost: SavedWindow = { bounds: { x: 100, y: -900, width: 1200, height: 800 }, maximized: false, displayId: 1, displayWorkArea: main.workArea };
    expect(placeWindow(lost, [main, right], main, defaults).how).toBe('default');
  });

  it('never opens larger than 90% of a small main screen', () => {
    const small: Display = { id: 1, workArea: { x: 0, y: 0, width: 1280, height: 680 } };
    expect(placeWindow(null, [small], small, defaults).bounds).toEqual({ x: 64, y: 34, width: 1152, height: 612 });
  });
});

describe('window-state.json', () => {
  const dirs: string[] = [];
  afterEach(() => { for (const d of dirs.splice(0)) rmSync(d, { recursive: true, force: true }); });
  const tempFile = () => { const d = mkdtempSync(path.join(os.tmpdir(), 'yawble-win-')); dirs.push(d); return path.join(d, 'data', 'window-state.json'); };

  it('saves and reads back the place', () => {
    const f = new WindowStateFile(tempFile());
    f.write(onRight);
    expect(f.read()).toEqual(onRight);
  });

  it('reads a missing, damaged or partial file as nothing saved', () => {
    const file = tempFile();
    const f = new WindowStateFile(file);
    expect(f.read()).toBeNull();
    f.write(onRight);
    writeFileSync(file, '{not json');
    expect(f.read()).toBeNull();
    writeFileSync(file, JSON.stringify({ bounds: { x: 0, y: 0, width: 'wide', height: 10 }, displayId: 1, displayWorkArea: main.workArea }));
    expect(f.read()).toBeNull();
  });
});
