/**
 * A stretch of time as a clock reads it: "2h 05m", "3m 07s", "42s". Whole seconds, floored, so a
 * run of 59.9 s reads 59s and never 60s. The KPI strip's execution and elapsed figures and a run's
 * duration in the live view share it.
 */
export function clockDuration(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000))
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const seconds = total % 60

  if (hours > 0) return `${hours}h ${String(minutes).padStart(2, '0')}m`
  if (minutes > 0) return `${minutes}m ${String(seconds).padStart(2, '0')}s`
  return `${seconds}s`
}
