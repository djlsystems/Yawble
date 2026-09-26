import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from '../../api/documents'
import { asDocumentsFolderKey, asTeamId } from '../../api/types'

/**
 * THE URLS THE DOCUMENTS SURFACE ACTUALLY BUILDS, AND NOT ONE MOCK OF THE MODULE THAT BUILDS THEM.
 *
 * The mounted specs over the Documents dialog and its ribbon item begin
 * `vi.mock('../../api/documents', ...)` - so the only code in the product that knows a URL is
 * replaced by a function that returns the right answer, and those specs prove only that the
 * components render what they are given. They would stay green if every path were wrong. A
 * FIXTURE CHOOSES WHICH HALF OF A CONTRACT YOU CAN SEE, and mocking the module under test chooses
 * the half that cannot be wrong.
 *
 * So: no mock of `api/documents`. `fetch` is stubbed at the boundary the browser actually crosses,
 * and the assertions are the STRINGS. They are duplicated from the route templates on purpose -
 * that duplication is the test. If somebody re-spells a route on the server, this is what goes red,
 * and it goes red naming both halves.
 *
 * WHAT IT STILL CANNOT SEE, stated so nobody reads more into a green run than is there: that the
 * server has a handler at these paths. Only a real host can answer that, and
 * `DocumentsOutliveTheirTeamTests` does - it drives the same routes over real HTTP and reads the
 * raw `JsonElement`. The pair is the contract; neither half is.
 */
describe('the documents client addresses the routes the Host serves', () => {
  let fetching: ReturnType<typeof vi.fn>

  beforeEach(() => {
    fetching = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({ folders: [] }),
      text: async () => '{}',
    })

    vi.stubGlobal('fetch', fetching)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  /** The url of the single call made, which is every case's subject. */
  const called = () => String(fetching.mock.calls[0]?.[0])

  const init = () => fetching.mock.calls[0]?.[1] as RequestInit | undefined

  /**
   * `/api/documents`, NOT `/api/documents/teams`. The route declares no `{team}` because the list
   * spans every folder there is: a folder outlives the team that wrote
   * it, so the list is strictly larger than the team list.
   */
  it('lists the folders from the tenant route', async () => {
    await api.listDocumentFolders()

    expect(called()).toBe('/api/documents')
  })

  /**
   * IT UNWRAPS AN ENVELOPE. The server answers `{ folders: [...] }` and this returns the array, so
   * every caller gets the shape its type promises. Asserted because a path fix alone would have
   * left the dialog rendering `undefined.length` and reading as an empty instance.
   */
  it('unwraps the folders envelope rather than handing back the body', async () => {
    fetching.mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        folders: [
          {
            folder: 'alpha',
            team: 'alpha',
            label: 'Alpha',
            exists: true,
            retired: false,
            entries: 3,
            modifiedAt: '2026-09-20T00:00:00Z',
          },
        ],
      }),
      text: async () => '{}',
    })

    const folders = await api.listDocumentFolders()

    expect(folders).toHaveLength(1)
    expect(folders[0]?.label).toBe('Alpha')
  })

  /**
   * THE TEAM SEGMENT IS THE FOLDER KEY, and for a retired folder that is NOT the team identifier.
   * Addressing by team would reach the successor team's documents - a 200, the wrong files, and
   * nothing anywhere saying so - which is why the key is branded and why this case uses a key that
   * could not be a legal team name.
   */
  it('browses under /api/teams/{folder}/documents, keyed by the folder', async () => {
    await api.listDocuments(asDocumentsFolderKey('alpha~2026-09-01'), 'reports', true)

    expect(called()).toBe(
      '/api/teams/alpha~2026-09-01/documents?path=reports&recursive=true',
    )
  })

  it('shows from the view route', () => {
    expect(api.documentViewUrl(asDocumentsFolderKey('alpha'), 'notes/plan.md'))
      .toBe('/api/teams/alpha/documents/view?path=notes%2Fplan.md')
  })

  it('downloads from the content route', () => {
    expect(api.documentUrl(asDocumentsFolderKey('alpha'), 'notes/plan.md'))
      .toBe('/api/teams/alpha/documents/content?path=notes%2Fplan.md')
  })

  it('creates a folder by POST to the folders route', async () => {
    await api.createFolder(asDocumentsFolderKey('alpha'), 'reports/q3')

    expect(called()).toBe('/api/teams/alpha/documents/folders')
    expect(init()?.method).toBe('POST')
    expect(init()?.body).toBe(JSON.stringify({ path: 'reports/q3' }))
  })

  /**
   * NO `content-type` ON THE UPLOAD. The browser sets it, and the multipart boundary it generates
   * is part of that value - setting it by hand produces a body the server cannot parse. Asserted
   * rather than commented, because it is the kind of line somebody adds for consistency.
   */
  it('uploads as multipart with no content-type of its own', async () => {
    const file = new File(['x'], 'measured.csv')
    await api.uploadDocument(asDocumentsFolderKey('alpha'), file, 'reports')

    expect(called()).toBe('/api/teams/alpha/documents/upload')
    expect(init()?.method).toBe('POST')
    expect(init()?.headers).toBeUndefined()
    expect(init()?.body).toBeInstanceOf(FormData)
  })

  it('deletes with the path in the query', async () => {
    await api.deleteDocument(asDocumentsFolderKey('alpha'), 'reports/old.md')

    expect(called()).toBe('/api/teams/alpha/documents?path=reports%2Fold.md')
    expect(init()?.method).toBe('DELETE')
  })

  /** `recursive` only when asked for, and an empty path with it is the whole folder. */
  it('sends recursive only when asked, and addresses the whole folder with an empty path', async () => {
    await api.deleteDocument(asDocumentsFolderKey('alpha'), 'reports', true)
    expect(called()).toBe('/api/teams/alpha/documents?path=reports&recursive=true')

    await api.deleteDocument(asDocumentsFolderKey('gone'), '', true)
    expect(String(fetching.mock.calls[1]?.[0])).toBe('/api/teams/gone/documents?path=&recursive=true')
    expect((fetching.mock.calls[1]?.[1] as RequestInit | undefined)?.method).toBe('DELETE')
  })

  /**
   * THE WRONG SPELLING, NAMED. Not a style rule - `/api/documents/teams/...` is an easy guess, and
   * a reader who writes it should be told by a failing test rather than by a 404 in front of
   * somebody.
   */
  it('never addresses the path family that did not exist', async () => {
    await api.listDocumentFolders()
    await api.listDocuments(asDocumentsFolderKey('alpha'))
    await api.createFolder(asDocumentsFolderKey('alpha'), 'x')
    await api.deleteDocument(asDocumentsFolderKey('alpha'), 'x')

    const urls = fetching.mock.calls.map(([input]) => String(input))

    expect(urls).not.toContain(expect.stringContaining('/api/documents/teams'))
    for (const url of urls) expect(url.startsWith('/api/documents/teams')).toBe(false)
  })

  /**
   * A TEAM ID IS NOT A FOLDER KEY, held at the type level. This case is here so the brand has a
   * reason on the record: it is compile-time only, so nothing at runtime would ever complain, and
   * a later edit that loosens the signature to `string` would leave every other case in this file
   * green.
   */
  it('keeps the folder key and the team id apart at the type level', () => {
    const team = asTeamId('alpha')

    // @ts-expect-error a TeamId is not a DocumentsFolderKey - see DocumentsFolder.folder
    expect(() => api.documentUrl(team, 'x')).not.toThrow()
  })
})
