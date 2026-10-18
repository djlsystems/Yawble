import { wireInstant } from '../localTime'

/**
 * AGENT STEPS: what a running member does between the rows of the log - each tool it calls, each
 * note, each thing it says - as the Host records it from the run's transcript, watched or not.
 *
 * A step is its kind, its tool's name and its time, and never any of the transcript's words. The
 * Host pushes each batch of a member's steps live as `agentSteps`, on the same connection and to the
 * same people as `workspaceChanged`, and a replay reads a period's back (`GET /api/run-steps`,
 * `readRecordedSteps`) and plays each at its time beside the log rows and the file changes.
 *
 * The scene draws each as a small icon that rises from the member's node and fades (`StepMs`), the
 * icon by kind and tool (`stepIcon`). Nothing of it goes in the activity list.
 */

export type StepKind = 'tool' | 'result' | 'text' | 'user' | 'note'
const StepKinds: readonly StepKind[] = ['tool', 'result', 'text', 'user', 'note']

/** One recorded step, as the Host sends it. */
export interface AgentStep {
  id: number
  team: string
  member: string
  run: number
  workflow: number | null
  /** When it happened, as the wire spells an instant. */
  at: string
  kind: StepKind
  /** The tool's name for a `tool` step (`Bash`, `Edit`, `MCP`...); '' for every other kind. */
  label: string
  /** The MCP tool it called (`kanban`, `tell`...), only when `label` is `MCP`. */
  mcpTool: string | null
  /** How many steps of this kind it stands for: more than 1 when the Host's per-second cap folded some in. */
  count: number
}

/** One `agentSteps` push: a batch of one member's steps. */
export interface StepsPush {
  team: string
  member: string
  steps: AgentStep[]
}

export const AgentSteps = 'agentSteps'

function text(value: unknown): string {
  return typeof value === 'string' ? value : ''
}

function whole(value: unknown, fallback: number): number {
  return typeof value === 'number' && Number.isFinite(value) ? Math.round(value) : fallback
}

/**
 * A step as the wire sent it, or null when it is not one: the Monitor never trusts the shape. A
 * step inside a push takes the push's team and member when it does not carry its own.
 */
export function readStep(value: unknown, team = '', member = ''): AgentStep | null {
  if (typeof value !== 'object' || value === null) return null
  const raw = value as Record<string, unknown>
  const kind = raw.kind as StepKind
  if (!StepKinds.includes(kind)) return null
  const ownTeam = text(raw.team) || team
  const ownMember = text(raw.member) || member
  if (ownTeam === '' || ownMember === '') return null
  const label = kind === 'tool' ? text(raw.label) : ''
  const mcpTool = label === 'MCP' && text(raw.mcpTool) !== '' ? text(raw.mcpTool) : null
  return {
    id: whole(raw.id, 0),
    team: ownTeam,
    member: ownMember,
    run: whole(raw.run, 0),
    workflow: typeof raw.workflow === 'number' && Number.isFinite(raw.workflow) ? raw.workflow : null,
    at: text(raw.at),
    kind,
    label,
    mcpTool,
    count: Math.max(1, whole(raw.count, 1)),
  }
}

/** A push as the wire sent it, or null when it is not one. A step in it that is not one is passed over. */
export function readStepsPush(value: unknown): StepsPush | null {
  if (typeof value !== 'object' || value === null) return null
  const raw = value as Record<string, unknown>
  const team = text(raw.team)
  const member = text(raw.member)
  if (team === '' || member === '' || !Array.isArray(raw.steps)) return null
  const steps: AgentStep[] = []
  for (const entry of raw.steps as unknown[]) {
    const step = readStep(entry, team, member)
    if (step && step.team === team && step.member === member) steps.push(step)
  }
  return { team, member, steps }
}

/** A step's time in ms; one whose time cannot be read is placed at 0, never dropped. */
export function stepTime(step: AgentStep): number {
  const at = wireInstant(step.at)
  return Number.isFinite(at) ? at : 0
}

// --- the icon ------------------------------------------------------------------------------------

/** A tool's icon by its name; a tool not named here is the gear. Each is a Material Symbols Outlined name. */
const ToolIcons: Readonly<Record<string, string>> = {
  Bash: 'terminal',
  Edit: 'edit',
  Write: 'edit',
  MultiEdit: 'edit',
  Read: 'search',
  Grep: 'search',
  Glob: 'search',
  LS: 'search',
  MCP: 'power',
  Skill: 'menu_book',
  TodoWrite: 'checklist',
  WebFetch: 'language',
  WebSearch: 'language',
  Task: 'account_tree',
  Agent: 'account_tree',
}

const KindIcons: Readonly<Record<Exclude<StepKind, 'tool'>, string>> = {
  text: 'chat_bubble',
  note: 'sticky_note_2',
  result: 'check_small',
  user: 'person',
}

export const UnknownToolIcon = 'settings'

/** What a step draws: a terminal for Bash, a pencil for an edit, a speech bubble for the agent's text... */
export function stepIcon(step: Pick<AgentStep, 'kind' | 'label'>): string {
  if (step.kind !== 'tool') return KindIcons[step.kind]
  return Object.hasOwn(ToolIcons, step.label) ? ToolIcons[step.label]! : UnknownToolIcon
}

/** Every icon a step can draw, for the test that the font has each. */
export const StepIcons: readonly string[] = [...new Set([...Object.values(ToolIcons), ...Object.values(KindIcons), UnknownToolIcon])]

// --- the recorded steps, for a replay ------------------------------------------------------------

/** One page of `GET /api/run-steps`: the file changes' envelope, its items step records. */
export interface StepsPage {
  steps?: unknown[]
  /** The file changes page's name for its items, read too. */
  changes?: unknown[]
  next: number | null
}

/** How many recorded steps one replay reads at most: the route pages them, this bounds the pages. */
export const MaxRecordedSteps = 20_000
const RecordedPage = 500

/** What a replay read: its steps, and whether it stopped before the period's last one. */
export interface RecordedSteps {
  steps: AgentStep[]
  /** True when the read stopped with pages still to come, at its bound or at a cursor that did not move on. */
  cut: boolean
}

/**
 * Every recorded step of a period, oldest first, paged with `after`/`next` until `next` is null.
 * One that is not a step is passed over. A read that stops before `next` is null says so in `cut`,
 * so the replay never stops silently.
 */
export async function readRecordedSteps(
  readPage: (after: number, take: number) => Promise<StepsPage>,
  max = MaxRecordedSteps,
): Promise<RecordedSteps> {
  const steps: AgentStep[] = []
  let after = 0
  let read = 0
  for (;;) {
    const page = await readPage(after, RecordedPage)
    const items = Array.isArray(page.steps) ? page.steps : Array.isArray(page.changes) ? page.changes : []
    for (const raw of items) {
      const step = readStep(raw)
      if (step) steps.push(step)
    }
    read += items.length
    const next = page.next
    if (typeof next !== 'number') return { steps, cut: false }
    // A cursor that does not move on would read the same page for ever.
    if (next <= after || read >= max) return { steps, cut: true }
    after = next
  }
}

// --- the setting ---------------------------------------------------------------------------------

export interface StepSettings {
  /** Off: no step icon live, and none in a replay. */
  show: boolean
}

export const DefaultStepSettings: StepSettings = { show: true }

/** Remembered per browser like Show file changes, a convenience only: the default stands without storage. */
export const StepSettingsKey = 'harness.monitor.agentSteps'

export function readStepSettings(): StepSettings {
  try {
    const raw: unknown = JSON.parse(localStorage.getItem(StepSettingsKey) ?? 'null')
    if (typeof raw !== 'object' || raw === null) return { ...DefaultStepSettings }
    const saved = raw as Record<string, unknown>
    return { show: typeof saved.show === 'boolean' ? saved.show : DefaultStepSettings.show }
  } catch {
    return { ...DefaultStepSettings }
  }
}

export function writeStepSettings(settings: StepSettings) {
  try {
    localStorage.setItem(StepSettingsKey, JSON.stringify(settings))
  } catch {
    // Remembered only while this page is open.
  }
}

// --- in the scene --------------------------------------------------------------------------------

/** How long a step's icon rises and fades, ms of display time. */
export const StepMs = 1600
/** At most this many step icons on one node at once: a burst keeps the newest. */
export const MaxStepIconsEach = 3
/** A node drawn at least this many screen pixels across its radius has room for an MCP tool's caption. */
export const StepCaptionMinPx = 16

/**
 * How strongly a step's icon shows `age` ms after it began, and how far it has risen, 0..1: in
 * quickly, then fading as it climbs. Under reduced motion it neither rises nor fades.
 */
export function stepMotion(age: number, reducedMotion: boolean): { alpha: number; rise: number } {
  if (age < 0 || age >= StepMs) return { alpha: 0, rise: 0 }
  if (reducedMotion) return { alpha: 1, rise: 0 }
  const t = age / StepMs
  const alpha = t < 0.12 ? t / 0.12 : 1 - Math.max(0, (t - 0.45) / 0.55)
  return { alpha: Math.max(0, Math.min(1, alpha)), rise: 1 - Math.pow(1 - t, 2) }
}
