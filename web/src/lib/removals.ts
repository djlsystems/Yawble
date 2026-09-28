import type { RemovalKind, UnfinishedRemoval } from '../api/types';

/**
 * HOW AN UNFINISHED REMOVAL READS on the Teams view's list: what it was removing, in words, and
 * whose it was. Kept out of the component so the words are one place and testable without a mount.
 */

const Kinds: Record<RemovalKind, string> = {
  'team-root': "Deleted team's folder",
  workspace: "Deleted member's workspace",
  emptied: 'Folder a Reset emptied',
};

/** The kind in words; a kind this build does not know is shown as the Host sent it. */
export function removalKindLabel(kind: RemovalKind | string): string {
  return Kinds[kind as RemovalKind] ?? kind;
}

/**
 * Whose folder it was: the team, and for a workspace the member in it. A team that is gone has no
 * label left on the board, so its id is what there is to show.
 */
export function removalOwner(removal: Pick<UnfinishedRemoval, 'team' | 'member'>, teamLabel?: string | null): string {
  const team = teamLabel || removal.team;
  return removal.member ? `${removal.member} in ${team}` : team;
}
