import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  DefaultDocumentsTree,
  DefaultDocumentsView,
  DocumentsSortKey,
  DocumentsTreeKey,
  DocumentsViewKey,
  readDocumentsSort,
  readDocumentsTree,
  readDocumentsView,
  writeDocumentsSort,
  writeDocumentsTree,
  writeDocumentsView,
} from '../documentsPrefs';
import { DefaultDocumentsSort } from '../documentsExplorer';

/**
 * WHAT THE EXPLORER REMEMBERS BESIDE ITS WINDOW: the view, the sort and the tree pane. Each reads
 * back what was written, and falls back to its default for anything else - including storage that
 * throws on read or on write. (The window itself is `windowGeometry.spec.ts`.)
 */
function useStorage(initial: Record<string, string> = {}) {
  const store = { ...initial };

  vi.stubGlobal('localStorage', {
    getItem: (k: string) => (k in store ? store[k] : null),
    setItem: (k: string, v: string) => {
      store[k] = v;
    },
  });

  return store;
}

function throwingStorage() {
  vi.stubGlobal('localStorage', {
    getItem: () => {
      throw new Error('blocked');
    },
    setItem: () => {
      throw new Error('blocked');
    },
  });
}

beforeEach(() => vi.unstubAllGlobals());

describe('the view', () => {
  it('reads back what was written', () => {
    useStorage();
    writeDocumentsView('tiles');

    expect(readDocumentsView()).toBe('tiles');
  });

  it.each([['an unknown view', '"gallery"'], ['bad JSON', '"tiles'], ['a number', '3']])('falls back for %s', (_name, raw) => {
    useStorage({ [DocumentsViewKey]: raw });

    expect(readDocumentsView()).toBe(DefaultDocumentsView);
  });

  it('survives storage that throws, both ways', () => {
    throwingStorage();

    expect(readDocumentsView()).toBe('details');
    expect(() => writeDocumentsView('list')).not.toThrow();
  });
});

describe('the sort', () => {
  it('reads back what was written', () => {
    useStorage();
    writeDocumentsSort({ column: 'size', descending: true });

    expect(readDocumentsSort()).toEqual({ column: 'size', descending: true });
  });

  it.each([
    ['an unknown column', '{"column":"colour","descending":false}'],
    ['bad JSON', '{"column":'],
    ['a direction that is not a boolean', '{"column":"size","descending":1}'],
  ])('falls back for %s', (_name, raw) => {
    useStorage({ [DocumentsSortKey]: raw });

    expect(readDocumentsSort()).toEqual(DefaultDocumentsSort);
  });

  it('survives storage that throws, both ways', () => {
    throwingStorage();

    expect(readDocumentsSort()).toEqual({ column: 'name', descending: false });
    expect(() => writeDocumentsSort({ column: 'kind', descending: false })).not.toThrow();
  });
});

describe('the tree pane', () => {
  it('reads back what was written', () => {
    useStorage();
    writeDocumentsTree({ open: false, width: 300 });

    expect(readDocumentsTree()).toEqual({ open: false, width: 300 });
  });

  it.each([
    ['a width below 160', '{"open":false,"width":20}', { open: false, width: 240 }],
    ['a width above 480', '{"open":true,"width":9000}', { open: true, width: 240 }],
    ['bad JSON', '{"open":', DefaultDocumentsTree],
    ['an array', '[true, 300]', DefaultDocumentsTree],
    ['open that is not a boolean', '{"open":"no","width":200}', { open: true, width: 200 }],
  ])('falls back for %s', (_name, raw, expected) => {
    useStorage({ [DocumentsTreeKey]: raw });

    expect(readDocumentsTree()).toEqual(expected);
  });

  it('survives storage that throws, both ways', () => {
    throwingStorage();

    expect(readDocumentsTree()).toEqual({ open: true, width: 240 });
    expect(() => writeDocumentsTree({ open: false, width: 200 })).not.toThrow();
  });
});
