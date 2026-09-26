import { describe, expect, it } from 'vitest'
import { breadcrumbs, childOf, isLegalSegment, parentWithin, sortEntries } from '../hostPath'

describe('breadcrumbs', () => {
  it('starts at the root and walks to the current folder', () => {
    expect(breadcrumbs('/srv/projects', '/srv/projects/alpha/docs')).toEqual([
      { label: '/srv/projects', path: '/srv/projects' },
      { label: 'alpha', path: '/srv/projects/alpha' },
      { label: 'docs', path: '/srv/projects/alpha/docs' },
    ])
  })

  it('is just the root when you are standing on it', () => {
    expect(breadcrumbs('/srv/projects', '/srv/projects')).toEqual([
      { label: '/srv/projects', path: '/srv/projects' },
    ])
  })

  it('trims a root handed over with a trailing slash', () => {
    expect(breadcrumbs('/srv/projects/', '/srv/projects/alpha')).toEqual([
      { label: '/srv/projects', path: '/srv/projects' },
      { label: 'alpha', path: '/srv/projects/alpha' },
    ])
  })

  // "/" is the one root whose separator IS the path. Trimming it would hand the server "".
  it('keeps the filesystem root as "/"', () => {
    expect(breadcrumbs('/', '/')).toEqual([{ label: '/', path: '/' }])
  })

  it('descends from "/" with a single slash, not two', () => {
    expect(breadcrumbs('/', '/srv/alpha')).toEqual([
      { label: '/', path: '/' },
      { label: 'srv', path: '/srv' },
      { label: 'alpha', path: '/srv/alpha' },
    ])
  })
})

describe('parentWithin', () => {
  it('walks up inside the root', () => {
    expect(parentWithin('/srv/projects', '/srv/projects/alpha')).toBe('/srv/projects')
  })

  // Up must STOP at the root. Offering a parent above it invites a click the server will refuse,
  // which reads as the picker being broken rather than bounded.
  it('refuses to walk above the root', () => {
    expect(parentWithin('/srv/projects', '/srv/projects')).toBeNull()
  })

  it('gives "/" as the parent one level below it', () => {
    expect(parentWithin('/', '/srv')).toBe('/')
  })
})

describe('childOf', () => {
  it('joins a name onto a path with a slash', () => {
    expect(childOf('/srv/projects', 'alpha')).toBe('/srv/projects/alpha')
  })

  it('trims a trailing slash before joining, rather than doubling it', () => {
    expect(childOf('/srv/projects/', 'alpha')).toBe('/srv/projects/alpha')
    expect(childOf('/', 'srv')).toBe('/srv')
  })
})

describe('isLegalSegment', () => {
  it('accepts an ordinary folder name', () => {
    expect(isLegalSegment('reports')).toBe(true)
    expect(isLegalSegment('my-notes_2')).toBe(true)
  })

  it('refuses a separator, traversal and a blank name', () => {
    expect(isLegalSegment('a/b')).toBe(false)
    expect(isLegalSegment('..')).toBe(false)
    expect(isLegalSegment('.')).toBe(false)
    expect(isLegalSegment('')).toBe(false)
    expect(isLegalSegment('   ')).toBe(false)
  })

  it('refuses a NUL character', () => {
    expect(isLegalSegment('a\0b')).toBe(false)
  })

  // Legal on a POSIX filesystem, so the picker must not refuse them for NTFS's sake.
  it('accepts names only Windows would have refused', () => {
    expect(isLegalSegment('a:b')).toBe(true)
    expect(isLegalSegment('a\\b')).toBe(true)
    expect(isLegalSegment('nope.')).toBe(true)
    expect(isLegalSegment('CON')).toBe(true)
    expect(isLegalSegment('com1.txt')).toBe(true)
    expect(isLegalSegment('.gitignore')).toBe(true)
  })
})

describe('sortEntries', () => {
  it('puts directories first, then sorts each group by name', () => {
    const sorted = sortEntries([
      { name: 'b.txt', type: 'file' as const },
      { name: 'zeta', type: 'dir' as const },
      { name: 'a.txt', type: 'file' as const },
      { name: 'alpha', type: 'dir' as const },
    ])

    expect(sorted.map((e) => e.name)).toEqual(['alpha', 'zeta', 'a.txt', 'b.txt'])
  })
})
