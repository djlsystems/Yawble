import type { AgentCredential, AgentCredentialStatus, IssuedCredentialDeclaration } from '../api/types'
import { stamp } from './agentVersions'

/**
 * What an Agents row says about its preset's credential source and its command's issued credential.
 *
 * TWO SCOPES. The source is the PRESET'S own choice; the credential is the COMMAND'S, one value shared
 * by every preset that runs that command. A row must say so, or setting a key on `claude-headless`
 * and seeing `claude` change too reads as a bug.
 *
 * NOTHING HERE EVER HOLDS A VALUE. The Host sends `set`, `setBy` and `setAt` and nothing more.
 */

/** Put in front of a person, word for word, wherever a credential is set. */
export const ProviderTermsSentence =
  "You are responsible for your provider's terms when one credential is used by many runs."

/** The entry for one preset, matched without regard to case, or undefined. */
export function credentialFor(list: readonly AgentCredential[], agent: string): AgentCredential | undefined {
  const wanted = agent.toLowerCase()

  return list.find((entry) => entry.agent.toLowerCase() === wanted)
}

/**
 * Every entry whose command `status` names, given that command's state: setting or clearing through
 * one preset changes the one stored value, so every row of that command changes with it.
 */
export function withCredentialStatus(list: readonly AgentCredential[], status: AgentCredentialStatus): AgentCredential[] {
  const command = status.command.toLowerCase()

  return list.map((entry) =>
    entry.command.toLowerCase() === command
      ? { ...entry, set: status.set, setBy: status.setBy, setAt: status.setAt }
      : entry,
  )
}

/** The one entry for `agent` with its source changed, every other as it was. */
export function withSource(list: readonly AgentCredential[], agent: string, source: AgentCredential['source']): AgentCredential[] {
  const wanted = agent.toLowerCase()

  return list.map((entry) => (entry.agent.toLowerCase() === wanted ? { ...entry, source } : entry))
}

/** "set by X at Y", or "not set". Who and when, never what. */
export function credentialState(entry: AgentCredential, format: (iso: string) => string = stamp): string {
  if (!entry.set) return 'not set'

  const by = entry.setBy ? ` by ${entry.setBy}` : ''
  const at = entry.setAt ? ` at ${format(entry.setAt)}` : ''

  return `set${by}${at}`
}

/** That the credential is the command's, shared by every preset that runs it, naming the others. */
export function sharedLine(entry: AgentCredential): string {
  const others = entry.sharedWith.length > 0 ? entry.sharedWith.join(', ') : 'no other preset'

  return `Shared by every preset that runs ${entry.command}: also ${others}.`
}

/**
 * Which credential the CLI uses when the shared home also holds a login, from the declaration's
 * measured `loginPrecedence`. It matters for the Concierge, which keeps that home; a member run under
 * issued has a home of its own and no login in it.
 */
export function conciergeLine(declaration: IssuedCredentialDeclaration): string {
  switch (declaration.loginPrecedence) {
    case 'credential':
      return 'The Concierge uses the issued credential, even when a login exists in the shared home.'
    case 'login':
      return "The Concierge uses the person's own login while one exists in the shared home, and the issued credential only without it."
    default:
      return 'Which credential the Concierge uses when a login also exists in the shared home is not measured for this CLI.'
  }
}

/** The label a person reads for a declared kind. */
export function kindLabel(kind: 'apiKey' | 'token'): string {
  return kind === 'apiKey' ? 'API key' : 'Token'
}
