import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  commentKanbanCard,
  editKanbanCard,
  getKanbanBoard,
  getKanbanCard,
  kanbanQuery,
  moveKanbanCard,
  type KanbanFilters,
} from '../kanban'
import { asTeamId } from '../types'

/**
 * The kanban half of the HTTP contract, exercised through the real `send`/`json` helpers with a
 * stubbed `fetch` - the pattern `members.spec.ts` and `teams.spec.ts` follow.
 *
 * What is worth pinning here is the URLs and the bodies, because those are the contract shared
 * with the backend: a filter spelled differently on the two sides is a board that fetches
 * successfully and comes back with the wrong cards.
 */
describe('the board query', () => {
  it('is empty when nothing is filtered', () => {
    expect(kanbanQuery({})).toBe('')
  })

  /** A fixed order, so two equal filters produce one URL a test, a cache and a log can compare. */
  it('writes the filters in the contract order regardless of how they were set', () => {
    expect(kanbanQuery({ status: 'running', member: 'Delia', team: 'Beta' })).toBe(
      '?team=Beta&member=Delia&status=running',
    )
  })

  /**
   * THREE FILTERS, AND `workflow`, `from` AND `to` ARE NOT AMONG THEM.
   *
   * Neither the UI nor the route has them. This end does not SEND them and the server does not
   * accept them - so this asserts on a cast object rather than a typed one, because the type does
   * not admit the keys and the thing worth pinning is what happens when they arrive anyway. A stray
   * key silently riding the query string to a route that does not bind it is invisible from either
   * side alone.
   */
  it('sends nothing for the three parameters outside the contract', () => {
    const stale = {
      team: 'Beta',
      workflow: '1842',
      from: '2026-09-01',
      to: '2026-09-19',
    } as unknown as KanbanFilters

    expect(kanbanQuery(stale)).toBe('?team=Beta')
  })

  it('drops an empty value rather than sending a filter with nothing in it', () => {
    expect(kanbanQuery({ status: 'running', member: '' })).toBe('?status=running')
  })

  it('encodes a value that would otherwise break the query string', () => {
    expect(kanbanQuery({ member: 'Delia One&x=1' })).toBe('?member=Delia%20One%26x%3D1')
  })

  /**
   * THE TEAM IS A FILTER.
   *
   * On `/api/teams/{team}/kanban/board` the team is a path segment `TeamGate` reads, so a `team=`
   * beside it would be a second, UNGATED answer to the same question; that route refuses one.
   *
   * The board is fetched from `/api/kanban/board`, which declares no `{team}` and resolves the
   * caller's effective teams itself. There is no path segment for this to contradict, and the
   * server intersects rather than substitutes - so `team` can only narrow. Stripping it here would
   * make the filter bar's team control inert: it would change the store, and nothing on the wire.
   */
  it('writes the team as a filter, because there is no path segment to contradict', () => {
    expect(kanbanQuery({ team: 'someone-else', status: 'running' } as KanbanFilters)).toBe(
      '?team=someone-else&status=running',
    )
  })

  it('drops an empty team the same way it drops any other empty filter', () => {
    expect(kanbanQuery({ team: '', status: 'running' })).toBe('?status=running')
  })
})

describe('the kanban endpoints', () => {
  let fetcher: ReturnType<typeof vi.fn>

  beforeEach(() => {
    fetcher = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ lanes: [], cards: [] }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    )

    vi.stubGlobal('fetch', fetcher)
  })

  afterEach(() => vi.unstubAllGlobals())

  // A LITERAL AT THE EDGE, which is what `asTeamId` is for: these paths are team-scoped, and the
  // brand is what makes a call site passing an unbranded string fail to compile.
  const team = asTeamId('researchkanban')

  const pathOf = () => fetcher.mock.calls[0]?.[0] as string
  const initOf = () => fetcher.mock.calls[0]?.[1] as RequestInit
  const bodyOf = () => JSON.parse(initOf().body as string)

  /**
   * THE BOARD IS ADDRESSED AT A ROUTE THAT NAMES NO TEAM, and it takes no team argument to give it
   * one. The only team the store could pass was the console's ACTIVE team, which is how the filter
   * bar's team control came to change the store and nothing else.
   */
  it('fetches the board from the tenant route, with every filter on the query string', async () => {
    await getKanbanBoard({ team: 'Beta', member: 'Delia' })

    expect(pathOf()).toBe('/api/kanban/board?team=Beta&member=Delia')
  })

  it('fetches an unfiltered board with no trailing question mark', async () => {
    await getKanbanBoard()

    expect(pathOf()).toBe('/api/kanban/board')
  })

  /**
   * THE PER-TEAM ROUTE IS UNCHANGED AND MUST STAY THAT WAY. It is what the CLI and a container
   * read, and it is still gated by its `{team}` declaration. Card writes address it with the
   * CARD'S own team, which is what the calls below are exercising.
   */
  it('still addresses a card under its own team', async () => {
    await getKanbanCard(team, 'a')

    expect(pathOf()).toBe('/api/teams/researchkanban/kanban/cards/a')
  })

  /** A card id is derived from a correlation and a member, so it can carry characters a path
   *  cannot. Encoded once, here, rather than at each of the four call sites. */
  it('encodes the card id in every card path', async () => {
    await getKanbanCard(team, 'researchkanban/1842/Developer Delia')

    expect(pathOf()).toBe(
      '/api/teams/researchkanban/kanban/cards/researchkanban%2F1842%2FDeveloper%20Delia',
    )
  })

  it('posts a move with the lane it is moving to', async () => {
    await moveKanbanCard(team, 'card-1', 'review')

    expect(pathOf()).toBe('/api/teams/researchkanban/kanban/cards/card-1/move')
    expect(initOf().method).toBe('POST')
    expect(bodyOf()).toEqual({ laneId: 'review' })
  })

  /** An absent note is ABSENT. `note: ''` would be a second spelling of "no note" for the
   *  projection to interpret, and it appends the message either way. */
  it('omits the note when there was none', async () => {
    await moveKanbanCard(team, 'card-1', 'review', '')

    expect('note' in bodyOf()).toBe(false)
  })

  it('sends the note when there was one', async () => {
    await moveKanbanCard(team, 'card-1', 'review', 'picked up by hand')

    expect(bodyOf()).toEqual({ laneId: 'review', note: 'picked up by hand' })
  })

  it('posts an edit with only the fields that changed', async () => {
    await editKanbanCard(team, 'card-1', { title: 'A better title' })

    expect(pathOf()).toBe('/api/teams/researchkanban/kanban/cards/card-1/edit')
    expect(bodyOf()).toEqual({ title: 'A better title' })
  })

  /**
   * A NOTE IS HOW A PERSON SAYS SOMETHING TO THE MANAGER NOW.
   *
   * This replaces a spec asserting that an `instruction` field rode the edit endpoint. It passed,
   * and the feature it described never worked once: the server's edit record has no such property,
   * so binding dropped the field and the append changed nothing. The spec could not see that
   * because it stopped at the request body - the half that WAS correct.
   */
  it('posts a note through edit', async () => {
    await editKanbanCard(team, 'card-1', { note: 'try the other repo' })

    expect(bodyOf()).toEqual({ note: 'try the other repo' })
  })

  it('posts a comment', async () => {
    await commentKanbanCard(team, 'card-1', 'looks stuck')

    expect(pathOf()).toBe('/api/teams/researchkanban/kanban/cards/card-1/comment')
    expect(bodyOf()).toEqual({ text: 'looks stuck' })
  })

  /**
   * A write must survive a 204. These endpoints append a message and are not required to answer
   * with a body - and `response.json()` on an empty one throws, which would surface a successful
   * append as a parse failure and invite the person to click again.
   */
  it('accepts an empty response body from a write', async () => {
    fetcher.mockResolvedValue(new Response(null, { status: 204 }))

    await expect(commentKanbanCard(team, 'card-1', 'still fine')).resolves.toBeUndefined()
  })

  /** The server's own refusal reaches the person, rather than a bare status code. */
  it('surfaces the server error text from a refused write', async () => {
    fetcher.mockResolvedValue(
      new Response(JSON.stringify({ error: 'No such lane.' }), { status: 400 }),
    )

    await expect(moveKanbanCard(team, 'card-1', 'nowhere')).rejects.toThrow('No such lane.')
  })
})
