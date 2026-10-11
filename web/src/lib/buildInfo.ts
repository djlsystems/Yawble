/**
 * WHICH BUILD THIS IS: the version, commit and build time. The bundle's own is injected at
 * build time by `quasar.config.ts` from `scripts/version.mjs`; the Host answers the same shape from
 * `GET /api/version`. Nothing here, and nothing in `package.json`, holds a version as a literal.
 */
export interface BuildInfo {
  /** `2026.09.23.1` at a release, `2026.09.23.1+3.eed3fd0` after one, `.dirty` from a dirty tree. */
  version: string
  /** The full commit id, or `unknown`. */
  commit: string
  /** When it was built (ISO 8601), or null when that is not known. */
  builtAt: string | null
}

declare const __HARNESS_BUILD__: BuildInfo | undefined

/** What a bundle built without the define says - the test runner, for one. Never a release. */
export const unknownBuild: BuildInfo = { version: '0.0.0+unknown', commit: 'unknown', builtAt: null }

/** This bundle's build. */
export const bundleBuild: BuildInfo =
  typeof __HARNESS_BUILD__ !== 'undefined' ? __HARNESS_BUILD__ : unknownBuild

/** The first seven characters of a commit id; anything that is not one is shown as it is. */
export function shortCommit(commit: string): string {
  return /^[0-9a-f]{7,40}$/.test(commit) ? commit.slice(0, 7) : commit
}

/** When it was built, in the viewer's locale, or `unknown`. */
export function builtWhen(builtAt: string | null): string {
  if (!builtAt) return 'unknown'
  const at = new Date(builtAt)
  return Number.isNaN(at.getTime()) ? builtAt : at.toLocaleString()
}

/**
 * THE RELEASE A BUILD BELONGS TO, as its tag: `v2026.09.23.1`. This is what the top bar and the
 * login card show; everything after the `+` (commits since the tag, the commit, `.dirty`) is in
 * {@link buildDetail}, on hover. A build from before the first release says `unreleased`.
 */
export function releaseLabel(version: string): string {
  const release = version.split('+')[0] ?? version
  return /^\d{4}\.\d{2}\.\d{2}\.\d+$/.test(release) ? `v${release}` : 'unreleased'
}

/** The hover text beside a version: `Version 2026.09.23.1+3.eed3fd0 · Commit eed3fd0 · built …`. */
export function buildDetail(info: BuildInfo): string {
  return `Version ${info.version} · Commit ${shortCommit(info.commit)} · built ${builtWhen(info.builtAt)}`
}
