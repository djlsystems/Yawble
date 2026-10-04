/**
 * TIMES AS THE BROWSER'S OWN LOCALE READS THEM, the one way every chart and dialog shows an instant:
 * a time of day is `toLocaleTimeString()` (with seconds, "3:41:17 PM" in en-US, "15:41:17" in
 * en-GB), and with its date it is `toLocaleString()`, exactly the Workflows dialog's stamp. Nothing
 * here chooses a 12- or 24-hour clock; the locale does.
 *
 * `timeZone` is only for a caller that already buckets in a named zone; left out, the browser's own.
 */
export interface LocalTimeOptions {
  /** With the date in front, as `toLocaleString()` gives it. */
  date?: boolean
  timeZone?: string
}

const zoned = (timeZone: string | undefined): Intl.DateTimeFormatOptions => (timeZone ? { timeZone } : {})

/** An instant's time of day with seconds, or its date and time. */
export function localTime(ms: number, options: LocalTimeOptions = {}): string {
  const at = new Date(ms)

  return options.date
    ? at.toLocaleString(undefined, zoned(options.timeZone))
    : at.toLocaleTimeString(undefined, zoned(options.timeZone))
}

/** An instant's date alone, as `toLocaleDateString()` gives it. */
export function localDate(ms: number, timeZone?: string): string {
  return new Date(ms).toLocaleDateString(undefined, zoned(timeZone))
}

/** Whether two instants fall on different local days: then a time of day alone is ambiguous. */
export function crossesDays(from: number, to: number, timeZone?: string): boolean {
  return localDate(from, timeZone) !== localDate(to, timeZone)
}

/** A stretch by its two ends, "11:38:02 AM – 3:41:17 PM", each with its date when it crosses days. */
export function localStretch(from: number, to: number, timeZone?: string): string {
  const options: LocalTimeOptions = { date: crossesDays(from, to, timeZone) }

  if (timeZone) options.timeZone = timeZone

  return `${localTime(from, options)} – ${localTime(to, options)}`
}
