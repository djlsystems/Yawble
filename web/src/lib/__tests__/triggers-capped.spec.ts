import { describe, expect, it } from 'vitest'
import { cappedLine } from '../triggers'

describe('cappedLine', () => {
  it('says when a schedule asleep on its cap resumes, in its own timezone', () => {
    expect(cappedLine({ cappedUntil: '2026-09-28T15:00:00+00:00', skippedToday: 1, timezone: 'Asia/Tokyo' }))
      .toBe('Capped until 2026-09-29 00:00 Asia/Tokyo')
  })

  it('reads a schedule with no timezone in UTC', () => {
    expect(cappedLine({ cappedUntil: '2026-09-29T00:00:30+00:00', skippedToday: 1, timezone: null }))
      .toBe('Capped until 2026-09-29 00:00 UTC')
  })

  it('counts the fires the cap skipped today when nothing is asleep', () => {
    expect(cappedLine({ cappedUntil: null, skippedToday: 3, timezone: null })).toBe('skipped today: 3')
  })

  it('says nothing when the cap holds nothing, or the Host sends no figures', () => {
    expect(cappedLine({ cappedUntil: null, skippedToday: 0, timezone: null })).toBeNull()
    expect(cappedLine({ timezone: null })).toBeNull()
  })
})
