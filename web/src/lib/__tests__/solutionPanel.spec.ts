import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { LauncherPath, memberStateLine, nextFireLine, panelPath, parseCap, sizeWords, stateBadge } from '../solutionPanel';
import { needsActiveWorkTeam, Ribbon, SolutionsAction } from '../ribbon';

describe('a solution state badge', () => {
  it('says each state in words, and names why when blocked or capped', () => {
    expect(stateBadge({ kind: 'running', reason: null })?.text).toBe('Running');
    expect(stateBadge({ kind: 'idle', reason: null })?.text).toBe('Idle');
    expect(stateBadge({ kind: 'paused', reason: null })?.text).toBe('Paused');
    expect(stateBadge({ kind: 'blocked', reason: 'Upload a file to Resume/' })?.text).toBe('Blocked: Upload a file to Resume/');
    expect(stateBadge({ kind: 'capped', reason: null })?.text).toBe('Capped today');
    expect(stateBadge(undefined)).toBeNull();
  });
});

describe('when a trigger next fires', () => {
  it('says off, event, folder and unscheduled in words', () => {
    expect(nextFireLine({ packageKind: 'schedule', enabled: false, nextDueAt: '2026-10-01T00:00:00Z' })).toBe('Off');
    expect(nextFireLine({ packageKind: 'event', enabled: true, nextDueAt: null })).toBe('Fires on its event');
    expect(nextFireLine({ packageKind: 'folder', enabled: true, nextDueAt: null })).toBe('Fires when a file changes');
    expect(nextFireLine({ packageKind: 'schedule', enabled: true, nextDueAt: null })).toBe('Not scheduled');
    expect(nextFireLine({ packageKind: 'schedule', enabled: true, nextDueAt: '2026-10-01T00:00:00Z' })).toMatch(/^Next /);
  });
});

describe("a member's state", () => {
  it('says the state, and what the snapshot says when it says something', () => {
    expect(memberStateLine({ state: 'idle' })).toBe('idle');
    expect(memberStateLine({ state: 'missing' })).toBe('no longer on the team');
    expect(memberStateLine({ state: 'running', queueDepth: 2 })).toBe('running · 2 queued');
    expect(memberStateLine({ state: 'idle', blocked: 'mailbox is not bound', needsDecision: null, failed: null })).toBe(
      'idle · blocked: mailbox is not bound',
    );
    expect(memberStateLine({ state: 'idle', failed: 'exit 1' })).toBe('idle · failed: exit 1');
  });
});

describe('a daily cap typed into its box', () => {
  it('is a whole number of tokens, empty for no cap, and anything else is refused', () => {
    expect(parseCap('50,000')).toBe(50000);
    expect(parseCap('')).toBeNull();
    expect(parseCap(null)).toBeNull();
    expect(parseCap('lots')).toBeUndefined();
    expect(parseCap('0')).toBeUndefined();
    expect(parseCap('1.5')).toBeUndefined();
  });
});

describe('the solutions addresses', () => {
  it('escape the team', () => {
    expect(panelPath('job tracker')).toBe('/solutions/job%20tracker');
    expect(LauncherPath).toBe('/solutions');
  });

  it('sizes a file in words', () => {
    expect(sizeWords(512)).toBe('512 B');
    expect(sizeWords(2048)).toBe('2.0 KB');
  });
});

describe('the Solutions ribbon button', () => {
  const tabs = Ribbon.tabs.map((tab) => tab.id);
  const tabOf = (action: string) => Ribbon.tabs.find((tab) => tab.items.some((item) => item.action === action))?.id;

  it('is near the start of the ribbon and not under Admin', () => {
    expect(tabOf(SolutionsAction)).toBe('solutions');
    expect(tabs.indexOf('solutions')).toBeLessThanOrEqual(1);
    expect(Ribbon.tabs.find((tab) => tab.id === 'admin')?.items.some((item) => item.action === SolutionsAction)).toBe(false);
  });

  it('needs no active team', () => {
    expect(needsActiveWorkTeam(SolutionsAction, undefined)).toBe(false);
  });
});

/**
 * PACKAGE TEXT IS TEXT. The launcher and the panel show names, descriptions, a status line filled from
 * site data and run output, all of which a package controls; none of them may be bound as HTML.
 */
describe('the solution screens', () => {
  it.each(['SolutionsLauncher.vue', 'SolutionPanel.vue'])('%s binds nothing as HTML', (file) => {
    const source = readFileSync(join(import.meta.dirname, '../../components', file), 'utf8');
    expect(source).not.toMatch(/v-html|innerHTML/);
  });
});
