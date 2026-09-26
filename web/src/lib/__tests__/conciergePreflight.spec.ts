import { describe, expect, it } from 'vitest';
import type { ConciergeEffective, ConciergeSettings } from '../../api/types';
import { conciergePreflight } from '../conciergePreflight';

function settings(effective: Partial<ConciergeEffective> | undefined): ConciergeSettings {
  return {
    agent: 'claude',
    ...(effective === undefined
      ? {}
      : {
          effective: {
            agent: 'claude',
            agentSource: 'chosen',
            auth: { installed: true, signedIn: true, detail: null },
            ...effective,
          },
        }),
  };
}

describe('conciergePreflight', () => {
  it('says nothing when both are chosen and the agent is signed in', () => {
    expect(conciergePreflight(settings({}))).toEqual({ kind: 'ready' });
  });

  /** A response with no effective block, or a failed read: not measured is not a problem to report. */
  it('says nothing, and does not hold the panel back, without an effective block', () => {
    expect(conciergePreflight(settings(undefined))).toEqual({ kind: 'ready' });
    expect(conciergePreflight(null)).toEqual({ kind: 'ready' });
  });

  it('names a default agent as the first signed-in one', () => {
    expect(conciergePreflight(settings({ agent: 'grok', agentSource: 'default' }))).toEqual({
      kind: 'notice',
      lead: 'Using grok, the first signed-in agent; change it in',
      detail: null,
    });
  });

  /** The prompt is the built-in Concierge prompt, so no sentence ever names a Prompt. */
  it('never mentions a Prompt, since none is chosen by a person', () => {
    const verdict = conciergePreflight(settings({ agent: 'grok', agentSource: 'default' }));

    expect(verdict.kind !== 'ready' && verdict.lead).not.toMatch(/prompt/i);
  });

  it('says an installed agent is not signed in, and still lets it open', () => {
    expect(
      conciergePreflight(settings({ auth: { installed: true, signedIn: false, detail: 'no credentials file' } })),
    ).toEqual({
      kind: 'notice',
      lead: 'claude is installed but not signed in; sign in from a terminal or add a key, or choose another agent in',
      detail: 'no credentials file',
    });
  });

  /** Not signed in outranks a default: the default sentence would claim it is signed in. */
  it('says not signed in rather than calling a default the first signed-in agent', () => {
    const verdict = conciergePreflight(
      settings({ agentSource: 'default', auth: { installed: true, signedIn: false, detail: null } }),
    );

    expect(verdict.kind).toBe('notice');
    expect(verdict.kind !== 'ready' && verdict.lead).toContain('not signed in');
  });

  it('holds the panel back when there is no agent at all', () => {
    expect(
      conciergePreflight(settings({ agent: null, agentSource: 'default', auth: { installed: false, signedIn: false, detail: null } })),
    ).toMatchObject({ kind: 'blocked', lead: expect.stringContaining('No installed agent can run the Concierge') });
  });

  it('holds the panel back when the agent is not installed', () => {
    expect(
      conciergePreflight(settings({ auth: { installed: false, signedIn: false, detail: null } })),
    ).toMatchObject({ kind: 'blocked', lead: 'claude is not installed; choose another agent in' });
  });
});
