import { ref, shallowRef, watch, type Ref, type WatchSource } from 'vue'

/**
 * ONE PAGE OF A KEYSET READ. `next` is the cursor for the page after this one, or null when there is
 * nothing older.
 *
 * Most readers never build one: a plain array is a page whose cursor is its last row's key, which is
 * what every `?before=` route in this product answers. The object form is for a reader whose
 * rows are NOT the server's rows - the activity feed keeps one member's messages out of a page of
 * every team's, so the cursor has to move past rows it threw away.
 */
export interface CursorPage<T> {
  rows: T[]
  next: number | null
}

/**
 * Asked for the rows strictly older than `before`, newest first. `before` is undefined for the top.
 * `take` is passed so a reader cannot disagree with the list about what a short page means.
 */
export type FetchCursorPage<T> = (before: number | undefined, take: number) => Promise<T[] | CursorPage<T>>

export interface CursorListOptions {
  /** Rows asked for per page. The servers default to 50 and clamp at 200. */
  take?: number

  /**
   * A filter. When it changes the list is CLEARED and read again from the top - never merged, because
   * rows kept from the old filter would sit above rows of the new one and read as matching it.
   */
  filter?: WatchSource<unknown>
}

export interface CursorList<T> {
  rows: Ref<T[]>

  /** The lowest key seen, and so the next `before`. Undefined until something has been read. */
  cursor: Ref<number | undefined>
  loading: Ref<boolean>
  exhausted: Ref<boolean>
  error: Ref<string>

  /** Reads the next page, older than every row held. A no-op while loading or once exhausted. */
  loadMore: () => Promise<void>

  /** Clears everything and reads the first page again. */
  reset: () => Promise<void>
}

/**
 * INFINITE SCROLL, AS ONE PIECE OF STATE.
 *
 * The four keyset-paged lists all hold the same four facts - what has been read, how
 * far back that reaches, whether a read is in flight, whether there is anything older - and a sentinel
 * at the bottom of each asks for more. Written once here so the four cannot drift on the two rules that
 * matter:
 *
 * ROWS ARE ONLY EVER APPENDED AT THE BOTTOM. A page is older than everything held, so reading one never
 * moves a row already on screen - which is what lets a person read row 40 while row 90 arrives.
 *
 * A RESPONSE FROM BEFORE A RESET IS DROPPED. A filter typed while a page was in flight would otherwise
 * append rows of the old filter to the new list; the generation counter is what tells them apart.
 */
export function useCursorList<T>(
  fetchPage: FetchCursorPage<T>,
  key: (row: T) => number,
  options: CursorListOptions = {},
): CursorList<T> {
  const take = options.take ?? 50

  const rows = shallowRef<T[]>([])
  const cursor = ref<number | undefined>(undefined)
  const loading = ref(false)
  const exhausted = ref(false)
  const error = ref('')

  let generation = 0

  async function loadMore(): Promise<void> {
    if (loading.value || exhausted.value) return

    const asked = generation
    loading.value = true
    error.value = ''

    try {
      const answer = await fetchPage(cursor.value, take)
      if (asked !== generation) return

      const page: CursorPage<T> = Array.isArray(answer)
        ? {
            rows: answer,
            next: answer.length < take ? null : key(answer[answer.length - 1] as T),
          }
        : answer

      // Deduplicated by key: a row written at the boundary between two reads must not appear twice.
      const before = cursor.value
      const held = new Set(rows.value.map(key))
      const fresh = page.rows.filter((row) => !held.has(key(row)))
      if (fresh.length) rows.value = [...rows.value, ...fresh]

      for (const row of page.rows) {
        const k = key(row)
        if (cursor.value === undefined || k < cursor.value) cursor.value = k
      }

      // The page's own cursor wins when it is lower: a reader that filtered rows out has read further
      // back than the rows it kept.
      if (page.next !== null && (cursor.value === undefined || page.next < cursor.value)) {
        cursor.value = page.next
      }

      // A page that did not move the cursor is exhausted too, whatever it claims - asking again
      // would ask the identical question forever.
      exhausted.value = page.next === null || cursor.value === before
    } catch (cause) {
      if (asked !== generation) return

      // NOT exhausted. A failed read is this browser's problem, and the sentinel asks again.
      error.value = cause instanceof Error ? cause.message : String(cause)
    } finally {
      if (asked === generation) loading.value = false
    }
  }

  async function reset(): Promise<void> {
    generation++
    rows.value = []
    cursor.value = undefined
    exhausted.value = false
    error.value = ''
    loading.value = false

    await loadMore()
  }

  if (options.filter) watch(options.filter, () => void reset(), { deep: true })

  return { rows, cursor, loading, exhausted, error, loadMore, reset }
}
