import { describe, expect, it } from 'vitest'
import { branchNameProblem, defaultBranchCaption } from '../defaultBranch'

describe('branchNameProblem', () => {
  it.each(['main', 'trunk', 'release/2026.09', ''])('accepts %s', (name) => {
    expect(branchNameProblem(name)).toBeNull()
  })

  it.each(['-x', 'a..b', 'a b', 'HEAD', 'x.lock', 'a:b', 'a/', '.hidden'])('refuses %s', (name) => {
    expect(branchNameProblem(name)).toContain('is not a branch name git accepts')
  })
})

describe('defaultBranchCaption', () => {
  it('says where the branch in force comes from', () => {
    expect(defaultBranchCaption({ repo: 'a', branch: 'x', fromRemote: 'trunk', setByPerson: 'x' }))
      .toContain('kept across Fetches until cleared')
    expect(defaultBranchCaption({ repo: 'a', branch: 'trunk', fromRemote: 'trunk', setByPerson: null }))
      .toContain('From origin: trunk')
    expect(defaultBranchCaption({ repo: 'a', branch: null, fromRemote: null, setByPerson: null }))
      .toBe('Not known. A Fetch reads it from origin, or set it here.')
  })
})
