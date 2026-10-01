import type { CapacitySample, Pressure } from '../api/types'

/** A pressure file's figures: `some` at these percents, no `full`. */
export const pressure = (avg10: number, avg60 = 0): Pressure => ({
  some: { avg10, avg60, avg300: 0, totalUsec: 0 },
  full: null,
})

/** A fully measured, quiet sample at `at`: 6.0 of 12.9 GB, 1.0 of 4 CPUs, no pressure. */
export function sample(over: Partial<CapacitySample> = {}, at = '2026-10-01T10:00:00Z'): CapacitySample {
  return {
    at,
    cgroup: 'v2',
    cpu: {
      limitCpus: 4,
      unlimited: false,
      usageUsec: 1_000_000,
      cpusInUse: 1,
      percentOfLimit: 25,
      throttledPeriods: 0,
      throttledUsec: 0,
      pressure: pressure(0),
    },
    memory: {
      limitBytes: 12.9e9,
      unlimited: false,
      currentBytes: 8e9,
      anonBytes: 5e9,
      fileBytes: 2e9,
      shmemBytes: 1e9,
      inUseBytes: 6e9,
      percentOfLimit: (6e9 / 12.9e9) * 100,
      pressure: pressure(0),
    },
    pids: { current: 312, limit: 4096, unlimited: false },
    notMeasured: [],
    runs: { limit: 4, managerReserved: 1, runningCount: 2, waitingCount: 0, running: [], waiting: [] },
    admission: { memoryPercent: 80, memoryPressurePercent: 10, holding: null },
    topByMemory: [],
    topByCpu: [],
    heavyLease: { holders: 1, holding: [], queued: [] },
    ...over,
  }
}

/** The same sample with memory in use at `percent` of its limit. */
export function memoryAt(percent: number, over: Partial<CapacitySample> = {}): CapacitySample {
  const base = sample(over)
  const inUseBytes = (base.memory.limitBytes! * percent) / 100

  return { ...base, memory: { ...base.memory, inUseBytes, percentOfLimit: percent } }
}

/** A sample from a container where nothing could be read. */
export function unmeasured(over: Partial<CapacitySample> = {}): CapacitySample {
  return sample({
    cgroup: null,
    cpu: {
      limitCpus: null, unlimited: false, usageUsec: null, cpusInUse: null, percentOfLimit: null,
      throttledPeriods: null, throttledUsec: null, pressure: null,
    },
    memory: {
      limitBytes: null, unlimited: false, currentBytes: null, anonBytes: null, fileBytes: null,
      shmemBytes: null, inUseBytes: null, percentOfLimit: null, pressure: null,
    },
    pids: { current: null, limit: null, unlimited: false },
    notMeasured: ['cpu.limit', 'cpu.usage', 'memory.limit', 'memory.current', 'cpu.pressure', 'memory.pressure', 'pids.current', 'pids.limit'],
    ...over,
  })
}
