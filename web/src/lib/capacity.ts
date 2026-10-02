import type { CapacitySample, Pressure, WorkerSample } from '../api/types'

/**
 * WHAT THE ACTIVITY MONITOR SAYS ABOUT ONE CAPACITY SAMPLE, as plain functions so the gauge's colour,
 * its tooltip and every figure's words are pinned without mounting anything.
 *
 * THE ONE RULE EVERYTHING HERE KEEPS: a null figure is NOT MEASURED. It reads "not measured" and it
 * never sets a colour - 0 would claim a measurement nobody made.
 */

/** The words for a figure the Host could not read. */
export const NotMeasured = 'not measured'

/** Memory in use, or a pressure line, at or above this is amber. */
export const AmberPercent = 75

/** ...and at or above this is red. */
export const RedPercent = 90

/**
 * A SUSTAINED STALL: some work waited at least this share of the last minute. Any such stall is
 * amber, however low the 10-second figure has dropped - the same 10% the admission gate holds a
 * member at by default.
 */
export const SustainedStallPercent = 10

/**
 * Figures older than this many sampler intervals are STALE: the Host has stopped answering, or the
 * live connection has. Never less than {@link StaleFloorSeconds}.
 */
export const StaleIntervals = 3
export const StaleFloorSeconds = 15

export type GaugeLevel = 'green' | 'amber' | 'red' | 'unmeasured' | 'stale'

export interface Gauge {
  level: GaugeLevel
  /** The tooltip: which figure set the colour, in words. */
  reason: string
}

interface Candidate {
  rank: number
  reason: string
}

const rankOf = (percent: number, sustained = false): number =>
  percent >= RedPercent ? 2 : percent >= AmberPercent || sustained ? 1 : 0

/** `11.2 GB`, in decimal gigabytes as the Host's own "waiting for memory" sentence counts them. */
export function gigabytes(bytes: number): string {
  return `${(bytes / 1e9).toFixed(1)} GB`
}

/** `512 MB` below a gigabyte, `11.2 GB` above. */
export function bytesWords(bytes: number | null): string {
  if (bytes === null) return NotMeasured
  if (bytes < 1e9) return `${Math.round(bytes / 1e6)} MB`

  return gigabytes(bytes)
}

function percent(value: number): string {
  return `${Math.round(value)}%`
}

/** "11.2 of 12.9 GB in use (87%)", "11.2 GB in use, no limit", or "not measured". */
export function memoryWords(sample: CapacitySample): string {
  const { inUseBytes, limitBytes, unlimited, percentOfLimit } = sample.memory
  if (inUseBytes === null) return NotMeasured
  if (unlimited) return `${gigabytes(inUseBytes)} in use, no limit`
  if (limitBytes === null || percentOfLimit === null) return `${gigabytes(inUseBytes)} in use, limit ${NotMeasured}`

  return `${(inUseBytes / 1e9).toFixed(1)} of ${gigabytes(limitBytes)} in use (${percent(percentOfLimit)})`
}

/** "1.6 of 4 CPUs in use (40%)", "1.6 CPUs in use, no limit", or "not measured". */
export function cpuWords(sample: CapacitySample): string {
  const { cpusInUse, limitCpus, unlimited, percentOfLimit } = sample.cpu
  if (cpusInUse === null) return NotMeasured

  const inUse = cpusInUse.toFixed(1)
  if (unlimited) return `${inUse} CPUs in use, no limit`
  if (limitCpus === null || percentOfLimit === null) return `${inUse} CPUs in use, limit ${NotMeasured}`

  return `${inUse} of ${limitCpus} CPUs in use (${percent(percentOfLimit)})`
}

/** "312 of 4096 processes", "312 processes, no limit", or "not measured". */
export function pidsWords(sample: CapacitySample): string {
  const { current, limit, unlimited } = sample.pids
  if (current === null) return NotMeasured
  if (unlimited) return `${current} processes, no limit`
  if (limit === null) return `${current} processes, limit ${NotMeasured}`

  return `${current} of ${limit} processes`
}

/**
 * A resource with a pressure file, by its label. (Capitalised, so the label is never a bare word the
 * icon-font check reads as an icon name.)
 */
export type Resource = 'Memory' | 'CPU'

/** "work waited for memory 14% of the last 10 s", or "not measured". */
export function pressureWords(pressure: Pressure | null, resource: Resource): string {
  if (pressure === null) return NotMeasured

  const noun = resource === 'CPU' ? resource : resource.toLowerCase()
  const now = `work waited for ${noun} ${percent(pressure.some.avg10)} of the last 10 s`
  if (pressure.some.avg60 < SustainedStallPercent) return now

  return `${now}, ${percent(pressure.some.avg60)} of the last minute`
}

/**
 * HOW OLD THE FIGURES ARE, in seconds, and whether that is stale. The Host does not mark staleness:
 * it is the gap between the sample's `at` and this browser's clock.
 */
export function staleness(
  sample: CapacitySample | null,
  intervalSeconds: number,
  now: number,
): { ageSeconds: number | null; stale: boolean } {
  if (!sample) return { ageSeconds: null, stale: false }

  const ageSeconds = Math.max(0, Math.round((now - Date.parse(sample.at)) / 1000))
  const limit = Math.max(StaleFloorSeconds, intervalSeconds * StaleIntervals)

  return { ageSeconds, stale: ageSeconds > limit }
}

/** "8 s", "2 min 5 s", "1 h 3 min". */
export function ageWords(seconds: number): string {
  if (seconds < 60) return `${seconds} s`
  if (seconds < 3600) return seconds % 60 ? `${Math.floor(seconds / 60)} min ${seconds % 60} s` : `${seconds / 60} min`

  return `${Math.floor(seconds / 3600)} h ${Math.floor((seconds % 3600) / 60)} min`
}

/**
 * THE GAUGE'S COLOUR: the WORST of memory in use against its limit, memory pressure and CPU
 * pressure. Green below {@link AmberPercent}; amber from it, or for any sustained stall; red from
 * {@link RedPercent}. A figure that is not measured takes no part; when none is measured the gauge
 * says so rather than reading green. Stale figures colour nothing - they say how old they are.
 */
export function gauge(sample: CapacitySample | null, age: { ageSeconds: number | null; stale: boolean }): Gauge {
  if (!sample) return { level: 'unmeasured', reason: 'No figures yet' }
  if (age.stale && age.ageSeconds !== null) {
    return { level: 'stale', reason: `Figures are stale: last measured ${ageWords(age.ageSeconds)} ago` }
  }

  const candidates: Candidate[] = []
  const memory = sample.memory.percentOfLimit
  if (memory !== null) {
    candidates.push({ rank: rankOf(memory), reason: `Memory: ${memoryWords(sample)}` })
  }

  for (const [pressure, resource] of [
    [sample.memory.pressure, 'Memory'],
    [sample.cpu.pressure, 'CPU'],
  ] as const) {
    if (pressure === null) continue

    const sustained = pressure.some.avg60 >= SustainedStallPercent
    candidates.push({
      rank: rankOf(pressure.some.avg10, sustained),
      reason: `${resource} pressure: ${pressureWords(pressure, resource)}`,
    })
  }

  if (candidates.length === 0) return { level: 'unmeasured', reason: `Memory and pressure: ${NotMeasured}` }

  // The FIRST worst wins a tie, so memory in use - the figure admission holds runs on - names it.
  const worst = candidates.reduce((best, next) => (next.rank > best.rank ? next : best))
  const level: GaugeLevel = worst.rank === 2 ? 'red' : worst.rank === 1 ? 'amber' : 'green'

  return { level, reason: worst.reason }
}

/** How full the gauge's arc is: memory in use against its limit, 0 when that is not measured. */
export function gaugeFill(sample: CapacitySample | null): number {
  const value = sample?.memory.percentOfLimit
  if (value === null || value === undefined) return 0

  return Math.min(100, Math.max(0, value))
}

/**
 * A SPARKLINE's points, as an SVG polyline over `width` x `height`, for a 0-100 percentage series.
 * A sample whose figure is not measured BREAKS the line rather than drawing a 0: each run of measured
 * samples is its own polyline.
 */
export function sparkline(values: (number | null)[], width: number, height: number): string[] {
  if (values.length === 0) return []

  const step = values.length > 1 ? width / (values.length - 1) : 0
  const segments: string[] = []
  let current: string[] = []

  values.forEach((value, index) => {
    if (value === null) {
      if (current.length) segments.push(current.join(' '))
      current = []
      return
    }

    const clamped = Math.min(100, Math.max(0, value))
    const x = (index * step).toFixed(1)
    const y = (height - (clamped / 100) * height).toFixed(1)
    current.push(`${x},${y}`)
  })
  if (current.length) segments.push(current.join(' '))

  return segments
}

/** "Developer on alpha", or "the Concierge" for a lease party with no team. */
export function partyWords(party: { team: string | null; member: string }): string {
  return party.team === null ? `the ${party.member}` : `${party.member} on ${party.team}`
}

/**
 * The runs line: "3 of 4 running, 2 waiting, 1 reserved for a Manager". A limit of 0 is no limit,
 * and has no reserved slot to name.
 */
/**
 * One worker in words: its memory against its own limit, its CPUs and its bound, each "not measured"
 * where it is, and whether it is dropped.
 */
export function workerWords(worker: WorkerSample): string {
  const capacity = worker.capacity
  const memory =
    capacity.memoryInUseBytes !== null && capacity.memoryLimitBytes !== null
      ? `${(capacity.memoryInUseBytes / 1e9).toFixed(1)} of ${gigabytes(capacity.memoryLimitBytes)} in use`
      : `memory ${NotMeasured}`
  const cpus = capacity.cpus === null ? `CPUs ${NotMeasured}` : `${capacity.cpus} CPU${capacity.cpus === 1 ? '' : 's'}`
  const bound = capacity.bound === null ? '' : `, up to ${capacity.bound} run${capacity.bound === 1 ? '' : 's'}`
  const runs = `${worker.runs.length} running`
  const state = worker.state === 'dropped' ? ' - connection dropped, waiting for it to come back' : ''

  return `${memory}, ${cpus}${bound}, ${runs}${state}`
}

export function runsWords(runs: CapacitySample['runs']): string {
  const running = runs.limit === 0 ? `${runs.runningCount} running, no limit` : `${runs.runningCount} of ${runs.limit} running`
  const reserved = runs.managerReserved > 0 ? `, ${runs.managerReserved} more reserved for a Manager` : ''

  return `${running}, ${runs.waitingCount} waiting${reserved}`
}
