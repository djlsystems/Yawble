import { describe, expect, it } from 'vitest';
import type { TeamWorkflows } from '../../api/types';
import { teamStatusIcon } from '../teamStatusIcon';

function latest(state: string | null): TeamWorkflows {
  return {
    available: true,
    openCount: 0,
    totalCount: state === null ? 0 : 1,
    earliestStartedAt: null,
    serverNow: '2026-10-07T18:00:00Z',
    workflows: state === null ? [] : [{ state } as TeamWorkflows['workflows'][number]],
    missing: null,
  };
}

describe('the icon beside a team\'s activity chart', () => {
  it('is a green check for an idle team whose latest workflow its Manager declared complete', () => {
    expect(teamStatusIcon('idle', latest('Completed'))).toMatchObject({ icon: 'check_circle', tone: 'positive', words: 'completed' });
  });

  it('is a grey check for an idle team whose latest workflow a person closed', () => {
    expect(teamStatusIcon('idle', latest('Closed'))).toMatchObject({ icon: 'check_circle', tone: 'neutral', words: 'closed by a person' });
  });

  it('is nothing for a team that has run no workflow, or whose workflows have not been read', () => {
    expect(teamStatusIcon('idle', latest(null))).toBeNull();
    expect(teamStatusIcon('idle', null)).toBeNull();
  });

  it.each([
    ['running', 'sync', 'active'],
    ['waiting', 'hourglass_top', 'neutral'],
    ['queued', 'hourglass_top', 'neutral'],
    ['paused', 'pause_circle', 'neutral'],
    ['blocked', 'block', 'warning'],
    ['undeclared', 'help', 'warning'],
    ['failed', 'error', 'negative'],
    ['misconfigured', 'warning', 'negative'],
  ] as const)('shows %s as %s in the %s tone, whatever the latest workflow says', (status, icon, tone) => {
    expect(teamStatusIcon(status, latest('Completed'))).toMatchObject({ icon, tone });
  });

  it('says a waiting team\'s held members, in the Status column\'s words', () => {
    expect(teamStatusIcon('waiting', null, ['Manager'])?.words).toBe('waiting for a slot: Manager');
  });

  it('turns only while the team runs', () => {
    expect(teamStatusIcon('running', null)?.spin).toBe(true);
    expect(teamStatusIcon('queued', null)?.spin).toBeUndefined();
  });
});
