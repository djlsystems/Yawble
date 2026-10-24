// @vitest-environment happy-dom
//
// THE LOGO ANIMATION: five quick clicks on the header's mark open it, and its graph grows in order -
// the mark, then Projects and Workflows off it, then each one's three nodes; then, after a hold, everything
// off the mark fades as the mark grows, disc and ring and all, over its sign-off.
import { describe, expect, it } from 'vitest'
import { arrival, logoLayout, CloseAfterMs, FinaleAt, SignOffShownAt, GrowMs, HoldMs, LogoClickGapMs, LogoClicks, LogoGraph, LogoStage, finaleAt, nextClick, shownAt, type ClickRun } from '../logoAnimation'

describe('the clicks that open it', () => {
  const run = (times: number[]) => times.reduce<ClickRun>((acc, at) => nextClick(acc, at), { count: 0, last: -Infinity })

  it('counts clicks in quick succession', () => {
    expect(run([0, 300, 600, 900, 1200]).count).toBe(LogoClicks)
  })

  it('starts again after a pause longer than the gap', () => {
    expect(run([0, 300, 600, 600 + LogoClickGapMs + 1]).count).toBe(1)
  })
})

describe('the graph', () => {
  it('has the mark at its middle, Projects and Workflows off it, and three nodes off each', () => {
    const children = (parent: string) => LogoGraph.filter((node) => node.parent === parent).map((node) => node.label)
    expect(LogoGraph.filter((node) => node.parent === null).map((node) => node.kind)).toEqual(['core'])
    expect(children('core')).toEqual(['Projects', 'Workflows'])
    expect(children('projects')).toEqual(['SaaS apps', 'Web sites', 'Plugins'])
    expect(children('workflows')).toEqual(['Customer Support', 'Fulfillment', 'Research'])
  })

  it('grows each node after its parent, its line growing first', () => {
    for (const node of LogoGraph) {
      const parent = LogoGraph.find((other) => other.id === node.parent)
      if (parent) expect(node.at - GrowMs).toBeGreaterThan(parent.at)
    }
    expect(shownAt(0).born).toEqual(['core'])
    const projects = LogoGraph.find((node) => node.id === 'projects')!
    expect(shownAt(projects.at - 1).growing).toContain('projects')
    expect(shownAt(projects.at).born).toContain('projects')
    expect(shownAt(10_000).born).toHaveLength(LogoGraph.length)
  })

  it('sends each leaf its own glowing tip, fanned away from the mark, and bears it where the tip arrived', () => {
    for (const hub of ['projects', 'workflows']) {
      const parent = hub === 'projects' ? { x: -190, y: -70 } : { x: 190, y: 70 }
      const tips = LogoGraph.filter((node) => node.parent === hub).map((node) => arrival(node, parent))
      expect(tips).toHaveLength(3)
      for (const [i, a] of tips.entries()) {
        for (const b of tips.slice(i + 1)) expect(Math.hypot(a.x - b.x, a.y - b.y)).toBeGreaterThan(60)
        // Further from the mark than its parent: the fan opens outward.
        expect(Math.hypot(a.x, a.y)).toBeGreaterThan(Math.hypot(parent.x, parent.y))
      }
    }
  })

  it('settles every node in its place before anything is drawn, none on another and every leaf beyond its hub', () => {
    const layout = logoLayout()
    const places = LogoGraph.map((node) => ({ node, at: layout.get(node.id)! }))
    expect(places.every(({ at }) => Number.isFinite(at.x) && Number.isFinite(at.y))).toBe(true)
    expect(layout.get('core')).toEqual({ x: 0, y: 0 })
    for (const [i, a] of places.entries()) {
      for (const b of places.slice(i + 1)) expect(Math.hypot(a.at.x - b.at.x, a.at.y - b.at.y)).toBeGreaterThan(50)
      if (a.node.kind === 'leaf') {
        const hub = layout.get(a.node.parent!)!
        expect(Math.hypot(a.at.x, a.at.y)).toBeGreaterThan(Math.hypot(hub.x, hub.y))
      }
    }
  })

  it('puts each node on the stage when it is due, driven by hand', () => {
    const canvas = document.createElement('canvas')
    const stage = new LogoStage(canvas, { fontFamily: 'sans-serif', fontPx: 14, reducedMotion: true, mark: null, signOff: { name: 'Name.', line: 'A line.', address: 'example.test' } })
    stage.resize(800, 600, 1)
    stage.step(1000)
    expect(stage.shown()).toEqual(['core'])
    stage.step(1000 + 2200)
    expect(stage.shown()).toEqual(['core', 'projects', 'workflows'])
    stage.step(1000 + 6000)
    expect(stage.shown()).toHaveLength(LogoGraph.length)
    stage.destroy()
  })
})

describe('the close', () => {
  it('holds the whole picture, then fades the rest as it grows the mark, and shows the sign-off', () => {
    const lastBorn = Math.max(...LogoGraph.map((node) => node.at))
    expect(FinaleAt - lastBorn).toBeGreaterThanOrEqual(HoldMs)
    expect(finaleAt(FinaleAt - 1)).toEqual({ others: 0, expand: 0, signOff: 0 })

    const order = (part: keyof ReturnType<typeof finaleAt>) => {
      for (let t = FinaleAt; t < FinaleAt + 20_000; t += 50) if (finaleAt(t)[part] >= 1) return t
      return Infinity
    }
    expect(finaleAt(FinaleAt + 200).others).toBeGreaterThan(0)
    expect(finaleAt(FinaleAt + 200).expand).toBeGreaterThan(0)
    expect(order('expand')).toBeLessThanOrEqual(order('signOff'))
    expect(finaleAt(SignOffShownAt).signOff).toBe(1)
    expect(CloseAfterMs - SignOffShownAt).toBe(4000)
    expect(finaleAt(FinaleAt + 20_000)).toEqual({ others: 1, expand: 1, signOff: 1 })
  })
})
