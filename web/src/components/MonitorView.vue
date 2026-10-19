<script setup lang="ts">
import FieldHelp from './FieldHelp.vue'
import { computed, nextTick, onMounted, onUnmounted, ref, shallowRef, watch } from 'vue'
import { useQuasar } from 'quasar'
import { getFileChanges, getMessages, getRunSteps, listEvents } from '../api/client'
import type { Message, MonitorHint, TeamId } from '../api/types'
import { useConsoleStore } from '../stores/console'
import { useWorkflowNavStore } from '../stores/workflowNav'
import { boardHref, memberHref } from '../lib/consoleLinks'
import { itemLabel } from '../lib/backlog'
import { useAppearanceStore } from '../stores/appearance'
import { textPx } from '../lib/appearance'
import { localTime } from '../lib/localTime'
import { ToneLegend } from '../lib/eventWords'
import { StateWords } from '../lib/states'
import {
  ItemSeparator,
  MonitorModel,
  ReplayClock,
  TokensTooltip,
  compact,
  stampOf,
  timeOf,
  timeline,
  tokensWords,
  workflowNameLine,
  type MonitorEffect,
  type NodeRef,
  type TickerEntry,
  type Tone,
} from '../lib/monitor/model'
import { MonitorScene, readPalette, type HoverInfo } from '../lib/monitor/scene'
import {
  SecondsMax,
  SecondsMin,
  clampSeconds,
  hintedPlugins,
  hintsFrom,
  readSettings,
  writeSettings,
  type ResourceAction,
  type ResourceSettings,
} from '../lib/monitor/hints'
import { MonitorSymbolsFamily, glyphOf, loadMonitorSymbols, type Codepoints } from '../lib/monitor/font'
import {
  fileWatch,
  MaxRecordedChanges,
  pushTime,
  readFileSettings,
  readRecorded,
  writeFileSettings,
  type FileDepth,
  type FileSettings,
  type RecordedChanges,
  type WorkspacePush,
} from '../lib/monitor/workspace'
import {
  MaxRecordedSteps,
  readRecordedSteps,
  readStepSettings,
  stepTime,
  writeStepSettings,
  type AgentStep,
  type RecordedSteps,
  type StepSettings,
  type StepsPush,
} from '../lib/monitor/steps'
import {
  LayoutDefaults,
  LayoutFields,
  LayoutMax,
  LayoutMin,
  LayoutStep,
  clampLayout,
  isDefaultLayout,
  readLayout,
  writeLayout,
  type LayoutKey,
  type LayoutSettings,
} from '../lib/monitor/layout'

/**
 * THE MONITOR: every team at once, as a living graph.
 *
 * Teams are territories, members orbit them, open workflows tie the members working in them
 * together, and every row the log takes moves something: an instruction is a comet from whoever
 * sent it, a finished run a burst, a hand-back a gold comet to the Manager, a finished workflow a
 * firework. It reads what the board already reads - the overview's snapshots and the message feed
 * (`GET /api/messages`) - and adds no route: LIVE follows the rows the board pulls, REPLAY plays the
 * newest window of the log back at speed, skipping the quiet.
 *
 * A plugin event whose type carries a monitor hint (`GET /api/events`, read each time it opens)
 * draws the thing the plugin acted on beside the plugin member, with its icon from the full
 * Material Symbols font that only this view loads (`lib/monitor/font.ts`).
 *
 * While it is open it joins the hub's file watch, and each `workspaceChanged` push draws the
 * member's file changes as repository, project and file nodes off its team (`lib/monitor/workspace.ts`).
 * The Host records them during every run, so a replay reads its period's (`GET /api/file-changes`)
 * and plays each at its time beside the rows, drawn and listed as it was live. It reads at most
 * `MaxRecordedChanges`, and when it stops short the replay bar says after what time none are shown.
 *
 * Each `agentSteps` push, heard on the same watch, draws each of a member's steps as a small icon
 * rising off its node (`lib/monitor/steps.ts`), and nothing in the activity list. A replay reads its
 * period's (`GET /api/run-steps`) at most `MaxRecordedSteps`, says after what time none are shown when
 * it stops short, and plays each at its time. Show agent steps off, neither is drawn.
 */

const board = useConsoleStore()
const workflowNav = useWorkflowNavStore()
const appearance = useAppearanceStore()

/** How much of the log is held: the route's own ceiling. */
const HistoryRows = 2000
const Speeds = [10, 30, 60, 120, 300] as const

const root = ref<HTMLElement | null>(null)
const stage = ref<HTMLElement | null>(null)
const canvas = ref<HTMLCanvasElement | null>(null)

const mode = ref<'live' | 'replay'>('live')
const speed = ref<(typeof Speeds)[number]>(60)
const loading = ref(true)
const loadError = ref('')
const finished = ref(false)

const history = shallowRef<Message[]>([])
const ticker = shallowRef<TickerEntry[]>([])
const counts = ref({ running: 0, waiting: 0, blocked: 0, queued: 0, open: 0, teams: 0, members: 0 })
const rate = ref(0)
const tokensShown = ref(0)
/** Where the token figure's stretch begins, and how many runs in it reported no usage. */
const tokensSince = ref(0)
const unmeasured = ref(0)
const clockAt = ref(0)
const hover = ref<HoverInfo | null>(null)
const picked = ref<NodeRef | null>(null)
const fullscreen = ref(false)
const $q = useQuasar()
/** Quasar's controls over the canvas: dark on the wall, and on the theme when the theme is dark. */
const darkControls = computed(() => fullscreen.value || $q.dark.isActive)

/** Remembered per browser, a convenience only: storage may be missing or refuse, and the default stands. */
const KeepViewKey = 'harness.monitor.keepView'
function readKeepView(): boolean {
  try {
    return localStorage.getItem(KeepViewKey) === '1'
  } catch {
    return false
  }
}
/** On: a view the person moved stays where they put it instead of fitting everything again. */
const keepView = ref(readKeepView())
function toggleKeepView() {
  keepView.value = !keepView.value
  scene?.setAutoFit(!keepView.value)
  try {
    if (keepView.value) localStorage.setItem(KeepViewKey, '1')
    else localStorage.removeItem(KeepViewKey)
  } catch {
    // Remembered only while this page is open.
  }
}

/** Plugin event types' monitor hints, from `GET /api/events`; none until it answers, or if it fails. */
const hints = shallowRef<ReadonlyMap<string, MonitorHint>>(new Map())
const hintPlugins = computed(() => hintedPlugins(hints.value))
/** The full icon font's codepoints once it has loaded; until then a plain dot stands for each icon. */
const symbols = shallowRef<Codepoints | null>(null)

/** Show changes plugins make: remembered per browser like Keep my view, and fine without storage. */
const changes = ref<ResourceSettings>(readSettings())
const settingsOpen = ref(false)
const secondsDraft = ref<number | string>(changes.value.seconds)

function applyChanges(next: ResourceSettings) {
  changes.value = next
  model.settings = { ...next, off: [...next.off] }
  writeSettings(next)
}
function setShow(show: boolean) {
  applyChanges({ ...changes.value, show })
}
function commitSeconds() {
  const seconds = clampSeconds(secondsDraft.value)
  secondsDraft.value = seconds
  applyChanges({ ...changes.value, seconds })
}
function pluginOn(plugin: string): boolean {
  return !changes.value.off.includes(plugin)
}
function setPlugin(plugin: string, on: boolean) {
  const off = changes.value.off.filter((id) => id !== plugin)
  applyChanges({ ...changes.value, off: on ? off : [...off, plugin] })
}

/** Show file changes, and how far down: remembered per browser like the plugin changes. */
const fileChanges = ref<FileSettings>(readFileSettings())
const depthOptions: { label: string; value: FileDepth }[] = [
  { label: 'Repository', value: 'repository' },
  { label: 'Project', value: 'project' },
  { label: 'File', value: 'file' },
]

function applyFileChanges(next: FileSettings) {
  fileChanges.value = next
  model.fileSettings = { ...next }
  scene?.setFileChanges(next)
  writeFileSettings(next)
  dirty = true
}
function setFilesShow(show: boolean) {
  applyFileChanges({ ...fileChanges.value, show })
}
function setFilesDepth(depth: FileDepth) {
  applyFileChanges({ ...fileChanges.value, depth })
}

/** Show agent steps: remembered per browser like Show file changes; off, no step icon live or in a replay. */
const agentSteps = ref<StepSettings>(readStepSettings())

function setStepsShow(show: boolean) {
  agentSteps.value = { show }
  model.stepSettings = { show }
  scene?.setAgentSteps(show)
  writeStepSettings({ show })
}

/** The layout forces: multipliers a person tunes in the settings, remembered per browser. */
const layout = ref<LayoutSettings>(readLayout())
function setLayoutValue(key: LayoutKey, value: number | null) {
  if (value === null) return
  applyLayout({ ...layout.value, [key]: clampLayout(value, LayoutDefaults[key]) })
}
function resetLayout() {
  applyLayout({ ...LayoutDefaults })
}
function applyLayout(next: LayoutSettings) {
  layout.value = next
  scene?.setLayout(next)
  writeLayout(next)
  dirty = true
}

/** A model with this view's hints and settings: every one the view makes goes through here. */
function newModel(only: Set<string> | null, replaying = false): MonitorModel {
  const made = new MonitorModel(board.managerName || 'Manager', 80, only)
  made.hints = hints.value
  made.settings = { ...changes.value, off: [...changes.value.off] }
  made.fileSettings = { ...fileChanges.value }
  made.stepSettings = { ...agentSteps.value }
  made.replay = replaying
  return made
}

/** The activity list's push lines: in no row, so kept here for a live model that starts again. */
let pushLines: TickerEntry[] = []
/** Pushes heard before the log is read, applied once it is. */
let pendingPushes: WorkspacePush[] = []
let stopWatching: (() => void) | null = null
let stopHearingSteps: (() => void) | null = null

/** One file-changes push: drawn and listed live; a replay plays the recorded ones instead. */
function onPush(push: WorkspacePush) {
  if (loading.value) {
    pendingPushes.push(push)
    return
  }
  if (mode.value !== 'live') return
  const effects = model.applyWorkspace(push)
  pushLines = model.ticker.filter((entry) => entry.workspace === true)
  dirty = true
  if (effects.length > 0) scene?.play(effects)
}

/** One member's agent steps: drawn live; a replay plays the recorded ones instead, and none are kept for later. */
function onSteps(push: StepsPush) {
  if (loading.value || mode.value !== 'live') return
  const effects = model.applySteps(push.steps)
  if (effects.length > 0) scene?.play(effects)
}

let model = new MonitorModel()
let scene: MonitorScene | null = null
let replay: ReplayClock | null = null
let frame = 0
let lastFrame = 0
let lastPublish = 0
let lastSized = 0
let dirty = true
let tokensTarget = 0
let unsubscribe: (() => void) | null = null
let resizeObserver: ResizeObserver | null = null
let unmounted = false

const reducedMotion = globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true

const liveTeams = computed(() => new Set<string>(board.choosableTeams.map((team) => String(team.id))))

const fontPx = computed(() => textPx(1, appearance.resolved.textSize))

/** The legend says each tone in the one table's words (`lib/eventWords`), as the list beside it does. */
const legend = ToneLegend

/** The rings a member wears: each a state's own token (`lib/states`), on the theme or the wall. */
const states: { colour: string; words: string }[] = [
  { colour: 'var(--os-monitor-state-running)', words: StateWords.running },
  { colour: 'var(--os-monitor-state-waiting)', words: StateWords.waiting },
  { colour: 'var(--os-monitor-state-decision)', words: StateWords['needs-you'] },
  { colour: 'var(--os-monitor-state-blocked)', words: StateWords.blocked },
  { colour: 'var(--os-monitor-state-failed)', words: StateWords.failed },
  { colour: 'var(--os-monitor-state-idle)', words: StateWords.idle },
]

/**
 * THE CANVAS READS ITS COLOURS AGAIN when the ground changes under it: into or out of full screen,
 * and on a change of theme, once the new tokens are on the page.
 */
watch(
  [fullscreen, () => $q.dark.isActive, () => appearance.resolved],
  async () => {
    await nextTick()
    if (canvas.value) readPalette(canvas.value)
  },
  { deep: true },
)

const buckets = computed(() => timeline(history.value, 140))
const bucketMax = computed(() => Math.max(1, ...buckets.value.map((bucket) => bucket.count)))
const span = computed(() => {
  const rows = history.value
  if (rows.length === 0) return null
  return { from: timeOf(rows[0]!), to: timeOf(rows[rows.length - 1]!) }
})
const playhead = computed(() => {
  const s = span.value
  if (!s || s.to <= s.from) return 1
  const at = mode.value === 'live' ? s.to : clockAt.value
  return Math.min(1, Math.max(0, (at - s.from) / (s.to - s.from)))
})

const shownTicker = computed(() => {
  const ref = picked.value
  if (!ref) return ticker.value
  if (ref.kind === 'team') return ticker.value.filter((entry) => entry.team === ref.id)
  if (ref.kind === 'workflow') return ticker.value.filter((entry) => entry.workflow === ref.id)
  if (ref.kind === 'member') {
    const [team, ...rest] = ref.key.split('/')
    const id = rest.join('/')
    const name = model.members.get(ref.key)?.name ?? id
    return ticker.value.filter((entry) => entry.team === team && entry.member === name)
  }
  return ticker.value
})

const pickedWords = computed(() => {
  const ref = picked.value
  if (!ref) return ''
  if (ref.kind === 'team') return model.teams.get(ref.id)?.name ?? ref.id
  if (ref.kind === 'member') return model.members.get(ref.key)?.name ?? ref.key
  if (ref.kind === 'workflow') {
    const workflow = model.workflows.get(ref.id)
    return workflow ? workflowNameLine(workflow) : `Workflow ${ref.id}`
  }
  return ''
})

/** The backlog item the picked workflow, or the picked member's workflow, works on: "Open item". */
const pickedItem = computed(() => {
  const ref = picked.value
  if (!ref) return null
  const id = ref.kind === 'workflow' ? ref.id : ref.kind === 'member' ? model.members.get(ref.key)?.workflow ?? null : null
  return id !== null ? model.workflows.get(id)?.item ?? null : null
})

function openItem() {
  if (pickedItem.value) workflowNav.openItem(pickedItem.value.id)
}

/** A picked member on a live team: "Open member", its card on its board (the highlight link). */
const pickedMemberHref = computed(() => {
  const ref = picked.value
  if (ref?.kind !== 'member') return null
  const [team, ...rest] = ref.key.split('/')
  return team && liveTeams.value.has(team) ? memberHref(team, rest.join('/')) : null
})

/** A picked workflow with no item, on a live team: "Open workflows", that team's Workflows dialog. */
const pickedWorkflowsTeam = computed(() => {
  const ref = picked.value
  if (ref?.kind !== 'workflow' || pickedItem.value) return null
  return pickedTeam.value
})

function openWorkflows() {
  if (pickedWorkflowsTeam.value) workflowNav.openWorkflows(pickedWorkflowsTeam.value as TeamId)
}

/** A ticker row's team line opens that team's board, while the team is live. */
function openBoard(team: string) {
  if (liveTeams.value.has(team)) workflowNav.openBoard(team as TeamId)
}

const pickedTeam = computed(() => {
  const ref = picked.value
  if (!ref) return null
  const team = ref.kind === 'team' ? ref.id : ref.kind === 'member' ? ref.key.split('/')[0]! : ref.kind === 'workflow' ? model.workflows.get(ref.id)?.team ?? null : null
  return team && liveTeams.value.has(team) ? team : null
})

function toneColour(tone: Tone): string {
  return `var(--os-monitor-${tone})`
}

function entryColour(entry: TickerEntry): string {
  return entry.resource ? actionColour(entry.resource.action) : toneColour(entry.tone)
}

function actionColour(action: ResourceAction): string {
  return `var(--os-monitor-${action})`
}

/** The mark before a listed file: what happened to it, beside its colour, so colour is never the only signal. */
const FileMarks: Record<ResourceAction, string> = { created: '+', changed: '~', deleted: '−' }

/** "Azure (Infra) created sql-db-maketags": who, on which team, did what to which thing. */
function resourceWords(entry: TickerEntry): string {
  const resource = entry.resource
  if (!resource) return entry.text
  const team = model.teams.get(entry.team)?.name ?? entry.team
  const who = entry.member !== null ? `${entry.member} (${team})` : team
  return `${who} ${resource.action} ${resource.label}`
}

/** An instant as every view says it: its time of day today, its date and time on another day. */
function timeWords(ms: number): string {
  return stampOf(ms)
}

/**
 * Apply rows, and the recorded file changes beside them in time order, playing their effects unless
 * `quiet`. Agent steps only ever play: a step that is over leaves nothing behind to apply.
 */
function feed(rows: readonly Message[], quiet: boolean, changes: readonly WorkspacePush[] = [], steps: readonly AgentStep[] = []) {
  const effects: MonitorEffect[] = []
  let next = 0
  const changesUntil = (at: number) => {
    while (next < changes.length && (pushTime(changes[next]!) ?? 0) <= at) {
      const made = model.applyWorkspace(changes[next]!)
      if (!quiet) effects.push(...made)
      next++
    }
  }
  for (const row of rows) {
    changesUntil(timeOf(row))
    const made = model.apply(row)
    if (!quiet) effects.push(...made)
  }
  changesUntil(Number.POSITIVE_INFINITY)
  if (!quiet) effects.push(...model.applySteps(steps))
  if (rows.length > 0 || changes.length > 0) dirty = true
  if (!quiet && effects.length > 0) scene?.play(effects)
}

function publish(now: number) {
  if (!dirty && now - lastPublish < 1000) return
  lastPublish = now
  dirty = false
  if (mode.value === 'live') model.prune(90_000)
  else model.prune(45_000 * (speed.value / 60))
  scene?.sync(model)
  ticker.value = [...model.ticker]
  counts.value = model.counts()
  rate.value = model.rate()
  tokensTarget = model.tokens
  tokensSince.value = model.since
  unmeasured.value = model.unmeasured
  clockAt.value = replay ? replay.at : Date.now()
}

function tick(now: number) {
  const dt = lastFrame === 0 ? 16 : Math.min(250, now - lastFrame)
  lastFrame = now

  if (mode.value === 'replay' && replay) {
    const due = replay.step(dt)
    feed(due.rows, false, due.changes, due.steps)
    clockAt.value = replay.at
    if (replay.done && !finished.value) finished.value = true
  }

  if (now - lastPublish > 180) publish(now)
  if (now - lastSized > 1000) {
    lastSized = now
    sizeRoot()
  }

  // The token figure rolls up to its value rather than jumping.
  const gap = tokensTarget - tokensShown.value
  tokensShown.value = Math.abs(gap) < 1 ? tokensTarget : tokensShown.value + gap * Math.min(1, dt / 350)

  frame = requestAnimationFrame(tick)
}

function startLive() {
  replay = null
  replaying++
  finished.value = false
  mode.value = 'live'
  model = newModel(new Set(liveTeams.value))
  feed(history.value, true)
  model.reconcile(board.choosableTeams)
  model.keepPushLines(pushLines)
  tokensShown.value = model.tokens
  dirty = true
  publish(performance.now())
}

/** Which replay is current: one started later, or live, makes an earlier one's late answer moot. */
let replaying = 0
/** The recorded file changes of the period last read, kept so a seek within it reads none again. */
let recorded: { key: string; changes: Promise<RecordedChanges> } | null = null
/** The time of the last recorded change a replay read when it stopped short of its period's end; null when nothing is cut off. */
const cutAfter = ref<number | null>(null)

/**
 * The members' file changes recorded over the rows held, oldest first, every page of them. A read
 * that fails leaves the replay without file changes and nothing else, as a failed watch does live.
 */
function recordedChanges(rows: readonly Message[]): Promise<RecordedChanges> {
  const period = { from: rows[0]!.occurredAt, to: rows[rows.length - 1]!.occurredAt }
  const key = `${period.from}|${period.to}`
  if (recorded?.key !== key) {
    const changes = readRecorded((after, take) => getFileChanges(period, after, take)).catch(
      (): RecordedChanges => ({ changes: [], cut: false }),
    )
    recorded = { key, changes }
  }
  return recorded.changes
}

/** The agent steps of the period last read, kept as the file changes are. */
let recordedStepsRead: { key: string; steps: Promise<RecordedSteps> } | null = null
/** The time of the last recorded step a replay read when it stopped short; null when nothing is cut off. */
const stepsCutAfter = ref<number | null>(null)

/**
 * The members' agent steps recorded over the rows held, oldest first, every page of them; none read
 * with Show agent steps off. A read that fails leaves the replay without steps and nothing else.
 */
function recordedSteps(rows: readonly Message[]): Promise<RecordedSteps> {
  if (!agentSteps.value.show) return Promise.resolve({ steps: [], cut: false })
  const period = { from: rows[0]!.occurredAt, to: rows[rows.length - 1]!.occurredAt }
  const key = `${period.from}|${period.to}`
  if (recordedStepsRead?.key !== key) {
    const steps = readRecordedSteps((after, take) => getRunSteps(period, after, take)).catch(
      (): RecordedSteps => ({ steps: [], cut: false }),
    )
    recordedStepsRead = { key, steps }
  }
  return recordedStepsRead.steps
}

/**
 * Replay from `from` (log time, ms): the rows and recorded file changes before it are applied
 * silently, then both are played, each at its time.
 */
async function startReplay(from?: number) {
  const rows = history.value
  if (rows.length === 0) return
  const current = ++replaying
  mode.value = 'replay'
  finished.value = false
  replay = null
  model = newModel(null, true)
  model.know(board.choosableTeams)
  // What live drew goes; the replay draws its own period's file changes.
  scene?.clearFileChanges()
  cutAfter.value = null
  stepsCutAfter.value = null
  const [{ changes, cut }, recordedRun] = await Promise.all([recordedChanges(rows), recordedSteps(rows)])
  if (current !== replaying || unmounted) return
  cutAfter.value = cut ? (changes.length > 0 ? (pushTime(changes[changes.length - 1]!) ?? 0) : timeOf(rows[0]!)) : null
  const steps = recordedRun.steps
  stepsCutAfter.value = recordedRun.cut ? (steps.length > 0 ? stepTime(steps[steps.length - 1]!) : timeOf(rows[0]!)) : null
  const start = from ?? timeOf(rows[0]!)
  feed(
    rows.filter((row) => timeOf(row) < start),
    true,
    changes.filter((change) => (pushTime(change) ?? 0) < start),
  )
  replay = new ReplayClock(rows, speed.value, 20_000, start, changes, steps)
  tokensShown.value = model.tokens
  dirty = true
  publish(performance.now())
  scene?.fit()
}

function seek(event: MouseEvent) {
  const s = span.value
  const bar = event.currentTarget as HTMLElement | null
  if (!s || !bar) return
  const rect = bar.getBoundingClientRect()
  const share = Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width))
  void startReplay(s.from + share * (s.to - s.from))
}

watch(speed, (value) => {
  if (replay) replay.speed = value
})

// LIVE: the rows the board pulls on every push, the same rows its cards show.
function followBoard() {
  unsubscribe = board.$onAction(({ name, args, after }) => {
    if (name !== 'record') return
    after(() => {
      const rows = [...(args[0] as Message[])].sort((a, b) => a.seq - b.seq)
      const last = history.value.at(-1)?.seq ?? 0
      const fresh = rows.filter((row) => row.seq > last)
      if (fresh.length === 0) return
      history.value = [...history.value, ...fresh].slice(-HistoryRows * 2)
      if (mode.value === 'live') feed(fresh, false)
    })
  })
}

// LIVE: the snapshots are the truth about who exists and what each is doing.
watch(
  () => board.choosableTeams,
  (teams) => {
    if (mode.value !== 'live' || loading.value) return
    const ids = new Set<string>(teams.map((team) => String(team.id)))
    const same = model.only !== null && ids.size === model.only.size && [...ids].every((id) => model.only!.has(id))
    if (!same) {
      // A team created or deleted, or the overview landing after the log: start again from the
      // rows held, so the new team's history is in the picture too.
      startLive()
      return
    }
    model.reconcile(teams)
    dirty = true
  },
  { deep: true },
)

watch(fontPx, (px) => scene?.setOptions({ fontPx: px }))

function measure() {
  const el = stage.value
  if (!el || !scene) return
  const rect = el.getBoundingClientRect()
  scene.resize(rect.width, rect.height)
}

function sizeRoot() {
  const el = root.value
  if (!el) return
  if (fullscreen.value) {
    el.style.height = ''
    return
  }
  const top = el.getBoundingClientRect().top + globalThis.scrollY
  el.style.height = `${Math.max(520, globalThis.innerHeight - top - 16)}px`
}

async function toggleFullscreen() {
  const el = root.value
  if (!el) return
  try {
    if (document.fullscreenElement) await document.exitFullscreen()
    else await el.requestFullscreen()
  } catch {
    // A browser that refuses full screen leaves the view as it is.
  }
}

function onFullscreenChange() {
  fullscreen.value = document.fullscreenElement === root.value
  sizeRoot()
}

function onWindowResize() {
  sizeRoot()
}

function openTeam() {
  const team = board.choosableTeams.find((candidate) => candidate.id === pickedTeam.value)
  if (team) board.openTeam(team.id)
}

function fitAll() {
  scene?.fit()
}

onMounted(async () => {
  sizeRoot()
  if (canvas.value) {
    readPalette(canvas.value)
    scene = new MonitorScene(canvas.value, {
      fontPx: fontPx.value,
      fontFamily: getComputedStyle(canvas.value).fontFamily || 'sans-serif',
      reducedMotion,
      onHover: (info) => (hover.value = info),
      onPick: (picked_) => (picked.value = picked_),
    })
    scene.setAutoFit(!keepView.value)
    scene.setFileChanges(fileChanges.value)
    scene.setAgentSteps(agentSteps.value.show)
    scene.setLayout(layout.value)
    measure()
    scene.start()
  }
  if (stage.value && typeof ResizeObserver !== 'undefined') {
    resizeObserver = new ResizeObserver(() => measure())
    resizeObserver.observe(stage.value)
  }
  globalThis.addEventListener('resize', onWindowResize)
  document.addEventListener('fullscreenchange', onFullscreenChange)

  followBoard()
  // Joined while the Monitor is open, left when it closes.
  stopWatching = fileWatch.listen(onPush)
  stopHearingSteps = fileWatch.listenSteps(onSteps)
  void loadMonitorSymbols().then((codepoints) => {
    // A font that does not load leaves the dots; nothing is wrong with the events.
    if (!codepoints || unmounted) return
    symbols.value = codepoints
    scene?.setSymbols(codepoints)
  })
  const [read, events] = await Promise.allSettled([getMessages(0, HistoryRows), listEvents()])
  if (read.status === 'fulfilled') {
    const known = history.value.at(0)?.seq ?? Number.POSITIVE_INFINITY
    history.value = [...read.value.filter((row) => row.seq < known), ...history.value].sort((a, b) => a.seq - b.seq)
  } else {
    const cause: unknown = read.reason
    loadError.value = cause instanceof Error ? cause.message : String(cause)
  }
  // Without the catalog every plugin row draws as the plain burst, as before hints.
  if (events.status === 'fulfilled') hints.value = hintsFrom(events.value)
  loading.value = false
  startLive()
  const heard = pendingPushes
  pendingPushes = []
  for (const push of heard) onPush(push)
  scene?.fit()
  frame = requestAnimationFrame(tick)
})

onUnmounted(() => {
  unmounted = true
  stopWatching?.()
  stopWatching = null
  stopHearingSteps?.()
  stopHearingSteps = null
  if (frame !== 0) cancelAnimationFrame(frame)
  unsubscribe?.()
  resizeObserver?.disconnect()
  globalThis.removeEventListener('resize', onWindowResize)
  document.removeEventListener('fullscreenchange', onFullscreenChange)
  scene?.destroy()
  scene = null
})
</script>

<template>
  <section ref="root" class="monitor" :class="{ 'monitor--fullscreen': fullscreen, 'monitor--wall': fullscreen }" aria-label="Monitor: every team's activity">
    <div class="monitor-main">
      <div ref="stage" class="monitor-stage">
        <canvas
          ref="canvas"
          class="monitor-canvas"
          role="img"
          aria-label="A live graph of every team, its members and its open workflows. The activity list beside it says the same in words."
        ></canvas>

        <div class="monitor-top">
        <!-- THE HEADS-UP DISPLAY: what is happening, in figures, over the graph. -->
        <div class="monitor-hud">
          <div class="monitor-title">
            <span v-if="mode === 'live'" class="monitor-live" data-monitor-mode="live">
              <span class="monitor-live-dot" aria-hidden="true"></span>LIVE
            </span>
            <span v-else class="monitor-replay" data-monitor-mode="replay">
              REPLAY {{ speed }}×
            </span>
            <span class="monitor-clock">{{ timeWords(clockAt) }}</span>
          </div>
          <div v-if="mode === 'replay' && cutAfter !== null" class="monitor-replay-cut" data-monitor-cut>
            File changes after {{ localTime(cutAfter) }} are not shown: this replay reads at most
            {{ MaxRecordedChanges.toLocaleString('en-US') }}.
          </div>
          <div v-if="mode === 'replay' && stepsCutAfter !== null" class="monitor-replay-cut" data-monitor-steps-cut>
            Agent steps after {{ localTime(stepsCutAfter) }} are not shown: this replay reads at most
            {{ MaxRecordedSteps.toLocaleString('en-US') }}.
          </div>
          <div class="monitor-stats">
            <div class="monitor-stat" data-stat="running">
              <span class="monitor-stat-value" :style="{ color: 'var(--os-monitor-state-running)' }">{{ counts.running }}</span>
              <span class="monitor-stat-words">running</span>
            </div>
            <div class="monitor-stat" data-stat="waiting">
              <span class="monitor-stat-value" :style="{ color: 'var(--os-monitor-state-waiting)' }">{{ counts.waiting }}</span>
              <span class="monitor-stat-words">waiting</span>
            </div>
            <div class="monitor-stat" data-stat="blocked">
              <span class="monitor-stat-value" :style="{ color: 'var(--os-monitor-state-blocked)' }">{{ counts.blocked }}</span>
              <span class="monitor-stat-words">need you</span>
            </div>
            <div class="monitor-stat" data-stat="open">
              <span class="monitor-stat-value" :style="{ color: toneColour('workflow') }">{{ counts.open }}</span>
              <span class="monitor-stat-words">open workflows</span>
            </div>
            <div class="monitor-stat" data-stat="rate">
              <span class="monitor-stat-value" :style="{ color: toneColour('tell') }">{{ rate }}</span>
              <span class="monitor-stat-words">events / min</span>
            </div>
            <div class="monitor-stat monitor-stat--tip" data-stat="tokens">
              <span class="monitor-stat-value" :style="{ color: toneColour('done') }">{{ compact(tokensShown) }}</span>
              <span class="monitor-stat-words">{{ tokensWords(tokensSince, clockAt, unmeasured) }}</span>
              <q-tooltip>{{ TokensTooltip }}</q-tooltip>
            </div>
          </div>
          <div class="monitor-sub">
            {{ counts.teams }} {{ counts.teams === 1 ? 'team' : 'teams' }} ·
            {{ counts.members }} {{ counts.members === 1 ? 'member' : 'members' }}
          </div>
        </div>

        <div class="monitor-controls">
          <q-btn-toggle
            :model-value="mode"
            dense
            no-caps
            unelevated
            toggle-color="primary"
            class="monitor-mode"
            :options="[
              { label: 'Live', value: 'live' },
              { label: 'Replay', value: 'replay' },
            ]"
            @update:model-value="(value: 'live' | 'replay') => (value === 'live' ? startLive() : startReplay())"
          />
          <q-select
            v-model="speed"
            :options="[...Speeds]"
            dense
            outlined
            :dark="darkControls"
            options-dense
            emit-value
            class="monitor-speed"
            :option-label="(value: number) => `${value}×`"
            :display-value="`${speed}×`"
            aria-label="Replay speed"
          />
          <q-btn flat dense round icon="center_focus_strong" aria-label="Fit everything in view" @click="fitAll">
            <q-tooltip>Fit everything in view (or double-click the graph)</q-tooltip>
          </q-btn>
          <q-btn
            flat
            dense
            round
            icon="keep"
            :class="{ 'monitor-tool-on': keepView }"
            :aria-pressed="keepView ? 'true' : 'false'"
            aria-label="Keep my view"
            data-monitor-keep-view
            @click="toggleKeepView"
          >
            <q-tooltip>{{
              keepView
                ? 'Keeping your view: it stays where you put it until you press Fit'
                : 'Keep my view: stop fitting everything again a few seconds after you move it'
            }}</q-tooltip>
          </q-btn>
          <span class="monitor-settings-anchor">
            <q-btn
              flat
              dense
              round
              icon="settings"
              :class="{ 'monitor-tool-on': settingsOpen }"
              :aria-expanded="settingsOpen ? 'true' : 'false'"
              aria-label="Monitor settings"
              data-monitor-settings
              @click="settingsOpen = !settingsOpen"
            >
              <q-tooltip v-if="!settingsOpen">Settings: what plugins' changes, file changes and agent steps show, and for how long</q-tooltip>
            </q-btn>
            <div
              v-if="settingsOpen"
              class="monitor-settings"
              role="dialog"
              aria-label="Monitor settings"
              data-monitor-settings-panel
              @keydown.esc="settingsOpen = false"
            >
              <q-toggle
                :model-value="changes.show"
                dense
                :dark="darkControls"
                label="Show changes plugins make"
                data-monitor-setting="show"
                @update:model-value="setShow"
              />
              <div class="monitor-settings-row">
                <label for="monitor-setting-seconds">Show each for</label>
                <q-input
                  v-model="secondsDraft"
                  for="monitor-setting-seconds"
                  type="number"
                  dense
                  outlined
                  :dark="darkControls"
                  :min="SecondsMin"
                  :max="SecondsMax"
                  suffix="seconds"
                  class="monitor-settings-seconds"
                  data-monitor-setting="seconds"
                  @change="commitSeconds"
                  @keydown.enter="commitSeconds"
                />
              </div>
              <div class="monitor-settings-head">Plugins</div>
              <q-toggle
                v-for="plugin in hintPlugins"
                :key="plugin"
                :model-value="pluginOn(plugin)"
                dense
                :dark="darkControls"
                :label="plugin"
                :data-monitor-plugin="plugin"
                @update:model-value="(on: boolean) => setPlugin(plugin, on)"
              />
              <div v-if="hintPlugins.length === 0" class="monitor-settings-none">
                No installed plugin says how to draw its changes.
              </div>
              <div class="monitor-settings-head">Files</div>
              <q-toggle
                :model-value="fileChanges.show"
                dense
                :dark="darkControls"
                label="Show file changes"
                data-monitor-setting="files"
                @update:model-value="setFilesShow"
              />
              <div class="monitor-settings-row">
                <span id="monitor-setting-depth">Draw down to</span>
                <q-btn-toggle
                  :model-value="fileChanges.depth"
                  :options="depthOptions"
                  :disable="!fileChanges.show"
                  dense
                  no-caps
                  unelevated
                  toggle-color="primary"
                  aria-labelledby="monitor-setting-depth"
                  data-monitor-setting="depth"
                  @update:model-value="setFilesDepth"
                />
              </div>
              <div class="monitor-settings-none">Each shows for the time above.</div>
              <div class="monitor-settings-head">Agents</div>
              <q-toggle
                :model-value="agentSteps.show"
                dense
                :dark="darkControls"
                label="Show agent steps"
                data-monitor-setting="steps"
                @update:model-value="setStepsShow"
              />
              <div class="monitor-settings-none">Each tool call, note and reply as a small icon rising off its member.</div>
              <div class="monitor-settings-head">Layout</div>
              <div
                v-for="field in LayoutFields"
                :key="field.key"
                class="monitor-settings-slider"
                :data-monitor-layout="field.key"
              >
                <div class="monitor-settings-row">
                  <span :id="`monitor-layout-${field.key}`">{{ field.label }} <FieldHelp :text="field.help" /></span>
                  <span class="monitor-settings-value">×{{ layout[field.key].toFixed(2) }}</span>
                </div>
                <q-slider
                  :model-value="layout[field.key]"
                  :min="LayoutMin"
                  :max="LayoutMax"
                  :step="LayoutStep"
                  dense
                  :dark="darkControls"
                  :aria-labelledby="`monitor-layout-${field.key}`"
                  @update:model-value="(value: number | null) => setLayoutValue(field.key, value)"
                />
              </div>
              <q-btn
                flat
                dense
                no-caps
                label="Reset layout"
                :disable="isDefaultLayout(layout)"
                data-monitor-layout-reset
                @click="resetLayout"
              />
            </div>
          </span>
          <q-btn
            flat
            dense
            round
            :icon="fullscreen ? 'fullscreen_exit' : 'fullscreen'"
            :aria-label="fullscreen ? 'Leave full screen' : 'Full screen'"
            @click="toggleFullscreen"
          >
            <q-tooltip>{{ fullscreen ? 'Leave full screen' : 'Full screen, for a wall or a demo' }}</q-tooltip>
          </q-btn>
        </div>
        </div>

        <div class="monitor-legend" aria-hidden="true">
          <span v-for="entry in legend" :key="entry.tone" class="monitor-legend-item">
            <span class="monitor-swatch" :style="{ background: toneColour(entry.tone) }"></span>{{ entry.words }}
          </span>
          <span class="monitor-legend-gap"></span>
          <span v-for="entry in states" :key="entry.words" class="monitor-legend-item">
            <span class="monitor-ring" :style="{ borderColor: entry.colour }"></span>{{ entry.words }}
          </span>
        </div>

        <div
          v-if="hover"
          class="monitor-tip"
          :style="{ left: `${hover.x + 16}px`, top: `${hover.y + 12}px`, borderColor: hover.colour }"
          data-monitor-tip
        >
          <div class="monitor-tip-title" :style="{ color: hover.colour }">{{ hover.title }}</div>
          <div class="monitor-tip-sub">{{ hover.subtitle }}</div>
          <div v-for="(line, index) in hover.lines" :key="index" class="monitor-tip-line">{{ line }}</div>
        </div>

        <div v-if="loading" class="monitor-overlay">Reading the log…</div>
        <div v-else-if="loadError" class="monitor-overlay monitor-overlay--error">
          The log could not be read: {{ loadError }}
        </div>
        <div v-else-if="history.length === 0 && counts.members === 0" class="monitor-overlay">
          Nothing has happened yet. Create a team or tell one something, and it appears here.
        </div>
        <div v-if="finished" class="monitor-finished">
          <span>Replay finished.</span>
          <q-btn dense no-caps unelevated color="primary" label="Back to live" @click="startLive" />
          <q-btn dense no-caps flat label="Play it again" @click="startReplay()" />
        </div>
      </div>

      <!-- THE TIMELINE: the rows held, by time and by what most of them were. Clicking replays from there. -->
      <div class="monitor-timeline">
        <div
          class="monitor-bars"
          role="slider"
          tabindex="0"
          aria-label="Replay from a point in time"
          :aria-valuemin="0"
          :aria-valuemax="100"
          :aria-valuenow="Math.round(playhead * 100)"
          @click="seek"
          @keydown.enter="startReplay()"
        >
          <span
            v-for="(bucket, index) in buckets"
            :key="index"
            class="monitor-bar"
            :style="{
              height: `${Math.max(bucket.count > 0 ? 8 : 0, (bucket.count / bucketMax) * 100)}%`,
              background: toneColour(bucket.tone),
            }"
          ></span>
          <span class="monitor-playhead" :style="{ left: `${playhead * 100}%` }"></span>
        </div>
        <div class="monitor-span">
          <span>{{ span ? timeWords(span.from) : '' }}</span>
          <span class="monitor-span-hint">{{ history.length }} rows · click to replay from a moment</span>
          <span>{{ span ? timeWords(span.to) : '' }}</span>
        </div>
      </div>
    </div>

    <!-- THE SAME ACTIVITY IN WORDS, newest first: what a screen reader reads, and what a person
         reads when the graph is busy. -->
    <aside class="monitor-ticker" aria-label="Activity">
      <div class="monitor-ticker-head">
        <span>Activity</span>
        <span v-if="picked" class="monitor-picked">
          {{ pickedWords }}
          <q-btn v-if="pickedTeam" flat dense no-caps size="sm" label="Open" @click="openTeam" />
          <q-btn
            v-if="pickedItem"
            flat
            dense
            no-caps
            size="sm"
            label="Open item"
            :title="`${itemLabel(pickedItem.id)}${pickedItem.title !== '' ? `${ItemSeparator}${pickedItem.title}` : ''}`"
            data-monitor-open-item
            @click="openItem"
          />
          <q-btn
            v-else-if="pickedWorkflowsTeam"
            flat
            dense
            no-caps
            size="sm"
            label="Open workflows"
            data-monitor-open-workflows
            @click="openWorkflows"
          />
          <q-btn
            v-if="pickedMemberHref"
            flat
            dense
            no-caps
            size="sm"
            label="Open member"
            :href="pickedMemberHref"
            data-monitor-open-member
          />
          <q-btn flat dense round size="sm" icon="close" aria-label="Show every team" @click="picked = null" />
        </span>
      </div>
      <ol class="monitor-rows">
        <li v-for="entry in shownTicker" :key="entry.seq" class="monitor-row" :data-tone="entry.tone">
          <span class="monitor-row-mark" :style="{ background: entryColour(entry) }"></span>
          <div class="monitor-row-body">
            <div class="monitor-row-head">
              <span class="monitor-row-verb" :style="{ color: entryColour(entry) }">{{ entry.verb }}</span>
              <span class="monitor-row-who">{{ entry.member ?? entry.team }}</span>
              <span class="monitor-row-time">{{ timeWords(entry.at) }}</span>
            </div>
            <div class="monitor-row-team">
              <a
                v-if="liveTeams.has(entry.team)"
                :href="boardHref(entry.team)"
                class="os-name-link"
                data-monitor-row-team
                @click.prevent="openBoard(entry.team)"
              >{{ model.teams.get(entry.team)?.name ?? entry.team }}</a>
              <template v-else>{{ model.teams.get(entry.team)?.name ?? entry.team }}</template>
            </div>
            <div
              v-if="entry.resource"
              class="monitor-row-resource"
              :data-action="entry.resource.action"
              data-monitor-resource
            >
              <span
                v-if="glyphOf(symbols, entry.resource.icon) !== null"
                class="monitor-row-icon"
                aria-hidden="true"
                :style="{ color: actionColour(entry.resource.action), fontFamily: `'${MonitorSymbolsFamily}'` }"
                >{{ glyphOf(symbols, entry.resource.icon) }}</span
              >
              <span v-else class="monitor-row-dot" aria-hidden="true" :style="{ background: actionColour(entry.resource.action) }"></span>
              <span class="monitor-row-words">{{ resourceWords(entry) }}</span>
            </div>
            <div v-else-if="entry.files" class="monitor-row-text monitor-row-text--files" data-monitor-push>
              <div v-if="entry.text" class="monitor-row-commit">{{ entry.text }}</div>
              <ul class="monitor-row-files">
                <li v-for="file in entry.files" :key="file.path" :data-action="file.action">
                  <span class="monitor-row-mark" :style="{ color: actionColour(file.action) }" :aria-label="file.action">{{
                    FileMarks[file.action]
                  }}</span>
                  {{ file.path }}
                </li>
              </ul>
              <div v-if="entry.more" class="monitor-row-more">{{ entry.more }}</div>
            </div>
            <div v-else class="monitor-row-text">{{ entry.text }}</div>
          </div>
        </li>
      </ol>
      <div v-if="shownTicker.length === 0" class="monitor-empty">No activity in view.</div>
    </aside>
  </section>
</template>

<style scoped>
/* THE MONITOR'S CANVAS FOLLOWS THE THEME in the normal view and keeps its dark wall palette in full
   screen. The `--os-monitor-*` tokens set here are what the canvas reads (`readPalette`) and what
   the legend and the list paint with, so the two cannot disagree. On the theme, the ground and the
   ink are the page's, a state's ring is its `--os-state-*` token, and each event hue is the wall's
   mixed 60/40 toward the theme's ink - darker on a light page, lighter on a dark one - which keeps
   every hue at 3.7:1 or more on every built-in preset's surface (`state-palette.spec.ts`). */
.monitor {
  --os-monitor-ground: var(--os-surface);
  --os-monitor-panel: var(--os-chrome);
  --os-monitor-rule: var(--os-rule);
  --os-monitor-ink: var(--os-ink);
  --os-monitor-ink-dim: var(--os-ink-muted);
  --os-monitor-ink-soft: var(--os-ink-muted);
  --os-monitor-tell: color-mix(in srgb, var(--os-monitor-wall-tell) 60%, var(--os-ink));
  --os-monitor-start: color-mix(in srgb, var(--os-monitor-wall-start) 60%, var(--os-ink));
  --os-monitor-progress: color-mix(in srgb, var(--os-monitor-wall-progress) 60%, var(--os-ink));
  --os-monitor-done: color-mix(in srgb, var(--os-monitor-wall-done) 60%, var(--os-ink));
  --os-monitor-handback: color-mix(in srgb, var(--os-monitor-wall-handback) 60%, var(--os-ink));
  --os-monitor-fail: color-mix(in srgb, var(--os-monitor-wall-fail) 60%, var(--os-ink));
  --os-monitor-blocked: color-mix(in srgb, var(--os-monitor-wall-blocked) 60%, var(--os-ink));
  --os-monitor-workflow: color-mix(in srgb, var(--os-monitor-wall-workflow) 60%, var(--os-ink));
  --os-monitor-card: color-mix(in srgb, var(--os-monitor-wall-card) 60%, var(--os-ink));
  --os-monitor-push: color-mix(in srgb, var(--os-monitor-wall-push) 60%, var(--os-ink));
  --os-monitor-plugin: color-mix(in srgb, var(--os-monitor-wall-plugin) 60%, var(--os-ink));
  --os-monitor-file: color-mix(in srgb, var(--os-monitor-wall-file) 60%, var(--os-ink));
  --os-monitor-quiet: color-mix(in srgb, var(--os-monitor-wall-quiet) 60%, var(--os-ink));
  --os-monitor-created: color-mix(in srgb, var(--os-monitor-wall-created) 60%, var(--os-ink));
  --os-monitor-changed: color-mix(in srgb, var(--os-monitor-wall-changed) 60%, var(--os-ink));
  --os-monitor-deleted: color-mix(in srgb, var(--os-monitor-wall-deleted) 60%, var(--os-ink));
  --os-monitor-state-idle: var(--os-state-idle);
  --os-monitor-state-running: var(--os-state-running);
  --os-monitor-state-waiting: var(--os-state-waiting);
  --os-monitor-state-blocked: var(--os-state-blocked);
  --os-monitor-state-decision: var(--os-state-needs-you);
  --os-monitor-state-failed: var(--os-state-failed);

  --monitor-ground: var(--os-monitor-ground);
  --monitor-panel: var(--os-monitor-panel);
  --monitor-rule: var(--os-monitor-rule);
  --monitor-ink: var(--os-monitor-ink);
  --monitor-ink-dim: var(--os-monitor-ink-dim);
  display: flex;
  gap: 0;
  min-height: 520px;
  background: var(--monitor-ground);
  color: var(--monitor-ink);
  border: 1px solid var(--monitor-rule);
  border-radius: 10px;
  overflow: hidden;
}

.monitor--fullscreen {
  border-radius: 0;
  border: 0;
  height: 100vh;
}

/* THE WALL: full screen keeps the dark palette `app.scss` holds, and each state's ring is the
   state's own hue lifted toward white so it glows on the dark ground. */
.monitor--wall {
  --os-monitor-ground: var(--os-monitor-wall-ground);
  --os-monitor-panel: var(--os-monitor-wall-panel);
  --os-monitor-rule: var(--os-monitor-wall-rule);
  --os-monitor-ink: var(--os-monitor-wall-ink);
  --os-monitor-ink-dim: var(--os-monitor-wall-ink-dim);
  --os-monitor-ink-soft: var(--os-monitor-wall-ink-soft);
  --os-monitor-tell: var(--os-monitor-wall-tell);
  --os-monitor-start: var(--os-monitor-wall-start);
  --os-monitor-progress: var(--os-monitor-wall-progress);
  --os-monitor-done: var(--os-monitor-wall-done);
  --os-monitor-handback: var(--os-monitor-wall-handback);
  --os-monitor-fail: var(--os-monitor-wall-fail);
  --os-monitor-blocked: var(--os-monitor-wall-blocked);
  --os-monitor-workflow: var(--os-monitor-wall-workflow);
  --os-monitor-card: var(--os-monitor-wall-card);
  --os-monitor-push: var(--os-monitor-wall-push);
  --os-monitor-plugin: var(--os-monitor-wall-plugin);
  --os-monitor-file: var(--os-monitor-wall-file);
  --os-monitor-quiet: var(--os-monitor-wall-quiet);
  --os-monitor-created: var(--os-monitor-wall-created);
  --os-monitor-changed: var(--os-monitor-wall-changed);
  --os-monitor-deleted: var(--os-monitor-wall-deleted);
  --os-monitor-state-idle: color-mix(in srgb, var(--os-state-idle) 60%, white);
  --os-monitor-state-running: color-mix(in srgb, var(--os-state-running) 60%, white);
  --os-monitor-state-waiting: color-mix(in srgb, var(--os-state-waiting) 60%, white);
  --os-monitor-state-blocked: color-mix(in srgb, var(--os-state-blocked) 60%, white);
  --os-monitor-state-decision: color-mix(in srgb, var(--os-state-needs-you) 60%, white);
  --os-monitor-state-failed: color-mix(in srgb, var(--os-state-failed) 60%, white);
}

.monitor-main {
  flex: 1 1 auto;
  min-width: 0;
  display: flex;
  flex-direction: column;
}

.monitor-stage {
  position: relative;
  flex: 1 1 auto;
  min-height: 0;
}

.monitor-canvas {
  position: absolute;
  inset: 0;
  display: block;
  touch-action: none;
  cursor: grab;
}

.monitor-top {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  padding: 12px 12px 18px 16px;
  background: linear-gradient(
    to bottom,
    color-mix(in srgb, var(--monitor-ground) 92%, transparent),
    color-mix(in srgb, var(--monitor-ground) 60%, transparent) 70%,
    transparent
  );
  z-index: 1;
  display: flex;
  flex-wrap: wrap-reverse;
  justify-content: space-between;
  align-items: flex-start;
  gap: 10px;
  pointer-events: none;
}

.monitor-hud {
  padding-top: 2px;
  text-shadow: 0 1px 6px var(--monitor-ground);
}

.monitor-title {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 0.75rem;
  letter-spacing: 0.14em;
  font-weight: 700;
}

.monitor-live {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--os-monitor-state-running);
}

.monitor-live-dot {
  width: 9px;
  height: 9px;
  border-radius: 50%;
  background: var(--os-monitor-state-running);
  box-shadow: 0 0 10px var(--os-monitor-state-running);
  animation: monitor-blink 1.4s ease-in-out infinite;
}

.monitor-replay {
  color: var(--os-monitor-handback);
}

.monitor-replay-cut {
  color: var(--monitor-ink-dim);
  font-size: 0.85em;
  margin-top: 2px;
}

.monitor-clock {
  color: var(--monitor-ink-dim);
  font-family: 'IBM Plex Mono', monospace;
  letter-spacing: 0.04em;
  font-weight: 500;
}

.monitor-stats {
  display: flex;
  gap: 22px;
  margin-top: 8px;
  flex-wrap: wrap;
}

.monitor-stat {
  display: flex;
  flex-direction: column;
  line-height: 1.05;
}

.monitor-stat--tip {
  pointer-events: auto;
}

.monitor-stat-value {
  font-size: 1.75rem;
  font-weight: 700;
  font-variant-numeric: tabular-nums;
}

.monitor-stat-words {
  font-size: 0.6875rem;
  text-transform: uppercase;
  letter-spacing: 0.1em;
  color: var(--monitor-ink-dim);
}

.monitor-sub {
  margin-top: 8px;
  font-size: 0.75rem;
  color: var(--monitor-ink-dim);
}

.monitor-controls {
  pointer-events: auto;
  margin-left: auto;
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 6px;
  background: color-mix(in srgb, var(--monitor-panel) 80%, transparent);
  border: 1px solid var(--monitor-rule);
  border-radius: 8px;
  color: var(--monitor-ink);
}

.monitor-mode :deep(.q-btn) {
  color: var(--monitor-ink);
}

/* A pressed toggle: lit, and filled so it does not depend on hue alone. */
.monitor-tool-on {
  color: var(--os-monitor-tell);
  background: var(--os-monitor-rule);
}

.monitor-speed {
  width: 5.5rem;
}

.monitor-settings-anchor {
  position: relative;
}

.monitor-settings {
  position: absolute;
  top: calc(100% + 8px);
  right: 0;
  z-index: 3;
  width: 18rem;
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  background: var(--monitor-panel);
  border: 1px solid var(--monitor-rule);
  border-radius: 8px;
  box-shadow: 0 8px 30px color-mix(in srgb, var(--monitor-ground) 70%, transparent);
  font-size: 0.8125rem;
  color: var(--monitor-ink);
  max-height: calc(100vh - 140px);
  overflow-y: auto;
}

.monitor-settings-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.monitor-settings-seconds {
  width: 8rem;
}

.monitor-settings-head {
  margin-top: 4px;
  font-size: 0.6875rem;
  font-weight: 700;
  letter-spacing: 0.1em;
  text-transform: uppercase;
  color: var(--monitor-ink-dim);
}

.monitor-settings-slider {
  padding: 0 0.25rem;
}

.monitor-settings-value {
  font-family: var(--os-mono);
  font-size: 0.75rem;
  color: var(--os-monitor-ink-soft);
}

.monitor-settings-none {
  color: var(--monitor-ink-dim);
}

.monitor-legend {
  position: absolute;
  left: 16px;
  bottom: 10px;
  right: 16px;
  display: flex;
  flex-wrap: wrap;
  gap: 4px 14px;
  font-size: 0.6875rem;
  color: var(--monitor-ink-dim);
  pointer-events: none;
}

.monitor-legend-item {
  display: inline-flex;
  align-items: center;
  gap: 5px;
}

.monitor-legend-gap {
  width: 10px;
}

.monitor-swatch {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  box-shadow: 0 0 6px currentColor;
}

.monitor-ring {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  border: 2px solid;
}

.monitor-tip {
  position: absolute;
  max-width: 20rem;
  padding: 8px 10px;
  background: color-mix(in srgb, var(--monitor-ground) 94%, transparent);
  border: 1px solid;
  border-radius: 8px;
  font-size: 0.75rem;
  pointer-events: none;
  box-shadow: 0 8px 30px color-mix(in srgb, var(--monitor-ground) 70%, transparent);
  z-index: 2;
}

.monitor-tip-title {
  font-weight: 700;
  font-size: 0.875rem;
}

.monitor-tip-sub {
  color: var(--monitor-ink-dim);
  margin-bottom: 4px;
}

.monitor-tip-line {
  color: var(--monitor-ink);
  overflow-wrap: anywhere;
}

.monitor-overlay {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  color: var(--monitor-ink-dim);
  font-size: 0.875rem;
  pointer-events: none;
  text-align: center;
  padding: 0 24px;
}

.monitor-overlay--error {
  color: var(--os-monitor-blocked);
}

.monitor-finished {
  position: absolute;
  left: 50%;
  bottom: 48px;
  transform: translateX(-50%);
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 12px;
  background: color-mix(in srgb, var(--monitor-panel) 92%, transparent);
  border: 1px solid var(--monitor-rule);
  border-radius: 8px;
  font-size: 0.8125rem;
}

.monitor-timeline {
  flex: 0 0 auto;
  padding: 6px 16px 8px;
  border-top: 1px solid var(--monitor-rule);
  background: var(--monitor-panel);
}

.monitor-bars {
  position: relative;
  display: flex;
  align-items: flex-end;
  gap: 1px;
  height: 38px;
  cursor: pointer;
}

.monitor-bars:focus-visible {
  outline: 2px solid var(--os-monitor-tell);
  outline-offset: 2px;
}

.monitor-bar {
  flex: 1 1 0;
  min-width: 1px;
  border-radius: 1px 1px 0 0;
  opacity: 0.75;
}

.monitor-playhead {
  position: absolute;
  top: -4px;
  bottom: -2px;
  width: 2px;
  background: var(--monitor-ink);
  box-shadow: 0 0 8px var(--monitor-ink);
  transition: left 0.2s linear;
}

.monitor-span {
  display: flex;
  justify-content: space-between;
  margin-top: 4px;
  font-size: 0.6875rem;
  color: var(--monitor-ink-dim);
}

.monitor-span-hint {
  opacity: 0.8;
}

.monitor-ticker {
  flex: 0 0 22rem;
  display: flex;
  flex-direction: column;
  min-height: 0;
  border-left: 1px solid var(--monitor-rule);
  background: var(--monitor-panel);
}

.monitor-ticker-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 10px 12px;
  border-bottom: 1px solid var(--monitor-rule);
  font-size: 0.75rem;
  font-weight: 700;
  letter-spacing: 0.12em;
  text-transform: uppercase;
  color: var(--monitor-ink-dim);
}

.monitor-picked {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  text-transform: none;
  letter-spacing: 0;
  color: var(--monitor-ink);
  font-weight: 600;
  min-width: 0;
}

.monitor-rows {
  list-style: none;
  overflow-x: hidden;
  margin: 0;
  padding: 6px 0;
  overflow-y: auto;
  flex: 1 1 auto;
  min-height: 0;
}

.monitor-row {
  display: flex;
  gap: 10px;
  padding: 7px 12px;
  border-bottom: 1px solid color-mix(in srgb, var(--monitor-ink-dim) 6%, transparent);
}

.monitor-row-mark {
  flex: 0 0 3px;
  border-radius: 2px;
  box-shadow: 0 0 8px currentColor;
}

.monitor-row-body {
  min-width: 0;
  flex: 1 1 auto;
}

.monitor-row-head {
  display: flex;
  align-items: baseline;
  gap: 8px;
  font-size: 0.75rem;
}

.monitor-row-verb {
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  font-size: 0.6875rem;
  white-space: nowrap;
}

.monitor-row-who {
  font-weight: 600;
  color: var(--monitor-ink);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  min-width: 0;
}

.monitor-row-time {
  margin-left: auto;
  color: var(--monitor-ink-dim);
  font-family: 'IBM Plex Mono', monospace;
  font-size: 0.6875rem;
  white-space: nowrap;
}

.monitor-row-team {
  font-size: 0.6875rem;
  color: var(--monitor-ink-dim);
}

.monitor-row-text {
  font-size: 0.75rem;
  color: var(--os-monitor-ink-soft);
  line-height: 1.35;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  overflow-wrap: anywhere;
}

/* A push lists every file it named, one a line: never clamped like a row's words. */
.monitor-row-text--files {
  display: block;
  -webkit-line-clamp: unset;
  overflow: visible;
}

.monitor-row-files {
  list-style: none;
  margin: 0.15rem 0 0;
  padding: 0;
}

.monitor-row-files li {
  font-family: var(--os-mono);
  font-size: 0.7rem;
  overflow-wrap: anywhere;
}

.monitor-row-mark {
  display: inline-block;
  width: 0.9em;
  font-weight: 700;
}

.monitor-row-commit {
  color: var(--os-monitor-ink);
}

.monitor-row-more {
  font-style: italic;
}

.monitor-row-resource {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 0.75rem;
  color: var(--os-monitor-ink-soft);
  line-height: 1.35;
  min-width: 0;
}

.monitor-row-icon {
  flex: 0 0 auto;
  font-size: 1.125rem;
  line-height: 1;
  font-weight: normal;
  font-style: normal;
}

.monitor-row-dot {
  flex: 0 0 auto;
  width: 6px;
  height: 6px;
  margin: 0 6px;
  border-radius: 50%;
}

.monitor-row-words {
  min-width: 0;
  overflow-wrap: anywhere;
}

.monitor-empty {
  padding: 16px 12px;
  color: var(--monitor-ink-dim);
  font-size: 0.8125rem;
}

/* A new row slides in once, as it is made; a CSS animation rather than a transition group,
   which a list replaced several times a second never let finish. */
.monitor-row {
  animation: monitor-row-in 0.5s cubic-bezier(0.2, 0.8, 0.2, 1);
}

@keyframes monitor-row-in {
  from {
    opacity: 0.2;
    transform: translateX(24px);
    background: color-mix(in srgb, var(--os-monitor-tell) 20%, transparent);
  }
  to {
    opacity: 1;
    transform: none;
    background: transparent;
  }
}

@keyframes monitor-blink {
  0%,
  100% {
    opacity: 1;
  }
  50% {
    opacity: 0.25;
  }
}

@media (max-width: 900px) {
  .monitor {
    flex-direction: column;
  }

  .monitor-ticker {
    flex: 0 0 16rem;
    border-left: 0;
    border-top: 1px solid var(--monitor-rule);
  }

  .monitor-stats {
    gap: 12px;
  }

  .monitor-stat-value {
    font-size: 1.25rem;
  }
}

@media (prefers-reduced-motion: reduce) {
  .monitor-live-dot {
    animation: none;
  }

  .monitor-row {
    animation: none;
  }

  .monitor-playhead {
    transition: none;
  }
}
</style>
