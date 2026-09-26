// Contributor mode's field rules mirror the server's `ContributorSettings.Validate`, and in
// contributor mode every ahead/behind sentence names upstream, never origin (the fork).
import { describe, expect, it } from 'vitest'
import {
  CLA_NOTE_LIMIT,
  claNoteProblem,
  contributorChanged,
  contributorDraft,
  forkOwnerProblem,
  upstreamUrlProblem,
} from '../contributor'
import { baseBranchWord, formatMainDelta } from '../repoStatus'
import type { RepoStatus } from '../../api/types'

const fork = 'https://github.com/fork-owner/Widget.git'

describe('contributor settings rules', () => {
  it('takes a blank upstream (owned) and an http(s) URL', () => {
    expect(upstreamUrlProblem('', fork)).toBeNull()
    expect(upstreamUrlProblem('https://github.com/project/Widget.git', fork)).toBeNull()
  })

  it('refuses what is not an http(s) URL, and the fork itself', () => {
    expect(upstreamUrlProblem('--upload-pack=x', fork)).toContain('not an absolute http or https')
    expect(upstreamUrlProblem('git@github.com:project/Widget.git', fork)).toContain('not an absolute http or https')
    expect(upstreamUrlProblem('https://github.com/fork-owner/Widget', fork)).toContain('own URL')
  })

  it('takes an account name for the fork owner, or blank', () => {
    expect(forkOwnerProblem('')).toBeNull()
    expect(forkOwnerProblem('fork-owner')).toBeNull()
    expect(forkOwnerProblem('no spaces')).toContain('not a GitHub account name')
  })

  it('bounds the CLA note', () => {
    expect(claNoteProblem('signed')).toBeNull()
    expect(claNoteProblem('x'.repeat(CLA_NOTE_LIMIT + 1))).toContain(String(CLA_NOTE_LIMIT))
  })

  it('counts a change only where the server would store one', () => {
    const stored = { repo: 'Widget', upstreamUrl: null, forkOwner: null, dcoSignOff: false, claSignedNote: null }
    expect(contributorChanged(contributorDraft(stored), stored)).toBe(false)
    expect(contributorChanged({ ...contributorDraft(stored), forkOwner: 'x' }, stored)).toBe(false)
    expect(contributorChanged({ ...contributorDraft(stored), dcoSignOff: true }, stored)).toBe(true)
    expect(contributorChanged({ ...contributorDraft(stored), upstreamUrl: ' https://github.com/p/W.git ' }, stored)).toBe(true)
  })
})

describe('the ref ahead/behind is measured against', () => {
  const status = (over: Partial<RepoStatus>) => ({
    defaultBranch: 'trunk', mainAhead: 0, mainBehind: 2, originCheckedAt: null, upstreamUrl: null, ...over,
  }) as RepoStatus

  it('is origin for an owned repository, as before', () => {
    expect(baseBranchWord(status({}))).toBe('origin/trunk')
    expect(formatMainDelta(status({}))).toContain('2 behind origin/trunk')
  })

  it('is upstream in contributor mode', () => {
    const contributing = status({ upstreamUrl: 'https://github.com/project/Widget.git' })
    expect(baseBranchWord(contributing)).toBe('upstream/trunk')
    expect(formatMainDelta(contributing)).toContain('2 behind upstream/trunk')
  })

  it('never names main when the default branch is not known', () => {
    expect(baseBranchWord(status({ defaultBranch: null, upstreamUrl: 'https://github.com/p/W.git' })))
      .toBe('upstream’s default branch')
  })
})
