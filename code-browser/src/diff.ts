// One file's diff, as the core's git.commitDiff sends it (B072), and the pure rules the read-only diff viewer
// (DiffView.vue) shows it by: inline (one column, old and new numbers) or side by side (old left, new right, a run of
// removed lines paired with the added run that follows it).

/** ' ' context, '+' added, '-' removed. oldLine / newLine: the line's number on each side; absent on the side it is not on. */
export interface DiffLine { kind: ' ' | '+' | '-'; text: string; oldLine?: number | null; newLine?: number | null }
export interface DiffHunk { oldStart: number; oldLines: number; newStart: number; newLines: number; header?: string | null; lines: DiffLine[] }

/** One row of the side-by-side view: the old side, the new side, or both; null is an empty cell. */
export interface DiffCell { num: number; text: string; kind: ' ' | '+' | '-' }
export interface DiffRow { left: DiffCell | null; right: DiffCell | null }

/**
 * The hunks with every line numbered: numbers the core sent are kept, missing ones are counted from the hunk's starts.
 * A diff sent as a flat `lines` list (no hunks) becomes one hunk starting at line 1.
 */
export function normalizeHunks(diff: { hunks?: DiffHunk[] | null; lines?: DiffLine[] | null }): DiffHunk[] {
  const raw = diff.hunks?.length ? diff.hunks : diff.lines?.length ? [{ oldStart: 1, oldLines: 0, newStart: 1, newLines: 0, lines: diff.lines }] : [];
  return raw.map((h) => {
    let o = h.oldStart || 1;
    let n = h.newStart || 1;
    const lines = (h.lines ?? []).map((l): DiffLine => {
      const kind: DiffLine['kind'] = l.kind === '+' || l.kind === '-' ? l.kind : ' ';
      const line: DiffLine = { kind, text: l.text ?? '' };
      if (kind !== '+') { line.oldLine = l.oldLine ?? o; o = line.oldLine + 1; }
      if (kind !== '-') { line.newLine = l.newLine ?? n; n = line.newLine + 1; }
      return line;
    });
    return { ...h, lines };
  });
}

/** A hunk's rows side by side: context on both sides; each run of removed lines beside the added run right after it. */
export function sideBySide(hunk: DiffHunk): DiffRow[] {
  const rows: DiffRow[] = [];
  const lines = hunk.lines;
  let i = 0;
  while (i < lines.length) {
    const l = lines[i];
    if (l.kind === ' ') {
      rows.push({ left: { num: l.oldLine ?? 0, text: l.text, kind: ' ' }, right: { num: l.newLine ?? 0, text: l.text, kind: ' ' } });
      i++;
      continue;
    }
    const removed: DiffLine[] = [];
    const added: DiffLine[] = [];
    while (i < lines.length && lines[i].kind === '-') removed.push(lines[i++]);
    while (i < lines.length && lines[i].kind === '+') added.push(lines[i++]);
    for (let k = 0; k < Math.max(removed.length, added.length); k++) {
      const r = removed[k];
      const a = added[k];
      rows.push({ left: r ? { num: r.oldLine ?? 0, text: r.text, kind: '-' } : null, right: a ? { num: a.newLine ?? 0, text: a.text, kind: '+' } : null });
    }
  }
  return rows;
}

/** "@@ -12,5 +12,7 @@ header" for a hunk's separator line. */
export function hunkTitle(h: DiffHunk): string {
  return `@@ -${h.oldStart},${h.oldLines} +${h.newStart},${h.newLines} @@${h.header ? ` ${h.header}` : ''}`;
}
