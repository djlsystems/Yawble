import { describe, expect, it } from 'vitest';
import { productCli } from '../../presentation/product';
import { runningWarning, updateCommand, updateHeadline, type UpdateStatus } from '../releaseUpdates';

const base: UpdateStatus = {
  current: '2026.10.06.1',
  enabled: true,
  checked: true,
  checkedAt: '2026-10-06T20:00:00Z',
  detail: null,
  latest: null,
  updateAvailable: false,
  latestIsPrerelease: null,
  newer: [],
};

describe('the update words', () => {
  it('names the pre-release flag only for a pre-release', () => {
    expect(updateCommand({ latestIsPrerelease: true })).toBe(`${productCli} update --prerelease`);
    expect(updateCommand({ latestIsPrerelease: false })).toBe(`${productCli} update`);
  });

  it('never says up to date when nothing is known', () => {
    expect(updateHeadline({ ...base, checked: false, detail: 'Not checked yet.' })).toBe('Not checked yet.');
    expect(updateHeadline({ ...base, enabled: false, checked: false, detail: 'Checking for updates is turned off in Admin > Settings > System.' }))
      .toBe('Checking for updates is turned off in Admin > Settings > System.');
    expect(updateHeadline(base)).toBe('You are on v2026.10.06.1, the newest release offered to this build.');
  });

  it('counts the releases newer than yours when there is more than one', () => {
    const newer = [
      { version: '2026.10.08.1', prerelease: false, publishedAt: null, url: '', notes: '' },
      { version: '2026.10.07.1', prerelease: false, publishedAt: null, url: '', notes: '' },
    ];
    expect(updateHeadline({ ...base, latest: '2026.10.08.1', updateAvailable: true, latestIsPrerelease: false, newer }))
      .toBe('v2026.10.08.1 is out. You are on v2026.10.06.1. 2 releases are newer than yours.');
  });

  it('warns about runs in progress only when there are some', () => {
    expect(runningWarning(0)).toBe('');
    expect(runningWarning(1)).toContain('1 agent run is in progress now.');
  });
});
