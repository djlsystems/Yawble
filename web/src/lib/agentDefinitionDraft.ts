import type { Agent, AgentInstall, AgentMode } from '../api/types'
import { launchFromDialogState } from './agentLaunch'

export interface AgentDraft {
  name: string
  mode: AgentMode
  fileName: string
  args: string
  systemPromptArguments: string
  instructionsFile: string
  usageFormat: string
  env: string
  timeout: string
  tags: string

  /** Whether this preset runs a language model. See `AgentLaunch.languageModel` in `api/types.ts`. */
  languageModel: boolean

  /**
   * Where to get this Agent's CLI, and the one line beside the link.
   *
   * CARRIED HERE BECAUSE THIS FUNCTION DELETES WHAT IT DOES NOT REBUILD. `PUT /api/agents` replaces
   * the catalog wholesale, so a field absent from this draft is a field the next edit removes,
   * silently and with no type error — the defect that cost three presets their `instructionsFile`
   * on a live instance. It matters more for this one than for its siblings: `LoadOrSeed` NEVER
   * MERGES, so this dialog is the only way an install link ever reaches an existing instance, and a
   * dialog that dropped it would make the field unreachable on exactly the tenants that need it.
   */
  installUrl: string
  installHint: string
}

const parseLines = (text: string): string[] =>
  text
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0)

const parseEnv = (text: string): Record<string, string> | null => {
  const entries: Array<[string, string]> = []

  for (const line of parseLines(text)) {
    const at = line.indexOf('=')
    if (at <= 0) continue
    entries.push([line.slice(0, at).trim(), line.slice(at + 1)])
  }

  return entries.length > 0 ? Object.fromEntries(entries) : null
}

const parseNumberOrNull = (text: string): number | null =>
  Number(text.trim()) > 0 ? Number(text.trim()) : null

export const parseTags = (text: string): string[] | null => {
  const deduped: string[] = []
  const seen = new Set<string>()

  for (const raw of parseLines(text)) {
    const tag = raw.trim()
    if (!tag) continue

    const folded = tag.toLowerCase()
    if (seen.has(folded)) continue

    seen.add(folded)
    deduped.push(tag)
  }

  return deduped.length > 0 ? deduped : null
}

export const joinLines = (values: string[] | null | undefined) => (values ?? []).join('\n')

export const joinEnv = (values: Record<string, string> | null | undefined) =>
  Object.entries(values ?? {})
    .map(([key, value]) => `${key}=${value}`)
    .join('\n')

export const joinTags = (values: string[] | null | undefined) => (values ?? []).join('\n')

export function rebuildAgentDefinition(draft: AgentDraft): Agent {
  return {
    name: draft.name.trim(),
    mode: draft.mode,

    // DELEGATED, not duplicated. Two teams independently built a pure rebuilder for this dialog -
    // this one for the whole definition, `launchFromDialogState` for the launch half - and two
    // functions constructing one record is exactly how a field comes to be carried by one and
    // dropped by the other. `PUT /api/agents` replaces the catalog wholesale, so a field this
    // object does not rebuild is a field the save DELETES, silently and with no type error. One
    // builder per record; the launch's fields are that builder's business alone.
    launch: launchFromDialogState({
      fileName: draft.fileName,
      argumentsText: draft.args,
      systemPromptArgumentsText: draft.systemPromptArguments,
      instructionsFile: draft.instructionsFile,
      usageFormat: draft.usageFormat,
      languageModel: draft.languageModel,
    }),
    env: parseEnv(draft.env),
    timeoutSeconds: parseNumberOrNull(draft.timeout),
    tags: parseTags(draft.tags),
    install: parseInstall(draft.installUrl, draft.installHint),
  }
}

/**
 * The install guidance, or null when there is no URL.
 *
 * THE URL IS WHAT MAKES IT EXIST. A hint with nowhere to go is a sentence with no remedy in it, and
 * an `install` object carrying an empty URL is a link to nowhere — the guessed-URL failure arriving
 * by the back door. So a blank URL clears the whole field, which is also how an operator REMOVES a
 * link that has gone stale: empty the box.
 *
 * NOTHING IS INFERRED. This never composes a URL from the preset's name, and there is deliberately
 * no table here to do it with: nothing in code may name an Agent.
 */
const parseInstall = (url: string, hint: string): AgentInstall | null => {
  const trimmed = url.trim()

  if (!trimmed) return null

  const line = hint.trim()

  return line ? { url: trimmed, hint: line } : { url: trimmed }
}
