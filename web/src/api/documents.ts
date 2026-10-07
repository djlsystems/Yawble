import { ActionRefused, json, send } from './client'
import type {
  DocumentEntry,
  DocumentsChangeAnswer,
  DocumentsClash,
  DocumentsChangeResult,
  DocumentsFolder,
  DocumentsFolderKey,
  DocumentsPackageAnswer,
  DocumentsPackageSaved,
  DocumentsRenameItem,
  DocumentsTransfer,
  DocumentsUploadAnswer,
  OnClash,
} from './types'

export type { DocumentEntry } from './types'

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
 * | upload a folder | `POST /api/teams/{folder}/documents/upload-folder` |
 * | upload a .zip | `POST /api/teams/{folder}/documents/upload-zip` |
 * | delete | `DELETE /api/teams/{folder}/documents` |
 * | rename | `POST /api/teams/{folder}/documents/rename` |
 * | move | `POST /api/teams/{folder}/documents/move` |
 * | copy | `POST /api/teams/{folder}/documents/copy` |
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
 * Every documents folder that EXISTS on disk - which is not the same list as the teams, because a
 * folder can outlive its team.
 *
 * Every person reaches every team, so which folders come back is the server's answer and this is
 * only where it arrives.
 */
export const listDocumentFolders = async (): Promise<DocumentsFolder[]> =>
  (await listDocumentsRoot()).folders

/**
 * The folders, and `root`: the absolute documents root as an agent sees it (`/data/documents`),
 * which Copy as path builds `<root>/<folder>/<path>` from. `root` is empty against a server that
 * does not send it yet, and Copy as path then says so rather than copying a wrong path.
 */
export const listDocumentsRoot = async (): Promise<{ folders: DocumentsFolder[]; root: string }> => {
  const answer = await json<{ folders: DocumentsFolder[]; root?: string }>('/api/documents')

  return { folders: answer.folders, root: answer.root ?? '' }
}

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

/**
 * One file into `path`. Without `onClash` it is the upload as it always was: a file of the same
 * name is replaced, and the saved entry comes back. With `onClash` the answer is data: `ask`
 * answers a clash as `clash` (nothing written), `skip` as `skipped`.
 */
export function uploadDocument(folder: DocumentsFolderKey, file: File, path?: string): Promise<DocumentEntry>
export function uploadDocument(
  folder: DocumentsFolderKey,
  file: File,
  path: string,
  onClash: 'ask' | OnClash,
): Promise<DocumentsUploadAnswer>
export async function uploadDocument(
  folder: DocumentsFolderKey,
  file: File,
  path = '',
  onClash?: 'ask' | OnClash,
): Promise<DocumentEntry | DocumentsUploadAnswer> {
  const form = new FormData()
  form.append('file', file)
  form.append('path', path)
  if (onClash) form.append('onClash', onClash)

  // No content-type header: the browser sets it, and the multipart boundary it generates is part
  // of that value. Setting it by hand produces a body the server cannot parse.
  const init: RequestInit = { method: 'POST', body: form }

  if (!onClash) return json<DocumentEntry>(docs(folder, '/upload'), init)

  try {
    const answer = await json<DocumentEntry | { skipped: true; path: string }>(docs(folder, '/upload'), init)

    if ('skipped' in answer && answer.skipped) return { kind: 'skipped', path: answer.path }

    return { kind: 'saved', entry: answer as DocumentEntry }
  } catch (failure) {
    const clash = clashOf(failure)
    if (clash) return clash

    throw failure
  }
}

/** One file of a folder upload, with its path inside the folder, top folder first
 *  (`File.webkitRelativePath`). */
export interface FolderUploadFile {
  file: File
  relativePath: string
}

/**
 * A whole folder into `path`, its subfolders kept: one `file` and one `relativePath` per file, in
 * the same order. The clash is about the top folder, with the single upload's `onClash` choices.
 */
export const uploadDocumentFolder = (
  folder: DocumentsFolderKey,
  files: FolderUploadFile[],
  path: string,
  onClash: 'ask' | OnClash,
) => {
  const form = new FormData()
  for (const { file, relativePath } of files) {
    form.append('file', file)
    form.append('relativePath', relativePath)
  }

  return sendPackage(docs(folder, '/upload-folder'), form, path, onClash, 'That folder is larger than 100 MB.')
}

/**
 * A .zip into `path`, unpacked into a folder named after it. A zip holding a link, an absolute
 * path or a `..` is refused whole and nothing is written; that refusal is thrown with its sentence.
 */
export const uploadDocumentZip = (
  folder: DocumentsFolderKey,
  zip: File,
  path: string,
  onClash: 'ask' | OnClash,
) => {
  const form = new FormData()
  form.append('file', zip)

  return sendPackage(docs(folder, '/upload-zip'), form, path, onClash, 'That zip is larger than 25 MB.')
}

/**
 * A 413 is the server's body limit refusing the request before the route reads it, so it carries
 * no sentence of the route's (a proxy in front may send an HTML page instead): it is thrown as
 * `tooLarge`, the route's own size sentence, never as a bare status.
 */
async function sendPackage(
  url: string,
  form: FormData,
  path: string,
  onClash: 'ask' | OnClash,
  tooLarge: string,
): Promise<DocumentsPackageAnswer> {
  form.append('path', path)
  form.append('onClash', onClash)

  try {
    const answer = await json<DocumentsPackageSaved | { skipped: true; path: string }>(url, { method: 'POST', body: form })

    if ('skipped' in answer && answer.skipped) return { kind: 'skipped', path: answer.path }

    return { kind: 'saved', saved: answer as DocumentsPackageSaved }
  } catch (failure) {
    const clash = clashOf(failure)
    if (clash) return clash

    if ((failure as { status?: number }).status === 413) throw Object.assign(new Error(tooLarge), { status: 413 })

    throw failure
  }
}

/** A 409 that carries `clashes`: the server did nothing and wants a choice per item. */
function clashOf(failure: unknown): { kind: 'clash'; error: string; clashes: DocumentsClash[] } | null {
  if (!(failure instanceof ActionRefused)) return null
  if ((failure as { status?: number }).status !== 409) return null
  if (!Array.isArray(failure.body.clashes)) return null

  return { kind: 'clash', error: failure.message, clashes: failure.body.clashes as DocumentsClash[] }
}

/**
 * A rename, move or copy, with its 409s handed back as DATA: a clash (nothing done, choose per
 * item) and a partial result (the row was written, some items failed - every item said). Matched
 * on the status and the presence of `clashes` or `results`, never on the sentence. Any other
 * refusal is thrown with the server's own sentence.
 */
async function change(url: string, body: unknown): Promise<DocumentsChangeAnswer> {
  try {
    const answer = await json<{ results: DocumentsChangeResult[] }>(url, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
    })

    return { kind: 'ok', results: answer.results }
  } catch (failure) {
    const clash = clashOf(failure)
    if (clash) return clash

    if (
      failure instanceof ActionRefused &&
      (failure as { status?: number }).status === 409 &&
      Array.isArray(failure.body.results)
    ) {
      return { kind: 'partial', error: failure.message, results: failure.body.results as DocumentsChangeResult[] }
    }

    throw failure
  }
}

/** Renames in place: same parent, new leaf name, a batch per request. */
export const renameDocuments = (folder: DocumentsFolderKey, items: DocumentsRenameItem[]) =>
  change(docs(folder, '/rename'), { items })

/** Moves from `folder` into `transfer.to`, which may be another team's documents folder. */
export const moveDocuments = (folder: DocumentsFolderKey, transfer: DocumentsTransfer) =>
  change(docs(folder, '/move'), transfer)

/** Copies from `folder` into `transfer.to`. */
export const copyDocuments = (folder: DocumentsFolderKey, transfer: DocumentsTransfer) =>
  change(docs(folder, '/copy'), transfer)

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
