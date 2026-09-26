import type { TeamRepoContributor } from '../api/types'

/**
 * Contributor mode, per repository. A repository with an upstream URL is one the team
 * contributes to: its own URL is the fork (origin) and the upstream is the original project. These
 * mirror the server's `ContributorSettings.Validate` so a refusal shows on the field before Save.
 */

/** `ContributorSettings.ClaNoteLimit`. */
export const CLA_NOTE_LIMIT = 500

/** The fields Team settings edits for one repository. */
export interface ContributorDraft {
  upstreamUrl: string
  forkOwner: string
  dcoSignOff: boolean
  claSignedNote: string
}

/** The draft a stored entry starts as. */
export function contributorDraft(entry: TeamRepoContributor | undefined): ContributorDraft {
  return {
    upstreamUrl: entry?.upstreamUrl ?? '',
    forkOwner: entry?.forkOwner ?? '',
    dcoSignOff: entry?.dcoSignOff ?? false,
    claSignedNote: entry?.claSignedNote ?? '',
  }
}

/** Whether a draft differs from what is stored, compared as the server would store it. */
export function contributorChanged(draft: ContributorDraft, stored: TeamRepoContributor | undefined): boolean {
  const upstream = draft.upstreamUrl.trim()
  const before = contributorDraft(stored)
  return (
    upstream !== before.upstreamUrl ||
    // A fork owner means nothing without an upstream; the server clears it with the upstream.
    (upstream !== '' && draft.forkOwner.trim() !== before.forkOwner) ||
    draft.dcoSignOff !== before.dcoSignOff ||
    draft.claSignedNote.trim() !== before.claSignedNote
  )
}

function withoutGitSuffix(url: string): string {
  const trimmed = url.trim().replace(/\/+$/, '')
  return trimmed.toLowerCase().endsWith('.git') ? trimmed.slice(0, -4) : trimmed
}

/** Why a typed upstream would be refused, or null. Blank is an owned repository, which is fine. */
export function upstreamUrlProblem(upstream: string, originUrl: string | undefined): string | null {
  const value = upstream.trim()
  if (value === '') return null

  let parsed: URL
  try {
    parsed = new URL(value)
  } catch {
    return `'${value}' is not an absolute http or https repository URL.`
  }
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    return `'${value}' is not an absolute http or https repository URL.`
  }

  if (originUrl && withoutGitSuffix(value).toLowerCase() === withoutGitSuffix(originUrl).toLowerCase()) {
    return 'That is this repository’s own URL. Here it is the fork; the upstream is the project it was forked from.'
  }

  return null
}

/** Why a typed fork owner would be refused, or null. Blank is read from the repository URL. */
export function forkOwnerProblem(owner: string): string | null {
  const value = owner.trim()
  if (value === '') return null
  return /^[A-Za-z0-9][A-Za-z0-9-]{0,38}$/.test(value) ? null : `'${value}' is not a GitHub account name.`
}

/** Why a CLA note would be refused, or null. */
export function claNoteProblem(note: string): string | null {
  return note.trim().length > CLA_NOTE_LIMIT ? `A CLA note is at most ${CLA_NOTE_LIMIT} characters.` : null
}

/** Every problem with a draft, first one only, or null. */
export function contributorDraftProblem(draft: ContributorDraft, originUrl: string | undefined): string | null {
  return (
    upstreamUrlProblem(draft.upstreamUrl, originUrl) ??
    (draft.upstreamUrl.trim() === '' ? null : forkOwnerProblem(draft.forkOwner)) ??
    claNoteProblem(draft.claSignedNote)
  )
}
