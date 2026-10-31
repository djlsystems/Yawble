import { describe, expect, it } from 'vitest';
import { detailWords, didWords } from '../tenantLogWords';

/**
 * A MOVE OF A MEMBER'S OWN SECRET reads in words from the detail the Host writes, key for key: one
 * `field` on a moved row, `fields` on a kept or failed one, with `slot`, `member`, `team`, `plugin`
 * and (failed) `reason`. The details below are the anonymous objects `PluginSecretMoves` serialises;
 * the sentence helper capitalises the first word, so a field name leads as `ApiKey`.
 */
const moved = JSON.stringify({ team: 'mail', member: 'Domains', plugin: 'pay', field: 'apiKey', slot: 'namecheap', key: 'namecheap', connection: 'conn-1' });
const kept = JSON.stringify({ team: 'mail', member: 'Domains', plugin: 'pay', slot: 'namecheap', fields: ['apiKey', 'apiUser'] });
const failed = JSON.stringify({
  team: 'mail', member: 'Domains', plugin: 'pay', slot: 'namecheap', fields: ['apiKey', 'apiUser'],
  reason: 'The database refused the move (19).',
});

describe("a secret move's row", () => {
  it('a moved row names its one field, the slot, the member, the team and the plugin', () => {
    const words = detailWords({ action: 'member.secret-moved', detail: moved })!;
    expect(words).toMatch(/^[Aa]piKey moved to slot namecheap for Domains on team mail;/);
    expect(words).toContain('plugin pay');
    expect(words).not.toContain('a field');
    expect(words).not.toContain('its slot');
    expect(didWords({ action: 'member.secret-moved', detail: moved })).toBe("moved a plugin member's own secret into a connection");
  });

  it('a kept row names every field and the slot that already had a connection', () => {
    const words = detailWords({ action: 'member.secret-move-kept', detail: kept })!;
    expect(words).toMatch(/^[Aa]piKey, apiUser kept for Domains on team mail/);
    expect(words).toContain('slot namecheap already had a connection');
    expect(words).toContain('plugin pay');
    expect(words).not.toContain('its fields');
  });

  it('a failed row names every field and the reason, and says the member keeps its values', () => {
    const words = detailWords({ action: 'member.secret-move-failed', detail: failed })!;
    expect(words).toMatch(/^[Aa]piKey, apiUser not moved for Domains on team mail/);
    expect(words).toContain('The database refused the move (19)');
    expect(words).toContain('the member keeps its values');
    expect(words).not.toContain('its fields');
  });
});
