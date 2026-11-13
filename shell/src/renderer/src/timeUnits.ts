// Timeouts are kept in seconds; the Guardrails page shows them as a number and a unit. A value shows in the largest
// unit that divides it exactly (7200 s is 2 hours, 1800 s is 30 minutes, 90 s stays 90 seconds), so nothing is lost
// to rounding, and saving gives back the same seconds.
export type TimeUnit = 'seconds' | 'minutes' | 'hours';

export const UNIT_SECONDS: Record<TimeUnit, number> = { seconds: 1, minutes: 60, hours: 3600 };

export function fromSeconds(sec: number): { value: number; unit: TimeUnit } {
  if (!Number.isInteger(sec)) return { value: sec, unit: 'seconds' };
  if (sec !== 0 && sec % 3600 === 0) return { value: sec / 3600, unit: 'hours' };
  if (sec % 60 === 0) return { value: sec / 60, unit: 'minutes' };
  return { value: sec, unit: 'seconds' };
}

/** The seconds a value in a unit stands for (not a whole number when the value is not, e.g. 1.5 minutes is 90). */
export function toSeconds(value: number, unit: TimeUnit): number {
  return Math.round(value * UNIT_SECONDS[unit] * 1e6) / 1e6;
}
