import type { CommitRangeRef, DeletePreview, DeletePreviewCommit, MergePreview, TeamGitStatus } from '../../shared/api';

// B073 (card 465): the Git dialog's clickable counts and previews, in words (pure, so each case is tested without
// mounting). The RPCs are DeveloperTomas's (contract YAWBLE_TEAM_DOCS\B073\contract.md): git.commitRange,
// git.mergePreview, git.mergeDiff and git.deletePreview.

export const commits = (n: number) => `${n} commit${n === 1 ? '' : 's'}`;

/** Which count was clicked: the team branch ahead of / behind the base, a member branch ahead of the base, or the base
 * against origin's copy of it. */
export type CountKind = 'team-ahead' | 'team-behind' | 'member-ahead' | 'base-behind-origin' | 'base-ahead-of-origin';

export interface RangeAsk extends CommitRangeRef { title: string }

const head = (b: string) => `refs/heads/${b}`;
const originRef = (b: string) => `refs/remotes/origin/${b}`;

/** The git.commitRange a count opens and the list's heading: the exact pair git.status gave for that count (full IDs), else
 * (an older core) the branches' full ref names. */
export function rangeFor(s: TeamGitStatus, kind: CountKind, branch?: string): RangeAsk | null {
  const base = s.baseBranch;
  const r = s.ranges ?? null;
  const pick = (given: CommitRangeRef | null | undefined, from: string, to: string) => (given ? { from: given.from, to: given.to } : r ? null : { from, to });
  let pair: CommitRangeRef | null;
  let title: string;
  switch (kind) {
    case 'team-ahead': pair = pick(r?.teamAheadOfBase, head(base), head(s.teamBranch)); title = `${commits(s.teamAheadOfBase)} on ${s.teamBranch} not in ${base}`; break;
    case 'team-behind': pair = pick(r?.teamBehindBase, head(s.teamBranch), head(base)); title = `${commits(s.teamBehindBase)} on ${base} not in ${s.teamBranch}`; break;
    case 'member-ahead': {
      const m = s.members.find((x) => x.branch === branch);
      if (!m) return null;
      pair = m.aheadRange ? { from: m.aheadRange.from, to: m.aheadRange.to } : r && m.aheadRange === null ? null : { from: head(base), to: head(m.branch) };
      title = `${commits(m.aheadOfBase)} on ${m.branch} not in ${base}`;
      break;
    }
    case 'base-behind-origin': pair = pick(r?.baseBehindOrigin, head(base), originRef(base)); title = `${commits(s.baseBehindOrigin ?? 0)} on origin's ${base} not in yours`; break;
    case 'base-ahead-of-origin': pair = pick(r?.baseAheadOfOrigin, originRef(base), head(base)); title = `${commits(s.baseAheadOfOrigin ?? 0)} on your ${base} not on origin`; break;
  }
  return pair ? { ...pair, title } : null;
}

/** "and 12 more" under a list the core cut at its limit. */
export function moreText(shown: number, total: number): string | null {
  return total > shown ? `and ${total - shown} more` : null;
}

export interface MergePreviewView {
  tone: 'ok' | 'bad' | 'plain';
  sentence: string;
  /** Merge is offered only for an outcome that merges. */
  canMerge: boolean;
  commitsHeading: string | null;
  filesHeading: string | null;
}

/** What the merge confirm shows from git.mergePreview: the core's summary (else one made from the outcome), what lands. */
export function mergePreviewView(p: MergePreview): MergePreviewView {
  const canMerge = p.outcome === 'fast-forward' || p.outcome === 'merge';
  const fallback: Record<MergePreview['outcome'], string> = {
    'fast-forward': `${p.baseBranch} will fast-forward to ${p.teamBranch}.`,
    merge: `A merge commit will join ${p.teamBranch} into ${p.baseBranch} (no rebase).`,
    conflict: `${p.teamBranch} does not merge cleanly into ${p.baseBranch}: nothing would change.`,
    'up-to-date': `${p.baseBranch} already has everything on ${p.teamBranch}.`,
    none: `There is no ${p.teamBranch} yet.`,
    refused: `Merge to ${p.baseBranch} would be refused.`,
  };
  return {
    tone: canMerge ? 'ok' : p.outcome === 'conflict' || p.outcome === 'refused' ? 'bad' : 'plain',
    sentence: p.summary?.trim() || fallback[p.outcome] || p.outcome,
    canMerge,
    commitsHeading: p.count > 0 ? `${commits(p.count)} will land on ${p.baseBranch}` : null,
    filesHeading: p.files.length ? `${p.files.length} file${p.files.length === 1 ? '' : 's'} changed, all together` : null,
  };
}

export const IN_MAIN = 'already in main';
export const OWN_WORK = 'work of its own';
export const MERGE_COMMIT = 'a merge commit';
const MARKS: Record<DeletePreviewCommit['state'], string> = { 'in-base': IN_MAIN, own: OWN_WORK, merge: MERGE_COMMIT };

export interface DeleteRow { commit: DeletePreviewCommit; mark: string; own: boolean }

/** git.deletePreview as rows (each marked by its state) and the core's one line on what deleting loses. */
export function deletePreviewView(p: DeletePreview): { rows: DeleteRow[]; summary: string; loses: boolean } {
  const rows = (p.exists ? p.commits : []).map((c) => ({ commit: c, mark: MARKS[c.state] ?? c.state, own: c.state === 'own' }));
  const base = p.baseBranch || 'main';
  const fallback = !p.exists ? `There is no local branch ${p.branch}.`
    : p.own > 0 ? `Deleting it loses ${commits(p.own)} of its own.`
    : rows.length ? `Nothing is lost: each of its ${commits(rows.length)} not in ${base} already has the same change there.`
    : `Nothing is lost: every commit of ${p.branch} is in ${base}.`;
  return { rows, summary: p.lost?.trim() || fallback, loses: p.exists && p.own > 0 };
}

/** The heading over the commits a sync, bring current or merge brought in. */
export function commitsInHeading(n: number, branch: string): string {
  return `${commits(n)} came into ${branch}:`;
}
