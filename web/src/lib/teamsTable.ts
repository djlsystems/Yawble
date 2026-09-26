import type { Team, TeamId, TeamWorkflowTiming, TeamWorkflows } from '../api/types'
import {
  RowLoudness,
  teamChip,
  teamLogFromTiming,
  heldMembers,
  teamStatus,
  WaitingForSlot,
  workflowsTile,
  type TeamChip,
  type TeamStatus,
} from './teamKpis'

/**
 * WHAT THE TABLE SORTS ON, which is never what it RENDERS.
 *
 * `startedAt` is a number rather than the rendered cell, because "started 14:02" sorts as text into
 * an order that has nothing to do with time, and null is a team that has never run rather than one
 * that ran at the epoch.
 */
export interface TeamRow {
  /**
   * BRANDED, so a row's id cannot be handed to a function that wants a team NAME - and so the
   * component that opens a row need not re-assert `id as TeamId` at the call. A cast at a call
   * site is a claim nothing checks; the brand moves it to where the value actually comes from,
   * which is `Team.id` and is already a `TeamId`.
   */
  id: TeamId
  name: string
  members: number
  /** Members by what they are doing now, e.g. `1 running · 3 queued · 2 idle`. */
  memberBreakdown: string
  status: TeamStatus

  /** Who is held behind the WIP limit, by label. Named beside a `waiting` status. */
  held: string[]
  startedAt: number | null

  /**
   * TWO ANSWERS, DELIBERATELY, BECAUSE THEY ARE TWO QUESTIONS.
   *
   * `status` is the ROSTER: is anybody working right now. `workflows` is the PROJECTION: what is
   * open and what is the loudest of it. One cell trying to be both is what produced a Teams table
   * reading IDLE beside that team's own tile reading STALLED, both true, about different workflows,
   * neither saying which.
   *
   * Null until the plural payload lands - never a stand-in word. An absent answer is not `idle`.
   */
  workflows: TeamChip | null
}

export interface TeamSort {
  // `'workflows'` (the plural projection's chip) is deliberately distinct from `'workflow'` (the
  // Last workflow column's instant) - two different questions, two different sort keys, the same
  // split {@link TeamRow.workflows} draws against `status`.
  column: 'name' | 'members' | 'status' | 'workflows' | 'workflow'
  descending: boolean
}

/** Name ascending. A person looking for a team looks for its name. */
export const DefaultTeamSort: TeamSort = { column: 'name', descending: false }

/**
 * ONE ROW OF THE TABLE, from the two things the store holds about a team.
 *
 * HERE RATHER THAN IN THE COMPONENT, and that is the same rule as everything else in this file:
 * script inside an SFC is exactly as untestable in this repository as a template binding, because
 * no spec can mount or import a `.vue` file. It shipped in the component for one revision and took
 * the `NaN` below with it - a defect the tests two functions away were careful to make impossible.
 */
export function teamRowFrom(
  team: Team,
  timing: TeamWorkflowTiming | null,
  workflows: TeamWorkflows | null,
  now: number,
): TeamRow {
  return {
    id: team.id,
    name: team.name,
    members: team.containers.length,
    memberBreakdown: memberBreakdown(team.containers),
    status: teamStatus(team.containers, teamLogFromTiming(timing), now, team.paused === true),
    held: heldMembers(team.containers),
    startedAt: instantOf(timing?.startedAt ?? null),

    // NULL UNTIL THE PLURAL PAYLOAD LANDS - never a stand-in word, and never derived from `timing`
    // above: that is the SINGULAR newest-workflow answer `status` already reads, and reusing it here
    // would give the chip the very "one workflow speaks for all of them" defect this field exists to
    // remove.
    workflows: workflows
      ? teamChip(workflowsTile(workflows, team.containers, now), team.containers)
      : null,
  }
}

/**
 * A SORTABLE INSTANT, or null - and NEVER `NaN`.
 *
 * `Date.parse` answers `NaN` for anything it cannot read, and `NaN` is not `null`: it walks
 * straight past `compareTeams`' nulls-last branch, makes every comparison involving it `NaN`, and
 * hands `Array.prototype.sort` a comparator that is not a total order - at which point the row
 * order is implementation-defined. The absent case and the unreadable case are the same answer
 * here BECAUSE they are the same answer to the only question the table asks: when did this team
 * last start work? Nobody knows.
 */
function instantOf(instant: string | null): number | null {
  if (instant === null) return null

  const parsed = Date.parse(instant)

  return Number.isNaN(parsed) ? null : parsed
}

/**
 * What clicking a column heading does: the same column flips direction, a different one starts
 * ascending.
 *
 * A NEW OBJECT rather than a mutation, so a caller holding a `ref` sees the change.
 */
export function nextSort(current: TeamSort, column: TeamSort['column']): TeamSort {
  return {
    column,
    descending: current.column === column ? !current.descending : false,
  }
}

/**
 * THE SORTABLE COLUMNS, typed as the union rather than as strings. Untyped, a column added to
 * {@link TeamSort} and forgotten here reads back as unknown and silently falls to the default -
 * and a string here that is NOT a column compiles happily and admits a value the comparator has
 * no arm for.
 */
const Columns: TeamSort['column'][] = ['name', 'members', 'status', 'workflows', 'workflow']

/** A narrowing read, so {@link readTeamSort} keeps no cast of its own. */
function isColumn(value: string): value is TeamSort['column'] {
  return (Columns as string[]).includes(value)
}

/**
 * THE RANKING, and it is the chip's own order rather than the alphabet: a column that sorted
 * `blocked, failed, idle, misconfigured` would bury the two rows a person is looking for.
 *
 * A `Record` RATHER THAN AN ARRAY, and the exhaustiveness is the whole reason. `indexOf` answers
 * -1 for a status this list has never heard of, so a new `TeamStatus` would rank FIRST - above
 * `misconfigured` - and nothing would fail: the column would simply put the new word at the top of
 * every page. As a `Record` keyed on the union, adding a status is a compile error here.
 */
const StatusOrder: Record<TeamStatus, number> = {
  misconfigured: 0,
  failed: 1,
  blocked: 2,
  paused: 3,
  running: 4,
  waiting: 5,
  queued: 6,
  undeclared: 7,
  idle: 8,
}

/**
 * THE WORKFLOWS COLUMN'S RANKING, BORROWED RATHER THAN REBUILT. {@link RowLoudness} in
 * `teamKpis.ts` is the one table that says which of the seven workflow words outranks another, and
 * a second copy here is exactly how a chip and the column beneath it would drift apart - the same
 * argument `StatusOrder` above already makes for the roster ranking.
 *
 * `MISCONFIGURED` sits OUTSIDE that table - it is a member-level fact, not a workflow one, per
 * `teamChip`'s own comment - and ranks loudest of all, ahead of every entry `RowLoudness` holds,
 * because `teamChip` itself checks it first. A `null` chip (the plural payload has not landed) or a
 * `null` label (nothing ranked - an empty tile, or an `UNDECLARED` row still inside its grace
 * period) ranks quietest of all: an absent answer is not a fact on the loudness scale.
 *
 * NO CAST, AND THE TWO CHECKS ABOVE THE LOOKUP ARE WHAT REMOVE THE NEED FOR ONE.
 * `TeamChip.label` is the union rather than `string`, so once `null` and `'MISCONFIGURED'` are
 * handled the compiler knows what is left is a `WorkflowState` and the lookup is total. This read
 * `RowLoudness[chip.label as WorkflowState]`, which defeated the very guarantee that `Record`
 * exists to give: a label outside the union yields `undefined`, a `NaN` comparator, and arbitrary
 * row order, with nothing failing. Safe while that union had one producer - and a state was about
 * to be added to it, which is exactly when it would have bitten.
 */
function chipRank(chip: TeamChip | null): number {
  if (chip === null || chip.label === null) return Number.POSITIVE_INFINITY
  if (chip.label === 'MISCONFIGURED') return -1
  if (chip.label === WaitingForSlot) return RowLoudness.RUNNING

  return RowLoudness[chip.label]
}

/**
 * What the Last workflow cell says, in four shapes and no more - and the first two share an em
 * dash while saying two different things.
 *
 * AN EM DASH FOR A TEAM THAT HAS NEVER RUN, never `0m`: zero is a measured duration and this is an
 * absent one. `ended` is said ONLY when a manager DECLARED the workflow finished - `endedAt` is
 * null while it is open, which is the ordinary state, because most workflows never get a
 * declaration. Saying "ended" about the last terminal row would tell a person work was finished
 * that nobody ever said was.
 *
 * NULL IS "THE ROLLUP HAS NOT LANDED" AND IS NOT "NEVER RUN". `workflowFor` answers null for both
 * a fetch that has not arrived and one that failed, and `never run` is the shape this file
 * reserves for a measurement the SERVER actually made - `available: false`, which is the server
 * saying it looked and found nothing. Collapsing the two put "never run" against every row for the
 * rest of a session whenever one rollup failed, about teams that run constantly: a confident wrong
 * answer where the honest one costs nothing. They render the same dash because there is nothing to
 * show either way; only the hint can tell them apart, so only the hint does.
 */
export function lastWorkflowText(
  timing: TeamWorkflowTiming | null,
): { text: string; hint: string } {
  // `== null`, LOOSE, for the same reason as the two tests below it - and it is the same field's
  // rule read at the same altitude. A caller holding an `undefined` rollup (a lookup that missed,
  // a payload that stopped carrying the entry) is saying exactly what a `null` one says: nothing
  // has landed. A strict test here would fall through to `timing.available` and throw on a value
  // this file already knows how to describe.
  if (timing == null) return { text: '—', hint: 'not loaded yet' }

  if (!timing.available) return { text: '—', hint: 'never run' }

  const state = timing.state ?? 'unknown'

  // `!= null`, LOOSE ON PURPOSE, so it catches `undefined` as well. An absent field on the wire
  // arrives as `undefined`, which is not `null` - so a strict test takes the `ended` arm for a
  // workflow nobody declared finished and renders `ended —, Running`, the one shape the
  // three-shape rule forbids. A server that stops sending the field would produce it, and nothing
  // would flag it.
  if (timing.endedAt != null) {
    return {
      text: `ended ${clock(timing.endedAt)}, ${state}`,

      // TWO WAYS A WORKFLOW ENDS, AND THE HINT IS THE ONLY THING THAT CAN SAY WHICH. `endedAt` is
      // non-null for a person's `workflow.closed` as well as a manager's `workflow.completed` -
      // both have ended and both stop the clock - so a single hint here would credit a manager for
      // a declaration nobody made - the fiction the log refuses to record, moved into a tooltip.
      hint:
        state === 'Closed'
          ? 'A person closed this workflow. Nobody declared what was delivered.'
          : 'A manager declared this workflow finished.',
    }
  }

  return {
    text: `started ${clock(timing.startedAt)}, ${state}`,
    hint: 'Open: no manager has declared it finished, which is the ordinary state.',
  }
}

function clock(instant: string | null): string {
  // `== null` for the same reason as the caller above: absent on the wire is `undefined`, and the
  // sibling of a defect is the defect.
  if (instant == null) return '—'

  const parsed = new Date(instant)

  return Number.isNaN(parsed.getTime())
    ? '—'
    : parsed.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
}

/**
 * The chosen column, then NAME.
 *
 * A SECOND KEY, because most of the columns are coarse: Status has seven values, Members is a
 * small integer, and a workflow instant is shared by every team that has never
 * run. Without a tiebreak the rows WITHIN one of those buckets fall in whatever order
 * `/api/overview` happened to answer in - so a person sorting by Status watches the idle teams
 * shuffle between two refreshes, and cannot find the row they were just looking at. `Array.sort`
 * is stable, which makes it worse rather than better: it faithfully preserves an order nobody
 * chose.
 *
 * THE TIEBREAK DOES NOT TAKE THE DIRECTION, and that is deliberate. The arrow in the header names
 * ONE column; flipping it should not silently reverse a key nobody clicked on. Names run A to Z
 * inside every bucket, in both directions, which is the order a person scans in either way.
 */
export function compareTeams(a: TeamRow, b: TeamRow, sort: TeamSort): number {
  const primary = comparePrimary(a, b, sort)

  return primary !== 0 ? primary : byName(a, b)
}

/** Case-insensitive, like the Name column's own arm - one rule, read from two places. */
function byName(a: TeamRow, b: TeamRow): number {
  return a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })
}

function comparePrimary(a: TeamRow, b: TeamRow, sort: TeamSort): number {
  const direction = sort.descending ? -1 : 1

  switch (sort.column) {
    case 'members':
      return (a.members - b.members) * direction
    case 'status':
      return (StatusOrder[a.status] - StatusOrder[b.status]) * direction
    case 'workflows': {
      const aRank = chipRank(a.workflows)
      const bRank = chipRank(b.workflows)

      // NULLS LAST IN BOTH DIRECTIONS, for `workflow`'s own reason below: a team whose plural
      // payload has not landed (or ranked nothing) is not the quietest team, it is no answer at
      // all, and flipping the arrow must not float that absence to the top.
      if (aRank === Number.POSITIVE_INFINITY && bRank === Number.POSITIVE_INFINITY) return 0
      if (aRank === Number.POSITIVE_INFINITY) return 1
      if (bRank === Number.POSITIVE_INFINITY) return -1

      // ASCENDING IS LOUDEST FIRST, matching `status` above: rank 0 (or MISCONFIGURED's -1) is the
      // worst news, and that is what a person sorting this column is looking for.
      return (aRank - bRank) * direction
    }
    case 'workflow': {
      // NULLS LAST IN BOTH DIRECTIONS. A team that has never run is not the oldest one; it is
      // absent, and flipping the arrow should not float absence to the top.
      if (a.startedAt === null && b.startedAt === null) return 0
      if (a.startedAt === null) return 1
      if (b.startedAt === null) return -1

      // ASCENDING IS OLDEST FIRST, like every other column, and that is what makes the arrow
      // honest. It was `b - a`, which sorted newest-first while the header rendered
      // `arrow_upward` - a control saying the opposite of what it did. Newest-first is one more
      // click, and inverting the arrow for this column alone would need a second rule explaining
      // why up means down here.
      return (a.startedAt - b.startedAt) * direction
    }
    default:
      return byName(a, b) * direction
  }
}

/**
 * A PER-VIEWER PREFERENCE, never team data - one of them is on a phone, and a sort order is the
 * same kind of thing as a card's size.
 *
 * Read defensively: an unknown column and a non-boolean direction both fall back to the default,
 * because a stored value that survives every comparison and lands in a comparator is how a table
 * ends up sorted by nothing at all.
 */
export function readTeamSort(raw: string | null): TeamSort {
  if (raw === null) return DefaultTeamSort

  try {
    const parsed = JSON.parse(raw) as Partial<TeamSort>

    if (typeof parsed.descending !== 'boolean') return DefaultTeamSort
    if (typeof parsed.column !== 'string' || !isColumn(parsed.column)) return DefaultTeamSort

    return { column: parsed.column, descending: parsed.descending }
  } catch {
    return DefaultTeamSort
  }
}

export function writeTeamSort(sort: TeamSort): string {
  return JSON.stringify(sort)
}

/**
 * The name the Clone dialog opens with.
 *
 * A CONVENIENCE AND NOTHING MORE. Availability is the SERVER'S answer, decided on the derived
 * IDENTIFIER, and a collision comes back 409 naming the existing team. The browser must not derive
 * an identifier: `DeriveName` is lossy and lives in C#, and a second copy in TypeScript is two
 * stores of one fact - which is the exact defect a suffix computed on the display name walks into.
 */
export function proposeCloneName(source: string, taken: string[]): string {
  const used = new Set(taken.map((name) => name.trim().toLowerCase()))

  for (let suffix = 2; suffix < 1000; suffix += 1) {
    const candidate = `${source} ${suffix}`

    if (!used.has(candidate.toLowerCase())) return candidate
  }

  // A THOUSAND TEAMS ALL NAMED AFTER THIS ONE, which nobody has, and the walk is bounded so a
  // pathological list cannot spin. Returning `source` here would hand the dialog the one name
  // this function KNOWS is taken - the source team's own - so the person meets a 409 on a value
  // that was prefilled for them. `1000` is simply the next candidate the loop never reached: not
  // known to be free, but not known to be taken either, and the server decides availability on
  // the derived identifier regardless. Prefilling is a convenience; being confidently wrong is not.
  return `${source} 1000`
}

/**
 * Members by what they are doing now, so a one-word team status explains itself. Running and
 * queued come first because they are what a person scanning the table is looking for; states with
 * nobody in them are left out.
 */
export function memberBreakdown(
  containers: readonly { state: string; queueDepth: number; blocked?: string | null; failed?: string | null; held?: boolean }[],
): string {
  let running = 0
  let waiting = 0
  let queued = 0
  let blocked = 0
  let failed = 0
  let idle = 0

  for (const container of containers) {
    if (container.state === 'Running') running++
    else if (container.held === true) waiting++
    else if (container.queueDepth > 0) queued++
    else if (container.blocked) blocked++
    else if (container.failed) failed++
    else idle++
  }

  return [
    [running, 'running'],
    [waiting, 'waiting for a slot'],
    [queued, 'queued'],
    [blocked, 'blocked'],
    [failed, 'failed'],
    [idle, 'idle'],
  ]
    .filter(([count]) => (count as number) > 0)
    .map(([count, label]) => `${count} ${label}`)
    .join(' · ')
}

/** The word a roster status reads as. Every status is its own word but `waiting`, which says why. */
export function teamStatusText(status: TeamStatus, held: readonly string[] = []): string {
  if (status !== 'waiting') return status

  return held.length > 0 ? `waiting for a slot: ${held.join(', ')}` : 'waiting for a slot'
}
