// One file's diff, as the core's git.commitDiff sends it (core TeamRepos.CommitView.cs CommitFileDiff), and the
// pure rules the read-only diff viewer (DiffView.vue) shows it by: inline (one column, old and new numbers) or side by
// side (old left, new right, a run of deleted lines paired with the added run that follows it).

/**
 * A line as the core sends it: kind context, added or deleted; old / new: its 1-based line number on each side, present
 * only on the side(s) the line is on.
 */
export interface DiffWireLine { kind: 'context' | 'added' | 'deleted'; old?: number | null; new?: number | null; text: string }
/** A hunk as the core sends it: header is git's own "@@ -a,b +c,d @@ ..." line. */
export interface DiffWireHunk { header?: string | null; oldStart: number; oldCount: number; newStart: number; newCount: number; lines: DiffWireLine[] }

/** A line as the viewer shows it: ' ' context, '+' added, '-' deleted; oldLine / newLine set on the side(s) it is on. */
export interface DiffLine { kind: ' ' | '+' | '-'; text: string; oldLine?: number; newLine?: number }
export interface DiffHunk { title: string; lines: DiffLine[] }

/** One row of the side-by-side view: the old side, the new side, or both; null is an empty cell. */
export interface DiffCell { num: number; text: string; kind: ' ' | '+' | '-' }
export interface DiffRow { left: DiffCell | null; right: DiffCell | null }

const KIND: Record<string, DiffLine['kind']> = { context: ' ', added: '+', deleted: '-' };

/**
 * The core's hunks as the viewer shows them, every line numbered: the numbers the core sent are kept, a missing one is
 * counted on from the hunk's start (or the line before). A binary, too-large or pure-rename diff has no hunks.
 */
export function normalizeHunks(diff: { hunks?: DiffWireHunk[] | null }): DiffHunk[] {
  return (diff.hunks ?? []).map((h) => {
    let o = h.oldStart || 1;
    let n = h.newStart || 1;
    const lines = (h.lines ?? []).map((l): DiffLine => {
      const kind = KIND[l.kind] ?? ' ';
      const line: DiffLine = { kind, text: l.text ?? '' };
      if (kind !== '+') { line.oldLine = l.old ?? o; o = line.oldLine + 1; }
      if (kind !== '-') { line.newLine = l.new ?? n; n = line.newLine + 1; }
      return line;
    });
    return { title: hunkTitle(h), lines };
  });
}

/** A hunk's rows side by side: context on both sides; each run of deleted lines beside the added run right after it. */
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

/** A hunk's separator line: git's own header as the core sent it, else "@@ -12,5 +12,7 @@" made from its numbers. */
export function hunkTitle(h: DiffWireHunk): string {
  const header = h.header?.trim();
  if (header?.startsWith('@@')) return header;
  const made = `@@ -${h.oldStart},${h.oldCount} +${h.newStart},${h.newCount} @@`;
  return header ? `${made} ${header}` : made;
}
