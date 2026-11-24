import type { BranchPush } from '../../shared/api';

// The one "not pushed" mark, for every branch and commit the app shows that origin does not have (the
// Git dialog's branches, the commit lists, the Tidy panel, the Commit view). LocalMark.vue renders it; whereLines uses the
// long text. The core computes the flag in one place (CommitRow.notPushed, BranchPush). Without an origin
// every commit is local: the list says so once (LOCAL_MARK.noOrigin) instead of marking each row.

export const LOCAL_MARK = {
  short: 'not pushed',
  long: 'Not pushed: only on this computer',
  title: 'Only on this computer: origin does not have it yet',
  noOrigin: 'This repository has no origin: every commit here is local only.',
} as const;

/** A commit row: notPushed in a repository with an origin (hasOrigin false or an older core's row: no mark). */
export function commitLocalOnly(c: { notPushed?: boolean | null }, hasOrigin: boolean): boolean {
  return hasOrigin && c.notPushed === true;
}

/** A branch: origin lacks it or some of its commits (no origin, or an older core without push: no mark). */
export function branchLocalOnly(push: BranchPush | null | undefined, hasOrigin: boolean): boolean {
  return hasOrigin && !!push && (!push.onOrigin || push.notPushed > 0);
}

/** The branch mark's hover: how much origin lacks. */
export function branchLocalTitle(push: BranchPush): string {
  if (!push.onOrigin) return `${LOCAL_MARK.title}: origin has no ${push.branch}`;
  return `${LOCAL_MARK.title}: ${push.notPushed} commit${push.notPushed === 1 ? '' : 's'} not on origin`;
}

/** A Tidy branch's where list (local, origin, clone:<member>): here but not on origin, in a repository that has an origin
 * (hasOrigin: some branch of that repository is on origin; without one there is nothing to push to). */
export function tidyLocalOnly(where: string[], hasOrigin: boolean): boolean {
  return hasOrigin && where.includes('local') && !where.includes('origin');
}
