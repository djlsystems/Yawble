import { describe, expect, it } from 'vitest'
import { FailureClasses, failureClassLabel, failureClassWords, resumesAutomatically } from '../failureClass'
import { diagnosticKindLabel } from '../diagnostics'

// A RUN LOST WITH ITS WORKER: the card and the feed say the worker stopped and that re-sending runs
// it elsewhere, badge it as the server spells it, and never promise to resume it.
describe('worker-lost', () => {
  it('says the worker stopped and re-sending runs it on a worker that is there', () => {
    expect(FailureClasses.WorkerLost).toBe('worker-lost')
    expect(failureClassWords('worker-lost')).toContain('the worker running this run stopped')
    expect(failureClassWords('worker-lost')).toContain('re-sending the instruction runs it on a worker that is there')
    expect(failureClassWords('worker-lost', 'plugin')).toContain('the worker running this run stopped')
    expect(failureClassLabel('worker-lost')).toBe('worker-lost')
  })

  it('is never resumed automatically', () => {
    expect(resumesAutomatically('worker-lost')).toBe(false)
  })

  it('has words for the worker diagnostics kinds', () => {
    expect(diagnosticKindLabel('worker.dropped')).toBe('Worker dropped')
    expect(diagnosticKindLabel('worker.stale-run-stopped')).toBe('Stale run stopped')
  })
})
