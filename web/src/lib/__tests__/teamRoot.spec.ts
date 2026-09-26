import { describe, expect, it } from 'vitest'
import { deriveTeamId, nextTeamName, teamFolderLine } from '../teamRoot'

describe('deriveTeamId', () => {
  it('mirrors ContainerId.DeriveName', () => {
    expect(deriveTeamId('Alpha-team')).toBe('Alpha-team')
    expect(deriveTeamId('Alpha Team')).toBe('AlphaTeam')
    expect(deriveTeamId('R&D / Tooling')).toBe('RDTooling')
    expect(deriveTeamId('- Platform')).toBe('Platform')
  })

  it('caps at 32 characters, like MaxNameLength', () => {
    expect(deriveTeamId('a'.repeat(40))).toHaveLength(32)
  })

  it('is empty when nothing derives, so the caller can say so', () => {
    expect(deriveTeamId('チーム')).toBe('')
  })

  it('does not treat a Windows device name as special', () => {
    expect(deriveTeamId('Com1')).toBe('Com1')
  })
})

describe('teamFolderLine', () => {
  it('shows the DERIVED id, not the typed label', () => {
    // The whole point: somebody naming a team "Alpha Team" sees .../teams/AlphaTeam BEFORE they
    // commit, rather than discovering the substitution afterwards.
    expect(teamFolderLine('/data', 'Alpha Team')).toBe('New team folder will be /data/teams/AlphaTeam')
  })

  it('does not double a root handed over with a trailing slash', () => {
    expect(teamFolderLine('/data/', 'Alpha')).toBe('New team folder will be /data/teams/Alpha')
  })

  it('says nothing useful is derivable rather than showing a dangling path', () => {
    expect(teamFolderLine('/data', 'チーム'))
      .toBe('The folder name will be generated — this name has no usable ASCII.')
  })

  it('waits for a root rather than rendering a half path', () => {
    expect(teamFolderLine('', 'Alpha')).toBe('')
  })
})

describe('nextTeamName', () => {
  it('is Team-1 when there are no teams', () => {
    expect(nextTeamName([])).toBe('Team-1')
  })

  it('takes the first unused integer, not the count', () => {
    // Count would give Team-3 and collide with the existing one.
    expect(nextTeamName(['Team-1', 'Team-3'])).toBe('Team-2')
  })

  it('ignores names that are not Team-<n>', () => {
    expect(nextTeamName(['Alpha', 'xyz team'])).toBe('Team-1')
  })

  it('is case-insensitive, because the server refuses a duplicate either way', () => {
    expect(nextTeamName(['team-1'])).toBe('Team-2')
  })
})
