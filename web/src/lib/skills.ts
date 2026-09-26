import type { SkillKind, SkillRecord, SkillRole } from '../api/types'

/**
 * The roles a custom skill may be offered to, in the order the picker shows them. `any` is every
 * role. A new skill starts on `member` - the default, and the role most custom skills are for.
 */
export const SkillRoles: readonly SkillRole[] = ['concierge', 'manager', 'member', 'any']
export const DefaultSkillRoles: readonly SkillRole[] = ['member']

const roleWords: Record<SkillRole, string> = {
  concierge: 'Concierge',
  manager: 'Manager',
  member: 'Member',
  any: 'Any',
}

export const roleLabel = (role: SkillRole): string => roleWords[role] ?? role

/** The Roles cell. `any` swallows the rest: a skill for every role is not also for one of them. */
export function rolesLabel(roles: readonly SkillRole[]): string {
  if (roles.includes('any')) return roleLabel('any')
  return roles.map(roleLabel).join(', ')
}

/**
 * The roles a person picked, as they are sent. Choosing `any` beside a specific role is choosing
 * `any`; an order is imposed so the same choice always reads the same.
 */
export function normaliseRoles(roles: readonly SkillRole[]): SkillRole[] {
  if (roles.includes('any')) return ['any']
  return SkillRoles.filter((role) => roles.includes(role))
}

export const kindLabel = (kind: SkillKind): string => (kind === 'builtin' ? 'Built-in' : 'Custom')

/**
 * A built-in comes from the build and changes only through a code change: every route
 * refuses to edit, rename or delete one, so the dialog never offers to.
 */
export const isReadOnlySkill = (row: Pick<SkillRecord, 'kind'>): boolean => row.kind === 'builtin'

/**
 * The Last modified cell: when, and by whom. A built-in carries neither - the build changed it - and
 * reads as such rather than as a blank a person might take for a missing answer.
 */
export function lastModified(
  row: Pick<SkillRecord, 'kind' | 'updatedAt' | 'updatedBy'>,
  format: (iso: string) => string = (iso) => new Date(iso).toLocaleString(),
): string {
  if (!row.updatedAt) return row.kind === 'builtin' ? 'With this build' : '—'
  const when = format(row.updatedAt)
  return row.updatedBy ? `${when} by ${row.updatedBy}` : when
}
