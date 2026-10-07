import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { localDay } from '../localTime'

/**
 * A DAY IS THE PERSON'S DAY. The wire carries UTC, and its first ten characters are the UTC date:
 * late in a New York evening that is already tomorrow. Read in the browser's zone it is today.
 */
let zone: string | undefined

beforeEach(() => {
  zone = process.env.TZ
  process.env.TZ = 'America/New_York'
})

afterEach(() => {
  if (zone === undefined) delete process.env.TZ
  else process.env.TZ = zone
})

describe('a calendar day', () => {
  it("is the browser's day, not the UTC one", () => {
    expect(localDay('2026-10-07T03:34:00Z')).toBe('2026-10-06')
    expect(localDay('2026-10-07T15:00:00Z')).toBe('2026-10-07')
  })

  it('is empty for nothing or for what is not a time', () => {
    expect(localDay(null)).toBe('')
    expect(localDay(undefined)).toBe('')
    expect(localDay('not a time')).toBe('')
  })
})
