import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { InstructionPrefix, ToneLegend, eventWords, toneInk } from '../eventWords'
import { MonitorModel, toneOf } from '../monitor/model'
import type { Message } from '../../api/types'

/**
 * ONE TABLE FROM A MESSAGE TYPE TO THE WORDS A PERSON READS. The Monitor's model, its legend, the
 * member card's feed and the Kanban panel's story all read `lib/eventWords`; this fails if the
 * Monitor's model or the feed grows a table of its own again.
 */
const src = join(import.meta.dirname, '..', '..')
const read = (path: string) => readFileSync(join(src, path), 'utf8')

describe('eventWords', () => {
  it('says each known type as a verb phrase, with its tone and an icon', () => {
    expect(eventWords('kanban.card.moved')).toEqual({ words: 'moved a card', tone: 'card', icon: 'view_kanban' })
    expect(eventWords('repo.pushed')).toEqual({ words: 'pushed', tone: 'push', icon: 'upload' })
    expect(eventWords('agentContainer.completed')).toEqual({ words: 'finished', tone: 'done', icon: 'check_circle' })
    expect(eventWords('agentContainer.handback').words).toBe('handed back')
    expect(eventWords('agentContainer.progress').words).toBe('working')
    expect(eventWords('agentContainer.needsDecision').words).toBe('needs you')
  })

  it('tells an instruction somebody sent from a member\'s own wake by the source alone', () => {
    expect(eventWords(`${InstructionPrefix}Alpha/Developer`, 'Alpha/Manager').words).toBe('told')
    expect(eventWords(`${InstructionPrefix}Alpha/Developer`, 'wake:12').words).toBe('woke')
  })

  it('reads the families by prefix: a card event, a plugin event', () => {
    expect(eventWords('kanban.card.commented').words).toBe('card commented')
    expect(eventWords('plugin.azure.sqlCreated')).toMatchObject({ words: 'sqlCreated', tone: 'plugin' })
  })

  it('reads an unknown type as it always did: the type without its agentContainer. prefix, quiet', () => {
    expect(eventWords('agentContainer.somethingNew')).toMatchObject({ words: 'somethingNew', tone: 'quiet' })
    expect(eventWords('team.renamed')).toMatchObject({ words: 'team.renamed', tone: 'quiet' })
  })

  it("is the Monitor model's tone and verb, row for row", () => {
    const types = ['kanban.card.moved', 'repo.pushed', 'agentContainer.completed', 'agentContainer.failed', 'agentContainer.somethingNew']
    const model = new MonitorModel()
    types.forEach((type, i) => {
      const message: Message = {
        seq: i + 1, type, source: 'Alpha/Developer', payload: '{}', correlationId: 1, causationSeq: null, depth: 0,
        occurredAt: `2026-10-15T10:00:0${i}Z`,
      }
      model.apply(message)
    })

    expect(model.ticker.map((entry) => entry.verb).reverse()).toEqual(types.map((type) => eventWords(type).words))
    expect(model.ticker.map((entry) => entry.tone).reverse()).toEqual(types.map((type) => eventWords(type).tone))
    expect(types.map(toneOf)).toEqual(types.map((type) => eventWords(type).tone))
  })

  it("names the Monitor's legend in the table's words", () => {
    expect(ToneLegend.find((entry) => entry.tone === 'card')?.words).toBe('moved a card')
    expect(ToneLegend.find((entry) => entry.tone === 'done')?.words).toBe('finished')
  })

  it('inks a state tone through its state token on the page', () => {
    expect(toneInk('fail')).toBe('var(--os-state-failed)')
    expect(toneInk('done')).toBe('var(--os-state-done)')
    expect(toneInk('start')).toBe('var(--os-state-running)')
  })
})

describe('there is one table', () => {
  // The phrases only this table should spell. A single word such as "failed" is also a member's
  // state, so the check is on what only an event table would say.
  const phrases = ['moved a card', 'planned a card', 'handed back', 'workflow done', 'push failed', 'files changed', 'asked to be woken', 'wake fired', 'package checked', 'site action']

  for (const path of ['lib/monitor/model.ts', 'components/ActivityFeed.vue', 'components/KanbanCardPanel.vue']) {
    it(`${path} spells no event phrase and assigns no verb of its own`, () => {
      const source = read(path)
      for (const phrase of phrases) expect(source, `${path} says '${phrase}'`).not.toContain(`'${phrase}'`)
      expect(source, `${path} sets a verb from a literal`).not.toMatch(/\bverb\s*=\s*['`]/)
      expect(source, `${path} sets a tone from a literal`).not.toMatch(/\btone\s*=\s*['`]/)
    })
  }

  it("the feed decides nothing from a type's spelling", () => {
    const feed = read('components/ActivityFeed.vue')
    expect(feed).not.toMatch(/type\.(endsWith|includes|startsWith)\(/)
    expect(feed).not.toMatch(/\blabel\(/)
  })
})
