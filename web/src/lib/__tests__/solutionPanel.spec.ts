import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import type { InstalledSolution, SolutionPanelTrigger, SolutionStateKind } from '../../api/types';
import { filterWords } from '../filterWords';
import {
  hasNewRun,
  instructionFirstLine,
  LauncherPath,
  memberStateLine,
  nextFireLine,
  panelPath,
  parseCap,
  runKeys,
  sizeWords,
  solutionMatches,
  stateBadge,
  stateChoices,
  teamChoices,
  triggerFacts,
} from '../solutionPanel';
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

describe('watching for the run Run now started', () => {
  const seen = runKeys([{ member: 'scout', seq: 41 }, { member: 'Manager', seq: 40 }]);

  it('is not done while only the runs already seen are listed', () => {
    expect(hasNewRun(seen, [{ member: 'scout', seq: 41 }, { member: 'Manager', seq: 40 }])).toBe(false);
    expect(hasNewRun(seen, [])).toBe(false);
  });

  it('is done once a run it had not seen is listed, whichever member ran it', () => {
    expect(hasNewRun(seen, [{ member: 'scout', seq: 42 }, { member: 'scout', seq: 41 }])).toBe(true);
    expect(hasNewRun(seen, [{ member: 'writer', seq: 41 }])).toBe(true);
  });
});

describe("the launcher's filter", () => {
  const row = (team: string, teamName: string, name: string, kind: SolutionStateKind, status: string): InstalledSolution => ({
    team,
    teamName,
    id: 'job-tracker',
    name,
    version: '1.0.0',
    installedAt: '2026-10-01T00:00:00Z',
    installedBy: 'person@example.test',
    plugins: [],
    status,
    state: { kind, reason: null },
  });
  const rows = [
    row('t-hunt', 'Job Hunt', 'Job Tracker', 'blocked', '2 new jobs · last checked 8:51 PM'),
    row('t-desk', 'Front Desk', 'Mail Sorter', 'paused', 'No runs yet · paused'),
    row('t-ops', 'Ops', 'Invoice Chaser', 'idle', 'Last run 9:00 AM (handback) · idle'),
  ];
  const shown = (filter: Parameters<typeof solutionMatches>[1]) => rows.filter((r) => solutionMatches(r, filter)).map((r) => r.team);
  const none = { text: '', team: null, state: null };

  it('shows every tile when empty, cleared or blank', () => {
    expect(shown(none)).toEqual(['t-hunt', 't-desk', 't-ops']);
    expect(shown({ ...none, text: null })).toEqual(['t-hunt', 't-desk', 't-ops']);
    expect(shown({ ...none, text: '   ' })).toEqual(['t-hunt', 't-desk', 't-ops']);
  });

  it('matches every word, in any case, against the name, team name and status line', () => {
    expect(shown({ ...none, text: 'mail' })).toEqual(['t-desk']);
    expect(shown({ ...none, text: 'FRONT desk' })).toEqual(['t-desk']);
    expect(shown({ ...none, text: 'new jobs' })).toEqual(['t-hunt']);
    expect(shown({ ...none, text: 'mail ops' })).toEqual([]);
    expect(shown({ ...none, text: 'job-tracker 1.0.0' })).toEqual(['t-hunt', 't-desk', 't-ops']);
  });

  it('narrows by team, by state, and by all three together', () => {
    expect(shown({ ...none, team: 't-ops' })).toEqual(['t-ops']);
    expect(shown({ ...none, state: 'paused' })).toEqual(['t-desk']);
    expect(shown({ text: 'chaser', team: 't-ops', state: 'idle' })).toEqual(['t-ops']);
    expect(shown({ text: 'chaser', team: 't-ops', state: 'paused' })).toEqual([]);
  });

  it('never changes the rows it filters', () => {
    const before = JSON.stringify(rows);
    shown({ text: 'job', team: 't-hunt', state: 'blocked' });
    expect(JSON.stringify(rows)).toBe(before);
  });

  it('offers only the teams and states some tile has', () => {
    expect(teamChoices(rows)).toEqual([
      { label: 'Front Desk', value: 't-desk' },
      { label: 'Job Hunt', value: 't-hunt' },
      { label: 'Ops', value: 't-ops' },
    ]);
    expect(stateChoices(rows)).toEqual([
      { label: 'Idle', value: 'idle' },
      { label: 'Blocked', value: 'blocked' },
      { label: 'Paused', value: 'paused' },
    ]);
    expect(stateChoices([])).toEqual([]);
  });
});

describe("a trigger's instruction and Details", () => {
  const trigger = (fields: Partial<SolutionPanelTrigger>): SolutionPanelTrigger =>
    ({
      id: 'trg-1',
      team: 't-hunt',
      container: 'Writer',
      name: 'Apply pressed',
      instruction: 'Draft a letter.',
      kind: 'event',
      expression: null,
      timezone: null,
      intervalSeconds: null,
      fireAt: null,
      idleOnly: false,
      enabled: true,
      nextDueAt: null,
      lastFiredAt: null,
      lastOutcome: null,
      lastSeq: null,
      missedCount: 0,
      createdAt: '2026-10-01T00:00:00Z',
      createdBy: 'person@example.test',
      eventType: 'site.tracker.apply',
      filter: null,
      dailyTokenCap: null,
      packageName: 'Apply pressed',
      packageKind: 'event',
      runNow: false,
      ...fields,
    }) as SolutionPanelTrigger;

  it('shows the first line of an instruction on the tile, the string itself unchanged', () => {
    expect(instructionFirstLine('Read the posting.\nThen write Drafts/<job id>.md.')).toBe('Read the posting.');
    expect(instructionFirstLine('One line\r\nand more')).toBe('One line');
    const long = 'A'.repeat(325);
    expect(instructionFirstLine(long)).toBe(long);
    expect(instructionFirstLine('<img src=x onerror=alert(1)>')).toBe('<img src=x onerror=alert(1)>');
    expect(instructionFirstLine(null)).toBe('');
  });

  it('lists what fires it, its filter as written, on or off, its cap and its last fire as text', () => {
    const facts = triggerFacts(trigger({ filter: 'status == "drafted"', dailyTokenCap: 200000 }));
    expect(facts.map((fact) => fact.label)).toEqual(['Fires', 'Filter', 'On', 'Daily cap', 'Last fired']);
    const [fires, ...rest] = facts.map((fact) => fact.value);
    expect(fires).toContain('site.tracker.apply');
    expect(rest).toEqual(['status == "drafted"', 'Yes', '200,000 tokens a day', 'Never']);
  });

  it('adds the timezone, the last outcome and missed fires when there are any', () => {
    const now = new Date('2026-10-02T12:00:00Z');
    const facts = triggerFacts(
      trigger({
        kind: 'cron',
        packageKind: 'schedule',
        expression: '0 8 * * *',
        timezone: 'Europe/London',
        enabled: false,
        lastFiredAt: '2026-10-01T08:00:00Z',
        lastOutcome: 'handback',
        missedCount: 2,
      }),
      now,
    );
    expect(facts.map((fact) => fact.label)).toEqual(['Fires', 'Timezone', 'On', 'Daily cap', 'Last fired', 'Last outcome', 'Missed']);
    expect(facts.find((fact) => fact.label === 'On')?.value).toBe('No, it is off');
    expect(facts.find((fact) => fact.label === 'Daily cap')?.value).toBe('no cap');
    expect(facts.find((fact) => fact.label === 'Last outcome')?.value).toBe('handback');
    expect(facts.find((fact) => fact.label === 'Missed')?.value).toBe('2');
    expect(facts.every((fact) => typeof fact.value === 'string')).toBe(true);
  });
});
