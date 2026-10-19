import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { afterAll, describe, expect, it } from 'vitest'
import { localStamp, localTime, wireInstant } from '../localTime'
import { timeOf } from '../summarise'
import { stampOf } from '../monitor/model'
import { tooltipHtml } from '../teamActivity'
import { runStartedText, runStartedTitle } from '../memberRuns'
import { liveLineTime, liveLineTitle, parseLiveLine } from '../liveLines'

/**
 * ONE CLOCK. The same instant formatted by the member card's feed, the Monitor and the Activity tile
 * gives one string: its time of day (with seconds) on the current day, its date and time on another,
 * in the browser's zone and the locale's own hour cycle. Run under UTC and under the zone this
 * machine is in; every expected string is computed through the same formatter, never written out.
 */
const original = process.env.TZ
const local = original && original !== 'UTC' ? original : Intl.DateTimeFormat().resolvedOptions().timeZone
const zones = [...new Set(['UTC', local, 'America/New_York'])]

afterAll(() => {
  if (original === undefined) delete process.env.TZ
  else process.env.TZ = original
})

const src = join(import.meta.dirname, '..', '..')

/** The Activity tile's hover stamp, out of its markup. */
const tileTime = (at: number, now: number) =>
  tooltipHtml([], at, now).match(/<div class="stats-tip-time">([^<]*)<\/div>/)![1]!
    .replace(/&#39;/g, "'").replace(/&amp;/g, '&')

describe('one instant, one string, on the feed, the Monitor and the Activity tile', () => {
  for (const zone of zones) {
    describe(`in ${zone}`, () => {
      const iso = '2026-10-15T17:35:08Z'
      const at = Date.parse(iso)

      it('is the time of day, with seconds, on the same day', () => {
        process.env.TZ = zone
        const now = at + 60_000
        const expected = localStamp(at, now)

        expect(expected).toBe(localTime(at))
        expect(timeOf(iso, now)).toBe(expected)
        expect(stampOf(at, now)).toBe(expected)
        expect(tileTime(at, now)).toBe(expected)
      })

      it('is the date and the time on another day', () => {
        process.env.TZ = zone
        const now = at + 3 * 86_400_000
        const expected = localStamp(at, now)

        expect(expected).toBe(localTime(at, { date: true }))
        expect(timeOf(iso, now)).toBe(expected)
        expect(stampOf(at, now)).toBe(expected)
        expect(tileTime(at, now)).toBe(expected)
      })
    })
  }

  it('reads each zone as its own clock: the same instant is a different time of day in New York', () => {
    const at = Date.parse('2026-10-15T17:35:08Z')
    process.env.TZ = 'UTC'
    const utc = localTime(at)
    process.env.TZ = 'America/New_York'
    expect(localTime(at)).not.toBe(utc)
  })

  for (const zone of zones) {
    it(`gives the live view's tooltip and the stamp beside it one instant, read as the Host means it, in ${zone}`, () => {
      process.env.TZ = zone
      // The Host means UTC by a time sent without its offset; `new Date` alone reads it as local.
      const wire = '2026-10-15T17:35:08'
      const at = wireInstant(wire)

      expect(runStartedTitle(wire)).toBe(localTime(at, { date: true }))
      expect(runStartedText(wire)).toBe(localStamp(at))

      const line = parseLiveLine(`${wire}\ttext\tworking`)
      expect(line.at?.getTime()).toBe(at)
      expect(liveLineTitle(line.at!)).toBe(localTime(at, { date: true }))
      expect(liveLineTime(line.at!)).toBe(localStamp(at))
    })
  }

  it("keeps every person-read instant on these views inside lib/localTime: no Date's own locale string, no wire time parsed by new Date", () => {
    // Numbers keep `toLocaleString()` (token counts); the Activity tile's month axis keeps
    // `toLocaleDateString`, a calendar label rather than an instant.
    const DateLocale = /(new Date\([^)]*\)|\b\w*(?:at|At|when|When|date|Date|started|Started))\.toLocale(?:Time)?String\(/
    const WireDate = /new Date\(\s*(?:iso|when|instant|text|\w*At)\s*\)/
    for (const path of [
      'lib/summarise.ts', 'lib/liveLines.ts', 'lib/memberRuns.ts', 'lib/teamsTable.ts', 'components/ActivityFeed.vue',
      'components/MonitorView.vue', 'lib/monitor/model.ts', 'lib/teamActivity.ts', 'components/KanbanCardPanel.vue',
      'components/LiveViewDialog.vue', 'components/TeamKpiStrip.vue',
    ]) {
      const source = readFileSync(join(src, path), 'utf8')
      expect(source, path).not.toMatch(DateLocale)
      expect(source, path).not.toMatch(WireDate)
    }
  })

  it("lets the locale choose the hour cycle: no formatter of the feed's own and no forced 24-hour clock", () => {
    // The Activity tile's bucketing formatter (`lib/teamActivity.ts`) fixes h23 to read a zone's
    // parts, never to show them, so it is not in this list; the scan above covers it.
    for (const path of [
      'lib/summarise.ts', 'lib/liveLines.ts', 'lib/memberRuns.ts', 'lib/teamsTable.ts', 'components/ActivityFeed.vue',
      'components/MonitorView.vue', 'lib/monitor/model.ts', 'components/KanbanCardPanel.vue', 'components/LiveViewDialog.vue',
      'components/TeamKpiStrip.vue',
    ]) {
      const source = readFileSync(join(src, path), 'utf8')
      expect(source, path).not.toMatch(/hour12|hourCycle|toLocaleTimeString|toLocaleString\(\s*(undefined|\[\])/)
    }
  })
})
