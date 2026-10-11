import { describe, expect, it } from 'vitest';
import { removalKindLabel, removalOwner } from '../removals';

describe('removalKindLabel', () => {
  it('names each kind the Host records', () => {
    expect(removalKindLabel('team-root')).toBe("Deleted team's folder");
    expect(removalKindLabel('workspace')).toBe("Deleted member's workspace");
    expect(removalKindLabel('emptied')).toBe('Folder a Reset emptied');
  });

  it('shows a kind it does not know as sent', () => {
    expect(removalKindLabel('something-new')).toBe('something-new');
  });
});

describe('removalOwner', () => {
  it('names the team, and the member for a workspace', () => {
    expect(removalOwner({ team: 'alpha', member: null })).toBe('alpha');
    expect(removalOwner({ team: 'alpha', member: 'Dev' })).toBe('Dev in alpha');
  });

  it("uses the team's label when the board still has it", () => {
    expect(removalOwner({ team: 'alpha', member: 'Dev' }, 'Alpha Team')).toBe('Dev in Alpha Team');
  });
});
