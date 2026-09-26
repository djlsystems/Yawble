import type { TeamRepoDefaultBranch } from '../api/types'

/**
 * Why a typed default branch would be refused, or null when git would take it. The server's
 * `BranchNames.IsValid` is the rule; this mirrors it so the refusal shows on the field before Save.
 */
export function branchNameProblem(name: string): string | null {
  const value = name.trim()
  if (value === '') return null
  if (
    value.startsWith('-') || value.startsWith('/') || value.endsWith('/') || value.endsWith('.') ||
    value.endsWith('.lock') || value === 'HEAD' || value === '@' ||
    value.includes('..') || value.includes('//') || value.includes('@{') ||
    /[\s~^:?*[\\\u0000-\u001f\u007f]/.test(value) ||
    value.split('/').some(part => part.length === 0 || part.startsWith('.'))
  ) {
    return `'${value}' is not a branch name git accepts.`
  }
  return null
}

/**
 * What Team settings says under one repository's branch field: where the branch in force comes
 * from, and what clearing a person's choice falls back to. It never names `main` it was not given.
 */
export function defaultBranchCaption(entry: TeamRepoDefaultBranch): string {
  if (entry.setByPerson) {
    return entry.fromRemote
      ? `Set here, and kept across Fetches until cleared. Origin names ${entry.fromRemote}.`
      : 'Set here, and kept across Fetches until cleared. Origin names no branch.'
  }
  if (entry.fromRemote) return `From origin: ${entry.fromRemote}. The next Fetch reads it again.`
  return 'Not known. A Fetch reads it from origin, or set it here.'
}
