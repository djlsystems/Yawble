// The Git dialog's "Tidy…": git.tidyPlan's branches in one list grouped by class, which rows are
// pre-selected to delete, the git.deleteBranches requests for a selection, and the report of what was kept and why.
// Pure, so the grouping and selection are unit-tested without the dialog.
import type { TidyBranch, TidyClass, TidyPlan } from '../../shared/api';

export interface TidyGroupInfo { cls: TidyClass; label: string; why: string; deletable: boolean }

/** The classes in the order the list shows them: the ones to delete first, then the kept ones, then the ones to look at. */
export const TIDY_GROUPS: TidyGroupInfo[] = [
  { cls: 'merged', label: 'Merged', why: 'Its commits are in the base branch.', deletable: true },
  { cls: 'contained', label: 'Contained in the base', why: 'Every commit has an equivalent in the base branch (cherry-picked), so nothing is lost.', deletable: true },
  { cls: 'moved', label: 'Moved in the member\'s clone', why: 'The team\'s copy differs from the member\'s clone (reset or rebased there). Realign sets the team\'s and origin\'s copy to the clone\'s.', deletable: false },
  { cls: 'own-work', label: 'Has work of its own', why: 'It has commits that are not in the base branch: needs a look.', deletable: false },
  { cls: 'working', label: 'Members\' working branches', why: 'The member\'s working branch, not a leftover: kept.', deletable: false },
  { cls: 'origin-only', label: 'Only on origin', why: 'On origin, and nobody has it here.', deletable: false },
  { cls: 'scratch', label: 'Scratch in a clone', why: 'A member clone\'s local-only scratch/ or tmp/ branch.', deletable: false },
];
const info = (c: TidyClass) => TIDY_GROUPS.find((g) => g.cls === c) ?? { cls: c, label: c, why: '', deletable: false };

export interface TidyRow { key: string; repo: string; branch: TidyBranch }
export interface TidyGroup extends TidyGroupInfo { rows: TidyRow[] }

export const rowKey = (repo: string, name: string) => `${repo}\u0000${name}`;

/** The plan's branches grouped by class in TIDY_GROUPS order (an unknown class last, as itself); empty groups left out. */
export function groupTidy(plan: TidyPlan | null | undefined): TidyGroup[] {
  const groups = new Map<string, TidyGroup>();
  for (const r of plan?.repos ?? []) {
    for (const b of r.branches ?? []) {
      let g = groups.get(b.class);
      if (!g) groups.set(b.class, g = { ...info(b.class), rows: [] });
      g.rows.push({ key: rowKey(r.repo, b.name), repo: r.repo, branch: b });
    }
  }
  const order = (c: string) => { const i = TIDY_GROUPS.findIndex((g) => g.cls === c); return i < 0 ? TIDY_GROUPS.length : i; };
  return [...groups.values()].sort((a, b) => order(a.cls) - order(b.cls));
}

/** A row can be deleted from Tidy: merged or contained in the base, and here or on origin (a clone's copy is the member's). */
export const canDelete = (b: TidyBranch) => info(b.class).deletable && b.where.some((w) => w === 'local' || w === 'origin');

/** The rows pre-selected when the plan loads: every one that can be deleted. */
export function defaultSelection(plan: TidyPlan | null | undefined): Set<string> {
  const s = new Set<string>();
  for (const g of groupTidy(plan)) for (const r of g.rows) if (canDelete(r.branch)) s.add(r.key);
  return s;
}

/** git.deleteBranches takes no repo for the main repository and the name for an extra one. */
export function repoParam(repo: string, mainName: string | null | undefined): string | null {
  if (!repo || repo === mainName || (!mainName && repo === 'main')) return null;
  return repo;
}

export interface DeleteRequest { repo: string; repoParam: string | null; remote: boolean; branches: string[] }

/** The selected deletable rows as git.deleteBranches requests: one per repository and per "also on origin" (remote), in
 * plan order. A branch on origin is deleted there too; one only here is deleted here. */
export function deleteRequests(plan: TidyPlan | null | undefined, selected: Set<string>, mainName: string | null | undefined): DeleteRequest[] {
  const out: DeleteRequest[] = [];
  for (const r of plan?.repos ?? []) {
    for (const b of r.branches ?? []) {
      if (!selected.has(rowKey(r.repo, b.name)) || !canDelete(b)) continue;
      const remote = b.where.includes('origin');
      let q = out.find((x) => x.repo === r.repo && x.remote === remote);
      if (!q) out.push(q = { repo: r.repo, repoParam: repoParam(r.repo, mainName), remote, branches: [] });
      q.branches.push(b.name);
    }
  }
  return out;
}

const short = (sha: string | null | undefined) => (sha ? sha.slice(0, 10) : '—');

/** Where a branch is, in words: "here, on origin, in Ada's clone". */
export function whereText(where: string[]): string {
  return where.map((w) => (w === 'local' ? 'here' : w === 'origin' ? 'on origin' : w.startsWith('clone:') ? `in ${w.slice('clone:'.length)}'s clone` : w)).join(', ');
}

/** Why a row is kept, one line (no Delete on it, or not selected). */
export function keptReason(b: TidyBranch, selectedButKept = false): string {
  switch (b.class) {
    case 'merged': case 'contained':
      return selectedButKept ? 'kept by git.deleteBranches (see its plan)' : !canDelete(b) ? 'only in a member\'s clone' : 'not selected';
    case 'working': return `${b.member ? `${b.member}'s` : 'the member\'s'} working branch, not a leftover`;
    case 'moved': return `moved: team ${short(b.teamSha)}, ${b.member ? `${b.member}'s ` : ''}clone ${short(b.cloneSha)} (Realign to fix)`;
    case 'own-work': { const n = b.commits?.length ?? 0; return `has work of its own${n ? ` (${n} commit${n === 1 ? '' : 's'})` : ''}, needs a look`; }
    case 'origin-only': return 'only on origin, nobody has it here';
    case 'scratch': return `scratch branch in ${b.member ? `${b.member}'s` : 'a member\'s'} clone`;
    default: return b.class;
  }
}

/** After an action, what the reloaded plan still has and why: "name (repo): reason", in list order. Rows that were
 * asked to be deleted but are still there say so. */
export function keptReport(plan: TidyPlan | null | undefined, askedToDelete: Set<string> = new Set()): string[] {
  const multi = (plan?.repos.length ?? 0) > 1;
  return groupTidy(plan).flatMap((g) => g.rows.map((r) =>
    `${r.branch.name}${multi ? ` (${r.repo})` : ''}: ${keptReason(r.branch, askedToDelete.has(r.key))}`));
}
