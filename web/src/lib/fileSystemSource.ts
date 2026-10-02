import { ref, type Ref } from 'vue'
import { browseFileSystem, createHostDirectory, fileSystemRoots, uploadHostFile } from '../api/client'
import type { DirectoryListing, FileSystemRoot } from '../api/types'
import { breadcrumbs, childOf, isLegalSegment, parentWithin, sortEntries } from './hostPath'
import type { FileBrowserEntry, FileBrowserListing, FileBrowserSource } from './fileBrowser'

/**
 * THE HOST SIDE OF THE FILE BROWSER - the machine's own filesystem, rendered by the shared `FileBrowser`.
 *
 * **THE BOUNDARY DOES NOT MOVE, and that is the entire point of the decision.** Every call below
 * goes through `api/client.ts` and the FOUR routes `/api/fs` has - `roots`, `browse`,
 * `directory`, `upload` - and it stays four. On the server `FileBrowserPolicy` and its
 * `FileBrowser:Roots` allowlist are the only thing bounding any of them, and `.HumansOnly()`
 * on all four is the only thing keeping a machine principal off the Host's filesystem.
 * Sharing a renderer must not move that, so nothing here reaches for a route that does not exist.
 *
 * **THERE IS NO `deletion` HERE, AND THAT IS THE MECHANISM RATHER THAN AN OMISSION.** Delete is a
 * separate optional prop on `FileBrowser`; this module hands back a source and nothing else, so a
 * picker built on it cannot pass one. The control is then ABSENT because it was never passed -
 * not present and disabled, and not hidden behind a flag. `/api/fs` has no delete route to give it
 * one, and adding a fifth is ruled out.
 *
 * NOTHING HERE SPLITS OR REJOINS A PATH THAT THE RENDERER THEN RE-PARSES. `hostPath.ts` is used
 * unchanged - `breadcrumbs`, `parentWithin`, `childOf` - and what it produces is handed over
 * already made. Host paths are absolute and may use either separator; a renderer that took them
 * apart would be a THIRD thing resolving them, which is the boundary the decision refuses to move.
 */

/** What `hostFileBrowser` hands back: the source, and the one fact ABOVE the browser needs. */
export interface HostBrowser {
  source: FileBrowserSource

  /**
   * The root the current listing sits inside, or `null` at the roots level.
   *
   * A ref because the picker's own caption names it ("Documents", "D:\") and only the source knows
   * which root a path landed in - the browser above it treats every path as opaque. Kept OUT of
   * `FileBrowserListing`: a field only one surface has is exactly what turns one shared row type
   * back into two.
   */
  activeRoot: Ref<FileSystemRoot | null>
}

/**
 * `total` rides with `truncated` in the same server object, so the pair is either both present or
 * neither. The optional types are the client's caution about a server it does not control, and
 * this is where that caution is spent: no note at all rather than a sentence reading "of
 * undefined".
 */
function truncationNote(listing: DirectoryListing): string | null {
  if (listing.truncated !== true || listing.total === undefined) return null

  return `Showing the first ${listing.entries.length} of ${listing.total} — navigate into a `
    + 'subfolder to narrow it down.'
}

/**
 * A browser over the Host's own filesystem, opening at the ROOTS LEVEL.
 *
 * **THE ROOTS ARE A LISTING LIKE ANY OTHER.** `list('')` answers the allowlist as ordinary folder
 * rows, so entering a root is an ordinary row click and there is no second list of rows anywhere
 * in the picker: one breadcrumb, one row template, one empty state.
 *
 * A NEW OBJECT PER OPEN, and the picker must keep it that way: `FileBrowser` re-lists from `start`
 * whenever the source it was given is REPLACED, so a fresh one is both how the dialog starts over
 * and how its "All locations" button gets back to the top. That also re-reads the allowlist once
 * per open.
 */
export function hostFileBrowser(options: { instanceOnly?: boolean } = {}): HostBrowser {
  const activeRoot = ref<FileSystemRoot | null>(null)

  /** The allowlist, fetched ONCE for the life of this source - see the note about a new object per
   *  open. Cached because entering a root has to know which root that is, and asking again on
   *  every navigation would make a second request out of a fact that cannot change under it. */
  let allowlist: FileSystemRoot[] | null = null

  async function roots(): Promise<FileSystemRoot[]> {
    if (allowlist === null) {
      const all = (await fileSystemRoots()).roots

      // `instanceOnly`: the instance's own data root and nothing else, by the server's own flag -
      // for a picker whose answer the Host refuses anywhere outside it (installing a plugin).
      allowlist = options.instanceOnly ? all.filter((root) => root.isInstance) : all
    }

    return allowlist
  }

  /** The roots level. Empty is a REAL state - an operator configured roots and every one of them
   *  was dropped - and the picker says so in the `empty` slot rather than showing a blank list. */
  async function listRoots(): Promise<FileBrowserListing> {
    const available = await roots()
    activeRoot.value = null

    return {
      path: '',
      crumbs: [],

      // Nothing above the allowlist, by construction. This is the top.
      up: null,

      // `absent`, not `refused`: there is no folder here to create anything IN, and a control that
      // is present and dead reads as a bug.
      writes: 'absent',

      entries: available.map((root): FileBrowserEntry => ({
        path: root.path,
        name: root.name,
        isFolder: true,
        caption: root.path,
      })),
    }
  }

  return {
    activeRoot,

    source: {
      start: '',

      async list(path: string): Promise<FileBrowserListing> {
        if (path === '') return listRoots()

        // WHICH ROOT ARE WE IN. Every path that reaches here was made by this source - a root row,
        // a crumb, an `up` target or a child path - so either it IS one of the roots (we are
        // entering it) or it is deeper inside the one we are already in. Set at CALL time rather
        // than when the listing lands, so the caption above the browser changes with the click,
        // which is what the picker has always done.
        const root = (await roots()).find((candidate) => candidate.path === path) ?? activeRoot.value
        activeRoot.value = root

        const listing = await browseFileSystem(path)

        return {
          path: listing.path,

          // `hostPath.ts` UNCHANGED, and it is the only thing here that knows a path has parts.
          // `Crumb` is already `{ label, path }`, which is `FileBrowserCrumb`.
          crumbs: root ? breadcrumbs(root.path, listing.path) : [],

          // UP STOPS AT THE ROOT - `parentWithin` returns null there on purpose, the same
          // guarantee the server's own `parent` field carries, so the `..` row never invites a
          // click the server would refuse. The way back OUT of a root is "All locations", not a
          // parent above it.
          up: root ? parentWithin(root.path, listing.path) : null,

          // `absent` rather than `refused`: today the Host picker HIDES these at a root without
          // `allowCreate`, and that is kept deliberately. Documents is the surface that wants them
          // present and disabled, where the write is withheld from a place a person could
          // otherwise write.
          writes: listing.permissions.allowCreate ? 'allowed' : 'absent',

          note: truncationNote(listing),

          entries: sortEntries(listing.entries).map((entry): FileBrowserEntry => ({
            path: childOf(listing.path, entry.name),
            name: entry.name,
            isFolder: entry.type === 'dir',

            // FOLDER MODE. A file row is rendered but not navigable, so a person can see a folder
            // is not empty even when nothing in it is choosable. A later file mode widens the
            // picker's `mode` type and this line together, deliberately.
            disabled: entry.type === 'file',

            // No `downloadUrl` and no `caption`: `/api/fs` has no content route, and the second
            // line is the roots' own business. Omitted rather than passed as `undefined` -
            // `exactOptionalPropertyTypes` tells those two apart.
          })),
        }
      },

      // A PARENT PLUS A NAME, which is what `POST /api/fs/directory` has always taken. Composing
      // is the source's job because the two surfaces compose differently: Documents posts one
      // relative string instead, and only each source knows which.
      createFolder: async (path: string, name: string) => {
        await createHostDirectory(path, name)
      },

      upload: async (path: string, file: File) => {
        await uploadHostFile(path, file)
      },

      // A PREVIEW of `IsSingleSegmentName` in `FileSystemEndpoints.cs`, so a person is told before
      // they press Create rather than after. The server re-checks regardless of what this says.
      checkName: (name: string) => (isLegalSegment(name) ? null : `'${name}' is not a legal folder name.`),
    },
  }
}
