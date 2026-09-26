/**
 * The client's half of `ContainerId.DeriveName`, so a person sees the folder they will get before
 * they commit to it rather than discovering the substitution afterwards.
 *
 * A SECOND STORE OF ONE FACT, knowingly. The server is authoritative and re-derives on every
 * create; this exists only to render a preview. It must never become the value SENT - the request
 * carries the label a person typed, exactly as it does today.
 */
const MAX_NAME_LENGTH = 32

/**
 * The derived folder name, or `''` when nothing in the label survives. The server collapses that
 * case the same way - a label it cannot derive from gets a generated identifier - so refusing
 * "チーム" because of its script would be the machinery leaking back out.
 */
export function deriveTeamId(label: string): string {
  const kept = [...label].filter((c) => /[A-Za-z0-9_-]/.test(c)).join('')
  return kept.replace(/^[-_]+/, '').slice(0, MAX_NAME_LENGTH)
}

export function teamFolderLine(root: string, label: string): string {
  if (root.trim() === '') return ''

  const id = deriveTeamId(label)
  if (id === '') return 'The folder name will be generated — this name has no usable ASCII.'

  return `New team folder will be ${root.replace(/\/+$/, '')}/teams/${id}`
}

/**
 * The default name New Team opens with — a convenience, not an identity. It scans for `Team-<n>`
 * case-insensitively (matching the server's own case-insensitive duplicate refusal) and returns
 * the first UNUSED integer, never the count: `['Team-1', 'Team-3']` must give `Team-2`, not
 * `Team-3` — the count would collide with the team already sitting there. A race between two
 * people opening this dialog at once is real and unhandled here on purpose; it degrades to the
 * server's existing "a team with that name already exists" refusal, which is enough.
 */
export function nextTeamName(existing: string[]): string {
  const used = new Set<number>()

  for (const name of existing) {
    const match = /^team-(\d+)$/i.exec(name)
    if (match) used.add(Number(match[1]))
  }

  let candidate = 1
  while (used.has(candidate)) candidate += 1

  return `Team-${candidate}`
}
