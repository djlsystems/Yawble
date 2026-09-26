/**
 * THE SHAPE `FileBrowser.vue` TAKES ITS BEHAVIOUR FROM.
 *
 * Documents and the Host picker answer different questions, so they are ONE component with TWO
 * DATA SOURCES: **the look and the navigation unify, the BOUNDARIES do not.** Documents stays bounded by `TeamDocuments.Resolve`; the Host picker stays bounded by
 * `FileBrowserPolicy` and its `FileBrowser:Roots` allowlist. Nothing in this file resolves a path,
 * decides what may be reached, or knows a URL - it is the vocabulary the renderer speaks, and each
 * source keeps its own containment behind it.
 *
 * SO `path` HERE IS AN OPAQUE STRING AND THE BROWSER NEVER TAKES IT APART. Documents paths are
 * `/`-joined and relative to a folder; Host paths are absolute and may use either separator. A
 * renderer that split them would be a third thing resolving a path, which is exactly the boundary
 * this design refuses to move: `TeamDocuments.Resolve` stays the only thing resolving a documents
 * path. The source hands back the crumbs already made, and the browser renders them.
 */

/** One row. Everything that distinguishes Documents' rows from the Host's is DATA here, not a mode
 *  flag the renderer branches on - a download link is a link the source supplied or did not. */
export interface FileBrowserEntry {
  /**
   * WHAT `list` IS CALLED BACK WITH when this row is opened, and what a deletion is addressed by.
   * Opaque: only the source that made it knows what shape it is in.
   */
  path: string

  name: string

  isFolder: boolean

  /** The second line under the name - Documents puts a size there, the Host's roots put their
   *  path. Absent renders no caption rather than an empty one. */
  caption?: string | null

  /** Set to render a download control on this row. Documents supplies one per file; the Host has
   *  no route that would answer it, so its rows simply carry none. */
  downloadUrl?: string | null

  /** Set when a browser tab can show this file: clicking the row opens it in a new tab.
   *  A file row with a download URL and no view URL downloads on click instead. */
  viewUrl?: string | null

  /** Rendered, but not navigable. The Host picker shows file rows disabled so a person can see a
   *  folder is not empty; Documents has no such row. */
  disabled?: boolean
}

/** One step of the trail, carrying the path a click on it navigates to. */
export interface FileBrowserCrumb {
  label: string
  path: string
}

/**
 * Whether New folder and Upload are offered at the location just listed, and it is THREE states
 * rather than two because the two browsers need different ones and both are right.
 *
 * - `allowed` - rendered and live.
 * - `refused` - rendered DISABLED. A place a person could write if something else were true, and
 *   saying so is the point: Documents refuses writes into a DEAD team's folder deliberately, and a
 *   button that vanished would look like the feature had gone rather than been withheld.
 * - `absent` - not rendered at all. Writing is not part of this place, and a control that is
 *   present and dead reads as a bug. This is the state the Host's read-only roots want.
 */
export type FileBrowserWrites = 'allowed' | 'refused' | 'absent'

/** What one call to `list` answers. */
export interface FileBrowserListing {
  /** Echoed back so the browser, and anything reading its `navigated` event, works from what the
   *  source actually opened rather than from what was asked for. */
  path: string

  entries: FileBrowserEntry[]

  /** The whole trail, ROOT FIRST, made by the source. Empty renders no breadcrumb. */
  crumbs: FileBrowserCrumb[]

  writes: FileBrowserWrites

  /** One level up, or `null` at the top. Offered as a `..` row IN ADDITION to the crumbs, for a
   *  source that wants one; a source whose root crumb already says "up" returns `null`. */
  up?: string | null

  /** A final, unclickable row beneath the entries - the Host's "showing the first N of M". A list
   *  that silently stops at a cap reads as a small folder. */
  note?: string | null
}

/**
 * EVERYTHING THE BROWSER CAN DO EXCEPT DELETE. Delete is not here on purpose - see
 * {@link FileBrowserDeletion}.
 */
export interface FileBrowserSource {
  /** Which path to open on. The browser navigates here when the source is first given to it, and
   *  again whenever it is REPLACED - so swapping the source is how a caller changes root. */
  readonly start: string

  list(path: string): Promise<FileBrowserListing>

  /** `name` is a single segment; `path` is the folder to create it in. Composing the two is the
   *  SOURCE's job, because the two surfaces compose them differently and only the source knows
   *  which. */
  createFolder(path: string, name: string): Promise<void>

  upload(path: string, file: File): Promise<void>

  /**
   * A PREVIEW of the server's own name rule, so a person is told before they press the button
   * rather than after. Returns the reason a name is illegal, or `null` when it is fine. Optional:
   * a source with no client-side rule to mirror simply lets the server answer.
   */
  checkName?(name: string): string | null
}

/**
 * DELETE IS A CAPABILITY, NOT A FLAG. A browser handed no `deletion` prop has no delete control at
 * all - absent because it was never passed, rather than present and disabled. That is the
 * acceptance the Host picker has to meet, and `/api/fs` has no delete route to give it one:
 * `/api/fs` keeps exactly its four routes, and adding a fifth is deliberately not done.
 *
 * Documents passes this, and passes it CONDITIONALLY - a dead team's folder is read-only, so the
 * capability is withheld there by the same mechanism.
 */
export interface FileBrowserDeletion {
  /** The control's accessible name, per row - Documents says "Remove folder" for a folder and
   *  "Delete document" for a file, and a person using a screen reader needs the difference. */
  label(entry: FileBrowserEntry): string

  /** A tooltip, when there is something to warn about before the click - Documents says a folder
   *  has to be empty first. `null` renders none. */
  hint?(entry: FileBrowserEntry): string | null

  remove(entry: FileBrowserEntry): Promise<void>
}
