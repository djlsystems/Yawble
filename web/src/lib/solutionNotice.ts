import type { Message } from '../api/types'

/**
 * The platform's check of a solution package a workflow wrote (`solution.checked`), as the board and
 * the backlog item show it: ready, with the install wizard's link, or the problems by file and field.
 *
 * THE LINK IS BUILT HERE FROM THE FOLDER, never taken from text: a notice is plain words plus one
 * link this bundle composes, so nothing in a payload can become markup or point anywhere else.
 */
export interface SolutionNoticeView {
  ok: boolean
  /** What a person reads: "<name> <version> is ready." or the failing notice's own sentence. */
  words: string
  folder: string
  /** `#/solutions/install?folder=<folder>`, on a pass only. */
  href: string | null
  problems: string[]
}

export const SolutionCheckedType = 'solution.checked'

export function installHref(folder: string): string {
  return `#/solutions/install?folder=${encodeURIComponent(folder)}`
}

/** A notice as the backlog detail carries it, or a payload read off the log: the same fields. */
export interface SolutionNoticeFields {
  ok?: unknown
  text?: unknown
  path?: unknown
  folder?: unknown
  name?: unknown
  version?: unknown
  problems?: unknown
}

export function solutionNotice(fields: SolutionNoticeFields | null | undefined): SolutionNoticeView | null {
  if (fields === null || fields === undefined) return null

  const folder = typeof fields.folder === 'string' ? fields.folder : typeof fields.path === 'string' ? fields.path : null
  if (folder === null || folder.trim() === '') return null

  const ok = fields.ok === true
  const name = typeof fields.name === 'string' && fields.name.trim() !== '' ? fields.name : null
  const version = typeof fields.version === 'string' && fields.version.trim() !== '' ? fields.version : null
  const text = typeof fields.text === 'string' ? fields.text : ''
  const problems = Array.isArray(fields.problems) ? fields.problems.filter((p): p is string => typeof p === 'string') : []
  const label = [name ?? folder, version].filter((part) => part !== null).join(' ')

  return {
    ok,
    words: ok ? `${label} is ready.` : text || `${label} did not pass the check.`,
    folder,
    href: ok ? installHref(folder) : null,
    problems: ok ? [] : problems,
  }
}

/** The notice on a feed row, or null for any other row. */
export function solutionNoticeOf(message: Message): SolutionNoticeView | null {
  if (message.type !== SolutionCheckedType) return null

  try {
    const payload: unknown = JSON.parse(message.payload)
    return typeof payload === 'object' && payload !== null ? solutionNotice(payload as SolutionNoticeFields) : null
  } catch {
    return null
  }
}
