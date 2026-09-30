// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount } from '@vue/test-utils';

import TeamKpiStrip from '../TeamKpiStrip.vue';
import { useConsoleStore } from '../../stores/console';
import { asTeamId } from '../../api/types';
import type { TeamTokenTotals } from '../../api/types';
import { bodyText, resetBody } from '../../test/mountQuasar';

const teamId = asTeamId('alpha');

/** The wave1 figures as `GET /api/teams/{team}/tokens` answered them. */
const wave1: TeamTokenTotals = {
  available: true,
  tokensIn: 2_142,
  tokensOut: 577_066,
  tokensCachedIn: 76_466_631,
  tokensCacheCreation: 2_393_199,
  tokensBillable: 11_217_369,
  partial: false,
  runsWithUsage: 12,
  runsWithoutUsage: 0,
  missing: null,
  members: [
    {
      member: 'Manager',
      brand: 'claude-headless',
      tokensIn: 2_142,
      tokensOut: 577_066,
      brandReportsUsage: true,
      runs: 12,
      tokensTotal: null,
      tokensCachedIn: 76_466_631,
      tokensCacheCreation: 2_393_199,
      tokensBillable: 11_217_369,
    },
  ],
};

function mountStrip(usage: TeamTokenTotals | null) {
  setActivePinia(createPinia());

  const board = useConsoleStore();
  board.$patch({
    teams: [{ id: teamId, name: 'Alpha', memberAgents: ['claude-headless'], repos: [] }],
    activeTeamId: teamId,
    overviewLanded: true,
  });
  vi.spyOn(board, 'pullWorkflows').mockResolvedValue();
  vi.spyOn(board, 'pullWorkflow').mockResolvedValue();

  return mount(TeamKpiStrip, {
    props: { teamId, containers: [], usage, timing: null, workflows: null, findings: [], clockOffset: 0 },
  });
}

const n = (value: number) => value.toLocaleString();
const squash = (text: string) => text.replace(/\s+/g, ' ');

/** Each row of a tokens-dialog table, as its figures by column. */
function tableRows(kind: 'brand' | 'member'): Record<string, string>[] {
  return [...document.body.querySelectorAll(`[data-token-table="${kind}"] tbody tr`)].map((tr) =>
    Object.fromEntries(
      [...tr.querySelectorAll('[data-cell]')].map((td) => [td.getAttribute('data-cell')!, squash(td.textContent ?? '').trim()]),
    ),
  );
}

describe('TeamKpiStrip token tile, mounted', () => {
  afterEach(resetBody);

  it('leads with billable and shows in, cache read, cache write and out beneath it', async () => {
    const wrapper = mountStrip(wave1);
    await flushPromises();

    const tile = squash(wrapper.find('.team-kpi--tokens').text());
    expect(squash(wrapper.find('.team-kpi--tokens .team-kpi-value').text())).toBe(
      `${n(11_217_369)} billable`,
    );
    expect(tile).toContain(
      `${n(2_142)} in · ${n(76_466_631)} cache read · ${n(2_393_199)} cache write · ${n(577_066)} out`,
    );

    wrapper.unmount();
  });

  it('shows the four figures and billable on the brand and member rows of the dialog', async () => {
    const wrapper = mountStrip(wave1);
    await flushPromises();
    await wrapper.find('.team-kpi--tokens').trigger('click');
    await flushPromises();

    const row = {
      billable: n(11_217_369),
      in: n(2_142),
      'cache-read': n(76_466_631),
      'cache-write': n(2_393_199),
      out: n(577_066),
    };
    // Once in the brand table, once in the member table: one column per figure.
    expect(tableRows('brand')).toEqual([row]);
    expect(tableRows('member')).toEqual([row]);

    wrapper.unmount();
  });

  it('still reads unavailable when the log carries no usage', async () => {
    const wrapper = mountStrip({ ...wave1, available: false, members: [] });
    await flushPromises();

    expect(wrapper.find('.team-kpi--tokens').text()).toContain('unavailable');
    expect(wrapper.find('.team-kpi--tokens').text()).not.toContain('billable');

    wrapper.unmount();
  });
});

/**
 * WHAT THE TOKEN CARD SAYS, read off the rendered component. These were once assertions over the
 * `.vue` file as source text, which passed as long as the words were somewhere in the file -
 * inside a comment, behind a `v-if` that never fires - and so proved nothing about the screen.
 */
describe('TeamKpiStrip token card copy, mounted', () => {
  afterEach(resetBody);

  async function openDialog(wrapper: ReturnType<typeof mountStrip>) {
    await flushPromises();
    await wrapper.find('.team-kpi--tokens').trigger('click');
    await flushPromises();
  }

  it('has an unavailable state, and points to the Agents screen there', async () => {
    const wrapper = mountStrip({ ...wave1, available: false, members: [], missing: 'No usage in the log' });
    await openDialog(wrapper);

    expect(wrapper.find('.team-kpi--tokens').text()).toMatch(/unavailable/i);
    const text = squash(bodyText());
    expect(text).toMatch(/unavailable/i);
    expect(text).toContain('Configure usage capture in the Agents screen.');
    expect(text).not.toContain('report usage are the ones with usage format set');

    wrapper.unmount();
  });

  it('labels an available total as a team total, and says partial when some runs have no usage', async () => {
    const wrapper = mountStrip({ ...wave1, partial: true, runsWithoutUsage: 2 });
    await openDialog(wrapper);

    const tile = wrapper.find('.team-kpi--tokens');
    expect(tile.find('.team-kpi-claim').text()).toBe('team total');
    expect(tile.find('.team-kpi-partial').text()).toMatch(/partial/i);
    const text = squash(bodyText());
    expect(text).toContain('Team total from the message log.');
    expect(text).not.toContain('Partial — some runs on this team have no usage recorded.');

    wrapper.unmount();
  });

  it('does not say partial when every run carries usage', async () => {
    const wrapper = mountStrip(wave1);
    await flushPromises();

    expect(wrapper.find('.team-kpi--tokens .team-kpi-partial').exists()).toBe(false);

    wrapper.unmount();
  });

  it('renders unknown member and brand totals as the literal (unknown)', async () => {
    const [manager] = wave1.members;
    const wrapper = mountStrip({
      ...wave1,
      members: [{ ...manager!, tokensIn: null, tokensCachedIn: null, tokensCacheCreation: null, tokensBillable: null }],
    });
    await openDialog(wrapper);

    const text = squash(bodyText());
    const row = {
      billable: '(unknown)',
      in: '(unknown)',
      'cache-read': '(unknown)',
      'cache-write': '(unknown)',
      out: n(577_066),
    };
    // Once in the brand table, once in the member table.
    expect(tableRows('brand')).toEqual([row]);
    expect(tableRows('member')).toEqual([row]);
    expect(text).not.toContain('undefined');

    wrapper.unmount();
  });

  it('says it does not sum the retained activity feed, which would count down as the team got busier', async () => {
    const wrapper = mountStrip({ ...wave1, available: false, members: [], missing: 'No usage in the log' });
    await openDialog(wrapper);

    expect(squash(bodyText())).toMatch(/does not sum/);

    wrapper.unmount();
  });
});
