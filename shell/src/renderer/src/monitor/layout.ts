/**
 * The Monitor's layout forces, as the person tunes them (the cog beside Pinned). Each is a multiplier on the scene's own
 * value (1 is the scene's own). The defaults are a tuned set: short, stiff links, little repulsion, strong pull home and
 * low friction. Remembered in this app's storage, a convenience only: storage may be missing or refuse, and the defaults stand.
 */
export interface LayoutSettings {
  /** How long the links are: members (and the workflow panel) from their team. */
  distance: number;
  /** How hard a link pulls its two ends back to that length. */
  strength: number;
  /** How hard nodes push each other apart. */
  repulsion: number;
  /** How hard everything is pulled toward its team's place on the wall. */
  gravity: number;
  /** How quickly motion dies away: more settles sooner, less keeps it drifting. */
  friction: number;
}

export type LayoutKey = keyof LayoutSettings;

export const LayoutDefaults: Readonly<LayoutSettings> = { distance: 0.3, strength: 2.75, repulsion: 0.35, gravity: 2.4, friction: 0.45 };

/** Every multiplier lies here; a stored one outside is brought inside, one that is not a number is the default. */
export const LayoutMin = 0.25;
export const LayoutMax = 3;
export const LayoutStep = 0.05;

/** The settings in the order the dialog lists them, each with its words. */
export const LayoutFields: readonly { key: LayoutKey; label: string; help: string }[] = [
  { key: 'distance', label: 'Link distance', help: 'How long the links are between a team and its members.' },
  { key: 'strength', label: 'Link strength', help: 'How hard a link pulls back to its length: a spring constant.' },
  { key: 'repulsion', label: 'Repulsion', help: 'How hard nodes push each other apart.' },
  { key: 'gravity', label: 'Gravity', help: "How hard every node is pulled toward its team's place." },
  { key: 'friction', label: 'Friction', help: 'How quickly motion dies away: more settles sooner, less keeps it drifting.' },
];

export const LayoutStorageKey = 'yawble.monitor.layout';

export function clampLayout(value: unknown, fallback = 1): number {
  const n = typeof value === 'number' ? value : typeof value === 'string' && value.trim() !== '' ? Number(value) : Number.NaN;
  if (!Number.isFinite(n)) return fallback;
  const stepped = Math.round(n / LayoutStep) * LayoutStep;
  return Math.min(LayoutMax, Math.max(LayoutMin, Math.round(stepped * 100) / 100));
}

/** The stored settings, each one checked; the defaults for anything missing, unreadable or refused. */
export function readLayout(storage: Pick<Storage, 'getItem'> | null = safeStorage()): LayoutSettings {
  try {
    const raw: unknown = JSON.parse(storage?.getItem(LayoutStorageKey) ?? 'null');
    if (typeof raw !== 'object' || raw === null) return { ...LayoutDefaults };
    const saved = raw as Record<string, unknown>;
    const read = { ...LayoutDefaults };
    for (const { key } of LayoutFields) if (saved[key] !== undefined) read[key] = clampLayout(saved[key], LayoutDefaults[key]);
    return read;
  } catch {
    return { ...LayoutDefaults };
  }
}

export function writeLayout(layout: LayoutSettings, storage: Pick<Storage, 'setItem' | 'removeItem'> | null = safeStorage()): void {
  try {
    if (isDefaultLayout(layout)) storage?.removeItem(LayoutStorageKey);
    else storage?.setItem(LayoutStorageKey, JSON.stringify(layout));
  } catch {
    // Remembered only while the Monitor is open.
  }
}

export function isDefaultLayout(layout: LayoutSettings): boolean {
  return LayoutFields.every(({ key }) => layout[key] === LayoutDefaults[key]);
}

function safeStorage(): Storage | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage;
  } catch {
    return null;
  }
}
