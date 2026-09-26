// @vitest-environment happy-dom
//
// THE VERSION BESIDE THE LOGO: the dated release tag, and the full version, commit
// and build time on hover.
import { afterEach, describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { QTooltip } from 'quasar';
import VersionTag from '../VersionTag.vue';
import { buildDetail, bundleBuild, releaseLabel, shortCommit, unknownBuild } from '../../lib/buildInfo';
import { resetBody } from '../../test/mountQuasar';

const release = {
  version: '2026.09.23.1',
  commit: 'eed3fd00b9717e141ce8e505e99dbd76e3586035',
  builtAt: '2026-09-23T12:00:00Z',
};

afterEach(resetBody);

describe('VersionTag', () => {
  it('shows the release tag and nothing else', () => {
    const tag = mount(VersionTag, { props: { info: release }, attachTo: document.body });

    expect(tag.text()).toBe('v2026.09.23.1');
    tag.unmount();
  });

  it('shows a dev build as the release it follows, and the full version on hover', () => {
    const tag = mount(VersionTag, {
      props: { info: { ...release, version: '2026.09.23.1+1.abc1234.dirty' } },
      attachTo: document.body,
    });

    expect(tag.text()).toBe('v2026.09.23.1');
    expect(tag.find('.version-tag').attributes('aria-label')).toContain('Version 2026.09.23.1+1.abc1234.dirty');
    tag.unmount();
  });

  it('shows the full version, the commit and the build time on hover, and names them to a screen reader', () => {
    const tag = mount(VersionTag, { props: { info: release }, attachTo: document.body });
    const detail = `Version 2026.09.23.1 · Commit eed3fd0 · built ${new Date(release.builtAt).toLocaleString()}`;

    // happy-dom does not run Quasar's hover, so the popup itself was checked in a browser; here
    // it is that there is one, and that the same words reach a screen reader.
    expect(tag.findComponent(QTooltip).exists()).toBe(true);
    expect(tag.find('.version-tag').attributes('aria-label')).toBe(`v2026.09.23.1. ${detail}`);
    tag.unmount();
  });

  it('is the bundle build by default, which outside a real build is unknown and not a release', () => {
    expect(bundleBuild).toEqual(unknownBuild);
    const tag = mount(VersionTag, { attachTo: document.body });

    expect(tag.text()).toBe('unreleased');
    tag.unmount();
  });
});

describe('buildDetail', () => {
  it('shortens a commit id and leaves anything else alone', () => {
    expect(shortCommit('eed3fd00b9717e141ce8e505e99dbd76e3586035')).toBe('eed3fd0');
    expect(shortCommit('unknown')).toBe('unknown');
  });

  it('says unknown for a build time it does not have', () => {
    expect(buildDetail(unknownBuild)).toBe('Version 0.0.0+unknown · Commit unknown · built unknown');
  });
});

describe('releaseLabel', () => {
  it('is the dated tag for a release and for every build after it', () => {
    expect(releaseLabel('2026.09.24.1')).toBe('v2026.09.24.1');
    expect(releaseLabel('2026.09.24.2+3.eed3fd0')).toBe('v2026.09.24.2');
    expect(releaseLabel('2026.09.24.1+0.eed3fd0.dirty')).toBe('v2026.09.24.1');
  });

  it('says unreleased before the first release', () => {
    expect(releaseLabel('0.0.0+208.92a862d')).toBe('unreleased');
    expect(releaseLabel('0.0.0+unknown')).toBe('unreleased');
  });
});
