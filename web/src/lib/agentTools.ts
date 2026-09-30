import type { ListedToolItem, PresetToolReport } from '../api/types'

/**
 * What an Agents row says about the tools its CLI would bring, from `GET /api/agents/tools`.
 *
 * FOUR ANSWERS FOR A MEMBER'S PRESET: isolated (green), foreign tools found and named (amber),
 * not verified (amber: a custom preset that declares no isolation), not measured (grey, never
 * green). THE CONCIERGE IS INFORMATION: what it has is listed in grey, never as a warning - it is
 * the person's own session and keeps every connector and server they set up.
 */
export interface ToolsStatus {
  readonly text: string
  readonly icon: string
  readonly tone: 'ok' | 'warn' | 'info'
  /** The names that matter for this row: the foreign ones, or the Concierge's tools. */
  readonly names: readonly string[]
}

/** Servers, connectors and plugins: what puts a tool in front of the model. Skills and hooks do not. */
export const offersTools = (item: ListedToolItem) =>
  item.kind === 'server' || item.kind === 'connector' || item.kind === 'plugin'

/** The report for one preset, matched without regard to case, or undefined. */
export function toolsReportFor(
  list: readonly PresetToolReport[],
  agent: string,
): PresetToolReport | undefined {
  const wanted = agent.toLowerCase()

  return list.find((report) => report.preset.toLowerCase() === wanted)
}

/** Null for a row with nothing to say: a program that runs no model. */
export function toolsStatus(report: PresetToolReport | undefined): ToolsStatus | null {
  if (report === undefined) {
    return { text: 'Tools not listed yet', icon: 'help', tone: 'info', names: [] }
  }

  const names = (items: readonly ListedToolItem[]) => items.map((item) => item.name)

  switch (report.verdict) {
    case 'isolated':
      return { text: 'Isolated: harness and its own tools only', icon: 'verified_user', tone: 'ok', names: [] }
    case 'foreignFound':
      return { text: 'Foreign tools found:', icon: 'warning', tone: 'warn', names: names(report.foreign) }
    case 'notVerified':
      return {
        text: 'Not verified: this preset declares no isolation',
        icon: 'warning',
        tone: 'warn',
        names: names(report.foreign),
      }
    case 'notMeasured':
      return { text: 'Tools not measured', icon: 'help', tone: 'info', names: [] }
    case 'concierge': {
      const tools = report.loaded.filter(offersTools)

      return {
        text: tools.length > 0 ? 'Concierge has:' : 'Concierge: no servers, connectors or plugins',
        icon: 'info',
        tone: 'info',
        names: names(tools),
      }
    }
    case 'notAModel':
      return null
  }
}
