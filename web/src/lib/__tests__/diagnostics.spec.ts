import { describe, expect, it } from 'vitest'
import type { DiagnosticsFilter } from '../../api/types'
import {
  DiagnosticsEmptyMessage,
  activeDiagnosticsFilterCount,
  diagnosticKindLabel,
  diagnosticsEmptyState,
  diagnosticsQuery,
  diagnosticsVisibleColumns,
} from '../diagnostics'

/** A view with no rows, which is the only interesting case for the empty state. */
const empty = (hasAny: boolean | null) => ({ page: { events: [] as unknown[] }, hasAny })

describe('the diagnostics query', () => {
  it('sends only the page size when nothing is narrowed', () => {
    expect(diagnosticsQuery({})).toBe('?take=50')
  })

  /** A cursor, never an offset. The top page has no `before` at all. */
  it('pages by cursor and never sends skip', () => {
    expect(diagnosticsQuery({}, 120, 50)).toBe('?before=120&take=50')
    expect(diagnosticsQuery({}, 120, 50)).not.toContain('skip')
  })

  /**
   * A FIXED ORDER, the way `kanbanQuery` has one: two equal filters must produce ONE url, which is
   * what lets a test, a cache and a network log compare them by string.
   */
  it('orders the narrowings the same way every time', () => {
    const filter: DiagnosticsFilter = {
      search: 'busy',
      to: '2026-09-19',
      from: '2026-09-18',
      severities: ['Error'],
      kinds: ['db.busy'],
    }

    expect(diagnosticsQuery(filter, 25, 25)).toBe(
      '?kind=db.busy&severity=Error&from=2026-09-18&to=2026-09-19&search=busy&before=25&take=25',
    )
  })

  /** REPEATED, not comma-joined. The server reads string arrays, and two kinds mean "either" -
   *  which is what a multi-select means to the person using it. */
  it('repeats a parameter per kind and per severity rather than joining them', () => {
    const query = diagnosticsQuery({
      kinds: ['db.busy', 'http.refused'],
      severities: ['Error', 'Warning'],
    })

    expect(query).toContain('kind=db.busy&kind=http.refused')
    expect(query).toContain('severity=Error&severity=Warning')
    expect(query).not.toContain(',')
  })

  /** Cleared has ONE spelling on the wire. An empty array and an empty string are both "not
   *  narrowed", so neither reaches the server as a parameter that means something else. */
  it('drops what became empty instead of sending it empty', () => {
    expect(diagnosticsQuery({ kinds: [], severities: [], from: '', to: '', search: '' })).toBe(
      '?take=50',
    )
  })

  /**
   * THE SEARCH IS SENT EXACTLY AS TYPED. The server takes `%` and `_` literally, so a client that
   * helpfully stripped or normalised something would be a second opinion about the same string -
   * and the person would get results that do not match what the box shows.
   */
  it('encodes a typed wildcard rather than removing it', () => {
    expect(diagnosticsQuery({ search: '100% _busy_' })).toContain(
      `search=${encodeURIComponent('100% _busy_')}`,
    )
  })

  it('counts narrowings for the clear button, and paging is not one', () => {
    expect(activeDiagnosticsFilterCount({})).toBe(0)
    expect(activeDiagnosticsFilterCount({ kinds: [] })).toBe(0)
    expect(
      activeDiagnosticsFilterCount({
        kinds: ['db.busy'],
        severities: ['Error'],
        from: '2026-09-18',
        to: '2026-09-19',
        search: 'x',
      }),
    ).toBe(5)
  })
})

/**
 * THE GRID SAYS WHEN IT IS EMPTY because nothing was captured, versus empty because nothing went
 * wrong. Those are different, and this product has the rule elsewhere - `(unknown)` is a real
 * state.
 */
describe('why the diagnostics grid is empty', () => {
  it('says nothing was ever captured when the store is readable and has never held a row', () => {
    expect(diagnosticsEmptyState(empty(false), {})).toBe('never')
  })

  it('says nothing matched when the store holds rows and the filter excluded them', () => {
    expect(diagnosticsEmptyState(empty(true), { search: 'busy' })).toBe('filtered')
  })

  it('says the store could not be read when it could not answer at all', () => {
    expect(diagnosticsEmptyState(empty(null), {})).toBe('unreadable')
  })

  /** THE THREE ARE THREE. Rendering any two of them the same way is what this whole decision
   *  exists to refuse, so it is asserted directly rather than implied by the cases above. */
  it('answers differently for all three', () => {
    const answers = [
      diagnosticsEmptyState(empty(false), {}),
      diagnosticsEmptyState(empty(true), { search: 'busy' }),
      diagnosticsEmptyState(empty(null), {}),
    ]

    expect(new Set(answers).size).toBe(3)
  })

  /**
   * UNREADABLE BEATS EVERYTHING ELSE. A store that cannot say whether it holds anything cannot be
   * reported as holding nothing, however narrow the filter was - which is the one ordering in this
   * function that could be got wrong without any test noticing.
   */
  it('reports unreadable even under a filter that would explain an empty page', () => {
    expect(diagnosticsEmptyState(empty(null), { kinds: ['db.busy'], search: 'x' })).toBe('unreadable')
  })

  it('has rows to show when it has rows to show', () => {
    expect(diagnosticsEmptyState({ page: { events: [{}] }, hasAny: true }, {})).toBe('rows')
  })

  /** Everything aged out under the retention bound: readable, has held rows, nothing narrowed, and
   *  still nothing to show. Not a fault, and not the same sentence as "nothing was captured". */
  it('tells a trimmed store apart from one that never held anything', () => {
    expect(diagnosticsEmptyState(empty(true), {})).toBe('quiet')
    expect(DiagnosticsEmptyMessage.quiet).not.toBe(DiagnosticsEmptyMessage.never)
  })

  /** Every state a person can land on says something. A blank explanation is the grey line this
   *  screen exists not to be. */
  it('has a sentence for every state that is not rows', () => {
    for (const state of ['unreadable', 'never', 'filtered', 'quiet'] as const) {
      expect(DiagnosticsEmptyMessage[state].length).toBeGreaterThan(0)
    }
  })
})

describe('the kind label', () => {
  it('gives a dotted kind human words', () => {
    expect(diagnosticKindLabel('http.unhandled-exception')).toBe('Unhandled exception')
  })

  /**
   * FALLS BACK TO THE RAW NAME, WHICH IS WHY THE WORDING MAP NEED NOT BE COMPLETE. The vocabulary
   * is SERVED by the host rather than copied into the SPA, so a kind added on the server shows up
   * in the filter the day it ships - reading a little more technically until somebody writes it a
   * sentence. A map that had to be exhaustive would be the second place the vocabulary lives, which
   * is exactly what serving it avoids.
   */
  it('shows an unknown kind rather than nothing at all', () => {
    expect(diagnosticKindLabel('quota.exceeded')).toBe('quota.exceeded')
  })
})

describe('diagnosticsVisibleColumns', () => {
  it('keeps when, severity, kind and message below md', () => {
    expect(diagnosticsVisibleColumns(true)).toEqual(['occurredAt', 'severity', 'kind', 'message'])
  })

  it('shows every column from md up', () => {
    expect(diagnosticsVisibleColumns(false)).toBeUndefined()
  })
})
