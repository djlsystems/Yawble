// THE VERSION RULE, pinned on `scripts/version.mjs` - the one place it is
// computed. The web build imports it, the .NET build runs it, and `version.ps1` prints it, so this
// is the rule for all three.
//
// The last block drives real git in a throwaway repository: a tag, a commit after it, a dirty
// file. Parsing a hand-written describe string would pass while git said something else.
import { execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  buildInfo,
  describeCheckout,
  formatVersion,
  parseDescribe,
  releaseOf,
  unknownVersion,
} from '../../../../scripts/version.mjs';

describe('formatVersion', () => {
  it('is the bare release at the tag', () => {
    expect(formatVersion({ tag: 'v2026.09.23.1', commits: 0, sha: 'eed3fd0', dirty: false })).toBe('2026.09.23.1');
  });

  it('names the commits since the release and the short sha after it', () => {
    expect(formatVersion({ tag: 'v2026.09.23.1', commits: 3, sha: 'eed3fd0', dirty: false })).toBe(
      '2026.09.23.1+3.eed3fd0',
    );
  });

  it('is 0.0.0 plus the commit count before the first release', () => {
    expect(formatVersion({ tag: null, commits: 173, sha: 'eed3fd0', dirty: false })).toBe('0.0.0+173.eed3fd0');
  });

  it('appends .dirty, and a dirty tree at the tag never reads as the release', () => {
    expect(formatVersion({ tag: 'v2026.09.23.1', commits: 0, sha: 'eed3fd0', dirty: true })).toBe(
      '2026.09.23.1+0.eed3fd0.dirty',
    );
    expect(formatVersion({ tag: 'v2026.09.23.2', commits: 1, sha: 'abc1234', dirty: true })).toBe(
      '2026.09.23.2+1.abc1234.dirty',
    );
    expect(formatVersion({ tag: null, commits: 5, sha: 'abc1234', dirty: true })).toBe('0.0.0+5.abc1234.dirty');
  });
});

describe('release tags', () => {
  it('accepts only v + a zero-padded date + a release number from 1', () => {
    expect(releaseOf('v2026.09.23.1')).toBe('2026.09.23.1');
    expect(releaseOf('v2026.12.01.14')).toBe('2026.12.01.14');
    for (const tag of ['2026.09.23.1', 'v2026.9.23.1', 'v2026.09.23.0', 'v2026.09.23', 'v1.2.3', 'v2026.09.23.1-rc']) {
      expect(() => releaseOf(tag), tag).toThrow();
    }
  });

  it('reads git describe --long --dirty output and refuses a tag that is not a release', () => {
    expect(parseDescribe('v2026.09.23.1-0-geed3fd0\n')).toEqual({
      tag: 'v2026.09.23.1',
      commits: 0,
      sha: 'eed3fd0',
      dirty: false,
    });
    expect(parseDescribe('v2026.09.23.1-3-gabc1234-dirty')).toEqual({
      tag: 'v2026.09.23.1',
      commits: 3,
      sha: 'abc1234',
      dirty: true,
    });
    expect(parseDescribe('v2026.09.23.1-rc-2-gabc1234')).toBeNull();
    expect(parseDescribe('eed3fd0')).toBeNull();
  });
});

describe('buildInfo', () => {
  const now = new Date('2026-09-23T12:00:00Z');

  it('takes the version handed in when there is no .git, as in the container build', () => {
    expect(buildInfo({ HARNESS_VERSION: '2026.09.23.1', HARNESS_COMMIT: 'eed3fd0' }, '/', now)).toEqual({
      version: '2026.09.23.1',
      commit: 'eed3fd0',
      builtAt: '2026-09-23T12:00:00.000Z',
    });
  });

  it('says 0.0.0+unknown, never a release, when it can neither ask git nor was told', () => {
    const empty = mkdtempSync(join(tmpdir(), 'version-nogit-'));
    try {
      expect(buildInfo({}, empty, now).version).toBe(unknownVersion);
    } finally {
      rmSync(empty, { recursive: true, force: true });
    }
  });
});

describe('a real repository', () => {
  let repo: string;
  const git = (...args: string[]) =>
    execFileSync('git', args, { cwd: repo, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  const commit = (file: string) => {
    writeFileSync(join(repo, file), file);
    git('add', file);
    git('-c', 'user.name=t', '-c', 'user.email=t@example.test', 'commit', '-q', '-m', file);
    return git('rev-parse', '--short=7', 'HEAD');
  };

  beforeEach(() => {
    repo = mkdtempSync(join(tmpdir(), 'version-repo-'));
    git('init', '-q', '-b', 'main');
  });

  afterEach(() => rmSync(repo, { recursive: true, force: true }));

  it('counts from the root before the first release', () => {
    commit('a');
    const sha = commit('b');

    expect(describeCheckout(repo)).toEqual({ version: `0.0.0+2.${sha}`, commit: git('rev-parse', 'HEAD') });
  });

  it('is the release at the tag, and release+1.sha one commit later', () => {
    commit('a');
    git('tag', 'v2026.09.23.1');
    expect(describeCheckout(repo).version).toBe('2026.09.23.1');

    const sha = commit('b');
    expect(describeCheckout(repo).version).toBe(`2026.09.23.1+1.${sha}`);
  });

  it('marks a modified tracked file .dirty and ignores an untracked one', () => {
    const sha = commit('a');
    git('tag', 'v2026.09.23.1');
    writeFileSync(join(repo, 'untracked'), 'x');
    expect(describeCheckout(repo).version).toBe('2026.09.23.1');

    writeFileSync(join(repo, 'a'), 'changed');
    expect(describeCheckout(repo).version).toBe(`2026.09.23.1+0.${sha}.dirty`);
  });

  it('passes over a tag that is not a release for the last one that is', () => {
    commit('a');
    git('tag', 'v2026.09.23.1');
    commit('b');
    git('tag', 'v2026.09.23.2-rc');
    git('tag', 'something-else');
    const sha = commit('c');

    expect(describeCheckout(repo).version).toBe(`2026.09.23.1+2.${sha}`);
  });

  it('takes the newest release when a day has two', () => {
    commit('a');
    git('tag', 'v2026.09.23.1');
    commit('b');
    git('tag', 'v2026.09.23.2');

    expect(describeCheckout(repo).version).toBe('2026.09.23.2');
  });
});
