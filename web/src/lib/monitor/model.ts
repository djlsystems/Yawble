import type { Message, MonitorHint, Team } from '../../api/types'
import { summarise } from '../summarise'
import { itemLabel } from '../backlog'
import { crossesDays, localMinute, wireInstant } from '../localTime'
import { DefaultSettings, drawsResource, resolveHint, type Resource, type ResourceSettings } from './hints'
import { DefaultFileSettings, filesOf, pushHeadline, pushMoreWords, pushTime, type FileAction, type FileSettings, type WorkspacePush } from './workspace'
import { DefaultStepSettings, stepTime, type AgentStep, type StepSettings } from './steps'

/**
 * THE MONITOR'S MODEL: every team, member and workflow the log has shown, and what each row does
 * to them.
 *
 * It reads the same rows the cards' feeds read (`GET /api/messages`) and the same snapshots the
 * board holds, and DECIDES NOTHING THE SERVER DECIDES: a member's state is the snapshot's whenever
 * one is held (`reconcile`), and the rows only move it between snapshots - which is all a replay
 * has. Each row also yields the EFFECTS the scene draws (a comet for an instruction, a burst for a
 * finished workflow), so what moves on screen is always a row that is in the log. A plugin row whose
 * type carries a monitor hint (`hints.ts`) draws the thing it acted on beside the plugin member.
 */

export const InstructionPrefix = 'agentContainer.instruction.'

/** The source of the instruction a member's own wake fires: `wake:<id>`. */
const WakePrefix = 'wake:'

/** What a member is doing, as the monitor draws it. */
export type MemberState = 'idle' | 'running' | 'waiting' | 'blocked' | 'decision' | 'failed'

/** The colour family of an effect, a ticker row and a legend entry. */
export type Tone =
  | 'tell'
  | 'start'
  | 'progress'
  | 'done'
  | 'handback'
  | 'fail'
  | 'blocked'
  | 'workflow'
  | 'card'
  | 'push'
  | 'plugin'
  | 'file'
  | 'quiet'

/** Where work comes from when no member sent it. */
export type Origin = 'person' | 'trigger' | 'backlog' | 'site'

export interface MonitorTeam {
  id: string
  name: string
  /** A hue in degrees, fixed by the order the team was first seen. */
  hue: number
}

export interface MonitorMember {
  /** `<team>/<id>`, the pair the log names a member by. */
  key: string
  team: string
  id: string
  name: string
  kind: 'agent' | 'plugin'
  manager: boolean
  state: MemberState
  /** Instructions accepted and not started. */
  queued: number
  /** The member's last progress line, its blocked reason or its question. */
  status: string
  /** The workflow its last row was in. */
  workflow: number | null
  /** Billable tokens of its finished runs in this window: never estimated. */
  tokens: number
  runs: number
  /** Model time of its last row, ms. */
  lastAt: number
  /** Its pending wakes, earliest due first: the snapshot's, moved by the `wake.*` rows between snapshots. */
  wakes: MonitorWake[]
}

/** A wake a member asked for and that has not fired, as the Monitor draws it: a small clock on the member. */
export interface MonitorWake {
  id: number
  /** When it is due, ms. */
  dueAt: number
  note: string
}

export interface MonitorWorkflow {
  id: number
  team: string
  /**
   * Its name: for a workflow a `backlog.item.dispatched` row began, the item's citation, a
   * separator and its title; else the subject of the instruction that began it. '' when neither
   * is known - then the number is all there is, and nothing is guessed.
   */
  title: string
  /** The backlog item it works on, from its dispatch row, or null. */
  item: { id: number; title: string } | null
  members: Set<string>
  openedAt: number
  endedAt: number | null
  ending: 'open' | 'completed' | 'closed'
  events: number
}

export type NodeRef =
  | { kind: 'member'; key: string }
  | { kind: 'team'; id: string }
  | { kind: 'workflow'; id: number }
  | { kind: 'origin'; team: string; origin: Origin }

export type MonitorEffect =
  | { kind: 'comet'; from: NodeRef; to: NodeRef; tone: Tone; label?: string }
  | { kind: 'burst'; at: NodeRef; tone: Tone; big?: boolean }
  | { kind: 'ripple'; at: NodeRef; tone: Tone }
  | { kind: 'float'; at: NodeRef; text: string; tone: Tone }
  | { kind: 'shake'; at: NodeRef }
  /** What a plugin's row did to a thing it manages, shown beside the member for `ttlMs` of display time. */
  | { kind: 'resource'; at: NodeRef; resource: Resource; ttlMs: number }
  /** A member's file changes (`workspace.ts`): the files under their projects and repository, for `ttlMs` of display time. */
  | { kind: 'workspace'; at: NodeRef; push: WorkspacePush; ttlMs: number }
  /** One agent step (`steps.ts`): a short-lived icon off the member's node, and nothing in the activity list. */
  | { kind: 'step'; at: NodeRef; step: AgentStep }

export interface TickerEntry {
  seq: number
  at: number
  team: string
  member: string | null
  tone: Tone
  /** One or two words: told, started, done, handed back... */
  verb: string
  text: string
  workflow: number
  /** A hinted plugin row: its icon, what happened and its label, said in words beside the graph. */
  resource?: Resource
  /** A file-changes push, or a recorded one a replay plays: not a row of the log. */
  workspace?: true
  /** That push's files, each listed by path with what happened to it, and what it left off. */
  files?: { path: string; action: FileAction }[]
  more?: string
}

const GoldenAngle = 137.508

/** A row's time in ms; a row whose time cannot be read is placed at 0, never dropped. */
export function timeOf(message: Message): number {
  const at = wireInstant(message.occurredAt)
  return Number.isFinite(at) ? at : 0
}

function payloadOf(message: Message): Record<string, unknown> {
  try {
    const parsed: unknown = JSON.parse(message.payload)
    return typeof parsed === 'object' && parsed !== null ? (parsed as Record<string, unknown>) : {}
  } catch {
    return {}
  }
}

function text(value: unknown): string {
  return typeof value === 'string' ? value : ''
}

function num(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0
}

/** `Team/Member` split at its first slash, or null when it is not one. */
export function splitMember(value: string): { team: string; id: string } | null {
  const slash = value.indexOf('/')
  if (slash <= 0 || slash === value.length - 1) return null
  if (/^(schedule|trigger|site|wake):/.test(value)) return null
  return { team: value.slice(0, slash), id: value.slice(slash + 1) }
}

/**
 * The member a row is about, and the team: the addressee of an instruction, the publisher of a
 * member's own row, the team a card, dispatch or folder row names. Null when the row names none;
 * the model then places it by its workflow.
 */
export function addressOf(message: Message): { team: string; member: string | null } | null {
  if (message.type.startsWith(InstructionPrefix)) {
    const to = splitMember(message.type.slice(InstructionPrefix.length))
    return to ? { team: to.team, member: to.id } : null
  }

  const payload = payloadOf(message)

  if (message.type === 'agentContainer.scheduleSkipped') {
    const who = splitMember(text(payload.member) || text(payload.container))
    if (who) return { team: who.team, member: who.id }
  }

  const from = splitMember(message.source)
  if (from) return { team: from.team, member: from.id }

  const team = text(payload.team)
  if (team !== '') return { team, member: text(payload.member) || null }

  return null
}

/** Where an instruction came from, when no member sent it. */
export function originOf(source: string): Origin {
  if (source.startsWith('schedule:') || source.startsWith('trigger:')) return 'trigger'
  if (source.startsWith('site:')) return 'site'
  return 'person'
}

/** "12.3k" for a token figure. */
export function compact(value: number): string {
  if (value >= 1_000_000) return `${(value / 1_000_000).toFixed(value >= 10_000_000 ? 0 : 1)}M`
  if (value >= 1000) return `${(value / 1000).toFixed(value >= 10_000 ? 0 : 1)}k`
  return String(Math.round(value))
}

function short(value: string, max = 48): string {
  const one = value.replace(/\s+/g, ' ').trim()
  return one.length > max ? `${one.slice(0, max - 1)}…` : one
}

/** Between an item's citation and its title in a workflow's name. */
export const ItemSeparator = ' — '

/**
 * A workflow's name with its number after (`<name> #N`), the name cut to `max` and the number never;
 * the number alone when there is no name.
 */
export function workflowNameLine(workflow: Pick<MonitorWorkflow, 'id' | 'title'>, max = 60): string {
  return workflow.title !== '' ? `${short(workflow.title, max)} #${workflow.id}` : `#${workflow.id}`
}

/** The item a dispatch row names, from its payload (`item`, `title`), or null. */
function dispatchedItem(payload: Record<string, unknown>): { id: number; title: string } | null {
  const id = payload.item
  if (typeof id !== 'number' || !Number.isInteger(id) || id < 0) return null
  return { id, title: text(payload.title) }
}

/** A whole token count as the wire sent it, or null when the row did not report one. */
function count(value: unknown): number | null {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 ? value : null
}

/**
 * A finished run's billable tokens, counted as `InvocationUsage.BillableTokens` counts them, in
 * whole numbers: uncached input and output in full, cache reads at 1/10, cache writes at 5/4, and a
 * combined `tokensTotal` as reported; a part of the split it did not report is 0. `'counted'` for a row whose usage another row carries
 * (`usageCountedOn`); null for a run that reported none, which is unmeasured and never a zero.
 */
export function runBillable(payload: Record<string, unknown>): number | 'counted' | null {
  if (payload.usageCountedOn !== undefined && payload.usageCountedOn !== null) return 'counted'
  if (payload.tokensSource === 'excludedEstimate') return null
  if (payload.tokensSource === 'noModel') return 0
  const total = count(payload.tokensTotal)
  if (total !== null) return total
  const split = [payload.tokensIn, payload.tokensOut, payload.tokensCachedIn, payload.tokensCacheCreation].map(count)
  if (split.every((part) => part === null)) return null
  const [tokensIn, tokensOut, cached, created] = split.map((part) => part ?? 0) as [number, number, number, number]
  return tokensIn + tokensOut + Math.floor(cached / 10) + Math.floor((created * 5) / 4)
}

/** The billable tokens a finished run adds to the figure: 0 when it is unmeasured or counted elsewhere. */
export function runTokens(payload: Record<string, unknown>): number {
  const billable = runBillable(payload)
  return typeof billable === 'number' ? billable : 0
}

/** The token figure's own float on a finished run: the same billable figure the stat adds. */
export function tokensFloat(spent: number): string {
  return `+${compact(spent)} tokens`
}

/**
 * The words under the token figure: "billable tokens · since 10:42", the stretch it covers from the
 * oldest row this Monitor holds (or the replay's start) to `now`, in the reader's own time, and how
 * many runs in it were not measured, when any were.
 */
export function tokensWords(since: number, now: number, unmeasured: number): string {
  const words = since > 0 ? `billable tokens · since ${localMinute(since, { date: crossesDays(since, now) })}` : 'billable tokens'
  return unmeasured > 0 ? `${words} · ${unmeasured} ${unmeasured === 1 ? 'run' : 'runs'} not measured` : words
}

/** The token figure's tooltip: what it counts, and that it is this window's, in one sentence. */
export const TokensTooltip =
  "Billable tokens of the runs in this Monitor's window, from its oldest row to now, with cache reads at a tenth and cache writes at five quarters - not the team strip's all-time total."

/** Is this the Manager, by the name every team gives it. */
function isManager(id: string, managerName: string): boolean {
  return id.toLowerCase() === managerName.toLowerCase()
}

export class MonitorModel {
  readonly teams = new Map<string, MonitorTeam>()
  readonly members = new Map<string, MonitorMember>()
  readonly workflows = new Map<number, MonitorWorkflow>()
  readonly ticker: TickerEntry[] = []

  /** The model's clock: the time of the last row applied, ms. */
  now = 0
  /** Billable tokens of every finished run applied (`runBillable`). */
  tokens = 0
  /** Finished runs applied that reported no usage: said beside the figure, never counted as zero. */
  unmeasured = 0
  /** The time of the oldest row applied, ms: where the token figure's stretch begins. */
  since = 0
  /** Times of recent rows, for the events-per-minute figure. */
  private recent: number[] = []
  /** Plugin event types' monitor hints, by type, from `GET /api/events`. */
  hints: ReadonlyMap<string, MonitorHint> = new Map()
  /** Whether and how long hinted rows are drawn as themselves; file changes share the time to live. */
  settings: ResourceSettings = DefaultSettings
  /** Whether file changes are drawn and listed, and how far down. */
  fileSettings: FileSettings = DefaultFileSettings
  /** Whether agent steps are drawn, live and in a replay. */
  stepSettings: StepSettings = DefaultStepSettings
  /** A replay plays the log, and the file changes recorded beside it (`GET /api/file-changes`). */
  replay = false
  /**
   * What the Host calls each team and member that exists now, and each member's kind: a replay
   * draws from the log alone, which names neither, so it takes them from here (`know`).
   */
  private known = new Map<string, { name: string; kind?: 'agent' | 'plugin' }>()
  /** Keys for the activity list's push lines, which have no seq: below every row's. */
  private pushSeq = 0

  /**
   * @param managerName What every team calls its Manager (`/api/overview`'s `managerName`).
   * @param tickerDepth How many rows the ticker keeps.
   * @param only When set, rows of any other team are passed over: a live monitor shows the teams
   *   that exist, a replay everything the log holds.
   */
  constructor(
    private readonly managerName = 'Manager',
    private readonly tickerDepth = 80,
    public only: Set<string> | null = null,
  ) {}

  /** Remembers the names and kinds of the teams and members that exist, without adding any. */
  know(teams: readonly Team[]) {
    for (const team of teams) {
      this.known.set(String(team.id), { name: team.name })
      for (const container of team.containers)
        this.known.set(`${String(team.id)}/${container.id}`, { name: container.name, kind: container.kind ?? 'agent' })
    }
  }

  team(id: string, name = id): MonitorTeam {
    let team = this.teams.get(id)
    if (!team) {
      if (name === id) name = this.known.get(id)?.name ?? id
      team = { id, name, hue: (this.teams.size * GoldenAngle + 190) % 360 }
      this.teams.set(id, team)
    } else if (name !== id) {
      team.name = name
    }
    return team
  }

  member(team: string, id: string, name = id, kind: 'agent' | 'plugin' = 'agent'): MonitorMember {
    this.team(team)
    const key = `${team}/${id}`
    let member = this.members.get(key)
    if (!member) {
      const known = this.known.get(key)
      if (known) {
        if (name === id) name = known.name
        kind = known.kind ?? kind
      }
      member = {
        key,
        team,
        id,
        name,
        kind,
        manager: isManager(id, this.managerName),
        state: 'idle',
        queued: 0,
        status: '',
        workflow: null,
        tokens: 0,
        runs: 0,
        lastAt: 0,
        wakes: [],
      }
      this.members.set(key, member)
    }
    return member
  }

  managerOf(team: string): string {
    return `${team}/${this.managerName}`
  }

  /**
   * The board's own snapshots: the teams and members that exist now, and each member's state as
   * the Host holds it. A snapshot wins over anything the rows said.
   */
  reconcile(teams: readonly Team[]) {
    const seen = new Set<string>()

    for (const team of teams) {
      if (team.archived === true) continue
      this.team(team.id, team.name)

      for (const container of team.containers) {
        const member = this.member(team.id, container.id, container.name, container.kind ?? 'agent')
        member.name = container.name
        member.kind = container.kind ?? 'agent'
        member.queued = container.queueDepth
        member.state = snapshotState(container)
        const said = container.blocked ?? container.needsDecision ?? container.failed
        if (said) member.status = said
        if (container.currentCorrelation !== null) member.workflow = container.currentCorrelation
        member.wakes = (container.wakes ?? []).map((wake) => ({ id: wake.id, dueAt: wireInstant(wake.dueAt), note: wake.note }))
        seen.add(member.key)
      }
    }

    if (this.only) {
      for (const [key, member] of this.members) {
        if (!seen.has(key) && this.only.has(member.team)) this.members.delete(key)
      }
    }
  }

  /** Events per minute over the last minute of model time. */
  rate(): number {
    const floor = this.now - 60_000
    this.recent = this.recent.filter((at) => at >= floor)
    return this.recent.length
  }

  counts() {
    let running = 0
    let waiting = 0
    let blocked = 0
    let queued = 0
    for (const member of this.members.values()) {
      if (member.state === 'running') running++
      if (member.state === 'waiting') waiting++
      if (member.state === 'blocked' || member.state === 'decision' || member.state === 'failed') blocked++
      queued += member.queued
    }
    let open = 0
    for (const workflow of this.workflows.values()) if (workflow.ending === 'open') open++
    return { running, waiting, blocked, queued, open, teams: this.teams.size, members: this.members.size }
  }

  /** Forget workflows that ended long enough ago, oldest first, keeping at most `keep` ended. */
  prune(endedFor: number, keep = 24) {
    const ended = [...this.workflows.values()]
      .filter((workflow) => workflow.endedAt !== null)
      .sort((a, b) => (a.endedAt ?? 0) - (b.endedAt ?? 0))
    const over = Math.max(0, ended.length - keep)
    ended.forEach((workflow, index) => {
      if (index < over || this.now - (workflow.endedAt ?? 0) > endedFor) this.workflows.delete(workflow.id)
    })
  }

  private workflow(id: number, team: string, title: string, at: number, item: MonitorWorkflow['item'] = null): MonitorWorkflow {
    let workflow = this.workflows.get(id)
    if (!workflow) {
      workflow = { id, team, title, item, members: new Set(), openedAt: at, endedAt: null, ending: 'open', events: 0 }
      this.workflows.set(id, workflow)
    }
    if (workflow.title === '' && title !== '') workflow.title = title
    if (workflow.item === null && item !== null) workflow.item = item
    return workflow
  }

  /**
   * One row. Returns what to draw for it, and appends its ticker line; a row of a team this model
   * does not show returns nothing and changes nothing.
   */
  apply(message: Message): MonitorEffect[] {
    const at = timeOf(message)
    if (at > this.now) this.now = at
    if (at > 0 && (this.since === 0 || at < this.since)) this.since = at

    const address = addressOf(message) ?? this.byWorkflow(message.correlationId)
    if (!address) return []
    if (this.only && !this.only.has(address.team)) return []

    this.recent.push(at)
    const payload = payloadOf(message)
    const effects: MonitorEffect[] = []
    const team = address.team
    this.team(team)
    const member = address.member ? this.member(team, address.member) : null
    const self: NodeRef = member ? { kind: 'member', key: member.key } : { kind: 'team', id: team }
    const flow: NodeRef = { kind: 'workflow', id: message.correlationId }
    const type = message.type
    let tone: Tone = 'quiet'
    let verb = type.replace('agentContainer.', '')
    let resource: Resource | undefined

    if (member) member.lastAt = at

    // A workflow row only ends a workflow already drawn; every other row may begin one.
    const drawn = (message.correlationId > 0 && !type.startsWith('workflow.')) || this.workflows.has(message.correlationId)
    // A DISPATCH ROW NAMES ITS WORKFLOW BY THE ITEM, before any instruction says anything: the
    // citation built from the number, a separator, the item's title.
    const item = type === 'backlog.item.dispatched' ? dispatchedItem(payload) : null
    const named = item
      ? `${itemLabel(item.id)}${item.title !== '' ? `${ItemSeparator}${item.title}` : ''}`
      : type.startsWith(InstructionPrefix) ? text(payload.subject) || text(payload.instruction) : ''
    const workflow = drawn
      ? this.workflow(message.correlationId, team, short(named, 60), at, item)
      : null
    if (workflow) {
      workflow.events++
      if (member) {
        workflow.members.add(member.key)
        member.workflow = workflow.id
      }
    }

    if (type.startsWith(InstructionPrefix) && member && message.source.startsWith(WakePrefix)) {
      // THE MEMBER'S OWN WAKE: nobody sent it, so it comes from no one - a ripple on the member.
      tone = 'tell'
      verb = 'woken'
      member.queued++
      if (member.state === 'idle' || member.state === 'failed') member.state = 'waiting'
      effects.push({ kind: 'ripple', at: self, tone })
    } else if (type.startsWith(InstructionPrefix) && member) {
      tone = 'tell'
      verb = 'told'
      member.queued++
      if (member.state === 'idle' || member.state === 'failed') member.state = 'waiting'
      const sender = splitMember(message.source)
      const from: NodeRef = sender
        ? { kind: 'member', key: this.member(sender.team, sender.id).key }
        : { kind: 'origin', team, origin: originOf(message.source) }
      effects.push({ kind: 'comet', from, to: self, tone, label: short(text(payload.subject) || text(payload.instruction), 36) })
    } else if (type.startsWith('wake.') && member) {
      // A WAKE'S LIFE, NAMED IN THE ACTIVITY LIST: asked, fired, cancelled or skipped. The clock on
      // the member follows: an ask sets it, anything else takes it down.
      const id = num(payload.wake)
      member.wakes = member.wakes.filter((wake) => wake.id !== id)
      if (type === 'wake.asked') {
        verb = 'asked to be woken'
        const dueAt = wireInstant(text(payload.dueAt))
        if (Number.isFinite(dueAt)) {
          member.wakes = [...member.wakes, { id, dueAt, note: text(payload.note) }].sort((a, b) => a.dueAt - b.dueAt)
        }
      } else if (type === 'wake.fired') {
        tone = 'tell'
        verb = 'wake fired'
      } else if (type === 'wake.cancelled') {
        verb = 'wake cancelled'
      } else {
        verb = 'wake skipped'
      }
      effects.push({ kind: 'ripple', at: self, tone })
    } else if (type === 'agentContainer.started' && member) {
      tone = 'start'
      verb = 'started'
      member.queued = Math.max(0, member.queued - 1)
      member.state = 'running'
      effects.push({ kind: 'ripple', at: self, tone })
    } else if (type === 'agentContainer.progress' && member) {
      tone = 'progress'
      verb = 'working'
      member.status = text(payload.status)
      if (member.state !== 'running') member.state = 'running'
      if (member.status !== '') effects.push({ kind: 'float', at: self, text: short(member.status, 40), tone })
    } else if (type === 'agentContainer.completed' && member) {
      tone = 'done'
      verb = 'finished'
      member.runs++
      const spent = this.spend(member, payload)
      member.state = member.queued > 0 ? 'waiting' : 'idle'
      effects.push({ kind: 'burst', at: self, tone })
      if (spent > 0) effects.push({ kind: 'float', at: self, text: tokensFloat(spent), tone: 'done' })
      if (workflow && payload.handedBack !== true) effects.push({ kind: 'comet', from: self, to: flow, tone })
    } else if (type === 'agentContainer.handback' && member) {
      tone = 'handback'
      verb = 'handed back'
      const manager = this.managerOf(team)
      if (manager !== member.key) {
        this.member(team, this.managerName)
        effects.push({ kind: 'comet', from: self, to: { kind: 'member', key: manager }, tone, label: 'hand-back' })
      }
    } else if (type === 'agentContainer.failed' && member) {
      tone = 'fail'
      verb = 'failed'
      member.runs++
      this.spend(member, payload)
      member.state = 'failed'
      member.status = summarise(message)
      effects.push({ kind: 'burst', at: self, tone, big: true }, { kind: 'shake', at: self })
    } else if (type === 'agentContainer.blocked' && member) {
      tone = 'blocked'
      verb = 'blocked'
      if (payload.item === undefined) member.state = 'blocked'
      member.status = text(payload.reason)
      effects.push({ kind: 'burst', at: self, tone, big: true })
    } else if (type === 'agentContainer.needsDecision' && member) {
      tone = 'blocked'
      verb = 'asks'
      member.state = 'decision'
      member.status = text(payload.question)
      effects.push({ kind: 'burst', at: self, tone })
      effects.push({ kind: 'float', at: self, text: 'needs a decision', tone })
    } else if (type === 'agentContainer.rejected' && member) {
      tone = 'fail'
      verb = 'refused'
      effects.push({ kind: 'ripple', at: self, tone })
    } else if (type === 'agentContainer.removed' && member) {
      this.members.delete(member.key)
      return []
    } else if (type === 'agentContainer.scheduleSkipped') {
      verb = 'skipped'
      effects.push({ kind: 'ripple', at: self, tone: 'quiet' })
    } else if (type === 'workflow.completed' || type === 'workflow.closed') {
      tone = type === 'workflow.completed' ? 'workflow' : 'quiet'
      verb = type === 'workflow.completed' ? 'workflow done' : 'closed'
      const ended = this.workflows.get(message.correlationId)
      if (ended && ended.ending === 'open') {
        ended.ending = type === 'workflow.completed' ? 'completed' : 'closed'
        ended.endedAt = at
        effects.push({ kind: 'burst', at: flow, tone, big: tone === 'workflow' })
      }
    } else if (type === 'workflow.paused' || type === 'workflow.resumed') {
      verb = type === 'workflow.paused' ? 'paused' : 'resumed'
      effects.push({ kind: 'ripple', at: flow, tone: 'quiet' })
    } else if (type.startsWith('kanban.card.')) {
      tone = 'card'
      const what = type.slice('kanban.card.'.length)
      verb = what === 'planned' ? 'planned a card' : what === 'moved' ? 'moved a card' : `card ${what}`
      const words = what === 'planned' ? text(payload.title) : what === 'moved' ? text(payload.change) : ''
      effects.push({ kind: 'float', at: self, text: words !== '' ? `▤ ${short(words, 40)}` : '▤ card', tone })
    } else if (type === 'backlog.item.dispatched') {
      tone = 'tell'
      verb = 'dispatched'
      const manager = this.member(team, this.managerName)
      effects.push({
        kind: 'comet',
        from: { kind: 'origin', team, origin: 'backlog' },
        to: { kind: 'member', key: manager.key },
        tone,
        label: short(text(payload.title), 36),
      })
    } else if (type === 'repo.pushed') {
      tone = 'push'
      verb = 'pushed'
      effects.push({ kind: 'ripple', at: self, tone })
      effects.push({ kind: 'float', at: self, text: `⇡ ${short(text(payload.branches), 32)}`, tone })
    } else if (type === 'repo.pushFailed') {
      tone = 'fail'
      verb = 'push failed'
      effects.push({ kind: 'ripple', at: self, tone })
    } else if (type.startsWith('plugin.')) {
      tone = 'plugin'
      verb = type.slice(type.lastIndexOf('.') + 1)
      const hint = this.hints.get(type)
      if (hint) {
        resource = resolveHint(hint, type, payload)
        verb = resource.action
      }
      // Settings off, or this plugin switched off: the plain burst, and the list still says it.
      if (resource && drawsResource(this.settings, resource.plugin)) {
        effects.push({ kind: 'resource', at: self, resource, ttlMs: this.settings.seconds * 1000 })
      } else {
        effects.push({ kind: 'burst', at: self, tone })
      }
    } else if (type === 'file.changed') {
      tone = 'file'
      verb = 'files changed'
      effects.push({ kind: 'ripple', at: { kind: 'team', id: team }, tone })
    } else if (type === 'site.action') {
      tone = 'tell'
      verb = 'site action'
      effects.push({
        kind: 'comet',
        from: { kind: 'origin', team, origin: 'site' },
        to: { kind: 'member', key: this.member(team, this.managerName).key },
        tone,
      })
    } else if (type === 'solution.checked') {
      tone = 'workflow'
      verb = 'package checked'
      effects.push({ kind: 'burst', at: self, tone })
    } else {
      effects.push({ kind: 'ripple', at: self, tone: 'quiet' })
    }

    // A progress line is the member talking, not news: the ticker keeps the rows a person reads.
    if (type !== 'agentContainer.progress') {
      this.ticker.unshift({
        seq: message.seq,
        at,
        team,
        member: member?.name ?? null,
        tone,
        verb,
        text: short(summarise(message), 140),
        workflow: message.correlationId,
        ...(resource ? { resource } : {}),
      })
      if (this.ticker.length > this.tickerDepth) this.ticker.length = this.tickerDepth
    }

    return effects
  }

  /** A finished run's billable tokens, added to its member and the figure; an unmeasured run is counted apart. */
  private spend(member: MonitorMember, payload: Record<string, unknown>): number {
    const billable = runBillable(payload)
    if (billable === null) this.unmeasured++
    const spent = typeof billable === 'number' ? billable : 0
    member.tokens += spent
    this.tokens += spent
    return spent
  }

  /**
   * One file-changes push. Returns what to draw for it and appends its line to the activity list;
   * nothing with the setting off, or for a team this model does not show. A replay feeds the
   * recorded changes through here at their times, so they draw and list as they did live.
   */
  applyWorkspace(push: WorkspacePush): MonitorEffect[] {
    if (!this.fileSettings.show) return []
    if (this.only && !this.only.has(push.team)) return []
    this.team(push.team)
    const member = this.members.get(`${push.team}/${push.member}`) ?? null
    const at = pushTime(push) ?? this.now
    this.ticker.unshift({
      seq: --this.pushSeq,
      at,
      team: push.team,
      member: member?.name ?? push.member,
      tone: 'file',
      verb: push.commits.length > 0 ? 'committed' : 'changed files',
      text: pushHeadline(push),
      workflow: 0,
      workspace: true,
      files: filesOf(push).map((file) => ({ path: file.path, action: file.action })),
      more: pushMoreWords(push),
    })
    if (this.ticker.length > this.tickerDepth) this.ticker.length = this.tickerDepth
    const self: NodeRef = member ? { kind: 'member', key: member.key } : { kind: 'team', id: push.team }
    return [{ kind: 'workspace', at: self, push, ttlMs: this.settings.seconds * 1000 }]
  }

  /**
   * A member's agent steps. Returns one step effect each, on the member's node; nothing with the
   * setting off, for a team this model does not show, or for a member it has no node for. Never a
   * line in the activity list: a step says what kind of thing was done, and the list says what was.
   */
  applySteps(steps: readonly AgentStep[]): MonitorEffect[] {
    if (!this.stepSettings.show) return []
    const effects: MonitorEffect[] = []
    for (const step of steps) {
      if (this.only && !this.only.has(step.team)) continue
      const member = this.members.get(`${step.team}/${step.member}`)
      if (!member) continue
      effects.push({ kind: 'step', at: { kind: 'member', key: member.key }, step })
    }
    return effects
  }

  /**
   * The push lines of an earlier model of this view, kept when it starts again from the rows held:
   * they are in no row, so feeding the log again would lose them.
   */
  keepPushLines(lines: readonly TickerEntry[]) {
    const kept = lines.filter((line) => line.workspace === true && (!this.only || this.only.has(line.team)))
    if (kept.length === 0) return
    this.pushSeq = Math.min(this.pushSeq, ...kept.map((line) => line.seq))
    this.ticker.push(...kept)
    this.ticker.sort((a, b) => b.at - a.at)
    if (this.ticker.length > this.tickerDepth) this.ticker.length = this.tickerDepth
  }

  private byWorkflow(correlation: number): { team: string; member: null } | null {
    const workflow = this.workflows.get(correlation)
    return workflow ? { team: workflow.team, member: null } : null
  }
}

function snapshotState(container: Team['containers'][number]): MemberState {
  if (container.failed) return 'failed'
  if (container.needsDecision) return 'decision'
  if (container.blocked) return 'blocked'
  if (container.state === 'Running') return container.held ? 'waiting' : 'running'
  if (container.queueDepth > 0 || container.held) return 'waiting'
  return 'idle'
}

/** A recorded file change's time in ms; one whose time cannot be read is placed at 0, never dropped. */
export function changeTime(push: WorkspacePush): number {
  return pushTime(push) ?? 0
}

/** What a replay's clock has due: the log rows, the recorded file changes and agent steps, each in time order. */
export interface ReplayDue {
  rows: Message[]
  changes: WorkspacePush[]
  steps: AgentStep[]
}

/**
 * THE REPLAY'S CLOCK. Rows are played at `speed` times their real pace, and a quiet stretch longer
 * than `maxGap` of real time is skipped down to that, so a night with nothing in it does not hold
 * the screen still. Returns the rows due by `elapsed` ms of wall time since the replay began.
 *
 * The members' recorded file changes (`GET /api/file-changes`) and agent steps (`GET /api/run-steps`)
 * play on the same clock, each at its own time beside the rows: a quiet stretch is one with none.
 */
export class ReplayClock {
  private index = 0
  private changeIndex = 0
  private readonly changes: readonly WorkspacePush[]
  private stepIndex = 0
  private readonly steps: readonly AgentStep[]
  private virtual: number
  speed: number

  constructor(
    private readonly rows: readonly Message[],
    speed: number,
    private readonly maxGap = 20_000,
    start = rows.length > 0 ? timeOf(rows[0]!) : 0,
    changes: readonly WorkspacePush[] = [],
    steps: readonly AgentStep[] = [],
  ) {
    this.speed = speed
    this.virtual = start
    this.index = rows.findIndex((row) => timeOf(row) >= start)
    if (this.index < 0) this.index = rows.length
    this.changes = [...changes].sort((a, b) => changeTime(a) - changeTime(b))
    this.changeIndex = this.changes.findIndex((change) => changeTime(change) >= start)
    if (this.changeIndex < 0) this.changeIndex = this.changes.length
    this.steps = [...steps].sort((a, b) => stepTime(a) - stepTime(b))
    this.stepIndex = this.steps.findIndex((step) => stepTime(step) >= start)
    if (this.stepIndex < 0) this.stepIndex = this.steps.length
  }

  get done(): boolean {
    return this.index >= this.rows.length && this.changeIndex >= this.changes.length && this.stepIndex >= this.steps.length
  }

  /** The log time the replay has reached, ms. */
  get at(): number {
    return this.virtual
  }

  /** Advance by `wallMs` of wall time and return the rows now due, in order. */
  advance(wallMs: number): Message[] {
    return this.step(wallMs).rows
  }

  /** Advance by `wallMs` of wall time and return the rows, file changes and steps now due, each in order. */
  step(wallMs: number): ReplayDue {
    const due: ReplayDue = { rows: [], changes: [], steps: [] }
    if (this.done) return due
    const nextRow = this.index < this.rows.length ? timeOf(this.rows[this.index]!) : Number.POSITIVE_INFINITY
    const nextChange = this.changeIndex < this.changes.length ? changeTime(this.changes[this.changeIndex]!) : Number.POSITIVE_INFINITY
    const nextStep = this.stepIndex < this.steps.length ? stepTime(this.steps[this.stepIndex]!) : Number.POSITIVE_INFINITY
    const next = Math.min(nextRow, nextChange, nextStep)
    // Skip the quiet: never wait longer than maxGap of real time for the next row, change or step.
    if (next - this.virtual > this.maxGap) this.virtual = next - this.maxGap
    this.virtual += wallMs * this.speed

    while (this.index < this.rows.length && timeOf(this.rows[this.index]!) <= this.virtual) {
      due.rows.push(this.rows[this.index]!)
      this.index++
    }
    while (this.changeIndex < this.changes.length && changeTime(this.changes[this.changeIndex]!) <= this.virtual) {
      due.changes.push(this.changes[this.changeIndex]!)
      this.changeIndex++
    }
    while (this.stepIndex < this.steps.length && stepTime(this.steps[this.stepIndex]!) <= this.virtual) {
      due.steps.push(this.steps[this.stepIndex]!)
      this.stepIndex++
    }
    return due
  }
}

export interface TimelineBucket {
  from: number
  to: number
  count: number
  /** The tone that most rows in it carry, progress aside. */
  tone: Tone
}

/** The rows' times cut into `buckets` equal slices, each with its count and leading tone. */
export function timeline(rows: readonly Message[], buckets: number): TimelineBucket[] {
  if (rows.length === 0 || buckets <= 0) return []
  const first = timeOf(rows[0]!)
  const last = Math.max(first + 1, timeOf(rows[rows.length - 1]!))
  const width = (last - first) / buckets
  const out: TimelineBucket[] = Array.from({ length: buckets }, (_, i) => ({
    from: first + i * width,
    to: first + (i + 1) * width,
    count: 0,
    tone: 'quiet' as Tone,
  }))
  const tones = out.map(() => new Map<Tone, number>())

  for (const row of rows) {
    const index = Math.min(buckets - 1, Math.floor((timeOf(row) - first) / width))
    out[index]!.count++
    const tone = toneOf(row.type)
    if (tone !== 'progress' && tone !== 'quiet') tones[index]!.set(tone, (tones[index]!.get(tone) ?? 0) + 1)
  }

  out.forEach((bucket, i) => {
    let best: Tone = bucket.count > 0 ? 'progress' : 'quiet'
    let most = 0
    for (const [tone, count] of tones[i]!) {
      if (count > most) {
        most = count
        best = tone
      }
    }
    bucket.tone = best
  })
  return out
}

/** A row type's tone, as `apply` gives it, for the timeline's colours. */
export function toneOf(type: string): Tone {
  if (type.startsWith(InstructionPrefix) || type === 'backlog.item.dispatched' || type === 'site.action') return 'tell'
  if (type === 'agentContainer.started') return 'start'
  if (type === 'agentContainer.progress') return 'progress'
  if (type === 'agentContainer.completed') return 'done'
  if (type === 'agentContainer.handback') return 'handback'
  if (type === 'agentContainer.failed' || type === 'agentContainer.rejected' || type === 'repo.pushFailed') return 'fail'
  if (type === 'agentContainer.blocked' || type === 'agentContainer.needsDecision') return 'blocked'
  if (type === 'workflow.completed' || type === 'solution.checked') return 'workflow'
  if (type.startsWith('kanban.card.')) return 'card'
  if (type === 'repo.pushed') return 'push'
  if (type.startsWith('plugin.')) return 'plugin'
  if (type === 'file.changed') return 'file'
  return 'quiet'
}
