import type { RepoResetItem, RepositoryResetPreview, TeamResetRequest } from '../api/types'

/**
 * What the Reset dialog is currently showing, as data.
 *
 * `members` is a map rather than a list because that is what a column of checkboxes IS — every
 * member of the team with a ticked/unticked state, including the unticked ones. The request only
 * carries the ticked names.
 */
export interface ResetChoices {
  members: Record<string, boolean>

  /**
   * ONE CONTROL OVER THREE SERVER FLAGS, and the collapse is deliberate.
   *
   * The server keeps `forgetHistory`, `purge` and `clearTranscripts` apart because they genuinely
   * are separable — an API caller may want a reversible forget, and the audit question is not the
   * disk question. But to a person there is only one thing here: what this member remembers. The
   * three-checkbox form made that one decision look like three, and let you reach an incoherent
   * state — delete the rows without moving the floor.
   *
   * So: everything a member remembers, gone. The floor advances, the messages are deleted, and the
   * verbatim run transcripts on disk go with them.
   */
  deleteMemory: boolean

  clearWorkspaces: boolean
  clearSharedDocuments: boolean

  /** Reset repositories: the ticked members' worktrees and branches, and with every member ticked
   *  the team branch. Team-wide, off by default: it is the one box here that touches code. */
  resetRepositories: boolean

  // THERE IS NO `resetConcierge`, AND ITS ABSENCE IS THE ENFORCEMENT. A Concierge is keyed on the
  // person, so a team reset ends none and cannot - and with the field gone from this record, a
  // dialog that reintroduces the box is a TYPECHECK FAILURE rather than a control that quietly
  // does nothing. Mounted specs exist now, but the compiler is still the only guard available
  // here; removing the flag is what arms it.
}

/** Every box cleared, every member of `names` ticked, and memory being deleted — which is what a
 *  reset MEANS, so it is the state the dialog opens in. */
export function choicesFor(names: string[]): ResetChoices {
  return {
    members: Object.fromEntries(names.map((name) => [name, true])),
    deleteMemory: true,
    clearWorkspaces: false,
    clearSharedDocuments: false,
    resetRepositories: false,
  }
}

/**
 * The request a set of choices means.
 *
 * A PURE FUNCTION IN `lib/` rather than a method on the dialog, and that placement is what makes it
 * testable at all: the dialog can be mounted now, and the request's meaning still belongs here,
 * where a spec can exercise it directly. The alternative — a spec that greps the component's
 * SOURCE — is not a test of it, and is the exact instrument that let a Critical through 116 green
 * tests.
 *
 * A flag that is FALSE is OMITTED rather than sent, except `forgetHistory`, whose absence the server
 * reads as TRUE. So the one flag emitted in the negative is the one whose default is positive.
 *
 * `deleteMemory` fans out to the three the server understands. It sends `purge` and
 * `clearTranscripts` together and lets `forgetHistory` default — never `forgetHistory: true`
 * explicitly, because an absent flag and a true one mean the same thing there and sending the
 * redundant one invites a reader to think it is doing something the default is not.
 */
export function requestFrom(choices: ResetChoices): TeamResetRequest {
  const request: TeamResetRequest = {
    members: Object.entries(choices.members)
      .filter(([, ticked]) => ticked)
      .map(([name]) => name),
  }

  if (choices.deleteMemory) {
    request.purge = true
    request.clearTranscripts = true
  } else {
    request.forgetHistory = false
  }

  if (choices.clearWorkspaces) request.clearWorkspaces = true
  if (choices.clearSharedDocuments) request.clearSharedDocuments = true
  if (choices.resetRepositories) request.resetRepositories = true
  return request
}

/**
 * Whether these choices would actually do anything.
 *
 * Not the same as "a member is ticked": every per-member option can be off while members are ticked,
 * which asks the server to floor nothing and clear nothing. The button is disabled rather than the
 * request refused, because a reset that answers "nothing moved" is a worse answer than a control
 * that will not fire.
 */
export function wouldDoSomething(choices: ResetChoices): boolean {
  const chosen = Object.values(choices.members).some(Boolean)
  const perMember = chosen && (choices.deleteMemory || choices.clearWorkspaces || choices.resetRepositories)

  return perMember || choices.clearSharedDocuments
}

/** What Reset repositories would remove for these choices, as the dialog lists it. */
export interface RepositoryLosses {
  worktrees: RepoResetItem[]
  branches: RepoResetItem[]

  /** `team/<id>`, only when EVERY member is ticked; null otherwise. */
  teamBranch: string | null

  /** Repositories whose default branch is not known: with every member ticked the reset is refused. */
  refusedFor: string[]
}

/**
 * The ticked members' lines of `preview`, and the team branch when every member is ticked - the
 * server's own rule (`RepositoryReset`), repeated here only to SHOW it, never to decide it.
 */
export function repositoryLosses(preview: RepositoryResetPreview, choices: ResetChoices): RepositoryLosses {
  const ticked = new Set(Object.entries(choices.members).filter(([, on]) => on).map(([name]) => name))
  const everyone = ticked.size > 0 && Object.values(choices.members).every(Boolean)
  const mine = (item: RepoResetItem) => item.member != null && ticked.has(item.member)

  return {
    worktrees: preview.worktrees.filter(mine),
    branches: preview.branches.filter(mine),
    teamBranch: everyone ? preview.teamBranch : null,
    refusedFor: everyone ? preview.defaultBranchNotKnown : [],
  }
}
