/**
 * A SORTABLE TABLE'S ORDER, for any table: which column, which way. The Teams table and the
 * Documents explorer's Details view both sort through this, so a header click and a stored order
 * mean the same thing in both.
 */
export interface TableSort<C extends string> {
  column: C
  descending: boolean
}

/**
 * What clicking a column heading does: the same column flips direction, a different one starts
 * ascending.
 *
 * A NEW OBJECT rather than a mutation, so a caller holding a `ref` sees the change.
 */
export function nextSort<C extends string>(current: TableSort<C>, column: C): TableSort<C> {
  return {
    column,
    descending: current.column === column ? !current.descending : false,
  }
}

/**
 * A stored order read back, or the fallback.
 *
 * Read defensively: an unknown column and a non-boolean direction both fall back, because a stored
 * value that survives every comparison and lands in a comparator is how a table ends up sorted by
 * nothing at all. `columns` is the closed list the caller sorts by.
 */
export function readSort<C extends string>(
  raw: string | null,
  columns: readonly C[],
  fallback: TableSort<C>,
): TableSort<C> {
  if (raw === null) return fallback

  try {
    const parsed = JSON.parse(raw) as Partial<TableSort<string>> | null

    if (typeof parsed !== 'object' || parsed === null) return fallback
    if (typeof parsed.descending !== 'boolean') return fallback
    if (typeof parsed.column !== 'string' || !(columns as readonly string[]).includes(parsed.column)) return fallback

    return { column: parsed.column as C, descending: parsed.descending }
  } catch {
    return fallback
  }
}

/** The `aria-sort` a header cell carries: every sortable header has one, `none` when not the sort. */
export function ariaSort<C extends string>(sort: TableSort<C>, column: C): 'ascending' | 'descending' | 'none' {
  if (sort.column !== column) return 'none'

  return sort.descending ? 'descending' : 'ascending'
}
