// @vitest-environment happy-dom
//
// THE BOARD'S TEAM SUMMARY SAYS WHEN A TEAM BRANCH IS NOT PUSHED, so a finished workflow whose work
// is only in the team's clone shows without opening the Git dialog. The same words as the dialog.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

import TeamKpiStrip from '../TeamKpiStrip.vue';
import { useConsoleStore } from '../../stores/console';
import { asTeamId } from '../../api/types';
import type { RepoStatus, TeamRepoStatus } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

const teamId = asTeamId('alpha');

const repo = (name: string, teamBranchUnpushed: boolean | null): RepoStatus => ({
  name,
  teamBranch: 'team/alpha',
  teamBranchUnpushed,
} as unknown as RepoStatus);

const statusOf = (...repos: RepoStatus[]): TeamRepoStatus => ({
  git: { command: 'git', resolves: true, message: '', usedBy: 'platform' },
  gh: { command: 'gh', resolves: true, message: '', usedBy: 'agents' },
  repos,
});

let wrapper: VueWrapper | undefined;

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  resetBody();
});

async function mountStrip(repoStatus: TeamRepoStatus | null) {
  setActivePinia(createPinia());
  useConsoleStore().$patch({
    teams: [{ id: teamId, name: 'Alpha', repos: [] }] as never,
    activeTeamId: teamId,
    overviewLanded: true,
  });

  wrapper = mount(TeamKpiStrip, {
    attachTo: document.body,
    props: { teamId, containers: [], usage: null, timing: null, workflows: null, findings: [], clockOffset: 0, repoStatus },
  });
  await flushPromises();
  return wrapper;
}

describe('the team summary and an unpushed team branch', () => {
  it('says "team/<id> is not pushed" when the local team branch is ahead of origin', async () => {
    const strip = await mountStrip(statusOf(repo('Harness', true)));

    expect(strip.find('.team-kpi-unpushed').text()).toBe('team/alpha is not pushed');
  });

  it('names the repositories when the team has several', async () => {
    const strip = await mountStrip(statusOf(repo('Harness', true), repo('Web', false)));

    expect(strip.find('.team-kpi-unpushed').text()).toBe('team/alpha is not pushed (Harness)');
  });

  it('says nothing when it is pushed, not measured, or not read yet', async () => {
    expect((await mountStrip(statusOf(repo('Harness', false)))).find('.team-kpi-unpushed').exists()).toBe(false);
    wrapper?.unmount();
    expect((await mountStrip(statusOf(repo('Harness', null)))).find('.team-kpi-unpushed').exists()).toBe(false);
    wrapper?.unmount();
    expect((await mountStrip(null)).find('.team-kpi-unpushed').exists()).toBe(false);
  });
});
