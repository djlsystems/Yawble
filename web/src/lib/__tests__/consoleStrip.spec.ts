import { describe, expect, it } from 'vitest';
import { stripScrollToShow } from '../consoleStrip';

describe('the console strip keeps the active tab in view', () => {
  it('moves nothing when the tab is already in view', () => {
    expect(stripScrollToShow(0, 300, 120, 120)).toBe(0);
    expect(stripScrollToShow(100, 300, 120, 120)).toBe(100);
  });

  it('brings a tab past the end in just far enough to show it whole', () => {
    expect(stripScrollToShow(0, 300, 600, 120)).toBe(420);
  });

  it('brings a tab before the start back to its left edge', () => {
    expect(stripScrollToShow(420, 300, 120, 120)).toBe(120);
    expect(stripScrollToShow(420, 300, 0, 120)).toBe(0);
  });

  it('shows the start of a tab wider than the strip', () => {
    expect(stripScrollToShow(0, 300, 600, 500)).toBe(600);
  });

  it('moves nothing on a strip not laid out', () => {
    expect(stripScrollToShow(0, 0, 600, 120)).toBe(0);
  });
});
