import { describe, expect, it } from 'vitest';
import { ariaSort, nextSort, readSort, type TableSort } from '../tableSort';

/** The generic half of every sortable table: Teams and the Documents explorer both use it. */
type Column = 'name' | 'size';
const Columns: Column[] = ['name', 'size'];
const Fallback: TableSort<Column> = { column: 'name', descending: false };

describe('nextSort', () => {
  it('flips the same column and starts a new one ascending', () => {
    expect(nextSort({ column: 'name', descending: false }, 'name')).toEqual({ column: 'name', descending: true });
    expect(nextSort({ column: 'name', descending: true }, 'size')).toEqual({ column: 'size', descending: false });
  });
});

describe('readSort', () => {
  it('reads back a stored order', () => {
    expect(readSort('{"column":"size","descending":true}', Columns, Fallback)).toEqual({ column: 'size', descending: true });
  });

  it.each([
    ['nothing stored', null],
    ['bad JSON', '{'],
    ['null', 'null'],
    ['an unknown column', '{"column":"colour","descending":false}'],
    ['a direction that is not a boolean', '{"column":"name","descending":"yes"}'],
  ])('falls back for %s', (_name, raw) => {
    expect(readSort(raw, Columns, Fallback)).toEqual(Fallback);
  });
});

describe('ariaSort', () => {
  it('says the direction on the sorted column and none elsewhere', () => {
    expect(ariaSort({ column: 'size', descending: true }, 'size')).toBe('descending');
    expect(ariaSort({ column: 'size', descending: false }, 'size')).toBe('ascending');
    expect(ariaSort({ column: 'size', descending: false }, 'name')).toBe('none');
  });
});
