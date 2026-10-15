// @vitest-environment happy-dom
//
// MONITOR HINTS: what a plugin row whose type carries a monitor hint draws. The model resolves the
// hint against the row (icon, action, label) and yields a resource effect instead of the plain
// plugin burst; the scene shows it beside the member until its time to live, in display time.
import { describe, expect, it } from 'vitest'
import type { EventDefinition, Message, MonitorHint } from '../../api/types'
import { MonitorModel, type MonitorEffect } from '../monitor/model'
import { MonitorScene } from '../monitor/scene'
import { DefaultSettings, actionOf, hintedPlugins, hintsFrom, resolveHint, type ResourceSettings } from '../monitor/hints'

let seq = 0
function row(type: string, payload: Record<string, unknown>, source = 'Infra/Azure'): Message {
  seq++
  return { seq, type, source, payload: JSON.stringify(payload), correlationId: 0, causationSeq: null, depth: 0, occurredAt: '2026-10-15T10:00:00Z' }
}

function event(type: string, monitor?: MonitorHint): EventDefinition {
  return { type, publisher: 'Agent', highVolume: false, inLedger: false, fields: [], summary: '', ...(monitor ? { monitor } : {}) }
}

const catalog = [
  event('plugin.azure.sqlCreated', { icon: 'database', action: 'created', label: '{name}' }),
  event('plugin.azure.applied', {
    icon: 'cloud',
    iconBy: { field: 'kind', icons: { sql: 'database', dns: 'dns' } },
    actionField: 'change',
    label: '{name} in {group}',
  }),
  event('plugin.azure.retired', { icon: 'cloud', action: 'deleted' }),
  event('plugin.namecheap.recordSet', { icon: 'dns', action: 'changed', label: '{host}' }),
  event('plugin.stripe.charged'),
  event('agentContainer.started', { icon: 'nope', action: 'created' }),
]

function model(settings: ResourceSettings = DefaultSettings): MonitorModel {
  const made = new MonitorModel()
  made.hints = hintsFrom(catalog)
  made.settings = settings
  return made
}

function only(effects: MonitorEffect[]) {
  expect(effects).toHaveLength(1)
  return effects[0]!
}

describe('a hinted plugin row', () => {
  it('makes a resource effect beside the member with the fixed icon, action and label', () => {
    const effect = only(model().apply(row('plugin.azure.sqlCreated', { name: 'sql-db-maketags' })))

    expect(effect).toEqual({
      kind: 'resource',
      at: { kind: 'member', key: 'Infra/Azure' },
      resource: { plugin: 'azure', icon: 'database', action: 'created', label: 'sql-db-maketags' },
      ttlMs: 30_000,
    })
  })

  it('picks the icon by iconBy, and uses icon for a value iconBy does not name', () => {
    const made = model()
    const icon = (kind: unknown) => {
      const effect = only(made.apply(row('plugin.azure.applied', { kind, change: 'created', name: 'x' })))
      return effect.kind === 'resource' ? effect.resource.icon : null
    }

    expect(icon('sql')).toBe('database')
    expect(icon('dns')).toBe('dns')
    expect(icon('vm')).toBe('cloud')
    expect(icon(undefined)).toBe('cloud')
    expect(icon('constructor')).toBe('cloud')
  })

  it('reads the action from actionField in each spelling, anything else as changed', () => {
    const spellings: [string, string][] = [
      ['created', 'created'],
      ['added', 'created'],
      ['create', 'created'],
      ['deleted', 'deleted'],
      ['removed', 'deleted'],
      ['delete', 'deleted'],
      ['Deleted', 'deleted'],
      ['updated', 'changed'],
      ['', 'changed'],
    ]
    const made = model()
    for (const [change, action] of spellings) {
      const effect = only(made.apply(row('plugin.azure.applied', { kind: 'sql', change, name: 'x' })))
      expect(effect.kind === 'resource' && effect.resource.action, change).toBe(action)
      expect(actionOf(change)).toBe(action)
    }
    const missing = only(made.apply(row('plugin.azure.applied', { kind: 'sql', name: 'x' })))
    expect(missing.kind === 'resource' && missing.resource.action).toBe('changed')
  })

  it('takes a fixed action over the payload', () => {
    const effect = only(model().apply(row('plugin.azure.retired', { change: 'created' })))

    expect(effect.kind === 'resource' && effect.resource.action).toBe('deleted')
  })

  it('fills the label from the payload, and uses the event suffix when there is no label', () => {
    const made = model()
    const filled = only(made.apply(row('plugin.azure.applied', { kind: 'sql', change: 'created', name: 'sql-db', group: 'rg-tags' })))
    const partial = only(made.apply(row('plugin.azure.applied', { kind: 'sql', change: 'created', name: 'sql-db' })))
    const none = only(made.apply(row('plugin.azure.retired', { name: 'ignored' })))

    expect(filled.kind === 'resource' && filled.resource.label).toBe('sql-db in rg-tags')
    expect(partial.kind === 'resource' && partial.resource.label).toBe('sql-db in')
    expect(none.kind === 'resource' && none.resource.label).toBe('retired')
  })

  it('puts the icon, the action in words and the label on the activity list', () => {
    const made = model()
    made.apply(row('plugin.azure.sqlCreated', { name: 'sql-db-maketags' }))

    expect(made.ticker[0]).toMatchObject({
      verb: 'created',
      member: 'Azure',
      resource: { icon: 'database', action: 'created', label: 'sql-db-maketags' },
    })
  })

  it('lists one switch per plugin that declares a hint, and only plugin events count', () => {
    expect(hintedPlugins(hintsFrom(catalog))).toEqual(['azure', 'namecheap'])
    expect(hintsFrom(catalog).has('agentContainer.started')).toBe(false)
  })

  it('resolves a hint with no action as changed', () => {
    expect(resolveHint({ icon: 'cloud' }, 'plugin.a.b.c', {})).toEqual({ plugin: 'a', icon: 'cloud', action: 'changed', label: 'b.c' })
  })
})

describe('a plugin row drawn as today', () => {
  it('is the plain burst when its type has no hint', () => {
    const made = model()
    const effect = only(made.apply(row('plugin.stripe.charged', { amount: 12 })))

    expect(effect).toEqual({ kind: 'burst', at: { kind: 'member', key: 'Infra/Azure' }, tone: 'plugin' })
    expect(made.ticker[0]!.verb).toBe('charged')
    expect(made.ticker[0]!.resource).toBeUndefined()
  })

  it('is the plain burst with the setting off, and the activity list still says it in words', () => {
    const made = model({ ...DefaultSettings, show: false })
    const effect = only(made.apply(row('plugin.azure.sqlCreated', { name: 'sql-db-maketags' })))

    expect(effect.kind).toBe('burst')
    expect(made.ticker[0]).toMatchObject({ verb: 'created', resource: { label: 'sql-db-maketags' } })
  })

  it('is the plain burst for a plugin switched off, and others still draw', () => {
    const made = model({ ...DefaultSettings, off: ['azure'] })

    expect(only(made.apply(row('plugin.azure.sqlCreated', { name: 'x' }))).kind).toBe('burst')
    expect(only(made.apply(row('plugin.namecheap.recordSet', { host: 'www' }, 'Infra/Namecheap'))).kind).toBe('resource')
  })
})

describe('the resource in the scene', () => {
  function scene(reducedMotion: boolean) {
    const canvas = document.createElement('canvas')
    document.body.appendChild(canvas)
    const made = new MonitorScene(canvas, { fontPx: 12, fontFamily: 'sans-serif', reducedMotion })
    const m = model({ ...DefaultSettings, seconds: 10 })
    m.member('Infra', 'Azure', 'Azure', 'plugin')
    made.sync(m)
    let now = 0
    made.step(now)
    const at = (ms: number) => {
      while (now < ms) made.step((now = Math.min(ms, now + 50)))
    }
    return { made, m, at }
  }

  it('shows until its time to live and is gone at it, fading over its last seconds', () => {
    const { made, m, at } = scene(false)
    made.play(m.apply(row('plugin.azure.sqlCreated', { name: 'sql-db' })))

    at(1000)
    expect(made.shownResources().map((shown) => shown.alpha)).toEqual([1])
    at(8000)
    const fading = made.shownResources()[0]!.alpha
    expect(fading).toBeGreaterThan(0)
    expect(fading).toBeLessThan(1)
    at(9950)
    expect(made.shownResources()).toHaveLength(1)
    at(10_050)
    expect(made.shownResources()).toHaveLength(0)
  })

  it('appears and disappears with no fade under reduced motion', () => {
    const { made, m, at } = scene(true)
    made.play(m.apply(row('plugin.azure.sqlCreated', { name: 'sql-db' })))

    at(50)
    expect(made.shownResources()[0]!.alpha).toBe(1)
    at(9950)
    expect(made.shownResources()[0]!.alpha).toBe(1)
    at(10_050)
    expect(made.shownResources()).toHaveLength(0)
  })

  it('fans several out around the member without two in one place, none below it, and draws a dot until the font is here', () => {
    const { made, m, at } = scene(true)
    for (let i = 0; i < 5; i++) made.play(m.apply(row('plugin.azure.sqlCreated', { name: `db-${i}` })))
    at(100)

    const shown = made.shownResources()
    expect(shown).toHaveLength(5)
    // No two marks closer than a mark's width.
    for (const a of shown) {
      for (const b of shown) if (a !== b) expect(Math.hypot(a.x - b.x, a.y - b.y)).toBeGreaterThanOrEqual(22)
    }
    // The first two sit level with the member, one each side; the rest climb above, clear of the
    // name under it, and each side's labels run outward, away from the other side's.
    const [first, second] = shown
    expect(first!.y).toBeCloseTo(second!.y)
    expect(Math.sign(first!.x - second!.x)).toBe(1)
    expect(shown.every((s) => s.y <= first!.y + 1e-9)).toBe(true)
    expect(shown.every((s) => s.glyph === null)).toBe(true)

    made.setSymbols({ database: 0xf20e })
    expect(made.shownResources()[0]!.glyph).toBe(String.fromCodePoint(0xf20e))
  })
})
