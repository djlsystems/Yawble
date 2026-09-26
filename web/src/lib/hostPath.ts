/**
 * The Host folder picker's logic, where a test can reach it. Mounted specs exist in this project
 * now, and this is still deliberately free of anything that touches the component that renders it
 * — see `web/src/lib/__tests__/hostPath.spec.ts`.
 *
 * The Host runs in a Linux container, so every path here is POSIX: `/` is the only separator and
 * `/` is the only root that keeps a trailing one.
 */

export interface Crumb {
  label: string
  path: string
}

// "/" itself is the one path whose trailing separator IS the path; trimming it would leave "".
function trimTrailingSeparators(path: string): string {
  const trimmed = path.replace(/\/+$/, '')
  return trimmed === '' && path.startsWith('/') ? '/' : trimmed
}

/**
 * The root, then each folder between it and `current`, each carrying the full path a click on it
 * should navigate to.
 */
export function breadcrumbs(root: string, current: string): Crumb[] {
  const rootLabel = trimTrailingSeparators(root)
  const crumbs: Crumb[] = [{ label: rootLabel, path: rootLabel }]

  const rest = current.startsWith(rootLabel) ? current.slice(rootLabel.length) : ''
  const segments = rest.split('/').filter((segment) => segment.length > 0)

  let path = rootLabel
  for (const segment of segments) {
    path = childOf(path, segment)
    crumbs.push({ label: segment, path })
  }

  return crumbs
}

/**
 * Joins a child's NAME onto `base`. The one place a folder picker descends into a listed row, kept
 * here rather than duplicated inside the component: trimming `base`'s own trailing separator is
 * what keeps `childOf('/', 'srv')` a single slash rather than two.
 */
export function childOf(base: string, name: string): string {
  return `${base.replace(/\/+$/, '')}/${name}`
}

/**
 * The folder one level up from `current`, or `null` when `current` IS the root. Up must stop at
 * the root — offering a parent above it invites a click the server will refuse, which reads as the
 * picker being broken rather than bounded.
 */
export function parentWithin(root: string, current: string): string | null {
  const crumbs = breadcrumbs(root, current)
  if (crumbs.length <= 1) return null
  return crumbs[crumbs.length - 2]!.path
}

// A PREVIEW of `IsSingleSegmentName` in `src/Harness.Host/FileSystemEndpoints.cs`, not the
// authority — the server re-checks everything regardless of what this says — and it exists so a
// person is told before they press the button rather than after. One segment of a POSIX path: not
// blank, no separator, no NUL, and not one of the two names that mean a directory already there.
export function isLegalSegment(name: string): boolean {
  if (name.trim() === '') return false
  if (name.includes('/')) return false
  if (name.includes('\0')) return false
  if (name === '.' || name === '..') return false
  return true
}

/**
 * Directories before files, each group alphabetical — the shape a folder picker's listing is
 * expected to render in.
 */
export function sortEntries<T extends { name: string; type: 'dir' | 'file' }>(entries: T[]): T[] {
  return [...entries].sort((a, b) => {
    if (a.type !== b.type) return a.type === 'dir' ? -1 : 1
    return a.name.localeCompare(b.name)
  })
}
