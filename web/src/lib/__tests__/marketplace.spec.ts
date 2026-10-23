import { describe, expect, it } from 'vitest';

import type { CatalogContents, MarketplaceCatalog, MarketplacePackage } from '../../api/types';
import { installedRow } from '../../test/solutionFixtures';
import { published } from '../../test/catalogFixtures';
import { PROVIDER_WORDS, connectionWords, contentsLines, includedInWords, packageMatches, providerWords, updateFor } from '../marketplace';

/**
 * Every provider id the Host knows, as `PluginConnectionSlot.Parse` takes them
 * (`ConnectionProviders.ManifestProviders`); a .NET test pins the same list against the table.
 */
const HostProviders = ['google', 'microsoft', 'custom', 'imap'];

describe('the provider wording table', () => {
  it('has words for every provider id the Host knows, none of them the id itself', () => {
    for (const id of HostProviders) {
      expect(PROVIDER_WORDS[id]?.name, id).toBeTruthy();
      expect(providerWords(id), id).not.toBe(id);
    }
  });

  it('reads an id it has no words for as itself, and never blank', () => {
    expect(providerWords('acme')).toBe('acme');
    expect(providerWords('custom-acme')).toBe('custom-acme');
    expect(providerWords('')).toBe('an unnamed provider');
    expect(providerWords('toString')).toBe('toString');
  });

  it('says a connection by what a person has', () => {
    expect(connectionWords({ slot: 'account', providers: ['google'], required: true })).toBe('Needs a Google account');
    expect(connectionWords({ slot: 'work', providers: ['microsoft', 'google'], required: false })).toBe('Can use a Microsoft or Google account');
    expect(connectionWords({ slot: 'mailbox', providers: ['imap'], required: true })).toBe(
      'Needs a mailbox: Gmail, iCloud, Yahoo or another IMAP mailbox',
    );
  });
});

describe('an installed package with a newer version in the catalog', () => {
  // The catalog lists job-tracker 1.1.0; a team's name says nothing about which package it holds.
  const catalog: MarketplaceCatalog = { checked: true, reason: null, checkedAt: '2026-10-09T00:40:00Z', packages: [published('job-tracker')] };

  it('is matched by package id, not by the team it was installed on', () => {
    expect(updateFor(installedRow('acme-jobs', 'Acme jobs', '1.0.0', 'job-tracker'), catalog)?.version).toBe('1.1.0');
    expect(updateFor(installedRow('job-tracker', 'Job tracker', '1.0.0', 'other'), catalog)).toBeNull();
  });
});

describe("a card's contents, in bullets", () => {
  const none: CatalogContents = { plugins: [], members: [], skills: [], triggers: [], sites: [], settings: [], secrets: [], connections: [], events: [] };

  it('says nothing without contents, so the card shows what it showed before', () => {
    expect(contentsLines({ kind: 'solution' })).toEqual([]);
    expect(contentsLines({ kind: 'plugin', contents: null })).toEqual([]);
    expect(includedInWords({ kind: 'plugin' })).toBe('');
  });

  it("names a member by its role, Agent for an agent, and by name alone when it has neither", () => {
    expect(contentsLines({
      kind: 'solution',
      contents: { ...none, members: [{ name: 'A', role: 'Reads', agent: false }, { name: 'B', role: 'ignored', agent: true }, { name: 'C', role: null, agent: false }] },
    })).toEqual(['Member A: Reads', 'Member B: Agent', 'Member C']);
  });

  it('says a trigger in its words, or by its name when it has none; a setting or key by its label, or its name', () => {
    expect(contentsLines({ kind: 'solution', contents: { ...none, triggers: [{ name: 'nightly', words: '' }] } })).toEqual(['Runs nightly']);
    expect(contentsLines({
      kind: 'plugin',
      contents: {
        ...none,
        settings: [{ name: 'limit', label: '', kind: 'number', required: false, default: '20' }],
        secrets: [{ name: 'TOKEN', label: '', required: false, masked: true }],
        connections: [{ slot: '', providers: [], required: false }],
        events: [{ type: 'thing.done', summary: '' }],
      },
    })).toEqual(['Setting: limit', 'Key: TOKEN', 'Sign-in: an account', 'Announces: thing.done']);
  });

  it("names a plugin's solutions on one line, and never on a solution's card", () => {
    const includedIn = [{ id: 'one', name: 'One' }, { id: 'two', name: '' }];
    expect(includedInWords({ kind: 'plugin', includedIn })).toBe('Included in One, two');
    expect(includedInWords({ kind: 'solution', includedIn })).toBe('');
  });

  it('lets the filter find a package by what it holds and what includes it', () => {
    const plugin = {
      id: 'p', kind: 'plugin', name: 'P', summary: '', version: '1.0.0', needs: [],
      catalogNeeds: { connections: [], secrets: [], inputs: [], runtimes: [] },
      installed: false, installedVersion: null, installedOn: [], updateAvailable: false,
      contents: { ...none, events: [{ type: 'x', summary: 'A parcel arrived' }] },
      includedIn: [{ id: 's', name: 'Shipping desk' }],
    } satisfies MarketplacePackage;
    expect(packageMatches(plugin, 'parcel')).toBe(true);
    expect(packageMatches(plugin, 'shipping')).toBe(true);
    expect(packageMatches(plugin, 'nowhere')).toBe(false);
  });
});
