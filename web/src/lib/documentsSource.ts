import * as api from '../api/documents'
import type { DocumentEntry } from '../api/documents'
import type { DocumentsFolderKey } from '../api/types'
import { isViewable } from './documentView'
import type {
  FileBrowserCrumb,
  FileBrowserDeletion,
  FileBrowserEntry,
  FileBrowserListing,
  FileBrowserSource,
} from './fileBrowser'

/**
 * THE DOCUMENTS SIDE OF THE SHARED FILE BROWSER - one folder of the TENANT documents root, rendered by the shared
 * `FileBrowser`.
 *
 * **THE BOUNDARY DOES NOT MOVE, and that is the entire point of the design.** Every call below
 * goes through `api/documents.ts` and its six routes - `GET /api/documents`, `GET|DELETE
 * /api/teams/{team}/documents`, `+/content`, `+/folders`, `+/upload`. Those routes declare
 * `{team}`, which is what puts them inside `TeamGate` STRUCTURALLY, and on the server
 * `TeamDocuments.Resolve` is the
 * only thing that resolves a documents path: it compares the RESOLVED path against the root rather
 * than searching for `..`, and an upload keeps only the leaf. Sharing a renderer must not move any
 * of that, so nothing here resolves, joins or inspects a path except to compose the one relative
 * string `POST +/folders` takes.
 *
 * NOTHING HERE KNOWS ABOUT `exists` OR `retired` EITHER. Those are two facts with different fixes
 * and they carry two different sentences; they are the DIALOG's chrome, above the browser. All this
 * is told is `readOnly`, which is the one consequence of them it has to act on.
 */

/** Documents paths are `/`-joined and relative to the folder, so `''` IS the root - not a falsy
 *  accident. The same trail `DocumentsDialog` has always drawn, with its `docs` crumb first. */
function trailOf(path: string): FileBrowserCrumb[] {
  const parts = path.split('/').filter(Boolean)

  return [
    { label: 'docs', path: '' },
    ...parts.map((name, index) => ({ label: name, path: parts.slice(0, index + 1).join('/') })),
  ]
}

function sizeOf(entry: DocumentEntry): string {
  if (entry.isFolder) return `${entry.children} item(s)`
  if (entry.size < 1024) return `${entry.size} B`
  if (entry.size < 1024 * 1024) return `${Math.round(entry.size / 1024)} KB`

  return `${(entry.size / (1024 * 1024)).toFixed(1)} MB`
}

/** The source and its delete capability, made together - see {@link documentsBrowser}. */
export interface DocumentsBrowser {
  source: FileBrowserSource

  /**
   * PASSED, OR NOT PASSED. The Host picker renders the same browser without one and delete is then
   * absent because it was never passed rather than present and disabled, which is why this is
   * handed back separately from `source` rather than hanging off it. A dead team's folder is given
   * it too.
   */
  deletion: FileBrowserDeletion
}

/**
 * A browser over one documents folder.
 *
 * `folder` IS A FOLDER KEY, NOT A TEAM ID - see `DocumentsFolder.folder`. The two are equal for
 * every folder anybody would open first, which is exactly why the type is branded apart: addressing
 * a RETIRED folder by its team reaches the successor's documents - a 200, the wrong files, and
 * nothing saying so.
 *
 * `readOnly` carries the decision that writing into a DEAD team's folder is refused.
 * It becomes `writes: 'refused'` rather than `'absent'`, so New folder and
 * Upload stay on screen and DISABLED: the write is withheld deliberately, and controls that
 * vanished would read as the feature having gone rather than been withheld. Reading is never
 * withheld, which is why the download link below is built regardless. Nor is
 * DELETE: a person may clear a record out, just not add to it.
 *
 * `confirm` asks the person before a folder with things in it is deleted. The dialog owns the
 * asking, because a question is chrome and this is a source.
 *
 * A NEW OBJECT PER FOLDER, and callers must keep it that way: `FileBrowser` re-lists whenever the
 * source it was given is REPLACED, and that is how the dialog changes which folder is on screen.
 */
export function documentsBrowser(
  folder: DocumentsFolderKey,
  readOnly: boolean,
  confirm: (question: string) => Promise<boolean>,
): DocumentsBrowser {
  /**
   * WHAT THE LAST LISTING SAID, so the delete tooltip can ask how many things are in a folder.
   *
   * `FileBrowserEntry` carries no `children` and should not: it is the shared row shape, and a
   * count that only one of the two surfaces has is exactly the kind of field that turns a shared
   * type back into two. Kept here instead, where it is this source's own business.
   */
  const children = new Map<string, number>()

  return {
    source: {
      start: '',

      async list(path: string): Promise<FileBrowserListing> {
        const entries = await api.listDocuments(folder, path)

        children.clear()
        for (const entry of entries) children.set(entry.path, entry.children)

        return {
          path,
          crumbs: trailOf(path),
          writes: readOnly ? 'refused' : 'allowed',

          // `null` rather than a parent: the `docs` crumb is always first on the trail, so the way
          // back is already one click and a `..` row would be a second way to say it.
          up: null,

          entries: entries.map((entry): FileBrowserEntry => ({
            path: entry.path,
            name: entry.name,
            isFolder: entry.isFolder,
            caption: sizeOf(entry),

            // FILES ONLY - a folder has no content route. Built even for a dead team's folder: the
            // folder outliving the team is FOR reading.
            downloadUrl: entry.isFolder ? null : api.documentUrl(folder, entry.path),

            // Only what the server's `view` shows - anything else would open a tab onto a
            // 415, so it downloads on click instead.
            viewUrl: !entry.isFolder && isViewable(entry.name) ? api.documentViewUrl(folder, entry.path) : null,
          })),
        }
      },

      // The RELATIVE path the route takes, composed here because only this source knows
      // that Documents composes one where the Host picker posts a parent plus a name instead.
      createFolder: async (path: string, name: string) => {
        await api.createFolder(folder, path ? `${path}/${name}` : name)
      },

      upload: async (path: string, file: File) => {
        await api.uploadDocument(folder, file, path)
      },
    },

    deletion: {
      label: (entry) => (entry.isFolder ? 'Remove folder' : 'Delete document'),

      // A folder with things in it is deleted WITH them, after the person is asked and told
      // how many files go - so the recursive flag is only ever sent past a yes. The count is read
      // fresh, because the listing's `children` counts entries one level down, not files.
      remove: async (entry) => {
        const full = entry.isFolder && (children.get(entry.path) ?? 0) > 0

        if (full) {
          const files = (await api.listDocuments(folder, entry.path, true)).length
          const asked = `Delete the folder ${entry.name} and the ${files} files in it? This cannot be undone.`

          if (!(await confirm(asked))) return
        }

        await api.deleteDocument(folder, entry.path, full)
      },
    },
  }
}
