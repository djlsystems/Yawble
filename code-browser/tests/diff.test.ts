import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import { hunkTitle, normalizeHunks, sideBySide, type DiffWireHunk } from '../src/diff';
import DiffView from '../src/DiffView.vue';

// B072: the read-only diff viewer the Commit view opens a file in, fed git.commitDiff's hunks as the core sends them
// (kind context / added / deleted; old / new only on the side(s) a line is on; git's own header line).

const hunk: DiffWireHunk = { header: '@@ -10,4 +10,5 @@ class Refund', oldStart: 10, oldCount: 4, newStart: 10, newCount: 5, lines: [
  { kind: 'context', old: 10, new: 10, text: 'a' }, { kind: 'deleted', old: 11, text: 'b' }, { kind: 'deleted', old: 12, text: 'c' },
  { kind: 'added', new: 11, text: 'B' }, { kind: 'added', new: 12, text: 'C' }, { kind: 'added', new: 13, text: 'D' }, { kind: 'context', old: 13, new: 14, text: 'e' },
] };

describe('diff rules', () => {
  it('keeps the core\'s numbers, each line only on its own side(s)', () => {
    const [h] = normalizeHunks({ hunks: [hunk] });
    expect(h.lines.map((l) => [l.kind, l.oldLine ?? null, l.newLine ?? null])).toEqual([
      [' ', 10, 10], ['-', 11, null], ['-', 12, null], ['+', null, 11], ['+', null, 12], ['+', null, 13], [' ', 13, 14],
    ]);
    expect(h.title).toBe('@@ -10,4 +10,5 @@ class Refund');
  });

  it('a missing number is counted on from the hunk start or the line before', () => {
    const [h] = normalizeHunks({ hunks: [{ ...hunk, header: '', lines: [{ kind: 'context', old: 40, new: 41, text: 'x' }, { kind: 'added', text: 'y' }, { kind: 'deleted', text: 'z' }] }] });
    expect(h.lines.map((l) => [l.oldLine ?? null, l.newLine ?? null])).toEqual([[40, 41], [null, 42], [41, null]]);
  });

  it('no hunks (binary, too large, a pure rename) is nothing to show', () => {
    expect(normalizeHunks({ hunks: [] })).toEqual([]);
    expect(normalizeHunks({})).toEqual([]);
  });

  it('side by side pairs a deleted run with the added run after it', () => {
    const rows = sideBySide(normalizeHunks({ hunks: [hunk] })[0]);
    expect(rows.map((r) => [r.left?.text ?? null, r.right?.text ?? null])).toEqual([['a', 'a'], ['b', 'B'], ['c', 'C'], [null, 'D'], ['e', 'e']]);
    expect(rows[3].right).toEqual({ num: 13, text: 'D', kind: '+' });
  });

  it('a deletion with nothing added leaves the right side empty', () => {
    const rows = sideBySide(normalizeHunks({ hunks: [{ header: '@@ -1,2 +1,1 @@', oldStart: 1, oldCount: 2, newStart: 1, newCount: 1,
      lines: [{ kind: 'deleted', old: 1, text: 'gone' }, { kind: 'context', old: 2, new: 1, text: 'kept' }] }] })[0]);
    expect(rows[0]).toEqual({ left: { num: 1, text: 'gone', kind: '-' }, right: null });
  });

  it('hunk title: git\'s header as sent; made from the numbers when there is none', () => {
    expect(hunkTitle(hunk)).toBe('@@ -10,4 +10,5 @@ class Refund');
    expect(hunkTitle({ ...hunk, header: null })).toBe('@@ -10,4 +10,5 @@');
    expect(hunkTitle({ ...hunk, header: 'class Refund' })).toBe('@@ -10,4 +10,5 @@ class Refund');
  });
});

describe('DiffView', () => {
  it('shows side by side by default and inline on request, text escaped', async () => {
    const w = mount(DiffView, { props: { diff: { path: 'a.ts', binary: false, tooLarge: false, hunks: [{ ...hunk, lines: [{ kind: 'added', new: 10, text: '<img src=x onerror=alert(1)>' }] }] } } });
    expect(w.find('[data-test=diff-split]').exists()).toBe(true);
    expect(w.get('[data-test=diff-hunk]').text()).toBe('@@ -10,4 +10,5 @@ class Refund');
    expect(w.html()).not.toContain('<img');
    await w.get('[data-test=diff-mode-inline]').trigger('click');
    expect(w.find('[data-test=diff-inline]').exists()).toBe(true);
    expect(w.emitted('update:mode')?.[0]).toEqual(['inline']);
    expect(w.get('[data-test=diff-line]').text()).toContain('<img src=x onerror=alert(1)>');
    expect(w.get('[data-test=diff-line]').attributes('data-kind')).toBe('+');
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
