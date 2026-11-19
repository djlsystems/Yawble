// @vitest-environment happy-dom
// B075 (card 447): "Open on …" links, the provider in Team settings > Repositories, and the Git dialog's pull request
// line, against a fake core on the card 446 contract: git.webLink {teamId, team, repo?, kind, …} -> { url | null };
// teams.repos entries gain provider, providerDetected, linkPattern, prSupported, prUnavailable; teams.setRepo saves.
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import GitDialog from '../../src/renderer/src/components/GitDialog.vue';
import TeamManageDialog from '../../src/renderer/src/components/TeamManageDialog.vue';
import type { RepoProviderInfo, TeamGitStatus, YawbleApi } from '../../src/shared/api';
import { effectiveProvider, openOnText } from '../../src/renderer/src/gitWebLinks';

function fakeApi(answers: Record<string, (p: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const api = {
    call: async (method: string, params?: any) => {
      calls.push({ method, params });
      const a = answers[method];
      if (!a) throw new Error(`unexpected ${method}`);
      return a(params);
    },
    onEvent: () => () => {},
  } as unknown as YawbleApi;
  return { api, calls };
}

const team = { id: 3, name: 'ops', repoPath: null, members: 2, running: 0, queued: 0 };
const status = (over: Partial<TeamGitStatus> = {}): TeamGitStatus => ({
  repoPath: 'C:\\work\\service', managed: false, baseBranch: 'main', checkedOut: 'main', dirty: false, origin: 'https://gitlab.com/acme/svc.git',
  baseSha: 'aaaaaaaaaaaa', originBaseSha: 'aaaaaaaaaaaa', baseAheadOfOrigin: 0, baseBehindOrigin: 0,
  teamBranch: 'team/ops', teamSha: 'bbbbbbbbbbbb', teamAheadOfBase: 2, teamBehindBase: 0, merge: 'conflict', conflictFiles: ['src/a.ts'],
  members: [{ branch: 'members/ops/ada', sha: 'cccccccccccc', aheadOfBase: 1, inTeamBranch: false }],
  ...over,
});
const gitlab: RepoProviderInfo = { provider: null, providerDetected: 'gitlab', linkPattern: null, prSupported: true, prUnavailable: null };

// The fake core's links: everything on origin except the member's commit (not pushed: null).
const webLink = (p: any) => {
  const base = 'https://gitlab.com/acme/svc';
  switch (p.kind) {
    case 'commit': return { url: p.commit === 'cccccccccccc' ? null : `${base}/-/commit/${p.commit}` };
    case 'branch': return { url: `${base}/-/tree/${p.branch}` };
    case 'compare': return { url: `${base}/-/compare/${p.from}...${p.to}` };
    case 'file': return { url: `${base}/-/blob/${p.commit}/${p.file}` };
    default: return { url: null };
  }
};
const gitAnswers = (s: TeamGitStatus, info: RepoProviderInfo | null, link: (p: any) => unknown = webLink) => ({
  'git.status': () => s,
  'teams.repos': () => ({ main: { name: 'svc', ...(info ?? {}) }, extras: [] }),
  'git.webLink': link,
});

afterEach(() => { document.body.innerHTML = ''; });

describe('Open on … links in the Git dialog (B075)', () => {
  it('shows a link for each commit, branch, compare and file the core gives a url for, with the provider in its text', async () => {
    const { api, calls } = fakeApi(gitAnswers(status(), gitlab));
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    const href = (t: string) => w.find(`[data-test=${t}]`).attributes('href');
    expect(href('git-link-base-branch')).toBe('https://gitlab.com/acme/svc/-/tree/main');
    expect(w.find('[data-test=git-link-base-branch]').text()).toBe('Open on GitLab');
    expect(href('git-link-base-commit')).toBe('https://gitlab.com/acme/svc/-/commit/aaaaaaaaaaaa');
    expect(href('git-link-team-commit')).toBe('https://gitlab.com/acme/svc/-/commit/bbbbbbbbbbbb');
    expect(href('git-link-team-branch')).toBe('https://gitlab.com/acme/svc/-/tree/team/ops');
    expect(href('git-link-compare')).toBe('https://gitlab.com/acme/svc/-/compare/main...team/ops');
    expect(w.find('[data-test="git-link-file-src/a.ts"]').attributes('href')).toBe('https://gitlab.com/acme/svc/-/blob/bbbbbbbbbbbb/src/a.ts');
    expect(w.find('[data-test="git-link-branch-members/ops/ada"]').exists()).toBe(true);
    // Every link opens in the browser like the app's other external links.
    for (const a of w.findAll('a.web, a.sha')) expect(a.attributes('target')).toBe('_blank');
    // The contract's names: git.webLink with the team (id and name), no repo for the main one, the kind and its fields.
    const compare = calls.find((c) => c.method === 'git.webLink' && c.params.kind === 'compare')!;
    expect(compare.params).toEqual({ teamId: 3, team: 'ops', kind: 'compare', from: 'main', to: 'team/ops' });
    const file = calls.find((c) => c.method === 'git.webLink' && c.params.kind === 'file')!;
    expect(file.params).toEqual({ teamId: 3, team: 'ops', kind: 'file', commit: 'bbbbbbbbbbbb', file: 'src/a.ts' });
  });

  it('a null url (a commit not pushed) shows no link: the short commit stays plain text', async () => {
    const { api } = fakeApi(gitAnswers(status(), gitlab));
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    expect(w.find('[data-test="git-link-commit-members/ops/ada"]').exists()).toBe(false);
    expect(w.find('[data-test=git-members]').text()).toContain('cccccccccc');
  });

  it('no links at all when the core answers null for everything, when it has no git.webLink, or with no origin', async () => {
    for (const [s, link] of [
      [status(), () => ({ url: null })],
      [status(), () => { throw new Error('unknown method git.webLink'); }],
      [status({ origin: null }), webLink],
    ] as [TeamGitStatus, (p: any) => unknown][]) {
      const { api, calls } = fakeApi(gitAnswers(s, gitlab, link));
      const w = mount(GitDialog, { props: { api, team } });
      await flushPromises();
      expect(w.findAll('a.web, a.sha')).toHaveLength(0);
      expect(w.find('[data-test=git-merge-state]').text()).toContain('bbbbbbbbbb');
      if (!s.origin) expect(calls.some((c) => c.method === 'git.webLink')).toBe(false);
      w.unmount();
    }
  });

  it('an extra repository asks with its name; a link pattern on a host with no provider reads "Open in the browser"', async () => {
    const { api, calls } = fakeApi({
      'git.status': () => status({ merge: 'up-to-date', conflictFiles: [], members: [] }),
      'teams.repos': () => ({ main: { name: 'svc', ...gitlab }, extras: [{ name: 'tools', writable: true, provider: null, providerDetected: 'none', linkPattern: '{base}/c/{commit}', prSupported: false, prUnavailable: 'x' }] }),
      'git.webLink': (p: any) => ({ url: `https://git.example.com/${p.kind}` }),
    });
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    await w.find('[data-test=git-repo-tools]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'git.webLink').some((c) => c.params.repo === 'tools')).toBe(true);
    expect(w.find('[data-test=git-link-team-branch]').text()).toBe('Open in the browser');
  });

  it('the text: the override wins over the detected provider; none or nothing is "Open in the browser"', () => {
    expect(openOnText(effectiveProvider({ provider: 'azuredevops', providerDetected: 'github' }))).toBe('Open on Azure DevOps');
    expect(openOnText(effectiveProvider({ provider: null, providerDetected: 'bitbucket' }))).toBe('Open on Bitbucket');
    expect(openOnText(effectiveProvider({ provider: null, providerDetected: 'gitea' }))).toBe('Open on Gitea');
    expect(openOnText(effectiveProvider({ provider: null, providerDetected: 'none' }))).toBe('Open in the browser');
    expect(openOnText(effectiveProvider(null))).toBe('Open in the browser');
  });
});

describe('the Git dialog\'s pull request line (B075)', () => {
  it('supported: says which provider, no GitHub-only wording', async () => {
    const { api } = fakeApi(gitAnswers(status(), gitlab));
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    const t = w.find('[data-test=git-prs]').text();
    expect(t).toContain('Pull requests on GitLab');
    expect(t).not.toContain('GitHub');
  });

  it('not supported: shows the core\'s prUnavailable text as is', async () => {
    const why = 'pull requests are not supported for Bitbucket from here; open them on bitbucket.org.';
    const { api } = fakeApi(gitAnswers(status(), { provider: null, providerDetected: 'bitbucket', prSupported: false, prUnavailable: why }));
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    expect(w.find('[data-test=git-prs]').text()).toBe(why);
  });

  it('an older core (no prSupported) shows no pull request line', async () => {
    const { api } = fakeApi(gitAnswers(status(), null));
    const w = mount(GitDialog, { props: { api, team } });
    await flushPromises();
    expect(w.find('[data-test=git-prs]').exists()).toBe(false);
  });
});

describe('Team settings > Repositories: provider and link pattern (B075)', () => {
  const answers = (state: { main: RepoProviderInfo; tools: RepoProviderInfo }) => ({
    'teams.quiet': () => ({ quiet: true, working: [], openWorkflows: [] }), 'teams.env': () => ({}), 'teams.agents': () => [],
    'teams.push': () => ({ on: false, origin: null }), 'teams.approvals': () => [], 'teams.syncToBase': () => ({ on: true, note: null }),
    'teams.workflowExpiry': () => ({ days: 3 }),
    'integrations.list': () => ({ integrations: [], pendingAsks: 0 }), 'integrations.mcpList': () => [], 'integrations.apiList': () => [],
    'teams.repos': () => ({
      main: { path: 'C:/r', baseBranch: 'main', managed: false, origin: 'https://git.corp.example/acme/svc.git', name: 'svc', ...state.main },
      extras: [{ name: 'tools', path: 'C:/t', writable: true, baseBranch: 'main', source: 'https://github.com/acme/tools.git', ...state.tools }],
    }),
    'teams.setRepo': (p: any) => {
      const target = p.name === 'tools' ? state.tools : state.main;
      target.provider = p.provider;
      target.linkPattern = p.linkPattern;
      return {};
    },
  });
  const mountIt = (api: YawbleApi) => mount(TeamManageDialog, { props: { team: { ...team, name: 'ops' }, api },
    global: { stubs: { IntegrationDialog: true, Modal: { template: '<div><slot /></div>' } } } });

  it('shows the detected provider read-only, and saving an override and a pattern round-trips through teams.setRepo', async () => {
    const state = { main: { provider: null, providerDetected: 'none', linkPattern: null } as RepoProviderInfo, tools: { provider: null, providerDetected: 'github', linkPattern: null } as RepoProviderInfo };
    const { api, calls } = fakeApi(answers(state));
    const w = mountIt(api);
    await flushPromises();
    await w.find('[data-test=manage-tab-repos]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=repo-main-provider-detected]').text()).toBe('plain git (no provider)');
    expect(w.find('[data-test=repo-tools-provider-detected]').text()).toBe('GitHub');
    expect(w.find('[data-test=repo-main-provider-save]').attributes('disabled')).toBeDefined(); // nothing changed yet

    await w.find('[data-test=repo-main-provider-select]').setValue('gitea');
    await w.find('[data-test=repo-main-link-pattern]').setValue('  https://git.corp.example/{path}/commit/{commit}  ');
    await w.find('[data-test=repo-main-provider-save]').trigger('click');
    await flushPromises();
    const save = calls.find((c) => c.method === 'teams.setRepo')!;
    expect(save.params).toEqual({ teamId: 3, provider: 'gitea', linkPattern: 'https://git.corp.example/{path}/commit/{commit}' });
    // Read back from teams.repos: the fields show what was saved, and Save is idle again.
    expect(calls.filter((c) => c.method === 'teams.repos').length).toBeGreaterThan(1);
    expect((w.find('[data-test=repo-main-provider-select]').element as HTMLSelectElement).value).toBe('gitea');
    expect((w.find('[data-test=repo-main-link-pattern]').element as HTMLInputElement).value).toBe('https://git.corp.example/{path}/commit/{commit}');
    expect(w.find('[data-test=repo-main-provider-save]').attributes('disabled')).toBeDefined();

    // An extra by name; "As detected" and a blank pattern clear the override (null, null).
    await w.find('[data-test=repo-tools-provider-select]').setValue('gitlab');
    await w.find('[data-test=repo-tools-provider-save]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'teams.setRepo')[1].params).toEqual({ teamId: 3, name: 'tools', provider: 'gitlab', linkPattern: null });
    await w.find('[data-test=repo-tools-provider-select]').setValue('');
    await w.find('[data-test=repo-tools-provider-save]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'teams.setRepo')[2].params).toEqual({ teamId: 3, name: 'tools', provider: null, linkPattern: null });
    expect(state.tools.provider).toBeNull();
  });

  it('a pattern of several templates is saved as typed; the hint gives each kind\'s placeholders; providerOverride is ignored', async () => {
    const state = {
      main: { provider: null, providerOverride: 'gitlab', providerDetected: 'none', linkPattern: null } as RepoProviderInfo,
      tools: { provider: null, providerDetected: 'github', linkPattern: null } as RepoProviderInfo,
    };
    const { api, calls } = fakeApi(answers(state));
    const w = mountIt(api);
    await flushPromises();
    await w.find('[data-test=manage-tab-repos]').trigger('click');
    await flushPromises();
    // Only provider is the override: providerOverride (core may send it, equal to provider) changes nothing.
    expect((w.find('[data-test=repo-main-provider-select]').element as HTMLSelectElement).value).toBe('');
    const hint = w.find('[data-test=repo-main-link-pattern-hint]').text();
    for (const part of ['commit {commit}', 'branch {branch}', 'file {commit} {file}', 'compare {from} {to}', 'pr {pr}', '{base} and {path}'])
      expect(hint).toContain(part);
    const two = 'https://git.corp.example/{path}/commit/{commit} https://git.corp.example/{path}/src/{branch}';
    await w.find('[data-test=repo-main-link-pattern]').setValue(` ${two} `);
    await w.find('[data-test=repo-main-provider-save]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'teams.setRepo')!.params).toEqual({ teamId: 3, provider: null, linkPattern: two });
  });

  it('an older core (no providerDetected) shows no provider fields', async () => {
    const { api } = fakeApi(answers({ main: {}, tools: {} }));
    const w = mountIt(api);
    await flushPromises();
    await w.find('[data-test=manage-tab-repos]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=repo-main-provider]').exists()).toBe(false);
    expect(w.find('[data-test=repo-tools-provider]').exists()).toBe(false);
  });
});
