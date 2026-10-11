import { describe, expect, it, vi } from 'vitest'
import { nextTick, ref } from 'vue'
import { flushPromises } from '@vue/test-utils'
import { useCursorList, type CursorPage } from '../useCursorList'

interface Row {
  seq: number
}

/** A log of `size` rows, seq 1..size, answering `?before=&take=` the way the keyset-paged routes do. */
function log(size: number) {
  const all = Array.from({ length: size }, (_, i) => ({ seq: size - i }))

  return vi.fn(async (before: number | undefined, take: number): Promise<Row[]> =>
    all.filter((row) => before === undefined || row.seq < before).slice(0, take),
  )
}

const seq = (row: Row) => row.seq

describe('useCursorList', () => {
  it('reads the top first and then strictly older pages', async () => {
    const fetch = log(7)
    const list = useCursorList(fetch, seq, { take: 3 })

    await list.loadMore()
    expect(list.rows.value.map(seq)).toEqual([7, 6, 5])
    expect(list.cursor.value).toBe(5)
    expect(list.exhausted.value).toBe(false)

    await list.loadMore()
    expect(fetch).toHaveBeenLastCalledWith(5, 3)
    expect(list.rows.value.map(seq)).toEqual([7, 6, 5, 4, 3, 2])
  })

  it('is exhausted by a short page and then stops asking', async () => {
    const fetch = log(4)
    const list = useCursorList(fetch, seq, { take: 3 })

    await list.loadMore()
    await list.loadMore()
    expect(list.rows.value.map(seq)).toEqual([4, 3, 2, 1])
    expect(list.exhausted.value).toBe(true)

    await list.loadMore()
    expect(fetch).toHaveBeenCalledTimes(2)
  })

  it('never has two reads in flight', async () => {
    const fetch = log(10)
    const list = useCursorList(fetch, seq, { take: 3 })

    const one = list.loadMore()
    const two = list.loadMore()
    await Promise.all([one, two])

    expect(fetch).toHaveBeenCalledTimes(1)
  })

  it('appends only at the bottom and never duplicates a row', async () => {
    const pages: Row[][] = [[{ seq: 9 }, { seq: 8 }], [{ seq: 8 }, { seq: 7 }]]
    const list = useCursorList(async () => pages.shift() ?? [], seq, { take: 2 })

    await list.loadMore()
    await list.loadMore()

    expect(list.rows.value.map(seq)).toEqual([9, 8, 7])
  })

  it('follows a reader cursor past rows it filtered out', async () => {
    const fetch = vi.fn(async (before: number | undefined): Promise<CursorPage<Row>> =>
      before === undefined ? { rows: [{ seq: 90 }], next: 50 } : { rows: [], next: null },
    )
    const list = useCursorList(fetch, seq)

    await list.loadMore()
    expect(list.cursor.value).toBe(50)

    await list.loadMore()
    expect(fetch).toHaveBeenLastCalledWith(50, 50)
    expect(list.exhausted.value).toBe(true)
  })

  it('treats a page that did not move the cursor as the end', async () => {
    const list = useCursorList(async () => ({ rows: [] as Row[], next: 5 }), seq)

    await list.loadMore()
    await list.loadMore()

    expect(list.exhausted.value).toBe(true)
  })

  it('keeps going after a failed read rather than calling it the end', async () => {
    const fetch = log(5)
    fetch.mockRejectedValueOnce(new Error('offline'))
    const list = useCursorList(fetch, seq, { take: 2 })

    await list.loadMore()
    expect(list.error.value).toBe('offline')
    expect(list.exhausted.value).toBe(false)
    expect(list.loading.value).toBe(false)

    await list.loadMore()
    expect(list.error.value).toBe('')
    expect(list.rows.value.map(seq)).toEqual([5, 4])
  })

  it('clears and refetches from the top when the filter changes', async () => {
    const filter = ref('a')
    const fetch = log(10)
    const list = useCursorList(fetch, seq, { take: 3, filter })

    await list.loadMore()
    await list.loadMore()
    expect(list.rows.value).toHaveLength(6)

    filter.value = 'b'
    await nextTick()
    await flushPromises()

    expect(fetch).toHaveBeenLastCalledWith(undefined, 3)
    expect(list.rows.value.map(seq)).toEqual([10, 9, 8])
  })

  it('drops a page that answers after a reset', async () => {
    let release: (rows: Row[]) => void = () => {}
    const slow = new Promise<Row[]>((resolve) => { release = resolve })
    const fetch = vi.fn<(before: number | undefined, take: number) => Promise<Row[]>>()
      .mockReturnValueOnce(slow)
      .mockResolvedValueOnce([{ seq: 3 }])
    const list = useCursorList(fetch, seq, { take: 1 })

    const stale = list.loadMore()
    await list.reset()
    release([{ seq: 99 }])
    await stale

    expect(list.rows.value.map(seq)).toEqual([3])
  })
})
