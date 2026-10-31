import { json, send } from './client'
import type { TeamId } from './types'

/**
 * EVERY CALL TO THE MCP SERVERS SURFACE (MCP 1): the instance's list (Admin → MCP servers), an
 * entry's check, and which teams were given each (Team settings → MCP servers).
 *
 * A SEPARATE FILE FROM `client.ts`, like `outcomes.ts` and `sites.ts`, borrowing `send`/`json` so a
 * 401 is still `Unauthorized` and a refusal still surfaces the server's own `error` sentence.
 *
 * NEVER A VALUE: an entry names its connection and field, never what the connection holds, and its
 * address is shown without its query - which may hold a key - so an edit that leaves `url` out keeps
 * the stored address whole.
 *
 * | operation | route |
 * |---|---|
 * | list | `GET /api/mcp-servers` |
 * | add | `POST /api/mcp-servers` |
 * | change | `PUT /api/mcp-servers/{id}` |
 * | test | `POST /api/mcp-servers/{id}/test` |
 * | remove | `DELETE /api/mcp-servers/{id}` |
 * | a team's | `GET /api/teams/{team}/mcp-servers` |
 * | give to a team | `POST /api/teams/{team}/mcp-servers` |
 * | take off a team | `DELETE /api/teams/{team}/mcp-servers/{id}` |
 */

/** `url`: a web address. `runs`: the platform runs it (MCP 3 starts it). */
export type McpServerKind = 'url' | 'runs'

/** `unchecked` until a check says otherwise; a `runs` entry stays so until MCP 3. */
export type McpServerHealth = 'ok' | 'needs-you' | 'unchecked'

export interface McpServerEnv {
  name: string
  connection: string
  connectionName?: string | null
  field: string
}

export interface McpServer {
  id: string
  name: string
  kind: McpServerKind
  /** The address without its query. */
  url: string | null
  connection: { id: string; name: string | null } | null
  field: string | null
  header: string | null
  template: string | null
  command: 'npx' | 'uvx' | null
  package: string | null
  version: string | null
  args: string[]
  env: McpServerEnv[]
  health: McpServerHealth
  healthReason: string | null
  tools: string[] | null
  toolCount: number | null
  checkedAt: string | null
  teams: string[]
}

/** How a check went: the tools when `ok`. */
export interface McpServerCheck {
  outcome: 'ok' | 'refused' | 'failed'
  sentence: string
  tools: string[] | null
  toolCount: number | null
}

/** What an add or a change answers: the entry, and its check (null for a `runs` entry). */
export interface McpServerSaved {
  server: McpServer
  check: McpServerCheck | null
}

export interface McpServerSave {
  name: string
  kind: McpServerKind
  url?: string
  connection?: string | null
  field?: string | null
  header?: string | null
  template?: string | null
  command?: string | null
  package?: string | null
  version?: string | null
  args?: string[]
  env?: { name: string; connection: string; field: string }[]
}

export interface TeamMcpServerEntry {
  assignedAt: string
  server: McpServer
}

/** What an entry's health reads as, in a person's words. */
export function healthLabel(server: Pick<McpServer, 'kind' | 'health'>): string {
  if (server.kind === 'runs') return 'Checked when MCP 3 is in place'
  if (server.health === 'ok') return 'Working'
  if (server.health === 'needs-you') return 'Needs you'
  return 'Not checked yet'
}

/** The rule a name follows, as the Host says it: it prefixes the server's tools' names. */
export const McpNameRule = /^[A-Za-z0-9_-]{1,20}$/

const body = (value: unknown): RequestInit => ({
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify(value),
})

export const listMcpServers = () => json<{ servers: McpServer[] }>('/api/mcp-servers').then((r) => r.servers)

export const addMcpServer = (save: McpServerSave) => json<McpServerSaved>('/api/mcp-servers', { method: 'POST', ...body(save) })

export const changeMcpServer = (id: string, save: McpServerSave) =>
  json<McpServerSaved>(`/api/mcp-servers/${encodeURIComponent(id)}`, { method: 'PUT', ...body(save) })

export const testMcpServer = (id: string) =>
  json<{ check: McpServerCheck; server: McpServer }>(`/api/mcp-servers/${encodeURIComponent(id)}/test`, { method: 'POST' })

export const removeMcpServer = (id: string) =>
  json<{ removed: string; name: string; teams: string[] }>(`/api/mcp-servers/${encodeURIComponent(id)}`, { method: 'DELETE' })

export const listTeamMcpServers = (team: TeamId) =>
  json<{ team: string; servers: TeamMcpServerEntry[] }>(`/api/teams/${encodeURIComponent(team)}/mcp-servers`).then((r) => r.servers)

export const assignTeamMcpServer = (team: TeamId, server: string) =>
  json<TeamMcpServerEntry>(`/api/teams/${encodeURIComponent(team)}/mcp-servers`, { method: 'POST', ...body({ server }) })

export const removeTeamMcpServer = (team: TeamId, id: string) =>
  send(`/api/teams/${encodeURIComponent(team)}/mcp-servers/${encodeURIComponent(id)}`, { method: 'DELETE' }).then(() => undefined)
