// @vitest-environment happy-dom
//
// The Monitor's layout forces: the multipliers a person tunes, how they are kept, and that the
// scene really moves to them.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { LayoutDefaults, LayoutKeyName, LayoutMax, LayoutMin, clampLayout, readLayout, writeLayout } from '../monitor/layout'
import { MonitorModel } from '../monitor/model'
import { MonitorScene } from '../monitor/scene'

afterEach(() => {
  vi.restoreAllMocks()
  localStorage.removeItem(LayoutKeyName)
})

describe('the layout settings', () => {
  it('reads the defaults with nothing kept, and what was written', () => {
    expect(readLayout()).toEqual(LayoutDefaults)
    writeLayout({ ...LayoutDefaults, distance: 1.5, friction: 0.5 })
    expect(readLayout()).toEqual({ ...LayoutDefaults, distance: 1.5, friction: 0.5 })
  })

  it('brings a kept value inside its range, and reads anything that is not a number as the default', () => {
    localStorage.setItem(LayoutKeyName, JSON.stringify({ distance: 99, strength: 0, repulsion: 'x', gravity: '1.25' }))
    expect(readLayout()).toEqual({ ...LayoutDefaults, distance: LayoutMax, strength: LayoutMin, gravity: 1.25 })
    expect(clampLayout(1.234)).toBe(1.25)
  })

  it('reads the defaults when storage refuses, and writing then throws nothing', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied')
    })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('denied')
    })
    expect(readLayout()).toEqual(LayoutDefaults)
    expect(() => writeLayout({ ...LayoutDefaults, gravity: 2 })).not.toThrow()
  })
})

describe('the scene under a layout', () => {
  /** How far apart a team's members settle: the mean distance between their centres. */
  function spread(distance: number): number {
    let seed = 11
    vi.spyOn(Math, 'random').mockImplementation(() => ((seed = (seed * 16807) % 2147483647) - 1) / 2147483646)
    const canvas = document.createElement('canvas')
    document.body.appendChild(canvas)
    const scene = new MonitorScene(canvas, { fontPx: 12, fontFamily: 'sans-serif', reducedMotion: true })
    scene.setLayout({ ...LayoutDefaults, distance })
    const model = new MonitorModel()
    for (const name of ['Manager', 'Developer', 'Tester', 'Designer']) model.member('Alpha', name, name)
    scene.sync(model)
    for (let now = 0; now <= 6000; now += 50) scene.step(now)
    const centres = scene.memberBoxes().filter((_, i) => i % 2 === 0).map((box) => ({ x: (box.left + box.right) / 2, y: (box.top + box.bottom) / 2 }))
    let total = 0
    let pairs = 0
    for (let i = 0; i < centres.length; i++)
      for (let j = i + 1; j < centres.length; j++) {
        total += Math.hypot(centres[i]!.x - centres[j]!.x, centres[i]!.y - centres[j]!.y)
        pairs++
      }
    vi.restoreAllMocks()
    return total / pairs
  }

  it('spreads a team further with a longer link distance', () => {
    expect(spread(2)).toBeGreaterThan(spread(1) * 1.3)
  })
})
