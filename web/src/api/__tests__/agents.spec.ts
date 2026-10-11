import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { saveCatalog } from '../client'
import { agentsForMode } from '../types'

/**
 * `PUT /api/agents` answers `Results.NoContent()` - a 204 with an EMPTY body, same as
 * `deleteUser`/`resetPassword` in `client.ts`. `json<T>()` calls `response.json()`
 * unconditionally, and parsing an empty body throws a `SyntaxError` - so a `saveAgents` built on
 * `json<void>()` would turn every successful save into a caught failure: the catalog written to
 * disk, but `AgentsDialog` showing an error and skipping its success notification and refetch.
 * This covers the contract through the real `fetch`/`send` path, following `users.spec.ts`'s own
 * 204-handling tests for `deleteUser`/`resetPassword`.
 */
describe('saveAgents', () => {
  beforeEach(() => vi.unstubAllGlobals())
  afterEach(() => vi.unstubAllGlobals())

  it('accepts a 204 with an empty body without trying to parse it', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 204 })))

    await expect(
      saveCatalog([{ name: 'echo', mode: 'Headless', launch: { fileName: 'echo', arguments: [] } }]),
    ).resolves.toBeUndefined()
  })

  /**
   * THE GUARD ON THE MOST DESTRUCTIVE WAY THIS FEATURE COULD FAIL.
   *
   * `PUT /api/agents` replaces the catalog wholesale. Hidden presets are dropped at RENDER, never
   * from the list a screen submits - so a save carrying an unrelated edit must still carry `echo`
   * and `shell` back. Filtering the ref that backs the submission instead would delete them
   * permanently on the next save of anything at all, with nothing failing and no type error.
   */
  it('submits hidden presets back, so an unrelated save does not delete them', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetchMock)

    const whole = [
      { name: 'claude', mode: 'Headless' as const, launch: { fileName: 'claude', arguments: [] } },
      {
        name: 'echo',
        mode: 'Headless' as const,
        hidden: true,
        launch: { fileName: 'cmd.exe', arguments: [] },
      },
    ]

    await saveCatalog(whole)

    const body = JSON.parse((fetchMock.mock.calls[0]![1] as RequestInit).body as string)

    expect(body.agents.map((a: { name: string }) => a.name)).toEqual(['claude', 'echo'])
  })

  /** The catalog carries launch definitions only. What an agent is told comes from the build. */
  it('sends no prompts', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetchMock)

    await saveCatalog([{ name: 'claude', mode: 'Headless', builtIn: true }])

    const body = JSON.parse((fetchMock.mock.calls[0]![1] as RequestInit).body as string)

    expect(Object.keys(body)).toEqual(['agents'])
  })

  it('surfaces the server’s own refusal wording', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({ error: "'echo' is what Manager in Alpha runs, and this catalog leaves it with no headless command. Either send it back with its command, or point that member at another Agent from its settings first and then remove this one." }),
          { status: 400, headers: { 'content-type': 'application/json' } },
        ),
      ),
    )

    await expect(saveCatalog([])).rejects.toThrow(/Manager in Alpha/)
  })
})

/**
 * `mode` is the ONLY thing the shape says about a preset's world, so `agentsForMode` reads it
 * directly; there is no `hasCommand`/`hasInteractive` on the wire. The second case keeps the
 * filter honest on a record with nothing but `name` and `mode` - a hand-written catalog entry with
 * no `launch` yet - since the route redacts nothing for anyone.
 */
describe('agentsForMode', () => {
  it('keeps only the requested mode, on the full shape', () => {
    const agents = [
      { name: 'claude', mode: 'Headless' as const, launch: { fileName: 'claude', arguments: [] } },
      { name: 'shell', mode: 'Interactive' as const, launch: { fileName: 'bash', arguments: [] } },
    ]

    expect(agentsForMode(agents, 'Headless').map((a) => a.name)).toEqual(['claude'])
    expect(agentsForMode(agents, 'Interactive').map((a) => a.name)).toEqual(['shell'])
  })

  it('keeps only the requested mode, on a record carrying nothing but name and mode', () => {
    const agents = [
      { name: 'claude', mode: 'Headless' as const },
      { name: 'shell', mode: 'Interactive' as const },
    ]

    expect(agentsForMode(agents, 'Headless').map((a) => a.name)).toEqual(['claude'])
    expect(agentsForMode(agents, 'Interactive').map((a) => a.name)).toEqual(['shell'])
  })
})

describe('agentsForMode and hidden presets', () => {
  it('never offers a hidden preset', () => {
    // The other half: hidden everywhere a person CHOOSES. One chokepoint rather than a filter
    // written out in each of five pickers, so hiding cannot be half-applied.
    const agents = [
      { name: 'claude', mode: 'Headless' as const },
      { name: 'echo', mode: 'Headless' as const, hidden: true },
      { name: 'shell', mode: 'Interactive' as const, hidden: true },
    ]

    expect(agentsForMode(agents, 'Headless').map((a) => a.name)).toEqual(['claude'])
    expect(agentsForMode(agents, 'Interactive')).toEqual([])
  })
})
