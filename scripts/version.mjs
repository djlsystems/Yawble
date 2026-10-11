// THE VERSION OF THIS CHECKOUT, and the one place it is computed.
//
// `node scripts/version.mjs` prints it on stdout and needs no arguments (`--commit` prints the full commit id instead). `version.ps1` is the same
// thing for PowerShell callers. The web build imports `buildInfo` from here and the .NET build runs
// this file, so the three stamps cannot disagree about a rule.
//
// Nothing in the repository holds the version as a literal: the git tag is the only source.
//   at the release tag v2026.09.23.1       2026.09.23.1
//   three commits after it                 2026.09.23.1+3.eed3fd0
//   no release tag yet                     0.0.0+42.eed3fd0
//   any of those from a dirty tree         ...+0.eed3fd0.dirty  (a dirty build never looks like a release)
//
// A container build has no .git, so it is handed the answer instead: `HARNESS_VERSION` and
// `HARNESS_COMMIT` (build args in the Containerfile) win over git in `buildInfo`. The command line
// ignores them, because a release script asking "what is this checkout" must get the checkout.

import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

/** A release tag, and nothing else: `v` + `yyyy.mm.dd.N`, zero-padded date, N from 1. */
export const releaseTag = /^v(\d{4})\.(\d{2})\.(\d{2})\.([1-9]\d*)$/;

/** What a build with no way to ask git says. Unmistakably not a release. */
export const unknownVersion = '0.0.0+unknown';

/**
 * The version string from its parts.
 * @param {{ tag: string | null, commits: number, sha: string, dirty: boolean }} parts
 *   `tag` is the last release tag (`v2026.09.23.1`) or null; `commits` how many commits since it
 *   (or since the root when there is none); `sha` the short commit id.
 * @returns {string}
 */
export function formatVersion({ tag, commits, sha, dirty }) {
  const release = tag === null ? '0.0.0' : releaseOf(tag);
  if (commits === 0 && !dirty && tag !== null) return release;
  return `${release}+${commits}.${sha}${dirty ? '.dirty' : ''}`;
}

/** `v2026.09.23.1` -> `2026.09.23.1`. Throws on anything that is not a release tag. */
export function releaseOf(tag) {
  if (!releaseTag.test(tag)) throw new Error(`'${tag}' is not a release tag (v yyyy.mm.dd.N).`);
  return tag.slice(1);
}

/**
 * Reads `git describe --long --dirty` output: `v2026.09.23.1-3-geed3fd0[-dirty]`.
 * @returns {{ tag: string, commits: number, sha: string, dirty: boolean } | null}
 */
export function parseDescribe(output) {
  const match = /^(.+)-(\d+)-g([0-9a-f]+)(-dirty)?$/.exec(output.trim());
  if (!match || !releaseTag.test(match[1])) return null;
  return { tag: match[1], commits: Number(match[2]), sha: match[3], dirty: match[4] !== undefined };
}

function git(cwd, ...args) {
  return execFileSync('git', args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
}

/**
 * The version and full commit id of the checkout at `cwd`. Throws when git cannot answer.
 * @returns {{ version: string, commit: string }}
 */
export function describeCheckout(cwd = process.cwd()) {
  const commit = git(cwd, 'rev-parse', 'HEAD');

  // The release tags this commit descends from, by name, and describe is held to exactly those.
  // A glob would be looser than `releaseTag`, and one `v2026.09.23.2-rc` nearer than the release
  // would then hide the release behind it.
  const releases = git(cwd, 'tag', '--merged', 'HEAD', '--list', 'v*')
    .split('\n')
    .filter((tag) => releaseTag.test(tag));

  const described = releases.length === 0
    ? null
    : parseDescribe(
        git(cwd, 'describe', '--tags', '--long', '--dirty', '--abbrev=7',
          ...releases.flatMap((tag) => ['--match', tag])),
      );

  if (described) return { version: formatVersion(described), commit };

  const dirty = git(cwd, 'status', '--porcelain', '--untracked-files=no') !== '';
  return {
    version: formatVersion({
      tag: null,
      commits: Number(git(cwd, 'rev-list', '--count', 'HEAD')),
      sha: git(cwd, 'rev-parse', '--short=7', 'HEAD'),
      dirty,
    }),
    commit,
  };
}

/**
 * What a build stamps: the handed-in version when there is one (a container build), else the
 * checkout's, else `unknownVersion`. `builtAt` is now.
 * @returns {{ version: string, commit: string, builtAt: string }}
 */
export function buildInfo(env = process.env, cwd = process.cwd(), now = new Date()) {
  const builtAt = now.toISOString();
  if (env.HARNESS_VERSION) {
    return { version: env.HARNESS_VERSION, commit: env.HARNESS_COMMIT || 'unknown', builtAt };
  }
  try {
    return { ...describeCheckout(cwd), builtAt };
  } catch {
    return { version: unknownVersion, commit: 'unknown', builtAt };
  }
}

// Run as a script: print the version, or with --commit the full commit id (for the .NET build).
if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const here = fileURLToPath(new URL('.', import.meta.url));
  try {
    const { version, commit } = describeCheckout(here);
    process.stdout.write(`${process.argv.includes('--commit') ? commit : version}\n`);
  } catch (error) {
    process.stderr.write(`version.mjs: git could not describe this checkout: ${error.message}\n`);
    process.exit(1);
  }
}
