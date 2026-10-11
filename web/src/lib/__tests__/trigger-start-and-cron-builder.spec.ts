import { describe, expect, it } from 'vitest'
import {
  createTriggerRequestFromDraft,
  cronFromBuilder,
  cronPreviewForDraft,
  draftForCreate,
  draftFromTrigger,
  fromDateTimeLocalValue,
  previewCronOccurrences,
  triggerFieldProblems,
  type TriggerDraft,
} from '../triggers'
import type { TeamTrigger } from '../../api/types'

// A cron or every-N schedule may START at a date and time - it never fires before it, and the loop
// begins once it is reached. The start travels as `fireAt`, which the Host reads as an every-N
// anchor and as a cron schedule's start.
function draft(overrides: Partial<TriggerDraft>): TriggerDraft {
  return { ...draftForCreate('Manager'), name: 'n', instruction: 'go', ...overrides }
}

describe('a schedule start', () => {
  it('is sent as fireAt for a cron schedule', () => {
    const request = createTriggerRequestFromDraft(draft({ mode: 'cron', cronExpression: '0 0 9 * * *', cronTimezone: 'UTC', startsAt: '2026-10-01T08:00' }))
    expect(request.fireAt).toBe(fromDateTimeLocalValue('2026-10-01T08:00'))
  })

  it('is sent as fireAt for an every-N schedule', () => {
    const request = createTriggerRequestFromDraft(draft({ mode: 'every', everyCount: 1, everyUnit: 'hours', startsAt: '2026-10-01T08:00' }))
    expect(request.fireAt).toBe(fromDateTimeLocalValue('2026-10-01T08:00'))
  })

  it('is absent when none is chosen', () => {
    expect(createTriggerRequestFromDraft(draft({ mode: 'cron', cronExpression: '0 0 9 * * *', cronTimezone: 'UTC', startsAt: '' })).fireAt).toBeNull()
  })

  it('is read back from fireAt when a cron or every-N trigger is edited', () => {
    const row = { name: 'n', container: 'Manager', instruction: 'go', kind: 'cron', expression: '0 0 9 * * *', timezone: 'UTC', intervalSeconds: null, fireAt: '2026-10-01T12:00:00Z', idleOnly: false, enabled: true, eventType: null, filter: null } as unknown as TeamTrigger
    const d = draftFromTrigger(row)
    expect(d.startsAt).not.toBe('')
    expect(fromDateTimeLocalValue(d.startsAt ?? '')).toBe(new Date('2026-10-01T12:00:00Z').toISOString())
  })

  it('that is not a date and time is refused', () => {
    expect(triggerFieldProblems(draft({ mode: 'cron', cronExpression: '0 0 9 * * *', cronTimezone: 'UTC', startsAt: 'soon' })).startsAt).toBeTruthy()
  })

  it('makes the cron preview begin at the start', () => {
    const start = new Date('2026-10-01T00:00:00Z')
    const lines = previewCronOccurrences('0 0 9 * * *', 'UTC', 2, new Date('2026-09-24T12:00:00Z'), start)
    expect(lines[0]).toContain('10/01/2026, 09:00:00')
    expect(lines[1]).toContain('10/02/2026, 09:00:00')
  })
})

// The builder writes the Host's seconds-format cron (six fields) from a preset a person picks.
describe('cronFromBuilder', () => {
  it('every N minutes', () => expect(cronFromBuilder({ preset: 'minutes', every: 15 })).toBe('0 */15 * * * *'))
  it('hourly at a minute', () => expect(cronFromBuilder({ preset: 'hourly', minute: 30 })).toBe('0 30 * * * *'))
  it('daily at a time', () => expect(cronFromBuilder({ preset: 'daily', hour: 9, minute: 5 })).toBe('0 5 9 * * *'))
  it('weekly on chosen days at a time, days in order', () =>
    expect(cronFromBuilder({ preset: 'weekly', days: [5, 1, 3], hour: 17, minute: 0 })).toBe('0 0 17 * * 1,3,5'))
  it('monthly on a day at a time', () => expect(cronFromBuilder({ preset: 'monthly', dayOfMonth: 1, hour: 8, minute: 0 })).toBe('0 0 8 1 * *'))
  it('weekly with no day chosen writes nothing', () => expect(cronFromBuilder({ preset: 'weekly', days: [], hour: 9, minute: 0 })).toBeNull())
})

// The dialog's "Next 5 occurrences" must not say "No preview available" just because the name is
// still empty. The preview depends only on what the schedule is made of.
describe('the trigger dialog preview', () => {
  const now = new Date('2026-09-24T12:00:00Z')

  it('shows the schedule while the name and instruction are still empty', () => {
    const lines = cronPreviewForDraft(draft({ name: '', instruction: '', mode: 'cron', cronExpression: '0 0 9 * * *', cronTimezone: 'UTC' }), 5, now)
    expect(lines).toHaveLength(5)
    expect(lines[0]).toContain('09/25/2026, 09:00:00')
  })

  it('begins at the start when there is one', () => {
    const lines = cronPreviewForDraft(draft({ name: '', mode: 'cron', cronExpression: '0 0 9 * * *', cronTimezone: 'UTC', startsAt: '2026-10-01T00:00' }), 1, now)
    expect(lines[0]).toContain('10/01/2026')
  })

  it('is empty for an expression or a timezone that cannot run, and for another kind', () => {
    expect(cronPreviewForDraft(draft({ mode: 'cron', cronExpression: 'every day', cronTimezone: 'UTC' }), 5, now)).toEqual([])
    expect(cronPreviewForDraft(draft({ mode: 'cron', cronExpression: '0 0 9 * * *', cronTimezone: 'Not/AZone' }), 5, now)).toEqual([])
    expect(cronPreviewForDraft(draft({ mode: 'every' }), 5, now)).toEqual([])
  })
})
