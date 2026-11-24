// @vitest-environment happy-dom
// B094 verify (card 428): every code the core's git.branchRealign really sends (UiRpc.BranchRealignAsync and
// TeamRepos.GitBranchRealignAsync) lands on the right side: git failures plain, the core saying no as "Refused: ".
// Messages are the core's own texts.
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import TidyPanel from '../../src/renderer/src/components/TidyPanel.vue';
import type { TidyPlan, YawbleApi } from '../../src/shared/api';

const team = { id: 3, name: 'payments', repoPath: null, members: 2, running: 0, queued: 0 };
const plan = (): TidyPlan => ({ repos: [{ repo: 'payments', baseBranch: 'main', branches: [
  { name: 'members/payments/marlo', where: ['local', 'origin', 'clone:Marlo'], class: 'moved', member: 'Marlo', teamSha: '5bbb6ba450df', cloneSha: 'e0f2a18aaaaa',
    commits: [{ sha: '7777777777ef', subject: 'Retry the upload' }], proof: 'the clone was reset' },
] }] });

async function realignError(code: string, message: string) {
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string) => {
      if (method === 'git.tidyPlan') return plan() as any;
      if (method === 'git.branchRealign') throw Object.assign(new Error(message), { code });
      throw new Error(`unexpected ${method}`);
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  const w = mount(TidyPanel, { props: { api, team, mainName: 'payments' } });
  await flushPromises();
  await w.find('[data-test=tidy-realign]').trigger('click');
  await w.find('[data-test=tidy-realign-yes]').trigger('click');
  await flushPromises();
  return w.find('[data-test=tidy-realign-error]').text();
}

describe('B094 verify: realign codes the core sends', () => {
  it.each([
    ['push_failed', "origin's members/payments/marlo was not changed (it moved on origin since the last fetch, or the push failed); nothing changed. git said: fatal: unable to access"],
    ['clone', "Marlo's clone cannot be read: .git is missing. Nothing changed."],
    ['clone', "could not fetch Marlo's members/payments/marlo: fatal: bad object. Nothing changed."],
    ['git_failed', 'git exited 128'],
    ['bad_params', 'member is required'],
    ['no_such_member', "payments has no member 'Marlo'"],
  ])('%s is shown plain', async (code, message) => {
    expect(await realignError(code, message)).toBe(message);
  });

  it.each([
    ['running', 'Marlo is running (run 12), so its clone may still move: realign after the run ends. Nothing changed.'],
    ['moved', "the team repository's members/payments/marlo moved while the app was working and was left as it is"],
    ['protected', 'main is the base branch: it is never realigned'],
    ['no_clone_branch', "Marlo's clone has no branch members/payments/marlo. Nothing changed."],
    ['checked_out', 'members/payments/marlo is checked out in C:\\x: its files would not follow, so it is left alone. Nothing changed.'],
    ['attribution', 'Marlo\'s new commits carry AI attribution. Nothing changed.'],
    ['push_rejected', "origin's members/payments/marlo was not changed (it moved on origin since the last fetch); nothing changed."],
    ['no_such_team', 'there is no team 3'],
    ['no_repositories', 'this core has no team repositories (no app data folder)'],
  ])('%s is shown as "Refused: "', async (code, message) => {
    expect(await realignError(code, message)).toBe(`Refused: ${message}`);
  });
});
