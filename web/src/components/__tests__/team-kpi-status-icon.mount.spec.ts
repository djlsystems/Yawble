// @vitest-environment happy-dom
//
// THE TEAM'S STATUS ICON ON ITS OWN STRIP, to the right of the Activity tile, from the status the
// tab already shows: a green check once its latest workflow was declared complete.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { mount } from '@vue/test-utils';

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  // The Activity tile's read; nothing here is about it, so it never answers.
  getTeamActivity: () => new Promise(() => {}),
}));

import TeamKpiStrip from '../TeamKpiStrip.vue';
import TeamStatisticsTile from '../TeamStatisticsTile.vue';
import { asTeamId } from '../../api/types';
import type { TeamWorkflows, TeamWorkflowTiming } from '../../api/types';
import type { TeamStatus } from '../../lib/teamKpis';
import { resetBody } from '../../test/mountQuasar';

const NOW = '2026-09-20T10:00:00Z';

function workflows(state: string): TeamWorkflows {
  return {
    available: true,
    openCount: 0,
    totalCount: 1,
    earliestStartedAt: null,
    serverNow: NOW,
    workflows: [{
      available: true, correlation: 1, state, startedAt: NOW, endedAt: NOW, serverNow: NOW,
      executionSeconds: 0, partial: false, runsCounted: 0, runsUnfinished: 0, blockedBy: null, runsFailed: 0,
      failedMembers: [], missing: null, members: [], lastActivityAt: NOW, awaitingFrom: null, subject: null,
      pausedAt: null, pausedReason: null, pausedLimit: null,
    } as TeamWorkflowTiming],
    missing: null,
  };
}

function mountStrip(status: TeamStatus | null, latest = 'Completed') {
  setActivePinia(createPinia());
  return mount(TeamKpiStrip, {
    props: {
      teamId: asTeamId('alpha'),
      containers: [],
      usage: null,
      timing: null,
      workflows: workflows(latest),
      findings: [],
      clockOffset: 0,
      status,
    },
  });
}

afterEach(() => resetBody());

describe('the status icon on a team\'s strip', () => {
  it('is a green check inside the Activity tile, beside its lanes, when the latest workflow was completed', () => {
    const strip = mountStrip('idle');

    const icon = strip.find('[data-team-status]');
    expect(icon.attributes('aria-label')).toBe('completed');
    expect(icon.classes()).toContain('team-status-icon--positive');
    expect(strip.findComponent(TeamStatisticsTile).element.contains(icon.element)).toBe(true);
    expect(icon.element.closest('.stats-status')).not.toBeNull();
  });

  it('is a grey check when a person closed the latest workflow', () => {
    const icon = mountStrip('idle', 'Closed').find('[data-team-status]');
    expect(icon.attributes('aria-label')).toBe('closed by a person');
    expect(icon.classes()).toContain('team-status-icon--neutral');
  });

  it('follows the team\'s status, not the latest workflow, while something louder is true', () => {
    const icon = mountStrip('blocked').find('[data-team-status]');
    expect(icon.attributes('data-team-status')).toBe('blocked');
    expect(icon.classes()).toContain('team-status-icon--warning');
  });

  it('is absent when no status is given', () => {
    expect(mountStrip(null).find('[data-team-status]').exists()).toBe(false);
  });
});
