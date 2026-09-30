import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { boundsHint, boundsProblem } from '../numberBounds';

describe('boundsHint', () => {
  it('says each shape of bound, and nothing for none', () => {
    expect(boundsHint({ min: 0, max: 10 })).toBe('Between 0 and 10.');
    expect(boundsHint({ min: 1 })).toBe('At least 1.');
    expect(boundsHint({ max: 5, integer: true })).toBe('At most 5, whole numbers only.');
    expect(boundsHint({ integer: true })).toBe('Whole numbers only.');
    expect(boundsHint({})).toBeNull();
    expect(boundsHint({ min: null, max: null })).toBeNull();
  });
});

describe('boundsProblem', () => {
  it('names the field and the bound', () => {
    expect(boundsProblem('salaryMax', { min: 0 }, '-2')).toBe('-2 is out of range for salaryMax: it must be at least 0.');
    expect(boundsProblem('days', { max: 365 }, 400)).toBe('400 is out of range for days: it must be at most 365.');
    expect(boundsProblem('days', { integer: true }, '2.5')).toBe('days takes whole numbers only; 2.5 is not one.');
  });

  it('passes a value inside the bounds, on the bounds, blank, or not a number (the Host names that)', () => {
    expect(boundsProblem('days', { min: 1, max: 365, integer: true }, '1')).toBeNull();
    expect(boundsProblem('days', { min: 1, max: 365, integer: true }, 365)).toBeNull();
    expect(boundsProblem('days', { min: 1 }, '')).toBeNull();
    expect(boundsProblem('days', { min: 1 }, null)).toBeNull();
    expect(boundsProblem('days', { min: 1 }, 'abc')).toBeNull();
  });
});

describe('the wheel guard', () => {
  it('is installed once, at boot, for every number box - no form opts in', () => {
    const config = readFileSync(resolve(__dirname, '../../../quasar.config.ts'), 'utf8');
    expect(config).toMatch(/boot:\s*\[[^\]]*'numberWheel'/);
  });
});
