import { describe, expect, it } from 'vitest'
import {
  DefaultSkillRoles,
  isReadOnlySkill,
  kindLabel,
  lastModified,
  normaliseRoles,
  rolesLabel,
  teamLabel,
} from '../skills'
import type { TeamId } from '../../api/types'

describe('skill roles', () => {
  it('defaults a new skill to member', () => {
    expect(DefaultSkillRoles).toEqual(['member'])
  })

  it('reads any as every role, whatever else was picked', () => {
    expect(normaliseRoles(['member', 'any'])).toEqual(['any'])
    expect(rolesLabel(['manager', 'any'])).toBe('Any')
  })

  it('orders the roles the same way every time', () => {
    expect(normaliseRoles(['member', 'concierge'])).toEqual(['concierge', 'member'])
    expect(rolesLabel(['manager', 'member'])).toBe('Manager, Member')
  })
})

describe('skill kinds', () => {
  it('labels the two kinds', () => {
    expect(kindLabel('builtin')).toBe('Built-in')
    expect(kindLabel('custom')).toBe('Custom')
  })

  it('makes a built-in read-only and a custom skill editable', () => {
    expect(isReadOnlySkill({ kind: 'builtin' })).toBe(true)
    expect(isReadOnlySkill({ kind: 'custom' })).toBe(false)
  })
})

describe('lastModified', () => {
  const format = (iso: string) => `at ${iso}`

  it('says when and by whom', () => {
    expect(lastModified({ kind: 'custom', updatedAt: 'T1', updatedBy: 'ines@example.com' }, format))
      .toBe('at T1 by ines@example.com')
  })

  it('says when alone when nobody is named', () => {
    expect(lastModified({ kind: 'custom', updatedAt: 'T1', updatedBy: null }, format)).toBe('at T1')
  })

  it('says a built-in changed with the build rather than leaving a blank', () => {
    expect(lastModified({ kind: 'builtin', updatedAt: null, updatedBy: null }, format)).toBe('With this build')
  })
})

describe('teamLabel', () => {
  const teams = [{ id: 'job-hunt' as TeamId, name: 'Job Hunt' }]

  it('reads "All teams" for an instance-wide skill', () => {
    expect(teamLabel(null, teams)).toBe('All teams')
    expect(teamLabel(undefined, teams)).toBe('All teams')
  })

  it("names a team skill's team by the name a person reads, and by its id when it is not listed", () => {
    expect(teamLabel('job-hunt' as TeamId, teams)).toBe('Job Hunt')
    expect(teamLabel('gone' as TeamId, teams)).toBe('gone')
  })
})
