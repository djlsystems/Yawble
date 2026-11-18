import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import { hunkTitle, normalizeHunks, sideBySide, type DiffHunk } from '../src/diff';
import DiffView from '../src/DiffView.vue';

// B072: the read-only diff viewer the Commit view opens a file in.

const hunk: DiffHunk = { oldStart: 10, oldLines: 4, newStart: 10, newLines: 5, header: 'class Refund', lines: [
  { kind: ' ', text: 'a' }, { kind: '-', text: 'b' }, { kind: '-', text: 'c' }, { kind: '+', text: 'B' }, { kind: '+', text: 'C' }, { kind: '+', text: 'D' }, { kind: ' ', text: 'e' },
] };

describe('diff rules', () => {
  it('numbers every line from the hunk starts, keeping numbers the core sent', () => {
    const [h] = normalizeHunks({ hunks: [hunk] });
    expect(h.lines.map((l) => [l.kind, l.oldLine ?? null, l.newLine ?? null])).toEqual([
      [' ', 10, 10], ['-', 11, null], ['-', 12, null], ['+', null, 11], ['+', null, 12], ['+', null, 13], [' ', 13, 14],
    ]);
    const [kept] = normalizeHunks({ hunks: [{ ...hunk, lines: [{ kind: ' ', text: 'x', oldLine: 40, newLine: 41 }, { kind: '+', text: 'y' }] }] });
    expect(kept.lines[1].newLine).toBe(42);
  });

  it('a flat line list becomes one hunk; nothing is no hunk', () => {
    expect(normalizeHunks({ lines: [{ kind: '+', text: 'new' }] })[0].lines[0]).toEqual({ kind: '+', text: 'new', newLine: 1 });
    expect(normalizeHunks({ hunks: [] })).toEqual([]);
    expect(normalizeHunks({})).toEqual([]);
  });

  it('side by side pairs a removed run with the added run after it', () => {
    const rows = sideBySide(normalizeHunks({ hunks: [hunk] })[0]);
    expect(rows.map((r) => [r.left?.text ?? null, r.right?.text ?? null])).toEqual([['a', 'a'], ['b', 'B'], ['c', 'C'], [null, 'D'], ['e', 'e']]);
    expect(rows[3].right).toEqual({ num: 13, text: 'D', kind: '+' });
  });

  it('a removal with nothing added leaves the right side empty', () => {
    const rows = sideBySide(normalizeHunks({ hunks: [{ oldStart: 1, oldLines: 2, newStart: 1, newLines: 1, lines: [{ kind: '-', text: 'gone' }, { kind: ' ', text: 'kept' }] }] })[0]);
    expect(rows[0]).toEqual({ left: { num: 1, text: 'gone', kind: '-' }, right: null });
  });

  it('hunk title', () => {
    expect(hunkTitle(hunk)).toBe('@@ -10,4 +10,5 @@ class Refund');
  });
});

describe('DiffView', () => {
  it('shows side by side by default and inline on request, text escaped', async () => {
    const w = mount(DiffView, { props: { diff: { path: 'a.ts', binary: false, tooLarge: false, hunks: [{ ...hunk, lines: [{ kind: '+', text: '<img src=x onerror=alert(1)>' }] }] } } });
    expect(w.find('[data-test=diff-split]').exists()).toBe(true);
    expect(w.html()).not.toContain('<img');
    await w.get('[data-test=diff-mode-inline]').trigger('click');
    expect(w.find('[data-test=diff-inline]').exists()).toBe(true);
    expect(w.emitted('update:mode')?.[0]).toEqual(['inline']);
    expect(w.get('[data-test=diff-line]').text()).toContain('<img src=x onerror=alert(1)>');
  });

  it('binary and too large: the summary line only', () => {
    const b = mount(DiffView, { props: { diff: { path: 'logo.png', binary: true, tooLarge: false, summary: null, hunks: [] } } });
    expect(b.get('[data-test=diff-summary]').text()).toBe('Binary file: its content is not shown.');
    const l = mount(DiffView, { props: { diff: { path: 'x.json', binary: false, tooLarge: true, summary: 'Too large: 9 MB', hunks: [] } } });
    expect(l.get('[data-test=diff-summary]').text()).toBe('Too large: 9 MB');
    expect(l.find('[data-test=diff-mode-split]').exists()).toBe(false);
  });

  it('a mode-only change says no line changed', () => {
    const w = mount(DiffView, { props: { diff: { path: 'run.sh', binary: false, tooLarge: false, hunks: [] } } });
    expect(w.get('[data-test=diff-empty]').text()).toContain('No line changed');
  });
});
