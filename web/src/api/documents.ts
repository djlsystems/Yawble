import { json, send } from './client'
import type { DocumentsFolder, DocumentsFolderKey } from './types'

/**
 * EVERY CALL TO THE TENANT DOCUMENTS SURFACE, AND THERE IS ONLY ONE OF THIS FILE.
 *
 * Documents live under ONE tenant-level root, in a folder per team, outside every team root and
 * outside what a deletion removes. So the browser does not ask for "the active team's
 * documents" - it asks for a NAMED folder, and that folder can outlive its team.
 *
 * | operation | route |
 * |---|---|
 * | list folders | `GET /api/documents` |
 * | list a folder | `GET /api/teams/{folder}/documents` |
 * | read a file | `GET /api/teams/{folder}/documents/content` |
 * | create a folder | `POST /api/teams/{folder}/documents/folders` |
 * | upload | `POST /api/teams/{folder}/documents/upload` |
 * | delete | `DELETE /api/teams/{folder}/documents` |
 *
 * **THE ROUTES DECLARE `{team}`**, which is what puts them inside `TeamGate` STRUCTURALLY - a route
 * naming a team any other way is not gated, silently.
 *
 * **THE LISTING IS AN ENVELOPE**, `{ folders: [...] }`, not a bare array; and its entries carry
 * `exists`/`retired` rather than a single `teamGone`. Pinned by the server's own
 * `DocumentsOutliveTheirTeamTests` and `RecreatedTeamDocumentsTests`, which read the raw
 * `JsonElement` - the only way to see this, since a typed binding is exactly what hides it.
 */

/**
 * One row inside a folder.
 *
 * `DocumentEntry` lives here rather than in `types.ts` because it is this surface's own row shape
 * and nothing else names it - where `DocumentsFolder` is the shared wire record in `types.ts`,
 * which is why it is imported rather than restated.
 */
export interface DocumentEntry {
  name: string
  path: string
  isFolder: boolean
  size: number
  modifiedAt: string
  /** How many things are in a folder. What makes it removable, so the UI can say so up front. */
  children: number
}

/**
 * Every documents folder that EXISTS on disk - which is not the same list as the teams, because a
 * folder can outlive its team.
 *
 * Every person reaches every team, so which folders come back is the server's answer and this is
 * only where it arrives.
 */
export const listDocumentFolders = async (): Promise<DocumentsFolder[]> =>
  (await json<{ folders: DocumentsFolder[] }>('/api/documents')).folders

const docs = (folder: DocumentsFolderKey, suffix = '') =>
  `/api/teams/${encodeURIComponent(folder)}/documents${suffix}`

/** One folder at a time, or every file beneath it when `recursive` - which is what a flat
 *  "pick a document" menu wants. */
export const listDocuments = (folder: DocumentsFolderKey, path = '', recursive = false) =>
  json<DocumentEntry[]>(
    docs(folder, `?path=${encodeURIComponent(path)}&recursive=${recursive}`),
  )

export const documentUrl = (folder: DocumentsFolderKey, path: string) =>
  docs(folder, `/content?path=${encodeURIComponent(path)}`)

/**
 * The same file SHOWN rather than downloaded: `inline`, typed by extension on the server, in
 * a sandboxed CSP. Only for a file `isViewable` (lib/documentView) says yes to - anything else answers 415.
 */
export const documentViewUrl = (folder: DocumentsFolderKey, path: string) =>
  docs(folder, `/view?path=${encodeURIComponent(path)}`)

export const createFolder = (folder: DocumentsFolderKey, path: string) =>
  json<DocumentEntry>(docs(folder, '/folders'), {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ path }),
  })

export const uploadDocument = (folder: DocumentsFolderKey, file: File, path = '') => {
  const form = new FormData()
  form.append('file', file)
  form.append('path', path)

  // No content-type header: the browser sets it, and the multipart boundary it generates is part
  // of that value. Setting it by hand produces a body the server cannot parse.
  return json<DocumentEntry>(docs(folder, '/upload'), { method: 'POST', body: form })
}

/**
 * `recursive` takes a folder with things in it, and is sent only after the person was asked -
 * without it the server refuses a non-empty folder with 409. An EMPTY `path` with `recursive` is
 * the whole documents folder, which the server allows only for a team that is gone.
 */
export const deleteDocument = async (folder: DocumentsFolderKey, path: string, recursive = false) => {
  await send(
    docs(folder, `?path=${encodeURIComponent(path)}${recursive ? '&recursive=true' : ''}`),
    { method: 'DELETE' },
  )
}
