// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import type { Skill, SkillVersion, TeamLessons, TeamSummary, YawbleApi } from '../../src/shared/api';
import { writerText } from '../../src/shared/api';
import SkillsDialog from '../../src/renderer/src/components/SkillsDialog.vue';
import TeamManageDialog from '../../src/renderer/src/components/TeamManageDialog.vue';
import { skillLink } from '../../src/renderer/src/skillLink';
import { stopConfirmStubs, stubConfirm } from './confirm-stub';

// Settings > Skills shows a team skill's versions and who last wrote it (Restore a version);
// Team settings shows "Lessons: n, last updated <date> by the retro of workflow <id>" with a link to the skill. Fakes only.

const team = { id: 4, name: 'payments' } as TeamSummary;
const retros: Skill = {
  name: 'team_retros', description: 'Lessons from this team\'s earlier work.', roles: ['any'], kind: 'custom', teamId: 4, team: 'payments', id: 31,
  body: '## Git\n- Fetch the team branch before merging (2026-11-18, workflow 12)\n- the person\'s own lesson', updatedAt: '2026-11-18T10:00:00Z',
};
const plain: Skill = { name: 'release-notes', description: 'How to write release notes.', roles: ['any'], kind: 'custom', teamId: null, team: null, id: 5, body: 'Short.', updatedAt: null };
const v = (o: Partial<SkillVersion>): SkillVersion => ({ id: 1, teamId: 4, name: 'team_retros', version: 1, body: '- old', writer: 'Manager', workflow: 12, restoredFrom: null, writtenAt: '2026-11-17T09:00:00Z', ...o });
const versions = [
  v({ id: 3, version: 3, body: retros.body, writer: 'person', workflow: null, writtenAt: '2026-11-18T10:00:00Z' }),
  v({ id: 2, version: 2, body: '- Fetch the team branch before merging (2026-11-18, workflow 12)', workflow: 12, writtenAt: '2026-11-18T09:00:00Z' }),
  v({ id: 1, version: 1, body: '- first lesson', workflow: 9 }),
];

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

afterEach(() => { stopConfirmStubs(); skillLink.value = null; document.body.innerHTML = ''; });

describe('Settings > Skills: a team skill\'s versions and last writer', () => {
  it('team_retros shows who last wrote it, its versions newest first, and each version\'s text', async () => {
    const { api, calls } = fakeApi({ 'skills.list': () => [plain, retros], 'skills.versions': () => versions });
    const w = mount(SkillsDialog, { props: { api, teams: [team], inline: true }, attachTo: document.body });
    await flushPromises();
    await w.get('[data-test=skill-team_retros]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'skills.versions')!.params).toEqual({ teamId: 4, name: 'team_retros' });
    expect(w.get('[data-test=skill-last-writer]').text()).toContain('Last written by you (Settings > Skills)');
    expect(w.get('[data-test=skill-last-writer]').text()).toContain('(version 3)');
    expect(w.get('[data-test=skill-lessons-note]').text()).toContain('every run of team payments gets this text in its prompt');
    expect(w.get('[data-test=skill-versions] summary').text()).toBe('Versions (3)');
    expect(w.findAll('[data-test=skill-versions] li').map((li) => li.attributes('data-test'))).toEqual(['skill-version-3', 'skill-version-2', 'skill-version-1']);
    expect(w.get('[data-test=skill-version-2]').text()).toContain('the retro of workflow 12');
    expect(w.find('[data-test=skill-version-restore-3]').exists()).toBe(false); // the current one
    await w.get('[data-test=skill-version-show-1]').trigger('click');
    expect(w.get('[data-test=skill-version-body-1]').text()).toBe('- first lesson');
    w.unmount();
  });

  it('Restore brings a version back after a confirm, and the list reloads', async () => {
    let restored = false;
    const { api, calls } = fakeApi({
      'skills.list': () => [plain, restored ? { ...retros, body: '- first lesson' } : retros],
      'skills.versions': () => restored ? [v({ id: 4, version: 4, body: '- first lesson', writer: 'person', workflow: null, restoredFrom: 1 }), ...versions] : versions,
      'skills.restore': () => { restored = true; return { ...retros, body: '- first lesson' }; },
    });
    const confirm = stubConfirm(() => true);
    const w = mount(SkillsDialog, { props: { api, teams: [team], inline: true }, attachTo: document.body });
    await flushPromises();
    await w.get('[data-test=skill-team_retros]').trigger('click');
    await flushPromises();
    await w.get('[data-test=skill-version-restore-1]').trigger('click');
    await flushPromises();
    expect(confirm.mock.calls[0]![0]).toContain('Bring back version 1 of team_retros?');
    expect(calls.find((c) => c.method === 'skills.restore')!.params).toEqual({ id: 1 });
    expect(w.get('[data-test=skill-view]').findAll('li').map((li) => li.text())).toEqual(['first lesson']); // rendered
    expect(w.get('[data-test=skill-last-writer]').text()).toContain('you (restored version 1)');
    expect(w.get('[data-test=skill-saved]').text()).toContain('Restored version 1 of team_retros');
    w.unmount();
  });

  it('a skill without versions (global, or an older core) shows no versions and no last writer', async () => {
    const { api, calls } = fakeApi({ 'skills.list': () => [plain, retros] }); // skills.versions throws: an older core
    const w = mount(SkillsDialog, { props: { api, teams: [team], inline: true }, attachTo: document.body });
    await flushPromises();
    await w.get('[data-test=skill-release-notes]').trigger('click');
    await flushPromises();
    expect(calls.some((c) => c.method === 'skills.versions')).toBe(false); // a global skill has none
    await w.get('[data-test=skill-team_retros]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=skill-versions]').exists()).toBe(false);
    expect(w.find('[data-test=skill-last-writer]').exists()).toBe(false);
    expect(w.find('[data-test=skills-error]').exists()).toBe(false);
    w.unmount();
  });

  it('the Lessons link selects that team\'s team_retros when Skills opens', async () => {
    skillLink.value = { teamId: 4, name: 'team_retros' };
    const { api } = fakeApi({ 'skills.list': () => [plain, { ...retros, teamId: 9, team: 'other' }, retros], 'skills.versions': () => versions });
    const w = mount(SkillsDialog, { props: { api, teams: [team], inline: true }, attachTo: document.body });
    await flushPromises();
    const view = w.get('[data-test=skill-view]'); // rendered markdown
    expect(view.get('h2').text()).toBe('Git');
    expect(view.findAll('li').map((li) => li.text())).toEqual(['Fetch the team branch before merging (2026-11-18, workflow 12)', 'the person\'s own lesson']);
    expect(w.get('h3').text()).toBe('team_retros');
    expect(w.get('.pane .chip').text()).toBe('team payments');
    expect(skillLink.value).toBeNull();
    w.unmount();
  });

  it('writerText names the person, a retro by its workflow, or the writer', () => {
    expect(writerText({ writer: 'person', workflow: null })).toBe('you (Settings > Skills)');
    expect(writerText({ writer: 'person', workflow: null, restoredFrom: 2 })).toBe('you (restored version 2)');
    expect(writerText({ writer: 'Manager', workflow: 41 })).toBe('the retro of workflow 41');
    expect(writerText({ writer: 'Manager', workflow: null })).toBe('Manager');
  });
});

describe('Team settings: the "Lessons" line and link', () => {
  const others = {
    'teams.workflowExpiry': () => ({ days: 3 }), 'teams.quiet': () => ({ quiet: true, working: [], openWorkflows: [] }), 'teams.env': () => ({}),
    'teams.agents': () => [], 'teams.push': () => ({ on: false, origin: null }), 'teams.syncToBase': () => ({ on: true, note: null }),
    'teams.repos': () => ({ main: null, extras: [] }), 'teams.approvals': () => [], 'integrations.list': () => ({ integrations: [] }),
    'integrations.mcpList': () => [], 'integrations.apiList': () => [],
  };

  it('reads "Lessons: n, last updated <date> by the retro of workflow <id>" and links to the skill', async () => {
    const lessons: TeamLessons = { count: 2, version: 2, updatedAt: '2026-11-18T09:00:00Z', writer: 'Manager', workflow: 12, skillId: 31 };
    const { api, calls } = fakeApi({ ...others, 'teams.lessons': () => lessons });
    const w = mount(TeamManageDialog, { props: { team, api }, attachTo: document.body });
    await flushPromises();
    expect(calls.find((c) => c.method === 'teams.lessons')!.params).toEqual({ teamId: 4 });
    const line = w.get('[data-test=team-lessons-line]').text();
    expect(line).toBe(`Lessons: 2, last updated ${new Date('2026-11-18T09:00:00Z').toLocaleDateString()} by the retro of workflow 12`);
    await w.get('[data-test=team-lessons-link]').trigger('click');
    expect(skillLink.value).toEqual({ teamId: 4, name: 'team_retros' });
    expect(w.emitted('close')).toHaveLength(1);
    w.unmount();
  });

  it('says "by you" after the person\'s edit, "none yet" before any, and nothing on an older core', async () => {
    const mine: TeamLessons = { count: 3, version: 3, updatedAt: '2026-11-18T10:00:00Z', writer: 'person', workflow: null, skillId: 31 };
    let w = mount(TeamManageDialog, { props: { team, api: fakeApi({ ...others, 'teams.lessons': () => mine }).api }, attachTo: document.body });
    await flushPromises();
    expect(w.get('[data-test=team-lessons-line]').text()).toMatch(/^Lessons: 3, last updated .+ by you$/);
    w.unmount();

    const none: TeamLessons = { count: 0, version: 0, updatedAt: null, writer: null, workflow: null, skillId: null };
    w = mount(TeamManageDialog, { props: { team, api: fakeApi({ ...others, 'teams.lessons': () => none }).api }, attachTo: document.body });
    await flushPromises();
    expect(w.get('[data-test=team-lessons-line]').text()).toBe('Lessons: none yet. The Manager keeps them after each completed workflow.');
    expect(w.find('[data-test=team-lessons-link]').exists()).toBe(false);
    w.unmount();

    w = mount(TeamManageDialog, { props: { team, api: fakeApi(others).api }, attachTo: document.body });
    await flushPromises();
    expect(w.find('[data-test=team-lessons]').exists()).toBe(false);
    expect(w.find('[data-test=manage-error]').exists()).toBe(false);
    w.unmount();
  });
});
