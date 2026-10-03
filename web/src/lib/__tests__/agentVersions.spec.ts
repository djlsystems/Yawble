import { describe, expect, it } from 'vitest'

import type { CliVersion } from '../../api/types'
import { versionLine } from '../agentVersions'

/** Who brought a version, in words: a line control wrote when it measured the workers is never a container start. */
describe('versionLine', () => {
  const entry = (updatedBy: string | null): CliVersion => ({
    cli: 'claude',
    version: '2.0.2',
    updatedAt: '2026-10-03T09:00:00Z',
    since: '2026-09-01T00:00:00Z',
    updatedBy,
    person: null,
  })

  it('a measured line reads as measured on a worker, never a container start', () => {
    const line = versionLine(entry('measured'), () => '3 Oct')
    expect(line.updated).toContain('as measured on a worker')
    expect(line.updated).not.toContain('container start')
  })

  it('a start line still reads as a container start', () => {
    expect(versionLine(entry('start'), () => '3 Oct').updated).toContain('at a container start')
  })
})
