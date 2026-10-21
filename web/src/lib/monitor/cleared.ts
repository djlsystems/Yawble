import type { Message } from '../../api/types'

/**
 * THE ACTIVITY PANE'S CLEAR, remembered in this browser: the seq of the last row held when a person
 * pressed Clear, so the Monitor's window reads from the row after it, a reload included. A
 * convenience only, beside the Monitor's other settings: nothing goes to the server, a replay reads
 * its own period, and storage that is missing or refuses leaves the whole window, as before.
 */
export const ClearedKey = 'harness.monitor.clearedAfter'

/** The seq the window was cleared after, or null when it never was or the mark cannot be read. */
export function readClearedAfter(): number | null {
  try {
    const raw = localStorage.getItem(ClearedKey)
    if (raw === null || !/^\d+$/.test(raw)) return null
    const seq = Number(raw)
    return Number.isSafeInteger(seq) ? seq : null
  } catch {
    return null
  }
}

/** Remember the clear's seq, or forget the mark with null: Show everything again. */
export function writeClearedAfter(seq: number | null) {
  try {
    if (seq === null) localStorage.removeItem(ClearedKey)
    else localStorage.setItem(ClearedKey, String(seq))
  } catch {
    // Remembered only while this page is open.
  }
}

/** The rows after the clear's seq, oldest first; every row when there is no mark. */
export function windowAfter(rows: readonly Message[], after: number | null): readonly Message[] {
  return after === null ? rows : rows.filter((row) => row.seq > after)
}
