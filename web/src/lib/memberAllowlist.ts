export function normalizeAllowlist(values: string[] | null | undefined): string[] {
  const normalized: string[] = []
  const seen = new Set<string>()

  for (const raw of values ?? []) {
    const entry = raw.trim()
    if (!entry) continue

    const folded = entry.toLowerCase()
    if (seen.has(folded)) continue

    seen.add(folded)
    normalized.push(entry)
  }

  return normalized
}

export function allowlistIncludes(
  allowlist: string[] | null | undefined,
  agent: string | null | undefined,
): boolean {
  if (!agent) return false

  const wanted = agent.toLowerCase()
  return normalizeAllowlist(allowlist).some((entry) => entry.toLowerCase() === wanted)
}

export function allowedAgentOptions(
  headlessAgents: string[],
  allowlist: string[] | null | undefined,
): string[] {
  const byName = new Map(headlessAgents.map((name) => [name.toLowerCase(), name]))
  const options: string[] = []

  for (const allowed of normalizeAllowlist(allowlist)) {
    const match = byName.get(allowed.toLowerCase())
    if (match) options.push(match)
  }

  return options
}
