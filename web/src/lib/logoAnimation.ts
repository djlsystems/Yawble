import { forceCollide, forceLink, forceManyBody, forceSimulation, forceX, forceY, type SimulationLinkDatum, type SimulationNodeDatum } from 'd3-force'
import { solidHex } from './monitor/scene'

/**
 * THE LOGO ANIMATION: the mark at the middle of a small graph drawn in the Monitor's style, its
 * branches growing out one after another. A fixed picture, so everything it shows is here: which
 * node grows from which, when, and in what hue. The stage is ticked from its own frame loop, as the
 * Monitor's is, so a test can drive it by hand.
 */

/** How many clicks on the header's mark open it, each within `LogoClickGapMs` of the one before. */
export const LogoClicks = 5
export const LogoClickGapMs = 1500


export interface ClickRun {
  count: number
  last: number
}

/** The next click's run: one more when it follows the last closely enough, else a new run of one. */
export function nextClick(run: ClickRun, now: number): ClickRun {
  return now - run.last <= LogoClickGapMs ? { count: run.count + 1, last: now } : { count: 1, last: now }
}

export type LogoNodeKind = 'core' | 'hub' | 'leaf'

export interface LogoNodeSpec {
  id: string
  label: string
  kind: LogoNodeKind
  parent: string | null
  /** When it is born, in ms from the start. Its line grows from its parent for `GrowMs` before that. */
  at: number
  hue: number
}

/** How long a line takes to reach its node, a comet riding its tip. */
export const GrowMs = 700

const ProjectsHue = 205
const WorkflowsHue = 152

export const LogoGraph: readonly LogoNodeSpec[] = [
  { id: 'core', label: '', kind: 'core', parent: null, at: 0, hue: 16 },
  { id: 'projects', label: 'Projects', kind: 'hub', parent: 'core', at: 1600, hue: ProjectsHue },
  { id: 'workflows', label: 'Workflows', kind: 'hub', parent: 'core', at: 1600, hue: WorkflowsHue },
  { id: 'saas', label: 'SaaS apps', kind: 'leaf', parent: 'projects', at: 3100, hue: ProjectsHue },
  { id: 'sites', label: 'Web sites', kind: 'leaf', parent: 'projects', at: 3100, hue: ProjectsHue },
  { id: 'plugins', label: 'Plugins', kind: 'leaf', parent: 'projects', at: 3100, hue: ProjectsHue },
  { id: 'customer-support', label: 'Customer Support', kind: 'leaf', parent: 'workflows', at: 3100, hue: WorkflowsHue },
  { id: 'fulfillment', label: 'Fulfillment', kind: 'leaf', parent: 'workflows', at: 3100, hue: WorkflowsHue },
  { id: 'research', label: 'Research', kind: 'leaf', parent: 'workflows', at: 3100, hue: WorkflowsHue },
]

/** The nodes born by `ms`, and the ones whose line is growing toward them. */
export function shownAt(ms: number): { born: string[]; growing: string[] } {
  return {
    born: LogoGraph.filter((node) => node.at <= ms).map((node) => node.id),
    growing: LogoGraph.filter((node) => node.parent !== null && node.at > ms && node.at - GrowMs <= ms).map((node) => node.id),
  }
}

/** Where each hub leans: Projects up and left, Workflows down and right, so the two trees part. */
const Lean: Record<string, { x: number; y: number }> = {
  projects: { x: -190, y: -70 },
  workflows: { x: 190, y: 70 },
}

const Radius: Record<LogoNodeKind, number> = { core: 44, hub: 22, leaf: 15 }
const BornMs = 900

/**
 * THE CLOSE: once the whole picture has stood for `HoldMs`, everything off the mark fades while the
 * mark grows - its disc and its turning ring with it - and the sign-off appears just beneath it. Each
 * part is a fraction 0..1 of its own stretch, so drawing reads them and a test can read them too.
 */
export const HoldMs = 500
const FadeOthersMs = 800
const ExpandMs = 1600
const SignOffMs = 1200
/** How much larger the mark is once grown. */
const Grown = 3.1
/** The width the camera frames at the close, so the grown mark keeps one size on the screen. */
const CloseFrameWidth = Radius.core * 12
/** The room between the grown mark's ring and the sign-off. */
const SignOffGap = 14

/** When the close begins: the last node fully grown, then the hold. */
export const FinaleAt = Math.max(...LogoGraph.map((node) => node.at)) + BornMs + HoldMs

/** When the sign-off is fully shown. */
export const SignOffShownAt = FinaleAt + ExpandMs * 0.6 + SignOffMs

/** The close button appears a little after the sign-off is shown; Escape closes it any time. */
export const CloseAfterMs = SignOffShownAt + 4000

/** The dialog shows its empty ground this long before the picture begins. */
export const LeadInMs = 1000

export interface Finale {
  /** Everything off the mark: 1 is gone. */
  others: number
  /** The mark growing: 1 is grown. */
  expand: number
  /** The sign-off beneath it: 1 is shown. */
  signOff: number
}

export function finaleAt(t: number): Finale {
  const part = (from: number, ms: number) => ease((t - from) / ms)
  return {
    others: part(FinaleAt, FadeOthersMs),
    expand: part(FinaleAt, ExpandMs),
    signOff: part(SignOffShownAt - SignOffMs, SignOffMs),
  }
}

/**
 * Where a node's line heads before the node is born, and so where it is born: a hub at its lean, a
 * leaf fanned out from its parent away from the mark, each sibling at its own angle, so every leaf
 * has a glowing tip of its own.
 */
export function arrival(spec: LogoNodeSpec, parent: { x?: number | undefined; y?: number | undefined }): { x: number; y: number } {
  const lean = Lean[spec.id]
  if (lean) return { x: lean.x * 0.55, y: lean.y * 0.55 }
  const px = parent.x ?? 0
  const py = parent.y ?? 0
  const siblings = LogoGraph.filter((node) => node.parent === spec.parent)
  const index = siblings.findIndex((node) => node.id === spec.id)
  const away = Math.atan2(py, px)
  const angle = away + (index - (siblings.length - 1) / 2) * 0.95
  return { x: px + Math.cos(angle) * LeafReach, y: py + Math.sin(angle) * LeafReach }
}

/** How far from its parent a leaf's line reaches before the leaf is born. */
const LeafReach = 110

/** The core's ring for a disc of radius `r`: its gap and stroke grow with the disc. */
function coreRing(r: number): { r: number; width: number } {
  const scale = r / Radius.core
  return { r: r + 7 * scale, width: 3.5 * scale }
}

/** Where the sign-off's top is, beneath the grown mark's ring. */
function signOffTop(ring: { r: number; width: number }): number {
  return ring.r + ring.width / 2 + SignOffGap
}

interface StageNode {
  spec: LogoNodeSpec
  r: number
  /** Where it is drawn this frame: its settled place, floating a little. */
  x: number
  y: number
}

interface LayoutNode extends SimulationNodeDatum {
  spec: LogoNodeSpec
}

type LayoutLink = SimulationLinkDatum<LayoutNode>

let settled: ReadonlyMap<string, { x: number; y: number }> | null = null

/**
 * WHERE EVERY NODE ENDS UP, worked out once before anything is drawn: the force layout run to rest
 * from the places each line heads for, the same every time. Nothing then moves a node but a slight
 * float, so the picture never jolts as it grows.
 */
export function logoLayout(): ReadonlyMap<string, { x: number; y: number }> {
  if (settled) return settled
  const nodes = new Map<string, LayoutNode>()
  for (const spec of LogoGraph) {
    const parent = spec.parent ? nodes.get(spec.parent) : undefined
    const start = parent ? arrival(spec, parent) : { x: 0, y: 0 }
    const node: LayoutNode = { spec, x: start.x, y: start.y }
    if (spec.kind === 'core') {
      node.fx = 0
      node.fy = 0
    }
    nodes.set(spec.id, node)
  }
  const links: LayoutLink[] = LogoGraph.flatMap((spec) => (spec.parent === null ? [] : [{ source: spec.parent, target: spec.id }]))
  const sim = forceSimulation<LayoutNode, LayoutLink>([...nodes.values()])
    .stop()
    .velocityDecay(0.35)
    .force(
      'link',
      forceLink<LayoutNode, LayoutLink>(links)
        .id((node) => node.spec.id)
        .distance((link) => ((link.target as LayoutNode).spec.kind === 'hub' ? 170 : 120))
        .strength(0.7),
    )
    .force('charge', forceManyBody<LayoutNode>().strength((node) => (node.spec.kind === 'leaf' ? -320 : -700)))
    .force('collide', forceCollide<LayoutNode>().radius((node) => Radius[node.spec.kind] + 30))
    .force('x', forceX<LayoutNode>((node) => Lean[node.spec.id]?.x ?? 0).strength((node) => (Lean[node.spec.id] ? 0.08 : 0.01)))
    .force('y', forceY<LayoutNode>((node) => Lean[node.spec.id]?.y ?? 0).strength((node) => (Lean[node.spec.id] ? 0.08 : 0.01)))
  sim.tick(400)
  settled = new Map([...nodes].map(([id, node]) => [id, { x: node.x ?? 0, y: node.y ?? 0 }]))
  return settled
}

/** How far and how slowly a node floats about its place, so the picture breathes without moving. */
const FloatPx = 2.5
const FloatMs = 3600

/** The camera starts closer on the mark and eases out to the whole picture over this long. */
const OpeningMs = 2600
const OpeningZoom = 1.35

/** The wall's colours, read from the `--os-monitor-wall-*` tokens the Monitor paints with. */
interface Wall {
  ground: string
  ink: string
  inkDim: string
  glow: string
  running: string
}

function hsl(hue: number, s: number, l: number, a = 1): string {
  return `hsla(${hue.toFixed(0)}, ${s}%, ${l}%, ${a})`
}

function alphaHex(colour: string, alpha: number): string {
  if (!/^#[0-9a-f]{6}$/i.test(colour)) return colour
  return colour + Math.round(Math.max(0, Math.min(1, alpha)) * 255).toString(16).padStart(2, '0')
}

/** A `#rrggbb` colour moved `share` of the way to white. */
function towardWhite(hex: string, share: number): string {
  return '#' + [1, 3, 5]
    .map((i) => parseInt(hex.slice(i, i + 2), 16))
    .map((part) => Math.round(part + (255 - part) * share).toString(16).padStart(2, '0'))
    .join('')
}

function lerpFrame(a: { x: number; y: number; k: number }, b: { x: number; y: number; k: number }, p: number) {
  return { x: a.x + (b.x - a.x) * p, y: a.y + (b.y - a.y) * p, k: a.k + (b.k - a.k) * p }
}

function ease(t: number): number {
  const c = Math.max(0, Math.min(1, t))
  return c < 0.5 ? 4 * c * c * c : 1 - Math.pow(-2 * c + 2, 3) / 2
}

export interface StageOptions {
  fontFamily: string
  fontPx: number
  reducedMotion: boolean
  /** The mark drawn on the core node. */
  mark: HTMLImageElement | null
  /** The sign-off beneath the grown mark: its name, set apart, then its line. */
  signOff: { name: string; line: string; address: string }
}

export class LogoStage {
  private readonly ctx: CanvasRenderingContext2D | null
  private readonly layout = logoLayout()
  private readonly nodes = new Map<string, StageNode>()
  /** Nothing until `readWall` reads the tokens: every colour here is the theme's. */
  private wall: Wall = { ground: '', ink: '', inkDim: '', glow: '', running: '' }
  private width = 800
  private height = 600
  private dpr = 1
  private camera = { x: 0, y: 0, k: 1 }
  private started = -1
  private frame = 0

  constructor(
    private readonly canvas: HTMLCanvasElement,
    private options: StageOptions,
  ) {
    this.ctx = canvas.getContext('2d')
  }

  setOptions(options: Partial<StageOptions>) {
    this.options = { ...this.options, ...options }
  }

  /** Read the wall's colours off an element inside the page. */
  readWall(element: Element) {
    const css = getComputedStyle(element)
    const token = (name: string, fallback: string) => {
      const value = solidHex(css.getPropertyValue(name).trim())
      return /^#[0-9a-f]{6}$/i.test(value) ? value : fallback
    }
    this.wall = {
      ground: token('--os-monitor-wall-ground', this.wall.ground),
      ink: token('--os-monitor-wall-ink', this.wall.ink),
      inkDim: token('--os-monitor-wall-ink-dim', this.wall.inkDim),
      glow: token('--os-monitor-wall-tell', this.wall.glow),
      running: this.wall.running,
    }
    // The running ring is the running state's token lifted toward white for the dark ground, as the Monitor's is.
    // Mixed here, not by `color-mix()`, which the browser hands back as `color(srgb ...)`, not hex.
    const running = solidHex(css.getPropertyValue('--os-state-running').trim())
    this.wall.running = /^#[0-9a-f]{6}$/i.test(running) ? towardWhite(running, 0.4) : this.wall.ink
  }

  resize(width: number, height: number, dpr = globalThis.devicePixelRatio || 1) {
    this.width = Math.max(1, width)
    this.height = Math.max(1, height)
    this.dpr = dpr
    this.canvas.width = Math.round(this.width * dpr)
    this.canvas.height = Math.round(this.height * dpr)
    this.canvas.style.width = `${this.width}px`
    this.canvas.style.height = `${this.height}px`
  }

  start() {
    if (this.frame !== 0) return
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
  }

  /** The ids on the stage now. */
  shown(): string[] {
    return [...this.nodes.keys()]
  }

  /** One frame at `now` (ms, any clock): everything on it is a function of the time alone. */
  step(now: number) {
    if (this.started < 0) this.started = now
    const t = now - this.started
    this.bear(t)
    this.frameAll(t)
    this.draw(t)
  }

  /** Each node due, at its settled place - where its line's glowing tip arrives - floating a little. */
  private bear(t: number) {
    for (const spec of LogoGraph) {
      if (spec.at > t) continue
      const node = this.nodes.get(spec.id) ?? { spec, r: Radius[spec.kind], x: 0, y: 0 }
      const place = this.placeOf(spec, t)
      node.x = place.x
      node.y = place.y
      this.nodes.set(spec.id, node)
    }
  }

  private placeOf(spec: LogoNodeSpec, t: number): { x: number; y: number } {
    const at = this.layout.get(spec.id) ?? { x: 0, y: 0 }
    if (spec.kind === 'core' || this.options.reducedMotion) return at
    const phase = (LogoGraph.indexOf(spec) * Math.PI * 2) / LogoGraph.length
    const turn = (t / FloatMs) * Math.PI * 2 + phase
    return { x: at.x + Math.cos(turn) * FloatPx, y: at.y + Math.sin(turn * 1.3) * FloatPx }
  }

  /**
   * The camera, as a function of the time: closer on the mark at first, easing out to frame the whole
   * picture (every settled place and its label, known from the start, so it never lurches), and at
   * the close easing in on the grown mark and its sign-off.
   */
  private frameAll(t: number) {
    let left = Infinity
    let right = -Infinity
    let top = Infinity
    let bottom = -Infinity
    for (const spec of LogoGraph) {
      const at = this.layout.get(spec.id) ?? { x: 0, y: 0 }
      const r = Radius[spec.kind]
      const reach = r + (spec.kind === 'leaf' ? 70 : 60)
      left = Math.min(left, at.x - reach)
      right = Math.max(right, at.x + reach)
      top = Math.min(top, at.y - r - 30)
      bottom = Math.max(bottom, at.y + r + 50)
    }
    const whole = {
      x: (left + right) / 2,
      y: (top + bottom) / 2,
      k: Math.min(1.6, (this.width * 0.9) / (right - left), (this.height * 0.86) / (bottom - top)),
    }

    const ring = coreRing(Radius.core * Grown)
    const closeTop = -(ring.r + ring.width + 20)
    const closeBottom = signOffTop(ring) + 90
    const close = {
      x: 0,
      y: (closeTop + closeBottom) / 2,
      k: Math.min(1.6, (this.width * 0.9) / CloseFrameWidth, (this.height * 0.9) / (closeBottom - closeTop)),
    }

    if (this.options.reducedMotion) {
      this.camera = t >= FinaleAt ? close : whole
      return
    }
    const opening = { x: 0, y: 0, k: whole.k * OpeningZoom }
    const from = lerpFrame(opening, whole, ease(t / OpeningMs))
    this.camera = lerpFrame(from, close, ease((t - FinaleAt) / ExpandMs))
  }

  private font(scale: number, weight = 600): string {
    return `${weight} ${Math.round(this.options.fontPx * scale)}px ${this.options.fontFamily}`
  }

  private draw(t: number) {
    const ctx = this.ctx
    if (!ctx) return
    const { width, height, dpr } = this
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
    ctx.globalCompositeOperation = 'source-over'
    this.drawGround(ctx, width, height)

    ctx.save()
    ctx.translate(width / 2, height / 2)
    ctx.scale(this.camera.k, this.camera.k)
    ctx.translate(-this.camera.x, -this.camera.y)
    this.drawTerritories(ctx, t)
    this.drawLinks(ctx, t)
    for (const kind of ['leaf', 'hub', 'core'] as const) {
      for (const node of this.nodes.values()) if (node.spec.kind === kind) this.drawNode(ctx, node, t)
    }
    this.drawSignOff(ctx, t)
    ctx.restore()
  }

  /** The product's name and line beneath the grown mark, and its address under them, fading in. */
  private drawSignOff(ctx: CanvasRenderingContext2D, t: number) {
    const shown = finaleAt(t).signOff
    if (shown <= 0) return
    const { name, line, address } = this.options.signOff
    const y = signOffTop(coreRing(Radius.core * Grown)) + (1 - shown) * 12
    ctx.save()
    ctx.globalAlpha = shown
    ctx.textBaseline = 'top'
    ctx.textAlign = 'left'
    const nameFont = `700 30px ${this.options.fontFamily}`
    const lineFont = `400 30px ${this.options.fontFamily}`
    ctx.font = nameFont
    const nameWidth = ctx.measureText(`${name} `).width
    ctx.font = lineFont
    const lineWidth = ctx.measureText(line).width
    const left = -(nameWidth + lineWidth) / 2
    ctx.font = nameFont
    ctx.fillStyle = hsl(16, 95, 58)
    ctx.fillText(`${name} `, left, y)
    ctx.font = lineFont
    ctx.fillStyle = this.wall.ink
    ctx.fillText(line, left + nameWidth, y)
    ctx.font = `500 22px ${this.options.fontFamily}`
    ctx.textAlign = 'center'
    ctx.fillStyle = this.wall.inkDim
    ctx.fillText(address, 0, y + 46)
    ctx.restore()
  }

  private drawGround(ctx: CanvasRenderingContext2D, width: number, height: number) {
    ctx.fillStyle = this.wall.ground
    ctx.fillRect(0, 0, width, height)
    const glow = ctx.createRadialGradient(width / 2, height / 2, 0, width / 2, height / 2, Math.max(width, height) * 0.7)
    glow.addColorStop(0, alphaHex(this.wall.glow, 0.08))
    glow.addColorStop(1, alphaHex(this.wall.ground, 0))
    ctx.fillStyle = glow
    ctx.fillRect(0, 0, width, height)

    const k = this.camera.k
    const step = 36 * k
    if (step > 9) {
      const ox = (width / 2 - this.camera.x * k) % step
      const oy = (height / 2 - this.camera.y * k) % step
      ctx.fillStyle = alphaHex(this.wall.inkDim, 0.09)
      for (let x = ox; x < width; x += step) for (let y = oy; y < height; y += step) ctx.fillRect(x, y, 1.2, 1.2)
    }
  }

  private grown(node: StageNode, t: number): number {
    return this.options.reducedMotion ? 1 : ease((t - node.spec.at) / BornMs)
  }

  /** A soft territory under the core and each hub, breathing while the picture grows. */
  private drawTerritories(ctx: CanvasRenderingContext2D, t: number) {
    ctx.globalCompositeOperation = 'lighter'
    for (const node of this.nodes.values()) {
      if (node.spec.kind === 'leaf' || node.x === undefined || node.y === undefined) continue
      const a = this.grown(node, t) * (node.spec.kind === 'core' ? 1 : 1 - finaleAt(t).others)
      const breathe = this.options.reducedMotion ? 1 : 1 + Math.sin(t / 700) * 0.04
      const reach = (node.spec.kind === 'core' ? 170 : 130) * breathe
      const g = ctx.createRadialGradient(node.x, node.y, 0, node.x, node.y, reach)
      g.addColorStop(0, hsl(node.spec.hue, 85, 55, 0.2 * a))
      g.addColorStop(0.55, hsl(node.spec.hue, 85, 50, 0.07 * a))
      g.addColorStop(1, hsl(node.spec.hue, 85, 50, 0))
      ctx.fillStyle = g
      ctx.beginPath()
      ctx.arc(node.x, node.y, reach, 0, Math.PI * 2)
      ctx.fill()
    }
    ctx.globalCompositeOperation = 'source-over'
  }

  /** Each line grows from its parent toward its child, a comet at its tip, then flows as a working tie does. */
  private drawLinks(ctx: CanvasRenderingContext2D, t: number) {
    const fade = 1 - finaleAt(t).others
    if (fade <= 0) return
    ctx.save()
    ctx.globalAlpha = fade
    for (const spec of LogoGraph) {
      if (!spec.parent || spec.at - GrowMs > t) continue
      const parent = this.nodes.get(spec.parent)
      if (!parent || parent.x === undefined || parent.y === undefined) continue
      const child = this.nodes.get(spec.id)
      const to = child ?? this.placeOf(spec, t)
      const p = this.options.reducedMotion ? 1 : ease((t - (spec.at - GrowMs)) / GrowMs)
      const tip = { x: parent.x + (to.x - parent.x) * p, y: parent.y + (to.y - parent.y) * p }

      ctx.strokeStyle = hsl(spec.hue, 90, 70, 0.55)
      ctx.lineWidth = 1.6
      ctx.setLineDash([5, 6])
      ctx.lineDashOffset = this.options.reducedMotion ? 0 : -t / 40
      ctx.beginPath()
      ctx.moveTo(parent.x, parent.y)
      ctx.lineTo(tip.x, tip.y)
      ctx.stroke()
      ctx.setLineDash([])

      if (p < 1) {
        ctx.globalCompositeOperation = 'lighter'
        const head = ctx.createRadialGradient(tip.x, tip.y, 0, tip.x, tip.y, 14)
        head.addColorStop(0, hsl(spec.hue, 100, 85, 0.95))
        head.addColorStop(1, hsl(spec.hue, 100, 60, 0))
        ctx.fillStyle = head
        ctx.beginPath()
        ctx.arc(tip.x, tip.y, 14, 0, Math.PI * 2)
        ctx.fill()
        ctx.globalCompositeOperation = 'source-over'
      }
    }
    ctx.restore()
  }

  private drawNode(ctx: CanvasRenderingContext2D, node: StageNode, t: number) {
    if (node.x === undefined || node.y === undefined) return
    const grow = this.grown(node, t)
    const fade = node.spec.kind === 'core' ? 1 : 1 - finaleAt(t).others
    if (fade <= 0) return
    ctx.save()
    ctx.globalAlpha = Math.max(0.05, grow) * fade
    if (node.spec.kind === 'core') this.drawCore(ctx, node.x, node.y, node.r * (0.5 + 0.5 * grow), t)
    else if (node.spec.kind === 'hub') this.drawHub(ctx, node, node.x, node.y, grow)
    else this.drawLeaf(ctx, node, node.x, node.y, node.r * (0.4 + 0.6 * grow), t)
    ctx.restore()
  }

  /**
   * The mark on a light disc, wearing the working ring: three arcs turning. At the close it grows
   * as it is, disc, ring and all.
   */
  private drawCore(ctx: CanvasRenderingContext2D, x: number, y: number, base: number, t: number) {
    const r = base * (1 + (Grown - 1) * finaleAt(t).expand)
    const ring = coreRing(r)
    ctx.shadowColor = this.wall.running
    ctx.shadowBlur = 22 * (r / base)
    ctx.fillStyle = this.wall.ink
    ctx.beginPath()
    ctx.arc(x, y, r, 0, Math.PI * 2)
    ctx.fill()
    ctx.shadowBlur = 0
    const mark = this.options.mark
    if (mark && mark.complete && mark.naturalWidth > 0) {
      // The mark's own ground is transparent, so it needs no clip to sit in the disc.
      const s = r * 1.6
      ctx.drawImage(mark, x - s / 2, y - s / 2, s, s)
    }
    const spin = this.options.reducedMotion ? 0 : t / 520
    ctx.strokeStyle = this.wall.running
    ctx.lineWidth = ring.width
    ctx.lineCap = 'round'
    for (let i = 0; i < 3; i++) {
      const start = spin + (i * Math.PI * 2) / 3
      ctx.beginPath()
      ctx.arc(x, y, ring.r, start, start + Math.PI / 2.4)
      ctx.stroke()
    }
  }

  /** A hub is its name on an oval, as a team is on the Monitor. */
  private drawHub(ctx: CanvasRenderingContext2D, node: StageNode, x: number, y: number, grow: number) {
    const hue = node.spec.hue
    ctx.font = this.font(1.05, 700)
    ctx.textAlign = 'center'
    ctx.textBaseline = 'middle'
    const scale = 0.6 + 0.4 * grow
    const width = (ctx.measureText(node.spec.label).width + 34) * scale
    const height = this.options.fontPx * 2.4 * scale
    ctx.shadowColor = hsl(hue, 90, 60, 0.9)
    ctx.shadowBlur = 24
    const g = ctx.createLinearGradient(x, y - height / 2, x, y + height / 2)
    g.addColorStop(0, hsl(hue, 75, 34, 0.96))
    g.addColorStop(1, hsl(hue, 80, 18, 0.96))
    ctx.fillStyle = g
    ctx.beginPath()
    ctx.roundRect(x - width / 2, y - height / 2, width, height, height / 2)
    ctx.fill()
    ctx.shadowBlur = 0
    ctx.strokeStyle = hsl(hue, 90, 70, 0.85)
    ctx.lineWidth = 1.5
    ctx.stroke()
    ctx.fillStyle = hsl(hue, 90, 88)
    ctx.fillText(node.spec.label, x, y + 0.5)
  }

  /** A leaf is a member's disc: a pulse as it is born, and its ring turning until it is gone. */
  private drawLeaf(ctx: CanvasRenderingContext2D, node: StageNode, x: number, y: number, r: number, t: number) {
    const hue = node.spec.hue
    const age = t - node.spec.at
    if (!this.options.reducedMotion && age < 700) {
      const p = age / 700
      ctx.fillStyle = hsl(hue, 90, 70, 0.35 * (1 - p))
      ctx.beginPath()
      ctx.arc(x, y, r + 6 + p * 14, 0, Math.PI * 2)
      ctx.fill()
    }
    ctx.shadowColor = hsl(hue, 90, 60, 0.8)
    ctx.shadowBlur = 14
    const body = ctx.createRadialGradient(x - r * 0.35, y - r * 0.35, r * 0.1, x, y, r)
    body.addColorStop(0, hsl(hue, 70, 55))
    body.addColorStop(1, hsl(hue, 60, 22))
    ctx.fillStyle = body
    ctx.beginPath()
    ctx.arc(x, y, r, 0, Math.PI * 2)
    ctx.fill()
    ctx.shadowBlur = 0

    const ring = r + 5
    ctx.lineCap = 'round'
    if (!this.options.reducedMotion) {
      ctx.strokeStyle = hsl(hue, 95, 75, 0.9)
      ctx.lineWidth = 2.2
      const spin = t / 520
      for (let i = 0; i < 3; i++) {
        const start = spin + (i * Math.PI * 2) / 3
        ctx.beginPath()
        ctx.arc(x, y, ring, start, start + Math.PI / 2.4)
        ctx.stroke()
      }
    } else {
      ctx.strokeStyle = hsl(hue, 90, 75, 0.7)
      ctx.lineWidth = 1.2
      ctx.beginPath()
      ctx.arc(x, y, ring - 1, 0, Math.PI * 2)
      ctx.stroke()
    }

    ctx.font = this.font(0.95, 600)
    ctx.textAlign = 'center'
    ctx.textBaseline = 'top'
    ctx.fillStyle = this.wall.ink
    ctx.shadowColor = alphaHex(this.wall.ground, 0.9)
    ctx.shadowBlur = 4
    ctx.fillText(node.spec.label, x, y + ring + 6)
    ctx.shadowBlur = 0
  }
}
