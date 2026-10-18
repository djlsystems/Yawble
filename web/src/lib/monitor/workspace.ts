import { wireInstant } from '../localTime'
import type { ResourceAction } from './hints'
import { readStepsPush, type StepsPush } from './steps'

/**
 * FILE CHANGES: what a member did to the files of a team's repositories, as the Host pushes it.
 *
 * A person's connection joins the hub's file watch with `JoinFileWatch()` while the Monitor is open
 * and leaves with `LeaveFileWatch()` when it closes; the Host then pushes `workspaceChanged`, one
 * push per batch of changes a member made: its files, the commits among them, and whether there
 * were more than it lists. The Host also records each of them during every run, watched or not, and
 * a replay reads them back (`GET /api/file-changes`, `readRecorded`) and plays each at its time
 * beside the log rows, through the same `readPush` and the same tree.
 *
 * The scene draws each as a small tree off the team: the repository, its project, the file, and a
 * line from the member to each file it touched. `WorkspaceGraph` is that tree in display time, kept
 * apart from the canvas so what is drawn, and for how long, can be read without one.
 */

export type FileAction = ResourceAction

export interface WorkspaceFile {
  path: string
  project: string
  action: FileAction
}

export interface WorkspaceCommit {
  sha: string
  subject: string
  files: WorkspaceFile[]
}

/** One `workspaceChanged` push, as the Host sends it. */
export interface WorkspacePush {
  team: string
  member: string
  repo: string
  /** When it happened, as the wire spells an instant. */
  at: string
  files: WorkspaceFile[]
  commits: WorkspaceCommit[]
  /** More files changed than `files` and `commits` list: a count, or true when the Host does not say how many. */
  more: number | boolean
}

export const WorkspaceChanged = 'workspaceChanged'
export const JoinFileWatch = 'JoinFileWatch'
export const LeaveFileWatch = 'LeaveFileWatch'

function text(value: unknown): string {
  return typeof value === 'string' ? value : ''
}

function readAction(value: unknown): FileAction | null {
  return value === 'created' || value === 'changed' || value === 'deleted' ? value : null
}

function readFiles(value: unknown): WorkspaceFile[] {
  if (!Array.isArray(value)) return []
  const files: WorkspaceFile[] = []
  for (const raw of value as unknown[]) {
    if (typeof raw !== 'object' || raw === null) continue
    const file = raw as Record<string, unknown>
    const action = readAction(file.action)
    const path = text(file.path)
    if (path === '' || action === null) continue
    files.push({ path, project: text(file.project), action })
  }
  return files
}

/** A push as the wire sent it, or null when it is not one: the Monitor never trusts the shape. */
export function readPush(value: unknown): WorkspacePush | null {
  if (typeof value !== 'object' || value === null) return null
  const raw = value as Record<string, unknown>
  const team = text(raw.team)
  const member = text(raw.member)
  const repo = text(raw.repo)
  if (team === '' || member === '' || repo === '') return null
  const commits: WorkspaceCommit[] = []
  if (Array.isArray(raw.commits)) {
    for (const entry of raw.commits as unknown[]) {
      if (typeof entry !== 'object' || entry === null) continue
      const commit = entry as Record<string, unknown>
      const sha = text(commit.sha)
      if (sha === '') continue
      commits.push({ sha, subject: text(commit.subject), files: readFiles(commit.files) })
    }
  }
  const more = typeof raw.more === 'number' && Number.isFinite(raw.more) && raw.more > 0 ? Math.round(raw.more) : raw.more === true
  return { team, member, repo, at: text(raw.at), files: readFiles(raw.files), commits, more }
}

/** A push's time in ms, or null when it cannot be read. */
export function pushTime(push: WorkspacePush): number | null {
  const at = wireInstant(push.at)
  return Number.isFinite(at) ? at : null
}

/** Every file a push names, its own and its commits', each once: the last word on a file wins. */
export function filesOf(push: WorkspacePush): WorkspaceFile[] {
  const files = new Map<string, WorkspaceFile>()
  for (const commit of push.commits) for (const file of commit.files) files.set(`${file.project}\u0001${file.path}`, file)
  for (const file of push.files) files.set(`${file.project}\u0001${file.path}`, file)
  return [...files.values()]
}

export function shortSha(sha: string): string {
  return sha.slice(0, 7)
}

/** The last part of a path: `web/src/lib/parser.ts` -> `parser.ts`. */
export function fileName(path: string): string {
  const parts = path.split(/[\\/]/).filter((part) => part !== '')
  return parts.at(-1) ?? path
}

/** A push's commit line for the activity list: the first commit's short sha and subject, and how
 * many more; '' for a push of uncommitted changes, whose files are its whole line. */
export function pushHeadline(push: WorkspacePush): string {
  const first = push.commits[0]
  if (!first) return ''
  const others = push.commits.length - 1
  const subject = first.subject.trim() === '' ? '' : ` ${first.subject.trim()}`
  const also = others > 0 ? ` and ${others} more ${others === 1 ? 'commit' : 'commits'}` : ''
  return `${shortSha(first.sha)}${subject}${also}`
}

/** What a push left off its list: "and 4 more", "and more", or '' when it listed every file. */
export function pushMoreWords(push: WorkspacePush): string {
  if (push.more === true) return 'and more'
  return typeof push.more === 'number' && push.more > 0 ? `and ${push.more} more` : ''
}

// --- the recorded changes, for a replay ----------------------------------------------------------

/** One page of `GET /api/file-changes`: each change is a push's shape with its row's id and workflow. */
export interface FileChangesPage {
  changes: unknown[]
  next: number | null
}

/** How many recorded changes one replay reads at most: the route pages them, this bounds the pages. */
export const MaxRecordedChanges = 10_000
const RecordedPage = 500

/** What a replay read: its changes, and whether it stopped before the period's last one. */
export interface RecordedChanges {
  changes: WorkspacePush[]
  /** True when the read stopped with pages still to come, at its bound or at a cursor that did not move on. */
  cut: boolean
}

/**
 * Every recorded change of a period, oldest first, paged with `after`/`next` until `next` is null,
 * each read as a push; one that is not a push is passed over, as a push from the hub would be.
 * A read that stops before `next` is null says so in `cut`, so the replay never stops silently.
 */
export async function readRecorded(
  readPage: (after: number, take: number) => Promise<FileChangesPage>,
  max = MaxRecordedChanges,
): Promise<RecordedChanges> {
  const pushes: WorkspacePush[] = []
  let after = 0
  let read = 0
  for (;;) {
    const page = await readPage(after, RecordedPage)
    const changes = Array.isArray(page.changes) ? page.changes : []
    for (const raw of changes) {
      const push = readPush(raw)
      if (push) pushes.push(push)
    }
    read += changes.length
    const next = page.next
    if (typeof next !== 'number') return { changes: pushes, cut: false }
    // A cursor that does not move on would read the same page for ever.
    if (next <= after || read >= max) return { changes: pushes, cut: true }
    after = next
  }
}

// --- settings ------------------------------------------------------------------------------------

/** How far down the tree is drawn: the repository alone, its projects too, or every file. */
export type FileDepth = 'repository' | 'project' | 'file'
export const FileDepths: readonly FileDepth[] = ['repository', 'project', 'file']

export interface FileSettings {
  /** Off: no repository, project or file node, no line to them, and no line in the activity list. */
  show: boolean
  depth: FileDepth
}

export const DefaultFileSettings: FileSettings = { show: true, depth: 'file' }

/** Remembered per browser, a convenience only: storage may be missing or refuse, and the defaults stand. */
export const FileSettingsKey = 'harness.monitor.fileChanges'

export function readFileSettings(): FileSettings {
  try {
    const raw: unknown = JSON.parse(localStorage.getItem(FileSettingsKey) ?? 'null')
    if (typeof raw !== 'object' || raw === null) return { ...DefaultFileSettings }
    const saved = raw as Record<string, unknown>
    return {
      show: typeof saved.show === 'boolean' ? saved.show : DefaultFileSettings.show,
      depth: FileDepths.includes(saved.depth as FileDepth) ? (saved.depth as FileDepth) : DefaultFileSettings.depth,
    }
  } catch {
    return { ...DefaultFileSettings }
  }
}

export function writeFileSettings(settings: FileSettings) {
  try {
    localStorage.setItem(FileSettingsKey, JSON.stringify(settings))
  } catch {
    // Remembered only while this page is open.
  }
}

// --- the hub -------------------------------------------------------------------------------------

/** The part of a hub connection the watch needs. */
export interface FileWatchHub {
  invoke(method: string, ...args: unknown[]): Promise<unknown>
}

/**
 * THE FILE WATCH: the hub group whose members hear `workspaceChanged`, and `agentSteps` with it. The page owns the
 * connection; the Monitor owns the wish to hear. Joined while anyone is listening and a connection
 * is here, again after a reconnect (group membership lives on the connection id, which a reconnect
 * replaces), and left when the last listener goes. A join that fails leaves the Monitor without
 * file changes and nothing else: it is a convenience, like the person's own group.
 */
export class FileWatch {
  private hub: FileWatchHub | null = null
  private joined = false
  private readonly listeners = new Set<(push: WorkspacePush) => void>()
  private readonly stepListeners = new Set<(push: StepsPush) => void>()

  /** The page's connection is up: join if the Monitor is already open. */
  attach(hub: FileWatchHub) {
    this.hub = hub
    this.joined = false
    void this.sync()
  }

  /** The connection is gone; nothing to leave on it. */
  detach() {
    this.hub = null
    this.joined = false
  }

  /** After a reconnect: the old membership went with the old connection id. */
  rejoin() {
    this.joined = false
    void this.sync()
  }

  /** Hear each push until the returned function is called. */
  listen(listener: (push: WorkspacePush) => void): () => void {
    this.listeners.add(listener)
    void this.sync()
    return () => {
      this.listeners.delete(listener)
      void this.sync()
    }
  }

  /** Hear each `agentSteps` push until the returned function is called. */
  listenSteps(listener: (push: StepsPush) => void): () => void {
    this.stepListeners.add(listener)
    void this.sync()
    return () => {
      this.stepListeners.delete(listener)
      void this.sync()
    }
  }

  /** A push from the hub, as it arrived. */
  deliver(raw: unknown) {
    const push = readPush(raw)
    if (!push) return
    for (const listener of [...this.listeners]) listener(push)
  }

  /** An `agentSteps` push from the hub, as it arrived. */
  deliverSteps(raw: unknown) {
    const push = readStepsPush(raw)
    if (!push || push.steps.length === 0) return
    for (const listener of [...this.stepListeners]) listener(push)
  }

  private async sync() {
    const hub = this.hub
    if (!hub) return
    const wanted = this.listeners.size > 0 || this.stepListeners.size > 0
    if (wanted === this.joined) return
    this.joined = wanted
    try {
      await hub.invoke(wanted ? JoinFileWatch : LeaveFileWatch)
    } catch {
      // Nothing to show for it: the Monitor simply draws no file changes.
      if (hub === this.hub && wanted) this.joined = false
    }
  }
}

/** The one watch for this page: `IndexPage` attaches its connection, the Monitor listens. */
export const fileWatch = new FileWatch()

// --- the tree in display time --------------------------------------------------------------------

export type WorkspaceKind = 'repository' | 'project' | 'file'

/** How long a mark fades in, and over how much of its end it fades out. */
export const MarkInMs = 300
export const MarkFadeMs = 4000
/** How long a commit's sha and subject show on its repository. */
export const CommitFlashMs = 5000

/** How many of a project's changed files are drawn as nodes at once: the newest. */
export const MaxFileNodesPerProject = 8

/**
 * How strongly a mark shows `age` ms after its latest change with `left` ms of its time still to
 * go: in quickly, out over its last seconds. The plugin changes' marks fade by the same rule.
 * Under reduced motion it is simply there until its time is up.
 */
export function markAlpha(age: number, left: number, ttl: number, reducedMotion: boolean): number {
  if (left <= 0) return 0
  if (reducedMotion) return 1
  return Math.max(0, Math.min(1, age / MarkInMs, left / Math.min(MarkFadeMs, ttl * 0.4)))
}

interface FileRecord {
  team: string
  repo: string
  project: string
  path: string
  action: FileAction
  /** Display time of its latest change, and when it goes. */
  latest: number
  until: number
  ttl: number
  /** The members that touched it, by `<team>/<id>`. */
  members: Set<string>
}

interface CommitFlash {
  team: string
  repo: string
  sha: string
  subject: string
  start: number
}

/** A node of the tree as it is to be drawn now. */
export interface WorkspaceNode {
  id: string
  kind: WorkspaceKind
  team: string
  /** The node it hangs off: the team's (`t:<team>`), a repository's or a project's id. */
  parent: string
  repo: string
  project: string
  /** The words beside it: the repository's name, the project's, the file's name. */
  label: string
  /** A file's whole path; '' for a repository or project. */
  path: string
  /** A file's latest action; null for a repository or project. */
  action: FileAction | null
  alpha: number
  /** Members that touched it (a file) or any file under it. */
  members: string[]
  /** Files under it now: 1 for a file. */
  files: number
}

/** A line from a member to what it touched, at the depth drawn. */
export interface WorkspaceTouch {
  member: string
  node: string
  action: FileAction | null
  alpha: number
}

export interface WorkspaceShown {
  id: string
  sha: string
  subject: string
  /** The repository node it flashes on. */
  node: string
  /** 0..1 through its time. */
  t: number
}

export interface WorkspaceView {
  nodes: WorkspaceNode[]
  touches: WorkspaceTouch[]
  flashes: WorkspaceShown[]
}

const Sep = '\u0001'

export function repoId(team: string, repo: string): string {
  return `r:${team}${Sep}${repo}`
}

export function projectId(team: string, repo: string, project: string): string {
  return `p:${team}${Sep}${repo}${Sep}${project}`
}

export function fileId(team: string, repo: string, project: string, path: string): string {
  return `f:${team}${Sep}${repo}${Sep}${project}${Sep}${path}`
}

export class WorkspaceGraph {
  private readonly files = new Map<string, FileRecord>()
  private flashes: CommitFlash[] = []
  private readonly repoUntil = new Map<string, number>()

  /** One push at display time `now`; each file stays `ttl` ms from its latest change. */
  apply(push: WorkspacePush, now: number, ttl: number, member: string | null) {
    for (const file of filesOf(push)) {
      const id = fileId(push.team, push.repo, file.project, file.path)
      const known = this.files.get(id)
      if (known) {
        known.action = file.action
        known.latest = now
        known.until = now + ttl
        known.ttl = ttl
        if (member) known.members.add(member)
      } else {
        this.files.set(id, {
          team: push.team,
          repo: push.repo,
          project: file.project,
          path: file.path,
          action: file.action,
          latest: now,
          until: now + ttl,
          ttl,
          members: new Set(member ? [member] : []),
        })
      }
    }
    if (push.commits.length > 0) {
      const id = repoId(push.team, push.repo)
      // The repository shows for its commit's flash even when the commit lists no file.
      this.repoUntil.set(id, Math.max(this.repoUntil.get(id) ?? 0, now + Math.min(ttl, CommitFlashMs)))
      for (const commit of push.commits) {
        this.flashes.push({ team: push.team, repo: push.repo, sha: shortSha(commit.sha), subject: commit.subject, start: now })
      }
    }
  }

  /** Forget what has run out by `now`. */
  tidy(now: number) {
    for (const [id, file] of this.files) if (file.until <= now) this.files.delete(id)
    for (const [id, until] of this.repoUntil) if (until <= now) this.repoUntil.delete(id)
    this.flashes = this.flashes.filter((flash) => now - flash.start < CommitFlashMs)
  }

  clear() {
    this.files.clear()
    this.repoUntil.clear()
    this.flashes = []
  }

  get size(): number {
    return this.files.size
  }

  /** What is drawn at `now`, to the depth asked; nothing with the setting off. */
  view(now: number, settings: FileSettings, reducedMotion: boolean): WorkspaceView {
    this.tidy(now)
    const view: WorkspaceView = { nodes: [], touches: [], flashes: [] }
    if (!settings.show) return view

    interface Group {
      node: Omit<WorkspaceNode, 'alpha' | 'members' | 'files'>
      latest: number
      until: number
      ttl: number
      members: Set<string>
      files: number
    }
    const groups = new Map<string, Group>()
    const group = (id: string, node: Group['node'], file: FileRecord) => {
      let found = groups.get(id)
      if (!found) {
        found = { node, latest: file.latest, until: file.until, ttl: file.ttl, members: new Set(), files: 0 }
        groups.set(id, found)
      }
      found.latest = Math.max(found.latest, file.latest)
      if (file.until > found.until) {
        found.until = file.until
        found.ttl = file.ttl
      }
      for (const member of file.members) found.members.add(member)
      found.files++
      return found
    }

    // ONLY THE NEWEST FILES OF EACH PROJECT ARE NODES. A first commit or a package install touches
    // hundreds of files at once; drawn one each they crowd the graph and shake against each other.
    // The rest count on their project's node (its `files`) until they too go.
    const shownFiles = new Set<string>()
    if (settings.depth === 'file') {
      const byParent = new Map<string, [string, FileRecord][]>()
      for (const entry of this.files) {
        const file = entry[1]
        const parent = file.project === '' ? repoId(file.team, file.repo) : projectId(file.team, file.repo, file.project)
        byParent.set(parent, [...(byParent.get(parent) ?? []), entry])
      }
      for (const entries of byParent.values()) {
        entries.sort((a, b) => b[1].latest - a[1].latest)
        for (const [id] of entries.slice(0, MaxFileNodesPerProject)) shownFiles.add(id)
      }
    }

    const touches = new Map<string, WorkspaceTouch & { latest: number; until: number; ttl: number }>()
    for (const [id, file] of this.files) {
      const repo = repoId(file.team, file.repo)
      group(repo, { id: repo, kind: 'repository', team: file.team, parent: `t:${file.team}`, repo: file.repo, project: '', label: file.repo, path: '', action: null }, file)
      // A file outside any project hangs off its repository.
      const project = file.project === '' ? null : projectId(file.team, file.repo, file.project)
      if (project && settings.depth !== 'repository') {
        group(project, { id: project, kind: 'project', team: file.team, parent: repo, repo: file.repo, project: file.project, label: file.project, path: '', action: null }, file)
      }
      const asFile = settings.depth === 'file' && shownFiles.has(id)
      if (asFile) {
        group(id, { id, kind: 'file', team: file.team, parent: project ?? repo, repo: file.repo, project: file.project, label: fileName(file.path), path: file.path, action: file.action }, file)
      }
      const drawn = asFile ? id : settings.depth !== 'repository' ? (project ?? repo) : repo
      for (const member of file.members) {
        const key = `${member}|${drawn}`
        const known = touches.get(key)
        if (!known || file.latest >= known.latest) {
          touches.set(key, {
            member,
            node: drawn,
            action: asFile ? file.action : null,
            alpha: 0,
            latest: Math.max(file.latest, known?.latest ?? 0),
            until: Math.max(file.until, known?.until ?? 0),
            ttl: file.ttl,
          })
        } else {
          known.until = Math.max(known.until, file.until)
        }
      }
    }

    // A repository kept only by a commit's flash, with no file of its own left.
    for (const flash of this.flashes) {
      const id = repoId(flash.team, flash.repo)
      const until = this.repoUntil.get(id)
      if (groups.has(id) || until === undefined) continue
      groups.set(id, {
        node: { id, kind: 'repository', team: flash.team, parent: `t:${flash.team}`, repo: flash.repo, project: '', label: flash.repo, path: '', action: null },
        latest: flash.start,
        until,
        ttl: until - flash.start,
        members: new Set(),
        files: 0,
      })
    }

    for (const found of groups.values()) {
      view.nodes.push({
        ...found.node,
        alpha: markAlpha(now - found.latest, found.until - now, found.ttl, reducedMotion),
        members: [...found.members].sort(),
        files: found.files,
      })
    }
    for (const touch of touches.values()) {
      view.touches.push({
        member: touch.member,
        node: touch.node,
        action: touch.action,
        alpha: markAlpha(now - touch.latest, touch.until - now, touch.ttl, reducedMotion),
      })
    }
    this.flashes.forEach((flash, index) => {
      const node = repoId(flash.team, flash.repo)
      if (!groups.has(node)) return
      view.flashes.push({ id: `${node}${Sep}${flash.sha}${Sep}${index}`, sha: flash.sha, subject: flash.subject, node, t: (now - flash.start) / CommitFlashMs })
    })
    return view
  }
}
