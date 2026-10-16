/**
 * THE MONITOR'S LAYOUT FORCES, as a person tunes them in the Monitor's settings. Each is a
 * multiplier on what the scene uses by default (1 is the scene's own), so a setting keeps its
 * meaning when the defaults change. Remembered per browser, a convenience only: storage may be
 * missing or refuse, and the defaults stand.
 */
export interface LayoutSettings {
  /** How long the links are: members from their team, files from their project. */
  distance: number
  /** How hard a link pulls its two ends back to that length. */
  strength: number
  /** How hard nodes push each other apart. */
  repulsion: number
  /** How hard everything is pulled toward its team's place on the stage. */
  gravity: number
  /** How quickly motion dies away: more settles sooner, less keeps it drifting. */
  friction: number
}

export type LayoutKey = keyof LayoutSettings

export const LayoutDefaults: Readonly<LayoutSettings> = { distance: 1, strength: 1, repulsion: 1, gravity: 1, friction: 1 }

/** Every multiplier lies here; a stored one outside is brought inside, one that is not a number is the default. */
export const LayoutMin = 0.25
export const LayoutMax = 3
export const LayoutStep = 0.05

/** The settings in the order the panel lists them, each with its words. */
export const LayoutFields: readonly { key: LayoutKey; label: string; help: string }[] = [
  { key: 'distance', label: 'Link distance', help: 'How long the links are between a team and its members, and a project and its files.' },
  { key: 'strength', label: 'Link strength', help: 'How hard a link pulls back to its length: a spring constant.' },
  { key: 'repulsion', label: 'Repulsion', help: 'How hard nodes push each other apart.' },
  { key: 'gravity', label: 'Gravity', help: "How hard every node is pulled toward its team's place." },
  { key: 'friction', label: 'Friction', help: 'How quickly motion dies away: more settles sooner, less keeps it drifting.' },
]

export const LayoutKeyName = 'harness.monitor.layout'

export function clampLayout(value: unknown, fallback = 1): number {
  const n = typeof value === 'number' ? value : typeof value === 'string' ? Number(value) : Number.NaN
  if (!Number.isFinite(n)) return fallback
  return Math.min(LayoutMax, Math.max(LayoutMin, Math.round(n / LayoutStep) * LayoutStep))
}

export function readLayout(): LayoutSettings {
  try {
    const raw: unknown = JSON.parse(localStorage.getItem(LayoutKeyName) ?? 'null')
    if (typeof raw !== 'object' || raw === null) return { ...LayoutDefaults }
    const saved = raw as Record<string, unknown>
    const read = { ...LayoutDefaults }
    for (const { key } of LayoutFields) if (saved[key] !== undefined) read[key] = clampLayout(saved[key], LayoutDefaults[key])
    return read
  } catch {
    return { ...LayoutDefaults }
  }
}

export function writeLayout(layout: LayoutSettings) {
  try {
    localStorage.setItem(LayoutKeyName, JSON.stringify(layout))
  } catch {
    // Remembered only while this page is open.
  }
}

export function isDefaultLayout(layout: LayoutSettings): boolean {
  return LayoutFields.every(({ key }) => layout[key] === LayoutDefaults[key])
}
