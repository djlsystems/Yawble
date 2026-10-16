// @vitest-environment happy-dom
//
// FILE CHANGES: a `workspaceChanged` push draws the member's files as a repository node off the
// team, a project node off the repository and a file node off the project, a line from the member
// to each file, tinted by what happened, for the plugin changes' time to live in display time. The
// push is built here as the Host sends it. The Host records each during every run, and a replay
// reads them back (`GET /api/file-changes`) and plays each at its time beside the log rows.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { MonitorModel, ReplayClock, type MonitorEffect } from '../monitor/model'
import type { Message } from '../../api/types'
import { MonitorScene, type Box } from '../monitor/scene'
import { DefaultSettings } from '../monitor/hints'
import {
  DefaultFileSettings,
  FileWatch,
  MaxFileNodesPerProject,
  JoinFileWatch,
  LeaveFileWatch,
  WorkspaceChanged,
  fileId,
  projectId,
  pushHeadline,
  pushMoreWords,
  readPush,
  readRecorded,
  type FileChangesPage,
  repoId,
  type FileSettings,
  type WorkspacePush,
} from '../monitor/workspace'

/** A push as the Host sends it on `workspaceChanged`. */
function push(overrides: Partial<WorkspacePush> = {}): WorkspacePush {
  return {
    team: 'Alpha',
    member: 'DeveloperMaren',
    repo: 'web',
    at: '2026-10-15T10:42:00Z',
    files: [
      { path: 'src/lib/parser.ts', project: 'harness-web', action: 'changed' },
      { path: 'src/lib/lexer.ts', project: 'harness-web', action: 'created' },
      { path: 'src/lib/old.ts', project: 'harness-web', action: 'deleted' },
    ],
    commits: [],
    more: false,
    ...overrides,
  }
}

const commitPush = () =>
  push({
    files: [],
    commits: [
      {
        sha: '1a2b3c4d5e6f7a8b9c0d',
        subject: 'Fix the parser',
        files: [
          { path: 'src/lib/parser.ts', project: 'harness-web', action: 'changed' },
          { path: 'src/lib/lexer.ts', project: 'harness-web', action: 'created' },
          { path: 'cli/main.go', project: 'cli', action: 'changed' },
          { path: 'README.md', project: '', action: 'changed' },
        ],
      },
    ],
  })

afterEach(() => {
  vi.restoreAllMocks()
})

const repo = repoId('Alpha', 'web')
const project = projectId('Alpha', 'web', 'harness-web')
const parser = fileId('Alpha', 'web', 'harness-web', 'src/lib/parser.ts')

function setup(options: { reducedMotion?: boolean; seconds?: number; files?: FileSettings } = {}) {
  const canvas = document.createElement('canvas')
  document.body.appendChild(canvas)
  const scene = new MonitorScene(canvas, { fontPx: 12, fontFamily: 'sans-serif', reducedMotion: options.reducedMotion === true })
  const model = new MonitorModel()
  model.settings = { ...DefaultSettings, seconds: options.seconds ?? 10 }
  model.fileSettings = options.files ?? DefaultFileSettings
  model.member('Alpha', 'Manager', 'Manager')
  model.member('Alpha', 'DeveloperMaren', 'Developer')
  model.member('Alpha', 'TesterIvo', 'Tester')
  scene.setFileChanges(model.fileSettings)
  scene.sync(model)
  let now = 0
  scene.step(now)
  const at = (ms: number) => {
    while (now < ms) scene.step((now = Math.min(ms, now + 50)))
  }
  const play = (made: MonitorEffect[]) => {
    scene.play(made)
    scene.sync(model)
  }
  return { scene, model, at, play }
}

const ids = (scene: MonitorScene) => scene.shownWorkspace().nodes.map((node) => node.id).sort()
const node = (scene: MonitorScene, id: string) => scene.shownWorkspace().nodes.find((found) => found.id === id)

function overlaps(a: Box, b: Box): boolean {
  return a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom
}

describe('a workspaceChanged push', () => {
  it('draws a repository off the team, a project off it and each file off the project, the member linked to each file', () => {
    const { scene, model, at, play } = setup()
    play(model.applyWorkspace(push()))
    at(500)

    const shown = scene.shownWorkspace()
    expect(shown.nodes.map((found) => [found.kind, found.label, found.parent])).toEqual([
      ['repository', 'web', 't:Alpha'],
      ['project', 'harness-web', repo],
      ['file', 'parser.ts', project],
      ['file', 'lexer.ts', project],
      ['file', 'old.ts', project],
    ])
    expect(shown.touches.map((touch) => [touch.member, touch.node])).toEqual(
      ['parser.ts', 'lexer.ts', 'old.ts'].map((name) => ['Alpha/DeveloperMaren', fileId('Alpha', 'web', 'harness-web', `src/lib/${name}`)]),
    )
  })

  it('tints each file by its action, as plugin changes are, and puts its path in the tooltip', () => {
    const { scene, model, at, play } = setup()
    play(model.applyWorkspace(push()))
    at(500)

    expect(scene.shownWorkspace().nodes.filter((found) => found.kind === 'file').map((found) => [found.label, found.action])).toEqual([
      ['parser.ts', 'changed'],
      ['lexer.ts', 'created'],
      ['old.ts', 'deleted'],
    ])
    expect(scene.describeNode(parser)).toMatchObject({
      kind: 'file',
      title: 'parser.ts',
      subtitle: 'src/lib/parser.ts',
      lines: ['changed · harness-web in web', 'by Developer'],
    })
    expect(scene.describeNode(project)).toMatchObject({ title: 'harness-web', subtitle: 'project in web', lines: ['3 files changed', 'by Developer'] })
  })

  it('refreshes a file touched again in place, never drawing it twice', () => {
    const { scene, model, at, play } = setup()
    play(model.applyWorkspace(push()))
    at(8000)
    const fading = node(scene, parser)!.alpha
    expect(fading).toBeLessThan(1)

    play(model.applyWorkspace(push({ files: [{ path: 'src/lib/parser.ts', project: 'harness-web', action: 'deleted' }] })))
    at(8500)

    expect(ids(scene).filter((id) => id === parser)).toHaveLength(1)
    expect(node(scene, parser)).toMatchObject({ action: 'deleted', alpha: 1 })
    // Its time runs from its latest change: still there after the first push's ten seconds.
    at(12_000)
    expect(ids(scene)).toEqual([parser, project, repo].sort())
  })

  it('is gone at the time to live, faded over its last seconds', () => {
    const { scene, model, at, play } = setup()
    play(model.applyWorkspace(push()))

    at(1000)
    expect(scene.shownWorkspace().nodes.every((found) => found.alpha === 1)).toBe(true)
    at(8000)
    const alpha = node(scene, parser)!.alpha
    expect(alpha).toBeGreaterThan(0)
    expect(alpha).toBeLessThan(1)
    at(9950)
    expect(ids(scene)).toHaveLength(5)
    at(10_050)
    expect(scene.shownWorkspace()).toEqual({ nodes: [], touches: [], flashes: [] })
  })

  it('draws with no fade under reduced motion', () => {
    const { scene, model, at, play } = setup({ reducedMotion: true })
    play(model.applyWorkspace(push()))

    at(50)
    expect(node(scene, parser)!.alpha).toBe(1)
    at(9950)
    expect(scene.shownWorkspace().nodes.map((found) => found.alpha)).toEqual([1, 1, 1, 1, 1])
    at(10_050)
    expect(scene.shownWorkspace().nodes).toEqual([])
  })

  it('keeps a project and its repository while any of their files is there', () => {
    const { scene, model, at, play } = setup()
    play(model.applyWorkspace(push()))
    at(6000)
    play(model.applyWorkspace(push({ member: 'TesterIvo', files: [{ path: 'src/lib/tests.ts', project: 'harness-web', action: 'created' }] })))

    at(10_500)
    const tests = fileId('Alpha', 'web', 'harness-web', 'src/lib/tests.ts')
    expect(ids(scene)).toEqual([tests, project, repo].sort())
    expect(scene.shownWorkspace().touches.map((touch) => touch.member)).toEqual(['Alpha/TesterIvo'])
    at(16_050)
    expect(ids(scene)).toEqual([])
  })

  it('flashes a commit on its repository with its short sha and subject, and adds its files under their projects', () => {
    const { scene, model, at, play } = setup()
    play(model.applyWorkspace(push({ files: [{ path: 'src/lib/parser.ts', project: 'harness-web', action: 'changed' }] })))
    at(500)
    play(model.applyWorkspace(commitPush()))
    at(1000)

    const shown = scene.shownWorkspace()
    expect(shown.flashes).toEqual([{ node: repo, sha: '1a2b3c4', subject: 'Fix the parser' }])
    expect(shown.nodes.map((found) => [found.kind, found.label, found.parent])).toEqual([
      ['repository', 'web', 't:Alpha'],
      ['project', 'harness-web', repo],
      ['file', 'parser.ts', project],
      ['file', 'lexer.ts', project],
      ['project', 'cli', repo],
      ['file', 'main.go', projectId('Alpha', 'web', 'cli')],
      // A file outside any project hangs off its repository.
      ['file', 'README.md', repo],
    ])
    expect(shown.nodes.filter((found) => found.label === 'parser.ts')).toHaveLength(1)
    at(6500)
    expect(scene.shownWorkspace().flashes).toEqual([])
  })

  it('fans several out so none covers another or any member or its name', () => {
    const { scene, model, at, play } = setup()
    play(model.applyWorkspace(push()))
    play(model.applyWorkspace(commitPush()))
    play(
      model.applyWorkspace(
        push({
          member: 'TesterIvo',
          files: Array.from({ length: 6 }, (_, i) => ({ path: `tests/case-${i}.spec.ts`, project: 'harness-web', action: 'created' as const })),
        }),
      ),
    )

    for (const moment of [300, 1500, 4000]) {
      at(moment)
      const nodes = scene.shownWorkspace().nodes
      // The repository, two projects and the newest MaxFileNodesPerProject files of the busy one.
      expect(nodes).toHaveLength(13)
      for (let i = 0; i < nodes.length; i++) {
        for (let j = i + 1; j < nodes.length; j++) expect(overlaps(nodes[i]!.box, nodes[j]!.box), `${nodes[i]!.label} / ${nodes[j]!.label}`).toBe(false)
        for (const box of scene.memberBoxes()) expect(overlaps(nodes[i]!.box, box), nodes[i]!.label).toBe(false)
      }
    }
  })
})

describe('a burst of files', () => {
  const burst = (count: number, at: string) =>
    push({ at, files: Array.from({ length: count }, (_, i) => ({ path: `src/gen/file-${i}.ts`, project: 'harness-web', action: 'created' as const })) })

  it("draws only each project's newest files as nodes and counts the rest on the project", () => {
    const { scene, model, at, play } = setup()
    play(model.applyWorkspace(burst(40, '2026-10-15T10:42:00Z')))
    at(500)

    const shown = scene.shownWorkspace().nodes
    expect(shown.filter((found) => found.kind === 'file')).toHaveLength(MaxFileNodesPerProject)
    expect(node(scene, project)!.files).toBe(40)
  })

  it('settles instead of shaking', () => {
    // The layout places a new node at a random angle: seeded here, so this is the same graph every run.
    let seed = 7
    vi.spyOn(Math, 'random').mockImplementation(() => ((seed = (seed * 16807) % 2147483647) - 1) / 2147483646)
    const { scene, model, at, play } = setup()
    play(model.applyWorkspace(burst(60, '2026-10-15T10:42:00Z')))
    play(model.applyWorkspace(push({ member: 'TesterIvo', files: Array.from({ length: 30 }, (_, i) => ({ path: `tests/t-${i}.spec.ts`, project: 'harness-tests', action: 'changed' as const })) })))
    at(4000)

    // SHAKING IS GOING BACK AND FORTH: a node's path over these frames much longer than the
    // distance it ends up from where it started. Drifting with the graph is a straight path.
    const path = new Map<string, number>()
    const start = new Map(scene.shownWorkspace().nodes.map((found) => [found.id, { x: found.shownX, y: found.shownY }]))
    let last = new Map(start)
    for (let frame = 1; frame <= 20; frame++) {
      at(4000 + frame * 50)
      const now = new Map(scene.shownWorkspace().nodes.map((found) => [found.id, { x: found.shownX, y: found.shownY }]))
      for (const [id, p] of now) {
        const was = last.get(id)
        if (was) path.set(id, (path.get(id) ?? 0) + Math.hypot(p.x - was.x, p.y - was.y))
      }
      last = now
    }
    let shake = 0
    for (const [id, length] of path) {
      const a = start.get(id)!
      const b = last.get(id)
      if (b) shake = Math.max(shake, length - Math.hypot(b.x - a.x, b.y - a.y))
    }
    expect(shake).toBeLessThan(3)
  })
})

describe('Draw down to', () => {
  function drawn(files: FileSettings) {
    const { scene, model, at, play } = setup({ files })
    play(model.applyWorkspace(commitPush()))
    at(500)
    const shown = scene.shownWorkspace()
    return { kinds: shown.nodes.map((found) => found.kind), touches: shown.touches.map((touch) => touch.node), flashes: shown.flashes.length }
  }

  it('repository draws the repository node only, the member linked to it', () => {
    expect(drawn({ show: true, depth: 'repository' })).toEqual({ kinds: ['repository'], touches: [repo], flashes: 1 })
  })

  it('project draws the repository and its projects, the member linked to each project', () => {
    expect(drawn({ show: true, depth: 'project' })).toEqual({
      kinds: ['repository', 'project', 'project'],
      touches: [project, projectId('Alpha', 'web', 'cli'), repo],
      flashes: 1,
    })
  })

  it('file draws all three', () => {
    expect(drawn({ show: true, depth: 'file' }).kinds).toEqual(['repository', 'project', 'file', 'file', 'project', 'file', 'file'])
  })

  it('applies to what is already shown', () => {
    const { scene, model, at, play } = setup()
    play(model.applyWorkspace(push()))
    at(500)
    expect(ids(scene)).toHaveLength(5)

    scene.setFileChanges({ show: true, depth: 'project' })
    at(600)
    expect(ids(scene)).toEqual([project, repo].sort())

    scene.setFileChanges({ show: true, depth: 'file' })
    at(700)
    expect(ids(scene)).toHaveLength(5)
  })
})

describe('Show file changes off', () => {
  it('draws none of these nodes and none of their lines, and lists nothing', () => {
    const { scene, model, at, play } = setup({ files: { show: false, depth: 'file' } })
    const lines = model.ticker.length

    expect(model.applyWorkspace(push())).toEqual([])
    expect(model.ticker).toHaveLength(lines)
    at(500)
    expect(scene.shownWorkspace()).toEqual({ nodes: [], touches: [], flashes: [] })

    // Switched off with some shown: they go at once.
    model.fileSettings = DefaultFileSettings
    scene.setFileChanges(DefaultFileSettings)
    play(model.applyWorkspace(push()))
    at(1000)
    expect(ids(scene)).toHaveLength(5)
    scene.setFileChanges({ show: false, depth: 'file' })
    at(1100)
    expect(scene.shownWorkspace()).toEqual({ nodes: [], touches: [], flashes: [] })
  })
})

describe('a replay', () => {
  const row = (seq: number, type: string, occurredAt: string): Message => ({
    seq, type, source: 'Alpha/DeveloperMaren', payload: '{}', correlationId: 7, causationSeq: null, depth: 0, occurredAt,
  })
  /** A period of the log, and the changes the Host recorded in it, as the route answers them. */
  const period = () => ({
    rows: [row(1, 'agentContainer.started', '2026-10-15T10:42:00Z'), row(2, 'agentContainer.completed', '2026-10-15T10:42:30Z')],
    changes: [
      { id: 11, correlation: 7, ...push({ at: '2026-10-15T10:42:10Z' }) },
      { id: 12, correlation: 7, ...commitPush(), at: '2026-10-15T10:42:20Z' },
    ],
  })

  /** Plays a period through the clock as the Monitor does, one `wallMs` frame at a time. */
  function replayer(options: { files?: FileSettings } = {}) {
    const made = setup(options)
    made.model.replay = true
    const { rows, changes } = period()
    const recorded = changes.map((change) => readPush(change)!)
    const clock = new ReplayClock(rows, 1, 20_000, Date.parse(rows[0]!.occurredAt), recorded)
    let wall = 0
    const until = (logTime: string) => {
      while (clock.at < Date.parse(logTime) && !clock.done) {
        const due = clock.step(100)
        for (const message of due.rows) made.play(made.model.apply(message))
        for (const change of due.changes) made.play(made.model.applyWorkspace(change))
        made.at((wall += 100))
      }
    }
    return { ...made, clock, until }
  }

  it('reads a period of recorded changes oldest first, page after page until next is null', async () => {
    const { changes } = period()
    const pages: Record<number, FileChangesPage> = {
      0: { changes: [changes[0]], next: 11 },
      11: { changes: [changes[1], { id: 13, team: '', member: 'x', repo: 'y' }], next: 13 },
      13: { changes: [], next: null },
    }
    const asked: number[] = []
    const read = await readRecorded(async (after) => {
      asked.push(after)
      return pages[after]!
    })

    expect(asked).toEqual([0, 11, 13])
    expect(read.cut).toBe(false)
    expect(read.changes.map((change) => change.at)).toEqual(['2026-10-15T10:42:10Z', '2026-10-15T10:42:20Z'])
    expect(read.changes[1]!.commits[0]).toMatchObject({ sha: '1a2b3c4d5e6f7a8b9c0d', subject: 'Fix the parser' })
  })

  it('stops at a cursor that does not move on, and at its bound, and says it stopped short', async () => {
    const one = { changes: [period().changes[0]], next: 5 }
    let reads = 0
    const stuck = await readRecorded(async () => {
      reads++
      return one
    })
    expect(reads).toBe(2)
    expect(stuck.cut).toBe(true)

    reads = 0
    let next = 0
    const bounded = await readRecorded(async () => {
      reads++
      return { changes: [period().changes[0]], next: ++next }
    }, 3)
    expect(reads).toBe(3)
    expect(bounded.cut).toBe(true)
  })

  it('draws the repository, project and file nodes and the commit at their times, and lists them', () => {
    const { scene, model, until } = replayer()

    until('2026-10-15T10:42:09Z')
    expect(scene.shownWorkspace().nodes).toEqual([])
    expect(model.ticker.filter((line) => line.workspace)).toEqual([])

    until('2026-10-15T10:42:10Z')
    expect(ids(scene)).toEqual([repo, project, parser, fileId('Alpha', 'web', 'harness-web', 'src/lib/lexer.ts'), fileId('Alpha', 'web', 'harness-web', 'src/lib/old.ts')].sort())
    expect(scene.shownWorkspace().touches.map((touch) => touch.member)).toContain('Alpha/DeveloperMaren')
    expect(scene.shownWorkspace().flashes).toEqual([])

    until('2026-10-15T10:42:20Z')
    expect(scene.shownWorkspace().flashes).toMatchObject([{ node: repo, sha: '1a2b3c4', subject: 'Fix the parser' }])
    expect(ids(scene)).toContain(projectId('Alpha', 'web', 'cli'))

    const lines = model.ticker.filter((line) => line.workspace)
    expect(lines.map((line) => [line.verb, line.text, line.at])).toEqual([
      ['committed', '1a2b3c4 Fix the parser', Date.parse('2026-10-15T10:42:20Z')],
      ['changed files', '', Date.parse('2026-10-15T10:42:10Z')],
    ])
    expect(lines[1]!.files).toEqual([
      { path: 'src/lib/parser.ts', action: 'changed' },
      { path: 'src/lib/lexer.ts', action: 'created' },
      { path: 'src/lib/old.ts', action: 'deleted' },
    ])
  })

  it('draws only as far down as the setting says', () => {
    const { scene, until } = replayer({ files: { show: true, depth: 'project' } })
    until('2026-10-15T10:42:20Z')

    expect(scene.shownWorkspace().nodes.map((found) => found.kind)).not.toContain('file')
    expect(ids(scene)).toEqual([repo, project, projectId('Alpha', 'web', 'cli')].sort())
  })

  it('draws none of it and lists none of it with the setting off, and the rows still play', () => {
    const { scene, model, clock, until } = replayer({ files: { show: false, depth: 'file' } })
    until('2026-10-15T10:42:30Z')

    expect(clock.done).toBe(true)
    expect(scene.shownWorkspace()).toEqual({ nodes: [], touches: [], flashes: [] })
    expect(model.ticker.filter((line) => line.workspace)).toEqual([])
    expect(model.ticker.map((line) => line.verb)).toHaveLength(2)
  })

  it('clears what live drew when it starts', () => {
    const { scene, model, at, play } = setup()
    play(model.applyWorkspace(push()))
    at(500)

    scene.clearFileChanges()
    at(600)
    expect(scene.shownWorkspace()).toEqual({ nodes: [], touches: [], flashes: [] })
  })
})

describe('the activity list', () => {
  it('lists every file of a push by its path and what happened to it, under the member display name', () => {
    const model = new MonitorModel()
    model.member('Alpha', 'DeveloperMaren', 'Developer')
    model.applyWorkspace(push())

    expect(model.ticker).toHaveLength(1)
    expect(model.ticker[0]).toMatchObject({
      member: 'Developer',
      tone: 'file',
      text: '',
      workspace: true,
      files: [
        { path: 'src/lib/parser.ts', action: 'changed' },
        { path: 'src/lib/lexer.ts', action: 'created' },
        { path: 'src/lib/old.ts', action: 'deleted' },
      ],
      more: '',
    })
  })

  it('heads a push with a commit by its short sha and subject, and lists its files under it', () => {
    const model = new MonitorModel()
    model.member('Alpha', 'DeveloperMaren', 'Developer')
    model.applyWorkspace(commitPush())

    expect(model.ticker[0]).toMatchObject({ verb: 'committed', text: '1a2b3c4 Fix the parser' })
    expect(model.ticker[0]!.files).toHaveLength(4)
  })

  it('says what a push left off its list', () => {
    expect(pushMoreWords(push({ more: 4 }))).toBe('and 4 more')
    expect(pushMoreWords(push({ more: true }))).toBe('and more')
    expect(pushMoreWords(push())).toBe('')
    expect(pushHeadline(push())).toBe('')
  })

  it('keeps its lines when a live model starts again from the rows held', () => {
    const first = new MonitorModel(undefined, 80, new Set(['Alpha']))
    first.applyWorkspace(push())
    const again = new MonitorModel(undefined, 80, new Set(['Alpha']))
    again.keepPushLines(first.ticker)

    expect(again.ticker.map((line) => line.files?.map((file) => file.path))).toEqual([['src/lib/parser.ts', 'src/lib/lexer.ts', 'src/lib/old.ts']])
  })

  it('passes over a team the live Monitor does not show', () => {
    const model = new MonitorModel(undefined, 80, new Set(['Beta']))
    expect(model.applyWorkspace(push())).toEqual([])
    expect(model.ticker).toEqual([])
  })
})

describe('the file watch', () => {
  function hub() {
    return { invoke: vi.fn((..._args: unknown[]) => Promise.resolve()) }
  }

  it('joins with JoinFileWatch while the Monitor listens and leaves with LeaveFileWatch, no team argument', async () => {
    const watch = new FileWatch()
    const connection = hub()
    watch.attach(connection)
    expect(connection.invoke).not.toHaveBeenCalled()

    const stop = watch.listen(() => {})
    await Promise.resolve()
    expect(connection.invoke.mock.calls).toEqual([[JoinFileWatch]])

    stop()
    await Promise.resolve()
    expect(connection.invoke.mock.calls).toEqual([[JoinFileWatch], [LeaveFileWatch]])
    expect([JoinFileWatch, LeaveFileWatch, WorkspaceChanged]).toEqual(['JoinFileWatch', 'LeaveFileWatch', 'workspaceChanged'])
  })

  it('joins when the connection arrives after the Monitor opened, and again after a reconnect', async () => {
    const watch = new FileWatch()
    watch.listen(() => {})
    const connection = hub()
    watch.attach(connection)
    await Promise.resolve()
    watch.rejoin()
    await Promise.resolve()

    expect(connection.invoke.mock.calls).toEqual([[JoinFileWatch], [JoinFileWatch]])
  })

  it('hands each push to the Monitor, and passes over what is not one', () => {
    const watch = new FileWatch()
    const heard: WorkspacePush[] = []
    watch.listen((made) => heard.push(made))

    watch.deliver(push())
    watch.deliver({ team: 'Alpha' })
    watch.deliver('nonsense')
    watch.deliver({ ...push(), files: [{ path: 'a.ts', project: 'p', action: 'renamed' }, { path: 'b.ts', project: 'p', action: 'created' }] })

    expect(heard).toHaveLength(2)
    expect(heard[1]!.files).toEqual([{ path: 'b.ts', project: 'p', action: 'created' }])
    expect(readPush(push())).toEqual(push())
  })
})
