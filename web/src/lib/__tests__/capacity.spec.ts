import { describe, expect, it } from 'vitest'
import {
  NotMeasured,
  cpuWords,
  gauge,
  memoryWords,
  pidsWords,
  pressureWords,
  runsWords,
  sparkline,
  staleness,
} from '../capacity'
import { memoryAt, pressure, sample, unmeasured } from '../../test/capacityFixtures'

const fresh = { ageSeconds: 2, stale: false }

describe('the gauge colour is the worst of memory in use, memory pressure and CPU pressure', () => {
  it('is green below 75% with no stall, and says memory set it', () => {
    const g = gauge(memoryAt(50), fresh)

    expect(g.level).toBe('green')
    expect(g.reason).toBe("Memory of this worker's container, against its own limit: 6.5 of 12.9 GB in use (50%)")
  })

  it('is amber from 75% memory and red from 90%', () => {
    expect(gauge(memoryAt(75), fresh).level).toBe('amber')
    expect(gauge(memoryAt(89), fresh).level).toBe('amber')
    expect(gauge(memoryAt(90), fresh).level).toBe('red')
  })

  it('takes memory pressure when it is worse, and names it', () => {
    const s = memoryAt(40)
    s.memory.pressure = pressure(92)

    const g = gauge(s, fresh)

    expect(g.level).toBe('red')
    expect(g.reason).toBe('Memory pressure: work waited for memory 92% of the last 10 s')
  })

  it('takes CPU pressure when it is worse, and names it', () => {
    const s = memoryAt(40)
    s.cpu.pressure = pressure(80)

    expect(gauge(s, fresh)).toEqual({ level: 'amber', reason: 'CPU pressure: work waited for CPU 80% of the last 10 s' })
  })

  it('is amber for any sustained stall, however low the last 10 s', () => {
    const s = memoryAt(40)
    s.memory.pressure = pressure(2, 12)

    const g = gauge(s, fresh)

    expect(g.level).toBe('amber')
    expect(g.reason).toContain('12% of the last minute')
  })

  it('reads not measured, never green, when nothing it colours by was measured', () => {
    expect(gauge(unmeasured(), fresh)).toEqual({ level: 'unmeasured', reason: `Memory and pressure: ${NotMeasured}` })
  })

  it('lets a measured figure colour it when the others are not measured', () => {
    const s = unmeasured()
    s.cpu.pressure = pressure(95)

    expect(gauge(s, fresh).level).toBe('red')
  })

  it('says the figures are stale and how old, rather than colouring by them', () => {
    expect(gauge(memoryAt(95), { ageSeconds: 125, stale: true })).toEqual({
      level: 'stale',
      reason: 'Figures are stale: last measured 2 min 5 s ago',
    })
  })
})

describe('staleness', () => {
  it('is the age of the sample against this clock, stale beyond three intervals', () => {
    const at = Date.parse('2026-10-01T10:00:00Z')

    expect(staleness(sample(), 5, at + 15_000)).toEqual({ ageSeconds: 15, stale: false })
    expect(staleness(sample(), 5, at + 16_000)).toEqual({ ageSeconds: 16, stale: true })
    expect(staleness(null, 5, at)).toEqual({ ageSeconds: null, stale: false })
  })
})

describe('every figure that is not measured reads "not measured", never 0', () => {
  it('in memory, CPU, processes and pressure', () => {
    const s = unmeasured()

    expect(memoryWords(s)).toBe(NotMeasured)
    expect(cpuWords(s)).toBe(NotMeasured)
    expect(pidsWords(s)).toBe(NotMeasured)
    expect(pressureWords(s.memory.pressure, 'Memory')).toBe(NotMeasured)
  })

  it('names a limit that is not measured beside a usage that is', () => {
    const s = sample()
    s.memory.limitBytes = null
    s.memory.percentOfLimit = null

    expect(memoryWords(s)).toBe(`6.0 GB in use, limit ${NotMeasured}`)
  })

  it('says no limit for an unlimited one', () => {
    const s = sample()
    s.pids = { current: 12, limit: null, unlimited: true }

    expect(pidsWords(s)).toBe('12 processes, no limit')
  })

  it('breaks a sparkline at a sample that is not measured rather than drawing 0', () => {
    expect(sparkline([10, null, 50, 100], 30, 10)).toEqual(['0.0,9.0', '20.0,5.0 30.0,0.0'])
  })
})

describe('the runs line', () => {
  it('names the limit and the Manager\'s reserved slot', () => {
    expect(runsWords({ limit: 4, managerReserved: 1, runningCount: 3, waitingCount: 2, running: [], waiting: [] })).toBe(
      '3 of 4 running, 2 waiting, 1 more reserved for a Manager',
    )
  })

  it('says no limit, with no reserved slot, for a limit of 0', () => {
    expect(runsWords({ limit: 0, managerReserved: 0, runningCount: 3, waitingCount: 0, running: [], waiting: [] })).toBe(
      '3 running, no limit, 0 waiting',
    )
  })
})
