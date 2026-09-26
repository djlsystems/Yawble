/**
 * WHICH DOCUMENTS A BROWSER TAB CAN SHOW - the same list `DocumentView` holds on the server for
 * `GET .../documents/view`, by extension and nothing else. A file outside it answers
 * 415 there, so the dialog downloads it instead of opening a tab onto an error.
 *
 * Kept apart from `api/documents.ts` because it is a pure fact about a name, and a mount spec that
 * mocks the API still needs the real answer.
 */
const VIEWABLE = new Set([
  // Markdown, rendered to a page on the server.
  '.md', '.markdown',
  '.html', '.htm',
  // Text and source, served as text/plain so nothing in them runs.
  '.txt', '.log', '.json', '.csv', '.yaml', '.yml', '.xml', '.toml',
  '.cs', '.ts', '.js', '.vue', '.py', '.go', '.sh', '.ps1', '.sql',
  '.png', '.jpg', '.jpeg', '.gif', '.webp', '.svg',
  '.pdf',
])

export function isViewable(name: string): boolean {
  const dot = name.lastIndexOf('.')
  return dot >= 0 && VIEWABLE.has(name.slice(dot).toLowerCase())
}
