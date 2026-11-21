// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import type { Skill, TeamSummary, YawbleApi } from '../../src/shared/api';
import SkillsDialog from '../../src/renderer/src/components/SkillsDialog.vue';
import {
  emptyFilterText, filterChoices, filterSkills, selectionAfterFilter, skillOrigin, skillTag,
} from '../../src/renderer/src/components/skillsFilter';

// Card 529: Settings > Skills' origin filter (All / Built-in / Team skills) and the skill's text as rendered markdown.

const sk = (o: Partial<Skill>): Skill => ({
  name: 'x', description: 'd', roles: ['member'], body: 'b', kind: 'builtin', teamId: null, team: null, id: null, updatedAt: null, ...o,
});
const manager = sk({ name: 'manager' });
const member = sk({ name: 'member' });
const retros = sk({ name: 'team_retros', kind: 'custom', teamId: 4, team: 'payments', id: 31, roles: ['any'] });
const mine = sk({ name: 'release-notes', kind: 'custom', teamId: null, team: null, id: 5 });
const all = [manager, member, retros, mine];
const same = (a: Skill, b: Skill) => a.name === b.name && a.teamId === b.teamId && a.kind === b.kind;

describe('skillsFilter: origins and the filter', () => {
  it('tells a skill\'s origin and tag the way the dialog shows it', () => {
    expect(skillOrigin(manager)).toBe('builtin');
    expect(skillTag(manager)).toBe('built-in');
    expect(skillOrigin(retros)).toBe('team');
    expect(skillTag(retros)).toBe('team payments');
    expect(skillOrigin(mine)).toBe('allTeams');
    expect(skillTag(mine)).toBe('all teams');
  });

  it('All shows every skill, Built-in the built-ins, Team skills the one-team skills', () => {
    expect(filterSkills(all, 'all')).toEqual(all);
    expect(filterSkills(all, 'builtin')).toEqual([manager, member]);
    expect(filterSkills(all, 'team')).toEqual([retros]);
    expect(filterSkills(all, 'allTeams')).toEqual([mine]);
  });

  it('offers All teams only while a person-added skill for every team exists, so none vanishes', () => {
    expect(filterChoices(all).map((c) => c.label)).toEqual(['All', 'Built-in', 'Team skills', 'All teams']);
    expect(filterChoices([manager, retros]).map((c) => c.id)).toEqual(['all', 'builtin', 'team']);
    expect(filterChoices([manager, retros], 'allTeams').map((c) => c.id)).toContain('allTeams'); // the current one stays
  });

  it('keeps the selection when visible, else the first visible skill, else none', () => {
    const builtins = filterSkills(all, 'builtin');
    expect(selectionAfterFilter(builtins, member, same)).toBe(member);
    expect(selectionAfterFilter(builtins, retros, same)).toBe(manager);
    expect(selectionAfterFilter(builtins, null, same)).toBe(manager);
    expect(selectionAfterFilter(filterSkills([manager], 'team'), manager, same)).toBeNull();
    expect(emptyFilterText('team')).toBe('No team skills.');
  });
});

function fakeApi(list: () => Skill[], save?: (p: any) => Skill) {
  const calls: { method: string; params: any }[] = [];
  const api = {
    call: async (method: string, params?: any) => {
      calls.push({ method, params });
      if (method === 'skills.list') return list();
      if (method === 'skills.versions') return [];
      if (method === 'skills.save' && save) return save(params);
      throw new Error(`unexpected ${method}`);
    },
    onEvent: () => () => {},
  } as unknown as YawbleApi;
  return { api, calls };
}
const team = { id: 4, name: 'payments' } as TeamSummary;
afterEach(() => { document.body.innerHTML = ''; });

const names = (w: ReturnType<typeof mount>) => w.findAll('[data-test=skills-list] .item strong').map((s) => s.text());

describe('Settings > Skills: the origin filter', () => {
  it('defaults to All; Built-in and Team skills narrow the list; a hidden selection moves to the first shown', async () => {
    const { api } = fakeApi(() => [manager, member, retros]);
    const w = mount(SkillsDialog, { props: { api, teams: [team], inline: true }, attachTo: document.body });
    await flushPromises();
    expect(w.get('[data-test=skills-filter-all]').attributes('aria-selected')).toBe('true');
    expect(w.find('[data-test=skills-filter-allTeams]').exists()).toBe(false);
    expect(names(w)).toEqual(['manager', 'member', 'team_retros']);

    await w.get('[data-test=skill-team_retros]').trigger('click');
    await w.get('[data-test=skills-filter-builtin]').trigger('click');
    await flushPromises();
    expect(names(w)).toEqual(['manager', 'member']);
    expect(w.get('[data-test=skills-pane] h3').text()).toBe('manager'); // team_retros was filtered out

    await w.get('[data-test=skill-member]').trigger('click');
    await w.get('[data-test=skills-filter-all]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=skills-pane] h3').text()).toBe('member'); // still visible: kept

    await w.get('[data-test=skills-filter-team]').trigger('click');
    await flushPromises();
    expect(names(w)).toEqual(['team_retros']);
    expect(w.get('[data-test=skills-pane] h3').text()).toBe('team_retros');
    w.unmount();
  });

  it('a filter with nothing in it says so in the list and the pane', async () => {
    const { api } = fakeApi(() => [manager, member]);
    const w = mount(SkillsDialog, { props: { api, teams: [team], inline: true }, attachTo: document.body });
    await flushPromises();
    await w.get('[data-test=skill-manager]').trigger('click');
    await w.get('[data-test=skills-filter-team]').trigger('click');
    await flushPromises();
    expect(names(w)).toEqual([]);
    expect(w.get('[data-test=skills-filter-none]').text()).toBe('No team skills.');
    expect(w.get('[data-test=skills-filter-empty]').text()).toBe('No team skills.');
    expect(w.find('[data-test=skill-view]').exists()).toBe(false);
    w.unmount();
  });

  it('a person-added skill for every team shows under All and under its own All teams filter', async () => {
    const { api } = fakeApi(() => all);
    const w = mount(SkillsDialog, { props: { api, teams: [team], inline: true }, attachTo: document.body });
    await flushPromises();
    expect(names(w)).toContain('release-notes');
    await w.get('[data-test=skills-filter-allTeams]').trigger('click');
    await flushPromises();
    expect(names(w)).toEqual(['release-notes']);
    w.unmount();
  });

  // Card 531 (TesterWren's F1 on card 529): the filter's change waited while editing, and Cancel showed the hidden skill.
  it('a filter changed while editing applies at Cancel: the pane shows a skill the list shows', async () => {
    const { api } = fakeApi(() => [manager, retros]);
    const w = mount(SkillsDialog, { props: { api, teams: [team], inline: true }, attachTo: document.body });
    await flushPromises();
    await w.get('[data-test=skill-team_retros]').trigger('click');
    await w.get('[data-test=skill-edit]').trigger('click');
    await w.get('[data-test=skills-filter-builtin]').trigger('click');
    await flushPromises();
    await w.get('[data-test=skill-cancel]').trigger('click');
    await flushPromises();
    expect(names(w)).toEqual(['manager']);
    expect(w.get('[data-test=skills-filter-builtin]').attributes('aria-selected')).toBe('true');
    expect(w.get('[data-test=skills-pane] h3').text()).toBe('manager');
    w.unmount();
  });

  it('Cancel with the edited skill still shown by the filter goes back to it', async () => {
    const { api } = fakeApi(() => [manager, retros]);
    const w = mount(SkillsDialog, { props: { api, teams: [team], inline: true }, attachTo: document.body });
    await flushPromises();
    await w.get('[data-test=skill-team_retros]').trigger('click');
    await w.get('[data-test=skill-edit]').trigger('click');
    await w.get('[data-test=skills-filter-team]').trigger('click');
    await flushPromises();
    await w.get('[data-test=skill-cancel]').trigger('click');
    await flushPromises();
    expect(names(w)).toEqual(['team_retros']);
    expect(w.get('[data-test=skills-pane] h3').text()).toBe('team_retros');
    w.unmount();
  });
});

const MD = '# Title\n\nSome text with `inline code` and a [link](https://example.com/docs).\n\n## Steps\n- one\n- two\n\n```sh\nnpm run build\n```\n\n<b>raw</b>';

describe('Settings > Skills: the skill\'s text as markdown', () => {
  it('renders headings, a list, inline code, a code block and a link (opening outside); raw HTML stays text', async () => {
    const { api } = fakeApi(() => [sk({ name: 'guide', kind: 'custom', id: 9, body: MD })]);
    const w = mount(SkillsDialog, { props: { api, teams: [team], inline: true }, attachTo: document.body });
    await flushPromises();
    await w.get('[data-test=skill-guide]').trigger('click');
    const view = w.get('[data-test=skill-view]');
    expect(view.element.tagName).toBe('DIV');
    expect(view.get('h1').text()).toBe('Title');
    expect(view.get('h2').text()).toBe('Steps');
    expect(view.findAll('li').map((li) => li.text())).toEqual(['one', 'two']);
    expect(view.get('p code').text()).toBe('inline code');
    expect(view.get('pre code').text().trim()).toBe('npm run build');
    expect(view.find('[data-test=copy-code]').exists()).toBe(true);
    const a = view.get('a');
    expect(a.attributes('href')).toBe('https://example.com/docs');
    expect(a.attributes('target')).toBe('_blank');
    expect(a.attributes('rel')).toContain('noopener');
    expect(view.find('b').exists()).toBe(false);
    expect(view.text()).toContain('<b>raw</b>');
    w.unmount();
  });

  it('Edit shows the raw source; Save returns to the rendered view with the new text; Cancel to the rendered old text', async () => {
    let body = MD;
    const { api, calls } = fakeApi(
      () => [sk({ name: 'guide', kind: 'custom', id: 9, body })],
      (p) => { body = p.body; return sk({ name: 'guide', kind: 'custom', id: 9, body }); },
    );
    const w = mount(SkillsDialog, { props: { api, teams: [team], inline: true }, attachTo: document.body });
    await flushPromises();
    await w.get('[data-test=skill-guide]').trigger('click');

    await w.get('[data-test=skill-edit]').trigger('click');
    expect(w.find('[data-test=skill-view]').exists()).toBe(false);
    expect((w.get('[data-test=skill-body]').element as HTMLTextAreaElement).value).toBe(MD);
    await w.get('[data-test=skill-cancel]').trigger('click');
    expect(w.get('[data-test=skill-view] h1').text()).toBe('Title'); // back to the same skill, rendered

    await w.get('[data-test=skill-edit]').trigger('click');
    await w.get('[data-test=skill-body]').setValue('## New heading\n- changed');
    await w.get('[data-test=skill-save]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'skills.save')!.params.body).toBe('## New heading\n- changed');
    expect(w.find('[data-test=skill-body]').exists()).toBe(false);
    const view = w.get('[data-test=skill-view]');
    expect(view.get('h2').text()).toBe('New heading');
    expect(view.findAll('li').map((li) => li.text())).toEqual(['changed']);
    w.unmount();
  });
});
