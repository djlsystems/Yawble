import { describe, expect, it } from 'vitest';
import { assignmentMoved } from '../concierge-assignment';
import { type ConciergeSettings } from '../../api/types';

/**
 * NO FIXTURE HERE BUILDS A TEAM, AND THAT IS DELIBERATE.
 *
 * `team.concierge` is what a team RECORDED when it was created; answering from it would leave the
 * header with nothing to say whenever the panel had no team, and otherwise answer from a row that
 * is not the thing that decides. The Concierge is chosen for the INSTANCE and names no team.
 *
 * The fixture uses the wire's own spelling (`concierge`, not `Concierge`): a fixture that mirrors
 * the code rather than the wire cannot catch the code.
 */
const settings = (overrides: Partial<ConciergeSettings> = {}): ConciergeSettings => ({
  agent: 'claude',
  ...overrides,
});

describe('assignmentMoved', () => {
  it('is false when what is running matches what is chosen', () => {
    expect(assignmentMoved(settings({ agent: 'claude' }), settings({ agent: 'claude' }))).toBe(false);
  });

  it('is true when the Agent has been repointed under a running session', () => {
    expect(assignmentMoved(settings({ agent: 'claude' }), settings({ agent: 'codex' }))).toBe(true);
  });

  /** A cleared Agent under a running session has moved: the next terminal starts the default. */
  it('is true when the Agent has been CLEARED under a running session', () => {
    expect(assignmentMoved(settings({ agent: 'claude' }), settings({ agent: null }))).toBe(true);
  });

  /**
   * AN UNKNOWN IS NOT A CHANGE. A warning about a session that does not exist, or against a setting
   * that could not be read, is worse than no warning: it appears while nothing is wrong and teaches
   * the reader to ignore it.
   */
  it('is false whenever there is nothing to compare', () => {
    expect(assignmentMoved(null, settings())).toBe(false);
    expect(assignmentMoved(settings(), null)).toBe(false);
    expect(assignmentMoved(settings(), undefined)).toBe(false);
    expect(assignmentMoved(null, null)).toBe(false);
  });
});
