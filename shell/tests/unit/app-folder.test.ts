import { describe, expect, it } from 'vitest';
import { appFolder } from '../../src/main/app-folder';

describe('appFolder', () => {
  it('uses local app data on Windows', () => {
    expect(appFolder('win32', { LOCALAPPDATA: 'C:\\Users\\a\\AppData\\Local' }, 'C:\\Users\\a'))
      .toBe('C:\\Users\\a\\AppData\\Local\\Yawble');
  });

  it('falls back to the home folder on Windows without LOCALAPPDATA', () => {
    expect(appFolder('win32', {}, 'C:\\Users\\a')).toBe('C:\\Users\\a\\AppData\\Local\\Yawble');
  });

  it('uses Application Support on macOS', () => {
    expect(appFolder('darwin', {}, '/Users/a')).toBe('/Users/a/Library/Application Support/Yawble');
  });

  it('uses the XDG data folder on Linux', () => {
    expect(appFolder('linux', {}, '/home/a')).toBe('/home/a/.local/share/yawble');
    expect(appFolder('linux', { XDG_DATA_HOME: '/data' }, '/home/a')).toBe('/data/yawble');
  });
});
