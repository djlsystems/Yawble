// @vitest-environment happy-dom
import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import AgentTile from '../../src/renderer/src/components/AgentTile.vue';
import { linkParts } from '../../src/renderer/src/links';

// A made-up detection only: nothing is installed or run.
const missing = (cliId: string, installHelp: string) => ({ cliId, found: false, launcherPath: null, executablePath: null, version: null, installKind: 'unknown', readOnlyFolders: [], configFolders: [], problems: [], installHelp, signIn: null });
const props = (cliId: string, help: string, extra: Record<string, unknown> = {}) => ({ title: cliId, cliId, custom: false, baseTitle: cliId, detected: missing(cliId, help) as any, version: null, defaultModel: '', remote: false, loading: false, running: null, starting: null, updating: false, ...extra });

describe('installing an agent CLI', () => {
  it('OffersInstallForAMissingCliWithACommand_NeverUpdate', async () => {
    const w = mount(AgentTile, { props: props('grok', 'Install it. Official page: https://x.ai/cli') });
    expect(w.find('[data-test=tile-update]').exists()).toBe(false);
    await w.find('[data-test=tile-install-btn]').trigger('click');
    expect(w.emitted('install')).toHaveLength(1);
  });

  it('OffersNoInstallWhereTheMakerHasNoCommand_OrForACloudViewer', () => {
    expect(mount(AgentTile, { props: props('antigravity', 'From https://antigravity.google, then sign in.') }).find('[data-test=tile-install-btn]').exists()).toBe(false);
    expect(mount(AgentTile, { props: props('claude', 'x', { remote: true }) }).find('[data-test=tile-install-btn]').exists()).toBe(false);
  });

  it('MakesTheHintsAddressesLinks', () => {
    const w = mount(AgentTile, { props: props('antigravity', "Install it from https://antigravity.google, then sign in by running 'agy'.") });
    const a = w.find('[data-test=cli-install-help] a');
    expect(a.attributes('href')).toBe('https://antigravity.google');
    expect(a.attributes('target')).toBe('_blank');
    expect(w.find('[data-test=cli-install-help]').text()).toBe("Install it from https://antigravity.google, then sign in by running 'agy'.");
  });
});

describe('linkParts', () => {
  it('SplitsTextAroundAddresses_LeavingClosingPunctuation', () => {
    expect(linkParts('See https://a.example/x. Or (https://b.example/y) now')).toEqual([
      { text: 'See ' }, { text: 'https://a.example/x', href: 'https://a.example/x' }, { text: '. Or (' },
      { text: 'https://b.example/y', href: 'https://b.example/y' }, { text: ') now' },
    ]);
    expect(linkParts('no address')).toEqual([{ text: 'no address' }]);
  });
});
