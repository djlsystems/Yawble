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

/**
 * A wire time as an instant, or NaN for what is not a time. The Host means UTC by every time it
 * sends, so a date-time that came without its offset is read as UTC - `new Date` alone would read it
 * as the browser's local time, hours away from the same moment sent with its Z.
 */
export function wireInstant(iso: string): number {
  return Date.parse(/T\d\d:\d\d(:\d\d(\.\d+)?)?$/.test(iso) ? `${iso}Z` : iso)
}

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

/**
 * An instant's calendar day as `2026-10-06`, in the browser's zone. Never the wire string's first ten
 * characters: those are the UTC day, which late in an American evening is already tomorrow. Empty
 * for nothing, or for what is not a time.
 */
export function localDay(iso: string | null | undefined): string {
  if (!iso) return ''

  const at = new Date(iso)
  if (Number.isNaN(at.getTime())) return ''

  const pad = (value: number) => String(value).padStart(2, '0')

  return `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}`
}

/** An ISO-8601 instant inside text: a date, `T`, a time to the minute or finer, and its zone. */
const IsoInstant = /\b\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})(?![\w:])/g

/**
 * Text with every ISO-8601 instant in it read in the browser's zone and format, so a line a package
 * or a log fills ("last checked 2026-10-07T14:41:07Z") says "last checked 10/7/2026, 3:41:07 PM" on
 * a London browser. The rest of the text is left exactly as it was.
 */
export function localInstants(text: string): string
export function localInstants(text: string | null | undefined): string | null | undefined
export function localInstants(text: string | null | undefined): string | null | undefined {
  if (!text) return text

  return text.replace(IsoInstant, (iso) => {
    const at = Date.parse(iso)
    return Number.isNaN(at) ? iso : localTime(at, { date: true })
  })
}
