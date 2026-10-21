import { initials } from '../initials'
import {
  forceCollide,
  forceLink,
  forceManyBody,
  forceSimulation,
  forceX,
  forceY,
  type Simulation,
  type SimulationLinkDatum,
  type SimulationNodeDatum,
} from 'd3-force'
import { stateWords } from '../memberState'
import { avatarSheet, memberAvatar, tileSource } from '../avatars'
import type { MemberState, MonitorEffect, MonitorModel, NodeRef, Origin, Tone } from './model'
import { compact, workflowNameLine } from './model'
import type { Resource, ResourceAction } from './hints'
import { MonitorSymbolsFamily, glyphOf, type Codepoints } from './font'
import { LayoutDefaults, type LayoutSettings } from './layout'
import { MaxStepIconsEach, StepCaptionMinPx, StepMs, stepIcon, stepMotion, type AgentStep } from './steps'
import { wakeLine } from '../wakes'
import {
  DefaultFileSettings,
  WorkspaceGraph,
  markAlpha,
  type FileSettings,
  type WorkspaceKind,
  type WorkspaceNode,
  type WorkspaceView,
} from './workspace'

/**
 * THE MONITOR'S STAGE: a force-directed graph on a canvas, and the effects the model's rows make.
 *
 * Teams are the large territories, members orbit their team, and every open workflow is a node
 * tied to the members working in it. The simulation is ticked from the scene's own frame loop
 * (never d3's timer), so a stopped scene costs nothing and a test can drive it by hand.
 *
 * The stage is dark in every theme on purpose: it is a screen to watch, like the Concierge's
 * terminal, and glow reads only on a dark ground. Its colours live here, in one map, and the
 * view's legend reads the same map.
 *
 * A member's file changes hang off its team as a small tree - repository, project, file - held in
 * display time by `WorkspaceGraph` and drawn here as nodes of the same simulation. They are kept
 * clear of each other and of every member's name by a pass after each tick (`separate`).
 */

const ToneNames: Tone[] = ['tell', 'start', 'progress', 'done', 'handback', 'fail', 'blocked', 'workflow', 'card', 'push', 'plugin', 'file', 'quiet']
/** How long a view the person moved is left alone before it fits everything again. */
export const RefitAfterMs = 12_000

const StateNames: MemberState[] =['idle', 'running', 'waiting', 'blocked', 'decision', 'failed']
const ActionNames: ResourceAction[] = ['created', 'changed', 'deleted']

/**
 * THE STAGE'S COLOURS, read from the `--os-monitor-*` tokens in `app.scss` by `readPalette`. One
 * place holds them for the canvas and the view's stylesheet alike, so the legend and the graph
 * cannot disagree. Filled in place, so every importer sees the values once they are read.
 */
export const Tones = Object.fromEntries(ToneNames.map((tone) => [tone, ''])) as Record<Tone, string>
export const StateColours = Object.fromEntries(StateNames.map((state) => [state, ''])) as Record<MemberState, string>
/** What a plugin's row did to a thing: green, amber, red. Never the only signal - see `drawResources`. */
export const ActionColours = Object.fromEntries(ActionNames.map((action) => [action, ''])) as Record<ResourceAction, string>
const Screen = { ground: '', ink: '', inkDim: '', light: false }

/** A probe the browser resolves a colour on: `color-mix()` and `var()`-derived tokens come back as hex. */
let probe: CanvasRenderingContext2D | null | undefined

/**
 * A token as `#rrggbb`, which `alphaHex` appends an alpha to. The theme's tokens are often a
 * `color-mix()`; the browser's own canvas resolves it. Left as written where there is no canvas.
 */
export function solidHex(value: string): string {
  if (/^#[0-9a-f]{6}$/i.test(value)) return value.toLowerCase()
  const short = value.match(/^#([0-9a-f])([0-9a-f])([0-9a-f])$/i)
  if (short) return `#${short[1]}${short[1]}${short[2]}${short[2]}${short[3]}${short[3]}`.toLowerCase()
  if (probe === undefined) {
    try {
      probe = typeof document === 'undefined' ? null : document.createElement('canvas').getContext('2d')
    } catch {
      probe = null
    }
  }
  if (!probe || value === '') return value
  probe.fillStyle = 'black'
  probe.fillStyle = value
  const resolved = String(probe.fillStyle)
  if (resolved.startsWith('#')) return resolved
  const rgb = resolved.match(/^rgba?\((\d+),\s*(\d+),\s*(\d+)/)
  return rgb ? `#${rgb.slice(1, 4).map((part) => Number(part).toString(16).padStart(2, '0')).join('')}` : value
}

/** Whether a ground is light: then a glow cannot add light to it and is painted over instead. */
export function isLightGround(hex: string): boolean {
  if (!/^#[0-9a-f]{6}$/i.test(hex)) return false
  const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255) as [number, number, number]
  return 0.2126 * r + 0.7152 * g + 0.0722 * b > 0.5
}

/** Read the monitor's tokens off an element inside the page: the wall palette, in the normal view and full screen alike. */
export function readPalette(element: Element) {
  const css = getComputedStyle(element)
  const token = (name: string) => solidHex(css.getPropertyValue(`--os-monitor-${name}`).trim())
  for (const tone of ToneNames) Tones[tone] = token(tone)
  for (const state of StateNames) StateColours[state] = token(`state-${state}`)
  for (const action of ActionNames) ActionColours[action] = token(action)
  Screen.ground = token('ground')
  Screen.ink = token('ink')
  Screen.inkDim = token('ink-dim')
  Screen.light = isLightGround(Screen.ground)
}

/** How a glow is laid on the ground: added light on a dark one, painted over a light one. */
function glowMode(): GlobalCompositeOperation {
  return Screen.light ? 'source-over' : 'lighter'
}

type NodeKind = 'member' | 'team' | 'workflow' | WorkspaceKind

const WorkspaceKinds: ReadonlySet<NodeKind> = new Set<NodeKind>(['repository', 'project', 'file'])

interface SceneNode extends SimulationNodeDatum {
  id: string
  kind: NodeKind
  team: string
  r: number
  born: number
  dying: number | null
  pulse: number
  shakeUntil: number
  /** Where a file change is DRAWN: eased toward its simulated x and y, so the small corrections
   * that keep labels apart never show as shaking. Unset for every other kind. */
  shownX?: number
  shownY?: number
}

interface SceneLink extends SimulationLinkDatum<SceneNode> {
  id: string
  /** `tree` ties a file change to what it hangs off; `touch` is drawn from a member to it and pulls nothing. */
  kind: 'spoke' | 'work' | 'tree' | 'touch'
}

/** A screen-space box, in world units. */
export interface Box {
  left: number
  top: number
  right: number
  bottom: number
}

/** A repository, project or file node as the scene draws it now. Exposed for tests. */
export interface ShownWorkspaceNode extends WorkspaceNode {
  x: number
  y: number
  /** Where it is drawn: x and y eased, so it never shakes. */
  shownX: number
  shownY: number
  /** What it covers: its mark and its label. */
  box: Box
}

/** What the file changes draw now. Exposed for tests. */
export interface ShownWorkspace {
  nodes: ShownWorkspaceNode[]
  /** A line from a member to what it touched: the member's key and the node's id. */
  touches: { member: string; node: string; alpha: number }[]
  /** A commit flashing on its repository: its short sha and subject. */
  flashes: { node: string; sha: string; subject: string }[]
}

interface Comet {
  from: NodeRef
  to: NodeRef
  tone: Tone
  label: string | undefined
  start: number
  duration: number
  bend: number
  trail: { x: number; y: number }[]
}

interface Burst {
  at: NodeRef
  tone: Tone
  big: boolean
  start: number
  sparks: { angle: number; speed: number; size: number }[]
}

interface Ripple {
  at: NodeRef
  tone: Tone
  start: number
}

interface Floater {
  at: NodeRef
  text: string
  tone: Tone
  start: number
  lane: number
}

interface ResourceMark {
  at: NodeRef
  resource: Resource
  start: number
  /** Display time it stays for, ms: the settings' seconds, not log time. */
  ttl: number
}

interface StepMark {
  at: NodeRef
  step: AgentStep
  start: number
  /** Its place beside the others on the node: 0 over the middle, 1 left of it, 2 right. */
  lane: number
}

/** A step's icon as the scene would draw it now. Exposed for tests. */
export interface ShownStep {
  at: NodeRef
  /** The Material Symbols name it draws. */
  icon: string
  /** The icon's character, or null while the font is not loaded: a plain dot is drawn instead. */
  glyph: string | null
  /** The small number on it: how many steps it stands for, or null for one. */
  count: number | null
  /** The MCP tool's name under it, only when the node is drawn large enough; null otherwise. */
  caption: string | null
  x: number
  y: number
  alpha: number
}

/** A resource mark as the scene would draw it now. Exposed for tests. */
export interface ShownResource {
  at: NodeRef
  resource: Resource
  x: number
  y: number
  alpha: number
  /** The icon's character, or null while the font is not loaded: a plain dot is drawn instead. */
  glyph: string | null
}

export interface HoverInfo {
  kind: NodeKind | 'origin'
  title: string
  subtitle: string
  lines: string[]
  colour: string
  x: number
  y: number
}

export interface SceneOptions {
  fontPx: number
  fontFamily: string
  reducedMotion: boolean
  onHover?: (info: HoverInfo | null) => void
  /** A node clicked: a member's key, a team's id or a workflow's number. */
  onPick?: (ref: NodeRef) => void
}

const CometMs = 1100
const BurstMs = 1400
const RippleMs = 1100
const FloatMs = 2600
const BornMs = 900
const DyingMs = 1200
const OriginMs = 5500
const MaxEffects = 260
/** A resource mark's radius; it fades as a file change does (`markAlpha`). */
const ResourceR = 11
/** A file change's marks: a repository's, a project's and a file's radius, and the room between them. */
const WorkspaceR: Record<WorkspaceKind, number> = { repository: 10, project: 8, file: 6 }
const WorkspaceGap = 4
/** At most this many sweeps a frame to take every file change clear of the rest. */
const SeparatePasses = 60
/** Past this many at once beside one member, the oldest goes early. */
const MaxResourcesEach = 8
/** A step's icon size, and how far it climbs over its time. */
const StepIconPx = 14
const StepRise = 28

function hsl(hue: number, s: number, l: number, a = 1): string {
  return `hsla(${hue.toFixed(0)}, ${s}%, ${l}%, ${a})`
}

function ease(t: number): number {
  return t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2
}

function alphaHex(colour: string, alpha: number): string {
  const a = Math.round(Math.max(0, Math.min(1, alpha)) * 255)
  return colour + a.toString(16).padStart(2, '0')
}

export class MonitorScene {
  private readonly ctx: CanvasRenderingContext2D | null
  private readonly sim: Simulation<SceneNode, SceneLink>
  private readonly nodes = new Map<string, SceneNode>()
  private readonly links = new Map<string, SceneLink>()
  private model: MonitorModel | null = null

  private comets: Comet[] = []
  private bursts: Burst[] = []
  private ripples: Ripple[] = []
  private floaters: Floater[] = []
  private resources: ResourceMark[] = []
  private steps: StepMark[] = []
  private stepsOn = true
  private readonly workspace = new WorkspaceGraph()
  private fileSettings: FileSettings = DefaultFileSettings
  /** The file changes as last laid out, by node id. */
  private workspaceView: WorkspaceView = { nodes: [], touches: [], flashes: [] }
  private readonly workspaceNodes = new Map<string, WorkspaceNode>()
  private symbols: Codepoints | null = null
  private readonly originUsed = new Map<string, number>()

  private width = 800
  private height = 600
  private dpr = 1
  private camera = { x: 0, y: 0, k: 1 }
  private target = { x: 0, y: 0, k: 1 }
  private userMovedAt = -Infinity
  /** Off: a view the person moved stays where they put it, instead of fitting again after a while. */
  private autoFit = true
  private frame = 0
  private last = 0
  private clock = 0
  private hovered: SceneNode | null = null
  private dragging: SceneNode | null = null
  private panning: { x: number; y: number; cx: number; cy: number } | null = null
  private teamTargets = new Map<string, { x: number; y: number }>()
  private layout: LayoutSettings = { ...LayoutDefaults }

  constructor(
    private readonly canvas: HTMLCanvasElement,
    private options: SceneOptions,
  ) {
    this.ctx = canvas.getContext('2d')
    this.sim = forceSimulation<SceneNode, SceneLink>([])
      .stop()
      .alphaDecay(0.02)
      .alphaTarget(options.reducedMotion ? 0 : 0.012)
      // FILE CHANGES KEEP THEIR LABELS APART AS A FORCE, so they glide clear and come to rest.
      // `separate` after each tick still makes it certain; with this it has almost nothing to do,
      // where alone it fought the simulation every frame and the nodes shook.
      .force('boxes', () => this.pushBoxesApart())
    this.applyLayout()

    canvas.addEventListener('pointermove', this.onPointerMove)
    canvas.addEventListener('pointerdown', this.onPointerDown)
    canvas.addEventListener('pointerup', this.onPointerUp)
    canvas.addEventListener('pointerleave', this.onPointerLeave)
    canvas.addEventListener('wheel', this.onWheel, { passive: false })
    canvas.addEventListener('dblclick', this.onDoubleClick)
  }

  /**
   * The person's layout multipliers (the Monitor's settings): from now on, and the graph is woken so
   * it moves to them rather than waiting for the next change.
   */
  setLayout(layout: LayoutSettings) {
    this.layout = { ...layout }
    this.applyLayout()
    this.sim.alpha(Math.max(this.sim.alpha(), 0.5))
  }

  /** The forces, at the scene's own values times the person's layout multipliers. Setting an
   * accessor again is what makes d3 read it again. */
  private applyLayout() {
    const { distance, strength, repulsion, gravity, friction } = this.layout
    this.sim
      .velocityDecay(Math.min(0.9, Math.max(0.05, 0.32 * friction)))
      .force(
        'link',
        ((this.sim.force('link') as ReturnType<typeof forceLink<SceneNode, SceneLink>> | undefined) ?? forceLink<SceneNode, SceneLink>([]))
          .id((node) => node.id)
          .distance((link) => this.linkDistance(link) * distance)
          .strength((link) => Math.min(1, (link.kind === 'spoke' ? 0.55 : link.kind === 'tree' ? 0.7 : 0.32) * strength)),
      )
      .force(
        'charge',
        forceManyBody<SceneNode>()
          .strength((node) => (node.kind === 'team' ? -600 : node.kind === 'workflow' ? -60 : WorkspaceKinds.has(node.kind) ? -90 : -280) * repulsion)
          .distanceMax(420 * Math.max(1, distance)),
      )
      .force('collide', forceCollide<SceneNode>().radius((node) => node.r + (node.kind === 'team' ? 30 : node.kind === 'member' ? 24 : 6)))
      .force('x', forceX<SceneNode>((node) => this.teamTarget(node.team).x).strength((node) => (node.kind === 'team' ? 0.12 : 0.02) * gravity))
      .force('y', forceY<SceneNode>((node) => this.teamTarget(node.team).y).strength((node) => (node.kind === 'team' ? 0.12 : 0.02) * gravity))
  }

  setOptions(options: Partial<SceneOptions>) {
    this.options = { ...this.options, ...options }
  }

  /** Whether file changes draw, and how far down: from the next frame, for every one shown. */
  setFileChanges(settings: FileSettings) {
    this.fileSettings = { ...settings }
  }

  /** Forget every file change drawn: a replay has none. */
  clearFileChanges() {
    this.workspace.clear()
    this.syncWorkspace()
  }

  /**
   * The activity pane's Clear: what the forgotten rows drew goes at once, with no fade - the
   * workflow nodes and their ties, the file changes and every effect in flight - and the graph
   * is brought in line with the model, whose teams and members stay.
   */
  forget(model: MonitorModel) {
    this.comets = []
    this.bursts = []
    this.ripples = []
    this.floaters = []
    this.resources = []
    this.steps = []
    for (const [id, node] of this.nodes) if (node.kind === 'workflow') this.nodes.delete(id)
    for (const [id, link] of this.links) if (link.kind === 'work') this.links.delete(id)
    this.clearFileChanges()
    this.sync(model)
    this.restartSim()
  }

  /** Show agent steps: off, every step icon goes at once and none is drawn until it is on again. */
  setAgentSteps(show: boolean) {
    this.stepsOn = show
    if (!show) this.steps = []
  }

  /** The full icon font arrived: from the next frame, icons draw instead of plain dots. */
  setSymbols(codepoints: Codepoints | null) {
    this.symbols = codepoints
  }

  resize(width: number, height: number, dpr = globalThis.devicePixelRatio || 1) {
    this.width = Math.max(1, width)
    this.height = Math.max(1, height)
    this.dpr = dpr
    this.canvas.width = Math.round(this.width * dpr)
    this.canvas.height = Math.round(this.height * dpr)
    this.canvas.style.width = `${this.width}px`
    this.canvas.style.height = `${this.height}px`
    this.layoutTeams()
  }

  start() {
    if (this.frame !== 0) return
    this.last = performance.now()
    const loop = (now: number) => {
      this.step(now)
      this.frame = requestAnimationFrame(loop)
    }
    this.frame = requestAnimationFrame(loop)
  }

  stop() {
    if (this.frame !== 0) cancelAnimationFrame(this.frame)
    this.frame = 0
  }

  destroy() {
    this.stop()
    this.sim.stop()
    this.canvas.removeEventListener('pointermove', this.onPointerMove)
    this.canvas.removeEventListener('pointerdown', this.onPointerDown)
    this.canvas.removeEventListener('pointerup', this.onPointerUp)
    this.canvas.removeEventListener('pointerleave', this.onPointerLeave)
    this.canvas.removeEventListener('wheel', this.onWheel)
    this.canvas.removeEventListener('dblclick', this.onDoubleClick)
  }

  /** Bring the graph in line with the model: new nodes are born near their team, gone ones fade. */
  sync(model: MonitorModel) {
    this.model = model
    const wanted = new Set<string>()
    const wantedLinks = new Set<string>()
    let changed = false

    const membersByTeam = new Map<string, number>()
    for (const member of model.members.values()) membersByTeam.set(member.team, (membersByTeam.get(member.team) ?? 0) + 1)

    for (const team of model.teams.values()) {
      const members = membersByTeam.get(team.id) ?? 0
      const id = `t:${team.id}`
      wanted.add(id)
      changed = this.ensure(id, 'team', team.id, 26 + Math.min(14, members * 1.5)) || changed
    }

    for (const member of model.members.values()) {
      const id = `m:${member.key}`
      wanted.add(id)
      changed = this.ensure(id, 'member', member.team, member.manager ? 17 : member.kind === 'plugin' ? 12 : 14) || changed
      const spoke = `${id}|t:${member.team}`
      wantedLinks.add(spoke)
      if (!this.links.has(spoke)) {
        this.links.set(spoke, { id: spoke, kind: 'spoke', source: id, target: `t:${member.team}` })
        changed = true
      }
    }

    for (const workflow of model.workflows.values()) {
      // A workflow nobody works is not drawn: with no member it has nothing to tie it, and would
      // drift. A model built from older rows may hold one; its node appears with its first member.
      if (workflow.members.size === 0) continue
      const id = `w:${workflow.id}`
      wanted.add(id)
      const first = [...workflow.members].map((key) => this.nodes.get(`m:${key}`)).find((node) => node !== undefined) ?? null
      changed = this.ensure(id, 'workflow', workflow.team, 7 + Math.min(6, workflow.members.size), first) || changed
      for (const key of workflow.members) {
        if (!model.members.has(key)) continue
        const link = `m:${key}|${id}`
        wantedLinks.add(link)
        if (!this.links.has(link)) {
          this.links.set(link, { id: link, kind: 'work', source: `m:${key}`, target: id })
          changed = true
        }
      }
    }

    const now = this.clock
    for (const node of this.nodes.values()) {
      if (WorkspaceKinds.has(node.kind)) continue
      if (!wanted.has(node.id) && node.dying === null) {
        node.dying = now
        changed = true
      } else if (wanted.has(node.id) && node.dying !== null) {
        node.dying = null
      }
    }
    for (const [id, link] of this.links) {
      if (link.kind === 'tree' || link.kind === 'touch') continue
      if (!wantedLinks.has(id)) {
        this.links.delete(id)
        changed = true
      } else if (typeof link.source === 'string' && !this.nodes.has(link.source)) {
        this.links.delete(id)
      }
    }

    if (changed) {
      this.layoutTeams()
      this.restartSim()
    }
  }

  /** Play a row's effects. Past the cap the oldest are dropped, so a burst of rows never stalls. */
  play(effects: readonly MonitorEffect[]) {
    const now = this.clock
    for (const effect of effects) {
      switch (effect.kind) {
        case 'comet':
          this.comets.push({
            from: effect.from,
            to: effect.to,
            tone: effect.tone,
            label: effect.label,
            start: now,
            duration: CometMs * (0.85 + Math.random() * 0.3),
            bend: (Math.random() - 0.5) * 0.6,
            trail: [],
          })
          if (effect.from.kind === 'origin') this.originUsed.set(`${effect.from.team}:${effect.from.origin}`, now)
          break
        case 'burst':
          this.bursts.push({
            at: effect.at,
            tone: effect.tone,
            big: effect.big === true,
            start: now,
            sparks: Array.from({ length: this.options.reducedMotion ? 0 : effect.big ? 26 : 12 }, () => ({
              angle: Math.random() * Math.PI * 2,
              speed: 0.04 + Math.random() * (effect.big ? 0.11 : 0.06),
              size: 1 + Math.random() * 2,
            })),
          })
          this.touch(effect.at)
          break
        case 'ripple':
          this.ripples.push({ at: effect.at, tone: effect.tone, start: now })
          this.touch(effect.at)
          break
        case 'float': {
          // A member talking is one line at a time; a busy replay would otherwise stack them.
          const mine = this.floaters.filter((f) => sameRef(f.at, effect.at))
          if (effect.tone === 'progress' && mine.some((f) => f.tone === 'progress')) break
          const lane = mine.filter((f) => now - f.start < FloatMs * 0.6).length
          if (lane < 2) this.floaters.push({ at: effect.at, text: effect.text, tone: effect.tone, start: now, lane })
          break
        }
        case 'shake': {
          const node = this.nodeOf(effect.at)
          if (node && !this.options.reducedMotion) node.shakeUntil = now + 500
          break
        }
        case 'workspace':
          this.workspace.apply(effect.push, now, effect.ttlMs, effect.at.kind === 'member' ? effect.at.key : null)
          this.touch(effect.at)
          break
        case 'step': {
          if (!this.stepsOn) break
          // A BURST KEEPS THE NEWEST: past the few a node shows at once, the oldest goes early.
          const mine = this.steps.filter((mark) => sameRef(mark.at, effect.at))
          const gone = new Set(mine.slice(0, Math.max(0, mine.length - MaxStepIconsEach + 1)))
          if (gone.size > 0) this.steps = this.steps.filter((mark) => !gone.has(mark))
          const taken = new Set(mine.filter((mark) => !gone.has(mark)).map((mark) => mark.lane))
          const lane = [0, 1, 2].find((free) => !taken.has(free)) ?? 0
          this.steps.push({ at: effect.at, step: effect.step, start: now, lane })
          break
        }
        case 'resource': {
          this.resources.push({ at: effect.at, resource: effect.resource, start: now, ttl: effect.ttlMs })
          const mine = this.resources.filter((mark) => sameRef(mark.at, effect.at))
          if (mine.length > MaxResourcesEach) {
            const gone = new Set(mine.slice(0, mine.length - MaxResourcesEach))
            this.resources = this.resources.filter((mark) => !gone.has(mark))
          }
          this.touch(effect.at)
          break
        }
      }
    }
    this.cap()
  }

  /** Frame the whole graph, and follow it again until the person moves the view. */
  fit() {
    this.userMovedAt = -Infinity
    this.frameAll(true)
  }

  /**
   * Whether a view the person moved fits everything again after `RefitAfterMs` of leaving it.
   * Off, it stays put until Fit is pressed; a view nobody has moved still follows the graph.
   */
  setAutoFit(on: boolean) {
    this.autoFit = on
  }

  /** Whether the camera is framing the whole graph at this moment. Exposed for tests. */
  get following() {
    return this.userMovedAt === -Infinity || (this.autoFit && this.clock - this.userMovedAt > RefitAfterMs)
  }

  /** The workflows drawn as nodes now, by number, not counting one fading out. Exposed for tests. */
  get drawnWorkflows(): number[] {
    return [...this.nodes.values()].filter((node) => node.kind === 'workflow' && node.dying === null).map((node) => Number(node.id.slice(2)))
  }

  /** Centre on one node. */
  focus(ref: NodeRef) {
    const node = this.nodeOf(ref)
    if (!node || node.x === undefined || node.y === undefined) return
    this.userMovedAt = this.clock
    this.target = { x: node.x, y: node.y, k: Math.max(this.camera.k, 1.4) }
  }

  /** One frame; public so a test can drive the scene without requestAnimationFrame. */
  step(now: number) {
    const dt = Math.min(64, Math.max(0, now - this.last))
    this.last = now
    this.clock += dt
    this.syncWorkspace()
    this.sim.tick()
    for (const node of this.nodes.values()) {
      const speed = Math.hypot(node.vx ?? 0, node.vy ?? 0)
      if (speed > 24) {
        node.vx = ((node.vx ?? 0) / speed) * 24
        node.vy = ((node.vy ?? 0) / speed) * 24
      }
    }
    this.separate()
    this.tidy()

    if (this.following) this.frameAll(false)
    const follow = 1 - Math.pow(0.0025, dt / 1000)
    this.camera.x += (this.target.x - this.camera.x) * follow
    this.camera.y += (this.target.y - this.camera.y) * follow
    this.camera.k += (this.target.k - this.camera.k) * follow

    this.easeShown()
    this.drawShown()
  }

  // ---------------------------------------------------------------------------------------------

  private linkDistance(link: SceneLink): number {
    if (link.kind === 'spoke') {
      const team = this.model?.teams.get(this.nodeTeam(link.target))
      const members = team ? [...(this.model?.members.values() ?? [])].filter((m) => m.team === team.id).length : 4
      return 100 + Math.min(90, members * 9)
    }
    if (link.kind === 'tree') {
      const target = typeof link.target === 'object' ? link.target : this.nodes.get(String(link.target))
      if (target?.kind === 'repository') return 150
      // ROOM FOR EVERY LABEL ON THE RING. Eight file names cannot sit 40 px from their project
      // without covering each other; held there, they were pushed apart and pulled back every
      // frame and shook. The ring grows with the number of siblings on it.
      // A tree link runs from the child (source) to its parent (target).
      const parent = idOf(link.target)
      let siblings = 0
      for (const other of this.links.values()) if (other.kind === 'tree' && idOf(other.target) === parent) siblings++
      return 40 + Math.max(0, siblings - 1) * 14
    }
    return 48
  }

  private nodeTeam(end: string | number | SceneNode): string {
    if (typeof end === 'object') return end.team
    return this.nodes.get(String(end))?.team ?? ''
  }

  private ensure(id: string, kind: NodeKind, team: string, r: number, near: SceneNode | null = null): boolean {
    const existing = this.nodes.get(id)
    if (existing) {
      existing.r = r
      return false
    }
    // Born beside what it belongs to and never on top of it: two bodies in one spot repel each
    // other hard enough to fling the new one off the stage.
    const anchor = near ?? this.nodes.get(`t:${team}`) ?? null
    const centre = anchor && anchor.x !== undefined ? { x: anchor.x, y: anchor.y ?? 0 } : this.teamTarget(team)
    const angle = Math.random() * Math.PI * 2
    const reach = kind === 'team' ? 0 : kind === 'workflow' ? 30 : WorkspaceKinds.has(kind) ? 40 : 70
    const spot = { x: centre.x + Math.cos(angle) * reach, y: centre.y + Math.sin(angle) * reach }
    const spread = kind === 'team' ? 0 : 10
    this.nodes.set(id, {
      id,
      kind,
      team,
      r,
      born: this.clock,
      dying: null,
      pulse: -Infinity,
      shakeUntil: 0,
      x: spot.x + (Math.random() - 0.5) * spread,
      y: spot.y + (Math.random() - 0.5) * spread,
    })
    return true
  }

  private restartSim() {
    const live = [...this.nodes.values()]
    this.sim.nodes(live)
    const link = this.sim.force('link') as ReturnType<typeof forceLink<SceneNode, SceneLink>>
    link.links(
      [...this.links.values()].filter(
        (l) => l.kind !== 'touch' && this.nodes.has(String(idOf(l.source))) && this.nodes.has(String(idOf(l.target))),
      ),
    )
    this.sim.alpha(Math.max(this.sim.alpha(), 0.45))
  }

  /** The file changes as the graph holds them now, made nodes and lines of the simulation. */
  private syncWorkspace() {
    const view = this.workspace.view(this.clock, this.fileSettings, this.options.reducedMotion)
    this.workspaceView = view
    this.workspaceNodes.clear()
    const wanted = new Set<string>()
    const wantedLinks = new Set<string>()
    let changed = false

    // Parents come before their children, so each is born beside what it hangs off.
    for (const node of view.nodes) {
      this.workspaceNodes.set(node.id, node)
      wanted.add(node.id)
      changed = this.ensure(node.id, node.kind, node.team, WorkspaceR[node.kind], this.nodes.get(node.parent) ?? null) || changed
      if (!this.nodes.has(node.parent)) continue
      const id = `${node.id}|${node.parent}`
      wantedLinks.add(id)
      if (!this.links.has(id)) {
        this.links.set(id, { id, kind: 'tree', source: node.id, target: node.parent })
        changed = true
      }
    }
    for (const touch of view.touches) {
      const member = `m:${touch.member}`
      if (!this.nodes.has(member) || !wanted.has(touch.node)) continue
      const id = `${member}|${touch.node}`
      wantedLinks.add(id)
      if (!this.links.has(id)) {
        this.links.set(id, { id, kind: 'touch', source: member, target: touch.node })
        changed = true
      }
    }

    // Gone at its time to live, having already faded: no second fade on the way out.
    for (const [id, link] of this.links) {
      if ((link.kind === 'tree' || link.kind === 'touch') && !wantedLinks.has(id)) {
        this.links.delete(id)
        changed = true
      }
    }
    for (const [id, node] of this.nodes) {
      if (WorkspaceKinds.has(node.kind) && !wanted.has(id)) {
        this.nodes.delete(id)
        changed = true
      }
    }
    if (changed) this.restartSim()
  }

  /** A label's width in world units at `scale` of the Monitor's text size. */
  private textWidth(text: string, scale: number, weight = 500): number {
    const ctx = this.ctx
    if (ctx) {
      ctx.font = this.font(scale, weight)
      return ctx.measureText(text).width
    }
    return text.length * this.options.fontPx * scale * 0.6
  }

  /** What a file change's node covers: its mark, and its label to the right of it. */
  private workspaceBox(node: SceneNode): Box | null {
    const ws = this.workspaceNodes.get(node.id)
    if (!ws || node.x === undefined || node.y === undefined) return null
    const r = node.r + 2
    const half = Math.max(r, (this.options.fontPx * 0.62 * 1.3) / 2)
    return { left: node.x - r, right: node.x + r + 5 + this.textWidth(workspaceLabel(ws), 0.62, labelWeight(ws)), top: node.y - half, bottom: node.y + half }
  }

  /**
   * What every member covers: its body with its rings, and its name under it (and the word under
   * that when it is blocked). A file change is never drawn over either. Exposed for tests.
   */
  memberBoxes(): Box[] {
    const boxes: Box[] = []
    for (const node of this.nodes.values()) {
      if (node.dying !== null || node.x === undefined || node.y === undefined) continue
      if (node.kind === 'team') {
        const reach = node.r + 10
        boxes.push({ left: node.x - reach, right: node.x + reach, top: node.y - reach, bottom: node.y + reach })
        continue
      }
      if (node.kind !== 'member') continue
      const member = this.model?.members.get(node.id.slice(2))
      if (!member) continue
      const ring = node.r + 5
      const body = member.manager ? ring + 11 : member.kind === 'plugin' ? node.r * 1.15 + 6 : ring + 3
      boxes.push({ left: node.x - body, right: node.x + body, top: node.y - body, bottom: node.y + body })
      const name = member.name.length > 22 ? `${member.name.slice(0, 21)}…` : member.name
      const width = this.textWidth(name, 0.66)
      const top = node.y + ring + (member.manager ? 9 : 5)
      const lines = member.state === 'blocked' || member.state === 'decision' ? 2.1 : 1.25
      boxes.push({ left: node.x - width / 2, right: node.x + width / 2, top, bottom: top + this.options.fontPx * 0.66 * lines })
    }
    return boxes
  }

  /** The simulation's force for `boxes`: each overlap of file-change labels, with each other or a
   * member and its name, turns into velocity apart, half of the overlap a tick, shared when both move. */
  private pushBoxesApart() {
    const items: { node: SceneNode; box: Box }[] = []
    for (const node of this.nodes.values()) {
      if (!WorkspaceKinds.has(node.kind) || node.fx != null) continue
      const box = this.workspaceBox(node)
      if (box) items.push({ node, box })
    }
    if (items.length === 0) return
    const push = (node: SceneNode, dx: number, dy: number) => {
      node.vx = (node.vx ?? 0) + dx
      node.vy = (node.vy ?? 0) + dy
    }
    for (const box of this.memberBoxes()) {
      for (const item of items) {
        const away = clearOf(item.box, box)
        if (away) push(item.node, away.dx * BoxPush, away.dy * BoxPush)
      }
    }
    for (let i = 0; i < items.length; i++) {
      for (let j = i + 1; j < items.length; j++) {
        const away = clearOf(items[i]!.box, items[j]!.box)
        if (!away) continue
        push(items[i]!.node, (away.dx * BoxPush) / 2, (away.dy * BoxPush) / 2)
        push(items[j]!.node, (-away.dx * BoxPush) / 2, (-away.dy * BoxPush) / 2)
      }
    }
  }

  /**
   * Keep every file change clear of every other and of every member and its name. The simulation
   * spreads them; this makes it certain, moving each the least way out of what it overlaps.
   */
  private separate() {
    const items: { node: SceneNode; box: Box; free: boolean }[] = []
    for (const node of this.nodes.values()) {
      if (!WorkspaceKinds.has(node.kind)) continue
      const box = this.workspaceBox(node)
      if (box) items.push({ node, box, free: node.fx == null })
    }
    if (items.length === 0) return
    const fixed = this.memberBoxes()
    // A node moved out of an overlap also loses its speed: left with it, the simulation carries it
    // straight back on the next tick, this moves it out again, and the node shakes.
    const move = (item: (typeof items)[number], dx: number, dy: number) => {
      item.node.x! += dx
      item.node.y! += dy
      item.node.vx = 0
      item.node.vy = 0
      item.box = { left: item.box.left + dx, right: item.box.right + dx, top: item.box.top + dy, bottom: item.box.bottom + dy }
    }
    for (let pass = 0; pass < SeparatePasses; pass++) {
      let moved = false
      for (let i = 0; i < items.length; i++) {
        const a = items[i]!
        for (const box of fixed) {
          const away = a.free ? clearOf(a.box, box) : null
          if (!away) continue
          move(a, away.dx, away.dy)
          moved = true
        }
        for (let j = i + 1; j < items.length; j++) {
          const b = items[j]!
          const away = a.free || b.free ? clearOf(a.box, b.box) : null
          if (!away) continue
          const share = a.free && b.free ? 0.5 : a.free ? 1 : 0
          if (share > 0) move(a, away.dx * share, away.dy * share)
          if (share < 1) move(b, -away.dx * (1 - share), -away.dy * (1 - share))
          moved = true
        }
      }
      if (!moved) break
    }

    // THE PASSES CAN STALL in a tight cluster, each move pushing one box into another. Whatever
    // still overlaps is then placed at the nearest clear spot, searched outward in a spiral, so
    // "never covers another" holds every frame rather than most of them.
    const clear = (box: Box, self: (typeof items)[number]) =>
      fixed.every((other) => !clearOf(box, other)) && items.every((other) => other === self || !clearOf(box, other.box))
    for (const item of items) {
      if (!item.free || clear(item.box, item)) continue
      const step = WorkspaceGap + (item.box.bottom - item.box.top) / 2
      for (let ring = 1; ring <= SpiralRings; ring++) {
        const spot = SpiralTurns.map((turn) => ({ dx: Math.cos(turn) * step * ring, dy: Math.sin(turn) * step * ring }))
          .map(({ dx, dy }) => ({ dx, dy, box: { left: item.box.left + dx, right: item.box.right + dx, top: item.box.top + dy, bottom: item.box.bottom + dy } }))
          .find((candidate) => clear(candidate.box, item))
        if (spot) {
          move(item, spot.dx, spot.dy)
          break
        }
      }
    }
  }

  private easeShown() {
    for (const node of this.nodes.values()) {
      if (!WorkspaceKinds.has(node.kind) || node.x === undefined || node.y === undefined) continue
      if (node.shownX === undefined || node.shownY === undefined || this.options.reducedMotion) {
        node.shownX = node.x
        node.shownY = node.y
        continue
      }
      node.shownX += (node.x - node.shownX) * ShownEase
      node.shownY += (node.y - node.shownY) * ShownEase
    }
  }

  /** Draws with each file change at its shown position, then puts its simulated one back. */
  private drawShown() {
    const held: [SceneNode, number, number][] = []
    for (const node of this.nodes.values()) {
      if (node.shownX === undefined || node.shownY === undefined || node.x === undefined || node.y === undefined) continue
      held.push([node, node.x, node.y])
      node.x = node.shownX
      node.y = node.shownY
    }
    try {
      this.draw()
    } finally {
      for (const [node, x, y] of held) {
        node.x = x
        node.y = y
      }
    }
  }

  /** The file changes as drawn now: where each node is, what it covers, its lines and its commit. Exposed for tests. */
  shownWorkspace(): ShownWorkspace {
    const nodes: ShownWorkspaceNode[] = []
    for (const ws of this.workspaceView.nodes) {
      const node = this.nodes.get(ws.id)
      const box = node ? this.workspaceBox(node) : null
      if (!node || !box) continue
      nodes.push({ ...ws, x: node.x!, y: node.y!, shownX: node.shownX ?? node.x!, shownY: node.shownY ?? node.y!, box })
    }
    const touches = this.workspaceView.touches
      .filter((touch) => this.links.has(`m:${touch.member}|${touch.node}`))
      .map((touch) => ({ member: touch.member, node: touch.node, alpha: touch.alpha }))
    const flashes = this.workspaceView.flashes.map((flash) => ({ node: flash.node, sha: flash.sha, subject: flash.subject }))
    return { nodes, touches, flashes }
  }

  /** Each team's home on the stage: one in the middle, more around an ellipse. */
  private layoutTeams() {
    const teams = this.model ? [...this.model.teams.keys()] : []
    this.teamTargets.clear()
    const n = teams.length
    const rx = Math.max(140, 150 + n * 34)
    const ry = rx * 0.62
    teams.forEach((team, i) => {
      if (n === 1) this.teamTargets.set(team, { x: 0, y: 0 })
      else {
        const angle = (i / n) * Math.PI * 2 - Math.PI / 2
        this.teamTargets.set(team, { x: Math.cos(angle) * rx, y: Math.sin(angle) * ry })
      }
    })
  }

  private teamTarget(team: string): { x: number; y: number } {
    return this.teamTargets.get(team) ?? { x: 0, y: 0 }
  }

  private nodeOf(ref: NodeRef): SceneNode | null {
    switch (ref.kind) {
      case 'member':
        return this.nodes.get(`m:${ref.key}`) ?? null
      case 'team':
        return this.nodes.get(`t:${ref.id}`) ?? null
      case 'workflow':
        return this.nodes.get(`w:${ref.id}`) ?? null
      default:
        return null
    }
  }

  private posOf(ref: NodeRef): { x: number; y: number } | null {
    if (ref.kind === 'origin') return this.originPos(ref.team, ref.origin)
    const node = this.nodeOf(ref)
    if (!node || node.x === undefined || node.y === undefined) {
      // A workflow drawn before its node exists starts from its team.
      if (ref.kind === 'workflow') {
        const team = this.model?.workflows.get(ref.id)?.team
        return team ? this.posOf({ kind: 'team', id: team }) : null
      }
      return null
    }
    return { x: node.x, y: node.y }
  }

  /** Where work arriving from outside a team appears: beyond the team, away from the middle. */
  private originPos(team: string, origin: Origin): { x: number; y: number } | null {
    const node = this.nodes.get(`t:${team}`)
    if (!node || node.x === undefined || node.y === undefined) return null
    const len = Math.hypot(node.x, node.y)
    // Away from the middle; a team alone in the middle takes its work from the left.
    const base = len < 40 ? Math.PI : Math.atan2(node.y, node.x)
    const offsets: Record<Origin, number> = { person: -0.5, backlog: 0, trigger: 0.5, site: 0.95 }
    const angle = base + offsets[origin]
    const reach = 175 + node.r * 2
    return { x: node.x + Math.cos(angle) * reach, y: node.y + Math.sin(angle) * reach }
  }

  private touch(ref: NodeRef) {
    const node = this.nodeOf(ref)
    if (node) node.pulse = this.clock
  }

  private cap() {
    const total = () => this.comets.length + this.bursts.length + this.ripples.length + this.floaters.length
    while (total() > MaxEffects) {
      const lists = [this.ripples, this.floaters, this.bursts, this.comets]
      const longest = lists.reduce((a, b) => (b.length > a.length ? b : a))
      longest.shift()
    }
  }

  private tidy() {
    const now = this.clock
    this.comets = this.comets.filter((c) => now - c.start < c.duration + 400)
    this.bursts = this.bursts.filter((b) => now - b.start < BurstMs)
    this.ripples = this.ripples.filter((r) => now - r.start < RippleMs)
    this.floaters = this.floaters.filter((f) => now - f.start < FloatMs)
    this.resources = this.resources.filter((mark) => now - mark.start < mark.ttl)
    this.steps = this.steps.filter((mark) => now - mark.start < StepMs)
    let removed = false
    for (const [id, node] of this.nodes) {
      if (node.dying !== null && now - node.dying > DyingMs) {
        this.nodes.delete(id)
        removed = true
      }
    }
    if (removed) {
      for (const [id, link] of this.links) {
        if (!this.nodes.has(String(idOf(link.source))) || !this.nodes.has(String(idOf(link.target)))) this.links.delete(id)
      }
      this.restartSim()
    }
  }

  private frameAll(immediate: boolean) {
    const live = [...this.nodes.values()].filter((n) => n.dying === null && n.x !== undefined)
    if (live.length === 0) {
      this.target = { x: 0, y: 0, k: 1 }
    } else {
      let minX = Infinity
      let minY = Infinity
      let maxX = -Infinity
      let maxY = -Infinity
      // Where outside work arrives is framed too while it shows.
      for (const key of this.originUsed.keys()) {
        const split = key.lastIndexOf(':')
        const pos = this.originPos(key.slice(0, split), key.slice(split + 1) as Origin)
        if (!pos) continue
        minX = Math.min(minX, pos.x - 40)
        maxX = Math.max(maxX, pos.x + 40)
        minY = Math.min(minY, pos.y - 30)
        maxY = Math.max(maxY, pos.y + 40)
      }
      for (const n of live) {
        const pad = n.kind === 'team' ? n.r + 90 : n.r + 40
        minX = Math.min(minX, n.x! - pad)
        maxX = Math.max(maxX, n.x! + pad)
        minY = Math.min(minY, n.y! - pad)
        maxY = Math.max(maxY, n.y! + pad)
      }
      // The figures across the top and the legend along the bottom cover the stage there.
      const top = Math.min(140, this.height * 0.22)
      const bottom = Math.min(44, this.height * 0.08)
      const usable = Math.max(120, this.height - top - bottom)
      const k = Math.min(1.6, Math.max(0.2, Math.min(this.width / (maxX - minX), usable / (maxY - minY))))
      this.target = { x: (minX + maxX) / 2, y: (minY + maxY) / 2 - (top - bottom) / 2 / k, k }
    }
    if (immediate) this.camera = { ...this.target }
  }

  private toWorld(sx: number, sy: number) {
    return {
      x: (sx - this.width / 2) / this.camera.k + this.camera.x,
      y: (sy - this.height / 2) / this.camera.k + this.camera.y,
    }
  }

  private hit(sx: number, sy: number): SceneNode | null {
    const { x, y } = this.toWorld(sx, sy)
    let best: SceneNode | null = null
    let bestD = Infinity
    for (const node of this.nodes.values()) {
      if (node.dying !== null || node.x === undefined || node.y === undefined) continue
      const d = Math.hypot(node.x - x, node.y - y)
      const reach = node.r + (node.kind === 'workflow' ? 8 : 6)
      if (d < reach && d < bestD) {
        best = node
        bestD = d
      }
    }
    return best
  }

  private readonly onPointerMove = (event: PointerEvent) => {
    const rect = this.canvas.getBoundingClientRect()
    const sx = event.clientX - rect.left
    const sy = event.clientY - rect.top
    if (this.dragging) {
      const { x, y } = this.toWorld(sx, sy)
      this.dragging.fx = x
      this.dragging.fy = y
      this.sim.alpha(Math.max(this.sim.alpha(), 0.2))
      return
    }
    if (this.panning) {
      this.target.x = this.panning.cx - (sx - this.panning.x) / this.camera.k
      this.target.y = this.panning.cy - (sy - this.panning.y) / this.camera.k
      this.camera.x = this.target.x
      this.camera.y = this.target.y
      this.userMovedAt = this.clock
      return
    }
    const node = this.hit(sx, sy)
    this.hovered = node
    this.canvas.style.cursor = node ? 'pointer' : 'grab'
    this.options.onHover?.(node ? this.describe(node, sx, sy) : null)
  }

  private readonly onPointerDown = (event: PointerEvent) => {
    const rect = this.canvas.getBoundingClientRect()
    const sx = event.clientX - rect.left
    const sy = event.clientY - rect.top
    const node = this.hit(sx, sy)
    this.canvas.setPointerCapture?.(event.pointerId)
    if (node) {
      this.dragging = node
      node.fx = node.x
      node.fy = node.y
      const ref = refOf(node)
      if (ref) this.options.onPick?.(ref)
    } else {
      this.panning = { x: sx, y: sy, cx: this.camera.x, cy: this.camera.y }
      this.canvas.style.cursor = 'grabbing'
    }
  }

  private readonly onPointerUp = () => {
    if (this.dragging) {
      this.dragging.fx = null
      this.dragging.fy = null
      this.dragging = null
    }
    this.panning = null
    this.canvas.style.cursor = this.hovered ? 'pointer' : 'grab'
  }

  private readonly onPointerLeave = () => {
    this.hovered = null
    this.options.onHover?.(null)
    this.onPointerUp()
  }

  private readonly onWheel = (event: WheelEvent) => {
    event.preventDefault()
    const rect = this.canvas.getBoundingClientRect()
    const sx = event.clientX - rect.left
    const sy = event.clientY - rect.top
    const before = this.toWorld(sx, sy)
    const k = Math.min(4, Math.max(0.15, this.camera.k * Math.exp(-event.deltaY * 0.0015)))
    this.camera.k = k
    const after = this.toWorld(sx, sy)
    this.camera.x += before.x - after.x
    this.camera.y += before.y - after.y
    this.target = { ...this.camera }
    this.userMovedAt = this.clock
  }

  private readonly onDoubleClick = () => this.fit()

  /** What hovering a node by its id would show, at its own place. Exposed for tests. */
  describeNode(id: string): HoverInfo | null {
    const node = this.nodes.get(id)
    return node ? this.describe(node, node.x ?? 0, node.y ?? 0) : null
  }

  private describe(node: SceneNode, sx: number, sy: number): HoverInfo | null {
    const model = this.model
    if (!model) return null
    if (WorkspaceKinds.has(node.kind)) {
      const ws = this.workspaceNodes.get(node.id)
      if (!ws) return null
      const names = ws.members.map((key) => model.members.get(key)?.name ?? key.slice(key.indexOf('/') + 1)).join(', ')
      const by = names !== '' ? `by ${names}` : ''
      const hue = model.teams.get(ws.team)?.hue ?? 200
      const files = `${ws.files} ${ws.files === 1 ? 'file' : 'files'} changed`
      if (ws.kind === 'file') {
        return {
          kind: 'file',
          title: ws.label,
          subtitle: ws.path,
          lines: [`${ws.action} · ${ws.project !== '' ? `${ws.project} in ` : ''}${ws.repo}`, by].filter((line) => line !== ''),
          colour: ws.action ? ActionColours[ws.action] : Tones.file,
          x: sx,
          y: sy,
        }
      }
      return {
        kind: ws.kind,
        title: ws.label,
        subtitle: ws.kind === 'project' ? `project in ${ws.repo}` : 'repository',
        lines: [files, by].filter((line) => line !== ''),
        colour: hsl(hue, 80, 70),
        x: sx,
        y: sy,
      }
    }
    if (node.kind === 'member') {
      const member = model.members.get(node.id.slice(2))
      if (!member) return null
      const team = model.teams.get(member.team)
      const lines = [
        `${stateWords(member.state)}${member.queued > 0 ? ` · ${member.queued} queued` : ''}`,
        member.runs > 0 ? `${member.runs} run${member.runs === 1 ? '' : 's'} · ${compact(member.tokens)} billable tokens` : '',
        // THE NAME, THEN THE NUMBER: what the member works on, and what agents quote.
        member.workflow !== null ? `workflow ${workflowNameLine(model.workflows.get(member.workflow) ?? { id: member.workflow, title: '' })}` : '',
        member.status !== '' ? `“${member.status.slice(0, 140)}”` : '',
        ...member.wakes.map((wake) => wakeLine({ dueAt: new Date(wake.dueAt).toISOString(), note: wake.note.slice(0, 140) })),
      ].filter((line) => line !== '')
      return {
        kind: 'member',
        title: member.name,
        subtitle: `${team?.name ?? member.team} · ${member.manager ? 'Manager' : member.kind === 'plugin' ? 'plugin member' : 'member'}`,
        lines,
        colour: StateColours[member.state],
        x: sx,
        y: sy,
      }
    }
    if (node.kind === 'team') {
      const team = model.teams.get(node.team)
      if (!team) return null
      const members = [...model.members.values()].filter((m) => m.team === team.id)
      const running = members.filter((m) => m.state === 'running').length
      const open = [...model.workflows.values()].filter((w) => w.team === team.id && w.ending === 'open').length
      return {
        kind: 'team',
        title: team.name,
        subtitle: 'team',
        lines: [
          `${members.length} member${members.length === 1 ? '' : 's'} · ${running} running`,
          `${open} open workflow${open === 1 ? '' : 's'}`,
        ],
        colour: hsl(team.hue, 80, 65),
        x: sx,
        y: sy,
      }
    }
    const workflow = model.workflows.get(Number(node.id.slice(2)))
    if (!workflow) return null
    const names = [...workflow.members].map((key) => model.members.get(key)?.name ?? key.split('/')[1]).join(', ')
    return {
      kind: 'workflow',
      title: workflow.title !== '' ? workflow.title : `Workflow ${workflow.id}`,
      subtitle: `workflow ${workflow.id} · ${workflow.ending === 'open' ? 'open' : workflow.ending}`,
      lines: [`${workflow.events} rows`, names !== '' ? names : ''].filter((line) => line !== ''),
      colour: Tones.workflow,
      x: sx,
      y: sy,
    }
  }

  // --- drawing ---------------------------------------------------------------------------------

  private draw() {
    const ctx = this.ctx
    if (!ctx) return
    const { width, height, dpr } = this
    const now = this.clock
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
    ctx.globalCompositeOperation = 'source-over'

    this.drawGround(ctx, width, height, now)

    ctx.save()
    ctx.translate(width / 2, height / 2)
    ctx.scale(this.camera.k, this.camera.k)
    ctx.translate(-this.camera.x, -this.camera.y)

    const focus = this.focusSet()
    this.drawTerritories(ctx, now)
    this.drawLinks(ctx, now, focus)
    this.drawOrigins(ctx, now)
    ctx.globalCompositeOperation = glowMode()
    this.drawRipples(ctx, now)
    this.drawComets(ctx, now)
    this.drawBursts(ctx, now)
    ctx.globalCompositeOperation = 'source-over'
    this.drawNodes(ctx, now, focus)
    this.drawCommits(ctx)
    this.drawResources(ctx)
    this.drawSteps(ctx)
    this.drawTeamNames(ctx, now, focus)
    this.drawFloaters(ctx, now)
    ctx.restore()
  }

  private font(scale: number, weight = 500): string {
    return `${weight} ${Math.round(this.options.fontPx * scale)}px ${this.options.fontFamily}`
  }

  private drawGround(ctx: CanvasRenderingContext2D, width: number, height: number, now: number) {
    ctx.fillStyle = Screen.ground
    ctx.fillRect(0, 0, width, height)

    const glow = ctx.createRadialGradient(width / 2, height / 2, 0, width / 2, height / 2, Math.max(width, height) * 0.7)
    glow.addColorStop(0, alphaHex(Tones.tell, 0.07))
    glow.addColorStop(0.5, alphaHex(Tones.progress, 0.035))
    glow.addColorStop(1, alphaHex(Screen.ground, 0))
    ctx.fillStyle = glow
    ctx.fillRect(0, 0, width, height)

    // A dot grid in world space, so panning and zooming feel like moving over a surface.
    const k = this.camera.k
    const step = 36 * k
    if (step > 9) {
      const ox = (width / 2 - this.camera.x * k) % step
      const oy = (height / 2 - this.camera.y * k) % step
      ctx.fillStyle = alphaHex(Screen.inkDim, 0.09)
      for (let x = ox; x < width; x += step) {
        for (let y = oy; y < height; y += step) ctx.fillRect(x, y, 1.2, 1.2)
      }
    }

    // A slow radar sweep from the middle: the screen is alive even when nothing is happening.
    if (!this.options.reducedMotion) {
      const angle = (now / 9000) * Math.PI * 2
      const reach = Math.hypot(width, height) / 2
      ctx.save()
      ctx.translate(width / 2, height / 2)
      ctx.rotate(angle)
      const sweep = ctx.createLinearGradient(0, 0, reach, 0)
      sweep.addColorStop(0, alphaHex(Tones.tell, 0.05))
      sweep.addColorStop(1, alphaHex(Tones.tell, 0))
      ctx.fillStyle = sweep
      ctx.beginPath()
      ctx.moveTo(0, 0)
      ctx.arc(0, 0, reach, -0.22, 0)
      ctx.closePath()
      ctx.fill()
      ctx.restore()
    }
  }

  private focusSet(): Set<string> | null {
    const hovered = this.hovered
    if (!hovered) return null
    const set = new Set<string>([hovered.id])
    for (const link of this.links.values()) {
      const s = String(idOf(link.source))
      const t = String(idOf(link.target))
      if (s === hovered.id) set.add(t)
      if (t === hovered.id) set.add(s)
    }
    if (hovered.kind === 'team') for (const node of this.nodes.values()) if (node.team === hovered.team) set.add(node.id)
    return set
  }

  private appear(node: SceneNode, now: number): number {
    const born = Math.min(1, (now - node.born) / BornMs)
    const dying = node.dying === null ? 1 : Math.max(0, 1 - (now - node.dying) / DyingMs)
    return ease(born) * dying
  }

  private drawTerritories(ctx: CanvasRenderingContext2D, now: number) {
    const model = this.model
    if (!model) return
    for (const node of this.nodes.values()) {
      if (node.kind !== 'team' || node.x === undefined || node.y === undefined) continue
      const team = model.teams.get(node.team)
      if (!team) continue
      const a = this.appear(node, now)
      const members = [...model.members.values()].filter((m) => m.team === team.id)
      const busy = members.some((m) => m.state === 'running')
      const reach = 90 + Math.min(150, members.length * 18)
      const breathe = busy && !this.options.reducedMotion ? 1 + Math.sin(now / 700) * 0.04 : 1
      const g = ctx.createRadialGradient(node.x, node.y, 0, node.x, node.y, reach * breathe)
      g.addColorStop(0, hsl(team.hue, 85, 55, 0.2 * a))
      g.addColorStop(0.55, hsl(team.hue, 85, 50, 0.07 * a))
      g.addColorStop(1, hsl(team.hue, 85, 50, 0))
      ctx.fillStyle = g
      ctx.beginPath()
      ctx.arc(node.x, node.y, reach * breathe, 0, Math.PI * 2)
      ctx.fill()
    }
  }

  private drawLinks(ctx: CanvasRenderingContext2D, now: number, focus: Set<string> | null) {
    const model = this.model
    if (!model) return
    for (const link of this.links.values()) {
      const s = typeof link.source === 'object' ? link.source : this.nodes.get(String(link.source))
      const t = typeof link.target === 'object' ? link.target : this.nodes.get(String(link.target))
      if (!s || !t || s.x === undefined || t.x === undefined || s.y === undefined || t.y === undefined) continue
      const hue = model.teams.get(s.team)?.hue ?? 200
      const a = Math.min(this.appear(s, now), this.appear(t, now))
      const dim = focus && !(focus.has(s.id) && focus.has(t.id)) ? 0.25 : 1
      if (link.kind === 'spoke') {
        ctx.strokeStyle = hsl(hue, 60, 65, 0.2 * a * dim)
        ctx.lineWidth = 1
        ctx.setLineDash([])
      } else if (link.kind === 'tree') {
        const shown = this.workspaceNodes.get(s.id)?.alpha ?? 0
        ctx.strokeStyle = hsl(hue, 50, 65, 0.4 * a * dim * shown)
        ctx.lineWidth = 1
        ctx.setLineDash([])
      } else if (link.kind === 'touch') {
        const ws = this.workspaceNodes.get(t.id)
        const colour = ws?.action ? ActionColours[ws.action] : Tones.file
        ctx.strokeStyle = alphaHex(colour, 0.5 * a * dim * (ws?.alpha ?? 0))
        ctx.lineWidth = 1
        ctx.setLineDash([3, 4])
        ctx.lineDashOffset = 0
      } else {
        const workflow = model.workflows.get(Number(t.id.slice(2)))
        const open = workflow?.ending === 'open'
        ctx.strokeStyle = open ? hsl(hue, 90, 70, 0.55 * a * dim) : hsl(hue, 30, 60, 0.18 * a * dim)
        ctx.lineWidth = open ? 1.6 : 1
        ctx.setLineDash(open ? [5, 6] : [2, 6])
        ctx.lineDashOffset = this.options.reducedMotion ? 0 : -now / 40
      }
      ctx.beginPath()
      ctx.moveTo(s.x, s.y)
      ctx.lineTo(t.x, t.y)
      ctx.stroke()
    }
    ctx.setLineDash([])
  }

  private drawOrigins(ctx: CanvasRenderingContext2D, now: number) {
    const glyphs: Record<Origin, string> = { person: 'you', backlog: 'backlog', trigger: 'schedule', site: 'site' }
    for (const [key, used] of this.originUsed) {
      const age = now - used
      if (age > OriginMs) {
        this.originUsed.delete(key)
        continue
      }
      const split = key.lastIndexOf(':')
      const team = key.slice(0, split)
      const origin = key.slice(split + 1) as Origin
      const pos = this.originPos(team, origin)
      if (!pos) continue
      const a = Math.min(1, age / 250) * Math.min(1, (OriginMs - age) / 1500)
      const colour = origin === 'person' ? Tones.tell : origin === 'backlog' ? Tones.card : origin === 'trigger' ? Tones.plugin : Tones.push
      ctx.save()
      ctx.globalAlpha = a
      ctx.strokeStyle = colour
      ctx.lineWidth = 1.5
      ctx.fillStyle = alphaHex(Screen.ground, 0.85)
      ctx.beginPath()
      if (origin === 'person') ctx.arc(pos.x, pos.y, 10, 0, Math.PI * 2)
      else ctx.roundRect(pos.x - 10, pos.y - 10, 20, 20, 4)
      ctx.fill()
      ctx.stroke()
      ctx.fillStyle = colour
      ctx.font = this.font(0.62, 600)
      ctx.textAlign = 'center'
      ctx.textBaseline = 'top'
      ctx.fillText(glyphs[origin].toUpperCase(), pos.x, pos.y + 14)
      ctx.beginPath()
      if (origin === 'person') {
        ctx.arc(pos.x, pos.y - 3, 3, 0, Math.PI * 2)
        ctx.moveTo(pos.x - 5.5, pos.y + 6)
        ctx.arc(pos.x, pos.y + 6, 5.5, Math.PI, 0)
      } else if (origin === 'trigger') {
        ctx.arc(pos.x, pos.y, 5, 0, Math.PI * 2)
        ctx.moveTo(pos.x, pos.y)
        ctx.lineTo(pos.x, pos.y - 4)
        ctx.moveTo(pos.x, pos.y)
        ctx.lineTo(pos.x + 3, pos.y + 1)
      } else {
        for (const dy of [-4, 0, 4]) {
          ctx.moveTo(pos.x - 5, pos.y + dy)
          ctx.lineTo(pos.x + 5, pos.y + dy)
        }
      }
      ctx.strokeStyle = colour
      ctx.stroke()
      ctx.restore()
    }
  }

  /** A member's pending wake: a small clock face, ground-filled, its hands at ten past ten. */
  private drawWakeClock(ctx: CanvasRenderingContext2D, x: number, y: number) {
    const r = 5
    ctx.fillStyle = Screen.ground
    ctx.strokeStyle = Tones.tell
    ctx.lineWidth = 1.4
    ctx.beginPath()
    ctx.arc(x, y, r, 0, Math.PI * 2)
    ctx.fill()
    ctx.stroke()
    ctx.lineWidth = 1.2
    ctx.beginPath()
    ctx.moveTo(x, y)
    ctx.lineTo(x, y - r * 0.7)
    ctx.moveTo(x, y)
    ctx.lineTo(x + r * 0.55, y + r * 0.1)
    ctx.stroke()
  }

  private drawRipples(ctx: CanvasRenderingContext2D, now: number) {
    for (const ripple of this.ripples) {
      const pos = this.posOf(ripple.at)
      if (!pos) continue
      const t = (now - ripple.start) / RippleMs
      const node = this.nodeOf(ripple.at)
      const base = node?.r ?? 10
      ctx.strokeStyle = alphaHex(Tones[ripple.tone], (1 - t) * 0.8)
      ctx.lineWidth = 2 * (1 - t) + 0.5
      ctx.beginPath()
      ctx.arc(pos.x, pos.y, base + 4 + ease(t) * 34, 0, Math.PI * 2)
      ctx.stroke()
    }
  }

  private drawComets(ctx: CanvasRenderingContext2D, now: number) {
    for (const comet of this.comets) {
      const from = this.posOf(comet.from)
      const to = this.posOf(comet.to)
      if (!from || !to) continue
      const raw = (now - comet.start) / comet.duration
      const colour = Tones[comet.tone]
      if (raw >= 1) {
        // Arrived: a flash at the target, fading out over the comet's last 400 ms.
        const fade = 1 - (now - comet.start - comet.duration) / 400
        if (fade > 0) {
          const g = ctx.createRadialGradient(to.x, to.y, 0, to.x, to.y, 26)
          g.addColorStop(0, alphaHex(colour, 0.7 * fade))
          g.addColorStop(1, alphaHex(colour, 0))
          ctx.fillStyle = g
          ctx.beginPath()
          ctx.arc(to.x, to.y, 26, 0, Math.PI * 2)
          ctx.fill()
        }
        continue
      }
      const t = ease(raw)
      const mx = (from.x + to.x) / 2 - (to.y - from.y) * comet.bend
      const my = (from.y + to.y) / 2 + (to.x - from.x) * comet.bend
      const x = (1 - t) * (1 - t) * from.x + 2 * (1 - t) * t * mx + t * t * to.x
      const y = (1 - t) * (1 - t) * from.y + 2 * (1 - t) * t * my + t * t * to.y
      comet.trail.push({ x, y })
      if (comet.trail.length > 18) comet.trail.shift()

      for (let i = 1; i < comet.trail.length; i++) {
        const a = comet.trail[i - 1]!
        const b = comet.trail[i]!
        const f = i / comet.trail.length
        ctx.strokeStyle = alphaHex(colour, f * 0.7)
        ctx.lineWidth = f * 4
        ctx.lineCap = 'round'
        ctx.beginPath()
        ctx.moveTo(a.x, a.y)
        ctx.lineTo(b.x, b.y)
        ctx.stroke()
      }
      const head = ctx.createRadialGradient(x, y, 0, x, y, 12)
      head.addColorStop(0, Screen.ink)
      head.addColorStop(0.25, alphaHex(colour, 0.95))
      head.addColorStop(1, alphaHex(colour, 0))
      ctx.fillStyle = head
      ctx.beginPath()
      ctx.arc(x, y, 12, 0, Math.PI * 2)
      ctx.fill()

      if (comet.label && this.comets.length <= 6 && raw > 0.08 && raw < 0.9) {
        ctx.globalCompositeOperation = 'source-over'
        ctx.font = this.font(0.62, 500)
        ctx.textAlign = 'center'
        ctx.textBaseline = 'bottom'
        ctx.fillStyle = alphaHex(colour, Math.min(1, Math.sin(raw * Math.PI) * 1.6))
        ctx.fillText(comet.label, x, y - 10)
        ctx.globalCompositeOperation = glowMode()
      }
    }
  }

  private drawBursts(ctx: CanvasRenderingContext2D, now: number) {
    for (const burst of this.bursts) {
      const pos = this.posOf(burst.at)
      if (!pos) continue
      const elapsed = now - burst.start
      const t = elapsed / BurstMs
      const colour = Tones[burst.tone]
      const node = this.nodeOf(burst.at)
      const base = node?.r ?? 10

      ctx.strokeStyle = alphaHex(colour, (1 - t) * 0.9)
      ctx.lineWidth = (burst.big ? 3 : 2) * (1 - t)
      ctx.beginPath()
      ctx.arc(pos.x, pos.y, base + ease(Math.min(1, t * 1.4)) * (burst.big ? 70 : 40), 0, Math.PI * 2)
      ctx.stroke()
      if (burst.big) {
        ctx.beginPath()
        ctx.arc(pos.x, pos.y, base + ease(Math.min(1, t * 1.1)) * 42, 0, Math.PI * 2)
        ctx.stroke()
      }

      const flash = ctx.createRadialGradient(pos.x, pos.y, 0, pos.x, pos.y, base * 3)
      flash.addColorStop(0, alphaHex(colour, Math.max(0, 0.6 - t)))
      flash.addColorStop(1, alphaHex(colour, 0))
      ctx.fillStyle = flash
      ctx.beginPath()
      ctx.arc(pos.x, pos.y, base * 3, 0, Math.PI * 2)
      ctx.fill()

      for (const spark of burst.sparks) {
        const d = base + spark.speed * elapsed
        const sx = pos.x + Math.cos(spark.angle) * d
        const sy = pos.y + Math.sin(spark.angle) * d + 0.00002 * elapsed * elapsed
        ctx.fillStyle = alphaHex(colour, 1 - t)
        ctx.beginPath()
        ctx.arc(sx, sy, spark.size * (1 - t * 0.6), 0, Math.PI * 2)
        ctx.fill()
      }
    }
  }

  private drawNodes(ctx: CanvasRenderingContext2D, now: number, focus: Set<string> | null) {
    const model = this.model
    if (!model) return
    const order: NodeKind[] = ['workflow', 'member', 'repository', 'project', 'file']
    for (const kind of order) {
      for (const node of this.nodes.values()) {
        if (node.kind !== kind || node.x === undefined || node.y === undefined) continue
        const ws = this.workspaceNodes.get(node.id)
        const a = this.appear(node, now) * (focus && !focus.has(node.id) ? 0.3 : 1) * (ws ? ws.alpha : 1)
        if (a <= 0.01) continue
        let x = node.x
        let y = node.y
        if (node.shakeUntil > now) {
          const s = (node.shakeUntil - now) / 500
          x += Math.sin(now / 18) * 5 * s
          y += Math.cos(now / 23) * 3 * s
        }
        ctx.save()
        ctx.globalAlpha = a
        if (kind === 'member') this.drawMember(ctx, node, x, y, now)
        else if (ws) this.drawWorkspaceNode(ctx, node, ws, x, y)
        else this.drawWorkflow(ctx, node, x, y, now)
        ctx.restore()
      }
    }
  }

  /**
   * Each team is its name on an oval at the team's place, drawn last so no member is ever drawn over
   * one: the oval is the team's own shape, the root its members orbit.
   */
  private drawTeamNames(ctx: CanvasRenderingContext2D, now: number, focus: Set<string> | null) {
    const model = this.model
    if (!model) return
    ctx.font = this.font(0.82, 700)
    ctx.textAlign = 'center'
    ctx.textBaseline = 'middle'
    for (const node of this.nodes.values()) {
      if (node.kind !== 'team' || node.x === undefined || node.y === undefined) continue
      const team = model.teams.get(node.team)
      if (!team) continue
      const a = this.appear(node, now) * (focus && !focus.has(node.id) ? 0.4 : 1)
      const grow = 0.6 + 0.4 * ease(Math.min(1, (now - node.born) / BornMs))
      const label = team.name.length > 34 ? `${team.name.slice(0, 33)}…` : team.name
      const width = (ctx.measureText(label).width + 28) * grow
      const height = this.options.fontPx * 2 * grow
      let { x, y } = node
      if (node.shakeUntil > now) {
        const s = (node.shakeUntil - now) / 500
        x += Math.sin(now / 18) * 5 * s
        y += Math.cos(now / 23) * 3 * s
      }
      ctx.globalAlpha = a
      ctx.shadowColor = hsl(team.hue, 90, 60, 0.9)
      ctx.shadowBlur = 24
      const g = ctx.createLinearGradient(x, y - height / 2, x, y + height / 2)
      g.addColorStop(0, hsl(team.hue, 75, 34, 0.96))
      g.addColorStop(1, hsl(team.hue, 80, 18, 0.96))
      ctx.fillStyle = g
      ctx.beginPath()
      ctx.roundRect(x - width / 2, y - height / 2, width, height, height / 2)
      ctx.fill()
      ctx.shadowBlur = 0
      ctx.strokeStyle = hsl(team.hue, 90, 70, 0.85)
      ctx.lineWidth = 1.5
      ctx.stroke()
      ctx.fillStyle = hsl(team.hue, 90, 88)
      ctx.fillText(label, x, y + 0.5)
    }
    ctx.globalAlpha = 1
  }

  private drawMember(ctx: CanvasRenderingContext2D, node: SceneNode, x: number, y: number, now: number) {
    const member = this.model?.members.get(node.id.slice(2))
    const team = member ? this.model?.teams.get(member.team) : undefined
    if (!member || !team) return
    const grow = ease(Math.min(1, (now - node.born) / BornMs))
    const r = node.r * (0.4 + 0.6 * grow)
    const state = StateColours[member.state]
    const motion = this.options.reducedMotion ? 0 : 1

    // A pulse when a row touched it.
    const since = now - node.pulse
    if (since < 600) {
      const p = since / 600
      ctx.fillStyle = alphaHex(state, 0.35 * (1 - p))
      ctx.beginPath()
      ctx.arc(x, y, r + 6 + p * 10, 0, Math.PI * 2)
      ctx.fill()
    }

    // The body: a disc, or a diamond for a plugin member.
    ctx.shadowColor = member.state === 'idle' ? alphaHex(Screen.ground, 0) : state
    ctx.shadowBlur = member.state === 'idle' ? 0 : 16
    const body = ctx.createRadialGradient(x - r * 0.35, y - r * 0.35, r * 0.1, x, y, r)
    body.addColorStop(0, hsl(team.hue, 70, member.state === 'idle' ? 38 : 52))
    body.addColorStop(1, hsl(team.hue, 60, member.state === 'idle' ? 16 : 22))
    ctx.fillStyle = body
    ctx.beginPath()
    if (member.kind === 'plugin') {
      ctx.moveTo(x, y - r * 1.15)
      ctx.lineTo(x + r * 1.15, y)
      ctx.lineTo(x, y + r * 1.15)
      ctx.lineTo(x - r * 1.15, y)
      ctx.closePath()
    } else {
      ctx.arc(x, y, r, 0, Math.PI * 2)
    }
    ctx.fill()
    ctx.shadowBlur = 0

    // An agent wears its avatar over the body, cut to the disc: the tile its board and Kanban cards show.
    const avatar = member.kind === 'plugin' ? -1 : memberAvatar(member.team, member.id)
    const sheet = avatarSheet.image
    if (avatar >= 0 && sheet) {
      const src = tileSource(avatar, { cols: avatarSheet.cols, rows: avatarSheet.rows, width: sheet.naturalWidth, height: sheet.naturalHeight })
      ctx.save()
      ctx.beginPath()
      ctx.arc(x, y, r, 0, Math.PI * 2)
      ctx.clip()
      ctx.globalAlpha = member.state === 'idle' ? 0.8 : 1
      ctx.drawImage(sheet, src.sx, src.sy, src.sw, src.sh, x - r, y - r, r * 2, r * 2)
      ctx.restore()
    }

    // The state ring.
    const ring = r + 5
    ctx.lineWidth = 2.5
    ctx.lineCap = 'round'
    if (member.state === 'running') {
      const spin = (now / 520) * motion
      ctx.strokeStyle = state
      for (let i = 0; i < 3; i++) {
        const start = spin + (i * Math.PI * 2) / 3
        ctx.beginPath()
        ctx.arc(x, y, ring, start, start + Math.PI / 2.4)
        ctx.stroke()
      }
    } else if (member.state === 'waiting') {
      ctx.strokeStyle = state
      ctx.setLineDash([3, 5])
      ctx.lineDashOffset = (-now / 60) * motion
      ctx.beginPath()
      ctx.arc(x, y, ring, 0, Math.PI * 2)
      ctx.stroke()
      ctx.setLineDash([])
    } else if (member.state === 'blocked' || member.state === 'decision' || member.state === 'failed') {
      const throb = motion ? 0.5 + 0.5 * Math.sin(now / 220) : 1
      ctx.strokeStyle = alphaHex(state, 0.5 + 0.5 * throb)
      ctx.lineWidth = 2.5 + throb * 1.5
      ctx.beginPath()
      ctx.arc(x, y, ring + throb * 3, 0, Math.PI * 2)
      ctx.stroke()
    } else {
      ctx.strokeStyle = alphaHex(state, 0.7)
      ctx.lineWidth = 1.2
      ctx.beginPath()
      ctx.arc(x, y, ring - 1, 0, Math.PI * 2)
      ctx.stroke()
    }

    // The Manager wears a second ring with three ticks.
    if (member.manager) {
      ctx.strokeStyle = hsl(team.hue, 90, 75, 0.85)
      ctx.lineWidth = 1
      ctx.beginPath()
      ctx.arc(x, y, ring + 5, 0, Math.PI * 2)
      ctx.stroke()
      for (let i = -1; i <= 1; i++) {
        const ang = -Math.PI / 2 + i * 0.35
        ctx.beginPath()
        ctx.moveTo(x + Math.cos(ang) * (ring + 5), y + Math.sin(ang) * (ring + 5))
        ctx.lineTo(x + Math.cos(ang) * (ring + 10), y + Math.sin(ang) * (ring + 10))
        ctx.stroke()
      }
    }

    // Queued instructions orbit it.
    const dots = Math.min(8, member.queued)
    for (let i = 0; i < dots; i++) {
      const ang = (now / 1400) * motion + (i / Math.max(1, dots)) * Math.PI * 2
      ctx.fillStyle = Tones.tell
      ctx.beginPath()
      ctx.arc(x + Math.cos(ang) * (ring + 9), y + Math.sin(ang) * (ring + 9), 2.4, 0, Math.PI * 2)
      ctx.fill()
    }
    if (member.queued > 8) {
      ctx.fillStyle = Tones.tell
      ctx.font = this.font(0.6, 700)
      ctx.textAlign = 'left'
      ctx.textBaseline = 'middle'
      ctx.fillText(`+${member.queued}`, x + ring + 14, y - ring)
    }

    // A pending wake is a small clock at its upper left: it will be woken once, later.
    if (member.wakes.length > 0) this.drawWakeClock(ctx, x - (ring + 8) * Math.SQRT1_2, y - (ring + 8) * Math.SQRT1_2)

    // Initials where there is no avatar (a plugin, or the sheet not loaded), then the name under it.
    ctx.textAlign = 'center'
    if (avatar < 0 || !sheet) {
      ctx.fillStyle = Screen.ink
      ctx.font = this.font(r > 15 ? 0.72 : 0.62, 700)
      ctx.textBaseline = 'middle'
      ctx.fillText(initials(member.name), x, y + 0.5)
    }

    ctx.font = this.font(0.66, 500)
    ctx.textBaseline = 'top'
    ctx.fillStyle = member.state === 'idle' ? Screen.inkDim : Screen.ink
    ctx.shadowColor = alphaHex(Screen.ground, 0.9)
    ctx.shadowBlur = 4
    const name = member.name.length > 22 ? `${member.name.slice(0, 21)}…` : member.name
    ctx.fillText(name, x, y + ring + (member.manager ? 9 : 5))
    if ((member.state === 'blocked' || member.state === 'decision') && this.camera.k > 0.6) {
      ctx.fillStyle = state
      ctx.font = this.font(0.58, 700)
      ctx.fillText(member.state === 'decision' ? 'NEEDS YOU' : 'BLOCKED', x, y + ring + (member.manager ? 9 : 5) + this.options.fontPx * 0.8)
    }
    ctx.shadowBlur = 0
  }

  private drawWorkflow(ctx: CanvasRenderingContext2D, node: SceneNode, x: number, y: number, now: number) {
    const workflow = this.model?.workflows.get(Number(node.id.slice(2)))
    const team = workflow ? this.model?.teams.get(workflow.team) : undefined
    if (!workflow || !team) return
    const open = workflow.ending === 'open'
    const r = node.r
    const colour = open ? Tones.workflow : workflow.ending === 'completed' ? Tones.done : Tones.quiet

    ctx.shadowColor = colour
    ctx.shadowBlur = open ? 14 : 4
    const g = ctx.createRadialGradient(x, y, 0, x, y, r)
    g.addColorStop(0, Screen.ink)
    g.addColorStop(0.3, colour)
    g.addColorStop(1, alphaHex(colour, open ? 0.35 : 0.15))
    ctx.fillStyle = g
    ctx.beginPath()
    ctx.arc(x, y, r * (open ? 0.75 + 0.08 * Math.sin(now / 400) : 0.6), 0, Math.PI * 2)
    ctx.fill()
    ctx.shadowBlur = 0

    if (open && !this.options.reducedMotion) {
      const ang = now / 700
      ctx.fillStyle = colour
      ctx.beginPath()
      ctx.arc(x + Math.cos(ang) * (r + 5), y + Math.sin(ang) * (r + 5), 2, 0, Math.PI * 2)
      ctx.fill()
    }

    if (open && this.camera.k > 1.05 && workflow.title !== '') {
      ctx.font = this.font(0.58, 500)
      ctx.textAlign = 'center'
      ctx.textBaseline = 'top'
      ctx.fillStyle = open ? alphaHex(Screen.ink, 0.75) : alphaHex(Screen.inkDim, 0.45)
      // The name, then the number: the name gives way, the number never.
      ctx.fillText(workflowNameLine(workflow, 30), x, y + r + 4)
    }
  }

  /**
   * A repository, a project or a file: a rounded square in the team's colour for the first two, a
   * disc in its action's colour for a file, and its name to the right. Colour is never the only
   * signal: a deleted file is struck through, and the activity list says each push in words.
   */
  private drawWorkspaceNode(ctx: CanvasRenderingContext2D, node: SceneNode, ws: WorkspaceNode, x: number, y: number) {
    const hue = this.model?.teams.get(ws.team)?.hue ?? 200
    const r = node.r
    const colour = ws.action ? ActionColours[ws.action] : hsl(hue, 75, 70)
    ctx.fillStyle = Screen.ground
    ctx.beginPath()
    if (ws.kind === 'file') ctx.arc(x, y, r, 0, Math.PI * 2)
    else ctx.roundRect(x - r, y - r, r * 2, r * 2, ws.kind === 'repository' ? 3 : 2)
    ctx.fill()
    ctx.fillStyle = ws.action ? alphaHex(colour, 0.3) : hsl(hue, 70, 45, 0.45)
    ctx.fill()
    ctx.strokeStyle = colour
    ctx.lineWidth = ws.kind === 'repository' ? 2 : 1.5
    ctx.stroke()
    if (ws.kind === 'repository') {
      // Two branches meeting: a repository.
      ctx.beginPath()
      ctx.moveTo(x - r * 0.35, y - r * 0.5)
      ctx.lineTo(x - r * 0.35, y + r * 0.5)
      ctx.moveTo(x - r * 0.35, y + r * 0.1)
      ctx.quadraticCurveTo(x + r * 0.4, y + r * 0.1, x + r * 0.4, y - r * 0.5)
      ctx.lineWidth = 1.4
      ctx.stroke()
    }

    const label = workspaceLabel(ws)
    const lx = x + r + 5
    ctx.font = this.font(0.62, labelWeight(ws))
    ctx.textAlign = 'left'
    ctx.textBaseline = 'middle'
    ctx.fillStyle = ws.action === 'deleted' ? Screen.inkDim : Screen.ink
    ctx.shadowColor = alphaHex(Screen.ground, 0.9)
    ctx.shadowBlur = 4
    ctx.fillText(label, lx, y + 0.5)
    ctx.shadowBlur = 0
    if (ws.action === 'deleted') {
      const width = this.textWidth(label, 0.62, labelWeight(ws))
      ctx.strokeStyle = colour
      ctx.lineWidth = 1.4
      ctx.beginPath()
      ctx.moveTo(lx - 1, y + 0.5)
      ctx.lineTo(lx + width + 1, y + 0.5)
      ctx.moveTo(x - r * 0.7, y - r * 0.7)
      ctx.lineTo(x + r * 0.7, y + r * 0.7)
      ctx.stroke()
    }
  }

  /** A commit flashing on its repository: a ring, and its short sha and subject above it. */
  private drawCommits(ctx: CanvasRenderingContext2D) {
    for (const flash of this.workspaceView.flashes) {
      const node = this.nodes.get(flash.node)
      if (!node || node.x === undefined || node.y === undefined) continue
      const t = Math.min(1, Math.max(0, flash.t))
      const a = this.options.reducedMotion ? 1 : t < 0.08 ? t / 0.08 : t > 0.7 ? (1 - t) / 0.3 : 1
      const colour = Tones.push
      ctx.save()
      ctx.globalAlpha = a
      if (!this.options.reducedMotion) {
        ctx.strokeStyle = alphaHex(colour, 1 - t)
        ctx.lineWidth = 2
        ctx.beginPath()
        ctx.arc(node.x, node.y, node.r + 4 + ease(Math.min(1, t * 3)) * 22, 0, Math.PI * 2)
        ctx.stroke()
      }
      const subject = flash.subject.length > 44 ? `${flash.subject.slice(0, 43)}…` : flash.subject
      const words = subject === '' ? flash.sha : `${flash.sha} ${subject}`
      ctx.font = this.font(0.62, 600)
      const width = ctx.measureText(words).width
      const height = this.options.fontPx * 1.2
      const y = node.y - node.r - 10 - height / 2
      ctx.fillStyle = alphaHex(Screen.ground, 0.85)
      ctx.strokeStyle = alphaHex(colour, 0.7)
      ctx.lineWidth = 1
      ctx.beginPath()
      ctx.roundRect(node.x - width / 2 - 7, y - height / 2, width + 14, height, height / 2)
      ctx.fill()
      ctx.stroke()
      ctx.fillStyle = colour
      ctx.textAlign = 'center'
      ctx.textBaseline = 'middle'
      ctx.fillText(words, node.x, y + 0.5)
      ctx.restore()
    }
  }

  /**
   * Where each resource mark sits now and how strongly it shows. They fan out beside their member:
   * the first at its right, the next at its left, then a row higher on each side, so no two ever
   * share a place and none reaches below the member, where its name is.
   */
  shownResources(): ShownResource[] {
    const now = this.clock
    const placed = new Map<string, number>()
    const row = Math.max(ResourceR * 2 + 6, this.options.fontPx * 1.5)
    const shown: ShownResource[] = []
    for (const mark of this.resources) {
      const node = this.nodeOf(mark.at)
      const pos = this.posOf(mark.at)
      if (!pos) continue
      const key = JSON.stringify(mark.at)
      const slot = placed.get(key) ?? 0
      placed.set(key, slot + 1)
      const side = slot % 2 === 0 ? 1 : -1
      const up = Math.floor(slot / 2)
      const reach = (node?.r ?? 10) + 36 + up * 8
      const age = now - mark.start
      const left = mark.ttl - age
      const alpha = markAlpha(age, left, mark.ttl, this.options.reducedMotion)
      shown.push({
        at: mark.at,
        resource: mark.resource,
        x: pos.x + side * reach,
        y: pos.y - up * row,
        alpha,
        glyph: glyphOf(this.symbols, mark.resource.icon),
      })
    }
    return shown
  }

  /**
   * A plugin's rows, as the things they acted on: the icon in a ring of its action's colour, joined
   * to the member by a short line, its label beside it. Colour is never the only signal: a deleted
   * thing's icon is struck through, and the activity list says each in words.
   */
  private drawResources(ctx: CanvasRenderingContext2D) {
    for (const mark of this.shownResources()) {
      if (mark.alpha <= 0.01) continue
      const member = this.posOf(mark.at)
      if (!member) continue
      const colour = ActionColours[mark.resource.action]
      const side = mark.x >= member.x ? 1 : -1
      const { x, y } = mark
      ctx.save()
      ctx.globalAlpha = mark.alpha

      // The line from the member's edge to the mark's.
      const node = this.nodeOf(mark.at)
      const angle = Math.atan2(y - member.y, x - member.x)
      const from = (node?.r ?? 10) + 6
      ctx.strokeStyle = alphaHex(colour, 0.55)
      ctx.lineWidth = 1.2
      ctx.beginPath()
      ctx.moveTo(member.x + Math.cos(angle) * from, member.y + Math.sin(angle) * from)
      ctx.lineTo(x - Math.cos(angle) * (ResourceR + 2), y - Math.sin(angle) * (ResourceR + 2))
      ctx.stroke()

      // The mark: a tinted disc with a ring, the icon in it, or a plain dot until the font is here.
      ctx.fillStyle = Screen.ground
      ctx.beginPath()
      ctx.arc(x, y, ResourceR, 0, Math.PI * 2)
      ctx.fill()
      ctx.fillStyle = alphaHex(colour, 0.2)
      ctx.fill()
      ctx.strokeStyle = colour
      ctx.lineWidth = 2
      ctx.stroke()
      ctx.fillStyle = colour
      if (mark.glyph !== null) {
        ctx.font = `${Math.round(ResourceR * 1.45)}px "${MonitorSymbolsFamily}"`
        ctx.textAlign = 'center'
        ctx.textBaseline = 'middle'
        ctx.fillText(mark.glyph, x, y + 0.5)
      } else {
        ctx.beginPath()
        ctx.arc(x, y, 3, 0, Math.PI * 2)
        ctx.fill()
      }
      if (mark.resource.action === 'deleted') {
        ctx.strokeStyle = colour
        ctx.lineWidth = 2
        ctx.lineCap = 'round'
        ctx.beginPath()
        ctx.moveTo(x - ResourceR * 0.75, y - ResourceR * 0.75)
        ctx.lineTo(x + ResourceR * 0.75, y + ResourceR * 0.75)
        ctx.stroke()
      }

      // The label, outward of the mark, in the Monitor's text size.
      ctx.font = this.font(0.66, 500)
      ctx.textAlign = side > 0 ? 'left' : 'right'
      ctx.textBaseline = 'middle'
      ctx.fillStyle = Screen.ink
      ctx.shadowColor = alphaHex(Screen.ground, 0.9)
      ctx.shadowBlur = 4
      const label = mark.resource.label.length > 40 ? `${mark.resource.label.slice(0, 39)}…` : mark.resource.label
      ctx.fillText(label, x + side * (ResourceR + 6), y + 0.5)
      ctx.restore()
    }
  }

  /**
   * Where each step's icon is now and how strongly it shows: it starts just over its node, rises and
   * fades, beside the others on the node rather than on top of them.
   */
  shownSteps(): ShownStep[] {
    const now = this.clock
    const shown: ShownStep[] = []
    const spread = StepIconPx + 4
    for (const mark of this.steps) {
      const pos = this.posOf(mark.at)
      if (!pos) continue
      const node = this.nodeOf(mark.at)
      const r = node?.r ?? 10
      const { alpha, rise } = stepMotion(now - mark.start, this.options.reducedMotion)
      const side = mark.lane === 0 ? 0 : mark.lane === 1 ? -1 : 1
      const icon = stepIcon(mark.step)
      const roomy = r * this.camera.k >= StepCaptionMinPx
      shown.push({
        at: mark.at,
        icon,
        glyph: glyphOf(this.symbols, icon),
        count: mark.step.count > 1 ? mark.step.count : null,
        caption: roomy && mark.step.label === 'MCP' && mark.step.mcpTool ? mark.step.mcpTool : null,
        x: pos.x + side * spread,
        y: pos.y - r - StepIconPx * 0.7 - rise * StepRise,
        alpha,
      })
    }
    return shown
  }

  /** The agents' steps: each a small icon in the ink colour, its count beside it, an MCP tool's name under it. */
  private drawSteps(ctx: CanvasRenderingContext2D) {
    for (const mark of this.shownSteps()) {
      if (mark.alpha <= 0.01) continue
      const { x, y } = mark
      ctx.save()
      ctx.globalAlpha = mark.alpha
      ctx.shadowColor = alphaHex(Screen.ground, 0.9)
      ctx.shadowBlur = 4
      ctx.fillStyle = Screen.ink
      ctx.textAlign = 'center'
      ctx.textBaseline = 'middle'
      if (mark.glyph !== null) {
        ctx.font = `${StepIconPx}px "${MonitorSymbolsFamily}"`
        ctx.fillText(mark.glyph, x, y)
      } else {
        ctx.beginPath()
        ctx.arc(x, y, 2.5, 0, Math.PI * 2)
        ctx.fill()
      }
      if (mark.count !== null) {
        ctx.font = this.font(0.5, 700)
        ctx.textAlign = 'left'
        ctx.fillText(String(mark.count), x + StepIconPx * 0.45, y - StepIconPx * 0.35)
      }
      if (mark.caption !== null) {
        ctx.font = this.font(0.45, 500)
        ctx.textAlign = 'center'
        ctx.textBaseline = 'top'
        ctx.fillStyle = Screen.inkDim
        ctx.fillText(mark.caption.length > 16 ? `${mark.caption.slice(0, 15)}…` : mark.caption, x, y + StepIconPx * 0.55)
      }
      ctx.restore()
    }
  }

  private drawFloaters(ctx: CanvasRenderingContext2D, now: number) {
    ctx.textAlign = 'center'
    ctx.textBaseline = 'bottom'
    for (const floater of this.floaters) {
      const pos = this.posOf(floater.at)
      if (!pos) continue
      const t = (now - floater.start) / FloatMs
      const node = this.nodeOf(floater.at)
      const rise = 18 + (node?.r ?? 10) + ease(Math.min(1, t * 1.3)) * 26 + floater.lane * this.options.fontPx * 1.1
      const a = t < 0.15 ? t / 0.15 : 1 - Math.max(0, (t - 0.6) / 0.4)
      ctx.font = this.font(0.6, 600)
      const width = ctx.measureText(floater.text).width
      ctx.fillStyle = alphaHex(Screen.ground, 0.72 * a)
      ctx.beginPath()
      ctx.roundRect(pos.x - width / 2 - 6, pos.y - rise - this.options.fontPx * 0.95, width + 12, this.options.fontPx * 1.15, 6)
      ctx.fill()
      ctx.fillStyle = alphaHex(Tones[floater.tone], a)
      ctx.fillText(floater.text, pos.x, pos.y - rise + 1)
    }
  }
}

function idOf(end: string | number | SceneNode): string | number {
  return typeof end === 'object' ? end.id : end
}

/** The least move that takes box `a` clear of box `b`, or null when they do not overlap. */
function clearOf(a: Box, b: Box): { dx: number; dy: number } | null {
  const ox = Math.min(a.right - b.left, b.right - a.left)
  const oy = Math.min(a.bottom - b.top, b.bottom - a.top)
  if (ox <= 0 || oy <= 0) return null
  const gap = WorkspaceGap
  if (ox <= oy) {
    const right = (a.left + a.right) / 2 >= (b.left + b.right) / 2
    return { dx: right ? b.right - a.left + gap : -(a.right - b.left + gap), dy: 0 }
  }
  const below = (a.top + a.bottom) / 2 >= (b.top + b.bottom) / 2
  return { dx: 0, dy: below ? b.bottom - a.top + gap : -(a.bottom - b.top + gap) }
}

function workspaceLabel(ws: WorkspaceNode): string {
  return ws.label.length > 32 ? `${ws.label.slice(0, 31)}…` : ws.label
}

function labelWeight(ws: WorkspaceNode): number {
  return ws.kind === 'file' ? 500 : 600
}

function refOf(node: SceneNode): NodeRef | null {
  if (node.kind === 'member') return { kind: 'member', key: node.id.slice(2) }
  if (node.kind === 'team') return { kind: 'team', id: node.team }
  if (node.kind === 'workflow') return { kind: 'workflow', id: Number(node.id.slice(2)) }
  return null
}

function sameRef(a: NodeRef, b: NodeRef): boolean {
  return JSON.stringify(a) === JSON.stringify(b)
}

/** The spiral `separate` searches when its passes stall: twelve directions a ring, out to this many rings. */
const SpiralRings = 40
const SpiralTurns = Array.from({ length: 12 }, (_, i) => (i * Math.PI) / 6)

/** How much of a label overlap the `boxes` force turns into velocity apart each tick. */
const BoxPush = 0.35

/** How far a file change's drawn position moves toward its simulated one each frame. */
const ShownEase = 0.1
