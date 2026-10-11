import { describe, expect, it } from 'vitest'
import {
  DevProxyRefusal,
  FallbackHost,
  describeDevProxyTarget,
  resolveDevProxyTarget,
} from '../devProxyTarget'

/**
 * NOTHING SETS `HARNESS_PORT` - the platform does not. So a `HARNESS_PORT ?? '8090'` fallback is
 * not a default, it is the only reachable value, and every member of every instance would proxy to
 * the same instance, with real accounts and real teams behind it.
 *
 * Nothing would fail. The proxy connects, the pages render, the API answers - about the wrong
 * instance. These tests are about which host is chosen and whether anything says so.
 */
describe('resolveDevProxyTarget prefers what the platform already minted', () => {
  it('takes HARNESS_URL, which names the exact host this container belongs to', () => {
    const target = resolveDevProxyTarget({ HARNESS_URL: 'http://127.0.0.1:8114' })
    expect(target.host).toBe('http://127.0.0.1:8114')
    expect(target.source).toBe('HARNESS_URL')
  })

  it('takes HARNESS_URL OVER HARNESS_PORT, so a stale override cannot win', () => {
    // The order is the whole point. A member holds HARNESS_URL always; if a port were preferred,
    // one left over in an environment would send it back to the wrong instance.
    const target = resolveDevProxyTarget({
      HARNESS_URL: 'http://127.0.0.1:8114',
      HARNESS_PORT: '8090',
    })
    expect(target.host).toBe('http://127.0.0.1:8114')
    expect(target.source).toBe('HARNESS_URL')
  })

  it('strips a trailing slash so the printed line and the proxy target agree', () => {
    expect(resolveDevProxyTarget({ HARNESS_URL: 'http://127.0.0.1:8114/' }).host)
      .toBe('http://127.0.0.1:8114')
  })

  it('keeps HARNESS_PORT working, because the printed hint has always told operators to set it',
    () => {
      const target = resolveDevProxyTarget({ HARNESS_PORT: '8117' })
      expect(target.host).toBe('http://127.0.0.1:8117')
      expect(target.source).toBe('HARNESS_PORT')
    })

  it('treats an empty or blank variable as absent rather than as an answer', () => {
    expect(resolveDevProxyTarget({ HARNESS_URL: '   ', HARNESS_PORT: '8117' }).source)
      .toBe('HARNESS_PORT')
    expect(resolveDevProxyTarget({ HARNESS_URL: '', HARNESS_PORT: '' }).source)
      .toBe('fallback')
  })
})

describe('a container with no answer REFUSES rather than falling back', () => {
  it('throws when HARNESS_MEMBER is set and neither variable names a host', () => {
    // The exception that proves the guard rule: the recoverable direction is refusing to start,
    // because the alternative is a member silently driving somebody else's live data.
    expect(() => resolveDevProxyTarget({ HARNESS_MEMBER: 'DeveloperInes' }))
      .toThrow(DevProxyRefusal)
  })

  it('names what is missing and what it refused to do, not merely that it failed', () => {
    try {
      resolveDevProxyTarget({ HARNESS_MEMBER: 'DeveloperInes' })
      expect.unreachable('should have refused')
    } catch (error) {
      const message = (error as Error).message
      expect(message).toContain('HARNESS_URL')
      expect(message).toContain('HARNESS_PORT')
      expect(message).toContain(FallbackHost)
    }
  })

  it('does NOT refuse a container that has an answer', () => {
    // The positive half. Without it, "refuses correctly" and "refuses always" are the same test.
    expect(resolveDevProxyTarget({
      HARNESS_MEMBER: 'DeveloperInes',
      HARNESS_URL: 'http://127.0.0.1:8114',
    }).source).toBe('HARNESS_URL')
  })

  it('does NOT refuse an operator, who is entitled to the fallback', () => {
    // No HARNESS_MEMBER means a person running `npm run dev` by hand in core\web, which is the
    // case the fallback exists for and is right there.
    const target = resolveDevProxyTarget({})
    expect(target.host).toBe(FallbackHost)
    expect(target.source).toBe('fallback')
  })
})

describe('the printed line names the SOURCE, not just the target', () => {
  it('says which variable decided, so a wrong instance is distinguishable from a right one', () => {
    expect(describeDevProxyTarget({ host: 'http://127.0.0.1:8114', source: 'HARNESS_URL' }))
      .toContain('from HARNESS_URL')
  })

  it('says outright when nothing told it, rather than looking like a decision', () => {
    const line = describeDevProxyTarget({ host: FallbackHost, source: 'fallback' })
    expect(line).toContain(FallbackHost)
    expect(line).toContain('built-in fallback')
  })
})
