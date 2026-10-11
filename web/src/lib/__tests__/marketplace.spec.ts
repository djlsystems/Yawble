import { describe, expect, it } from 'vitest';

import type { MarketplaceCatalog } from '../../api/types';
import { installedRow } from '../../test/solutionFixtures';
import { published } from '../../test/catalogFixtures';
import { PROVIDER_WORDS, connectionWords, providerWords, updateFor } from '../marketplace';

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
