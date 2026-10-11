// @vitest-environment happy-dom
//
// THE TEAM KPI CARD STAYS QUIET ON A PLUGIN-ONLY TEAM. Plugins run no model, so there is no usage to
// capture and no spend to bound: the Tokens tile says `none` rather than `unavailable` with advice
// about usage capture, and the workflow tile draws no budget line or bar. A team with an agent on
// it still says what it said before.
import { afterEach, describe, expect, it, vi } from 'vitest';

// The Activity tile reads `/activity` when it mounts, and the Tokens dialog's chart reads
// `/tokens/runs` when it opens. Nothing here is about either, so neither read ever answers,
// rather than reaching for a server that is not there.
vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getTeamActivity: () => new Promise(() => {}),
  getTeamTokenRuns: () => new Promise(() => {}),
}));
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

import TeamKpiStrip from '../TeamKpiStrip.vue';
import { useConsoleStore } from '../../stores/console';
import { asMemberId, asTeamId } from '../../api/types';
import type { ContainerSnapshot, TeamTokenTotals, TeamWorkflows } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

const teamId = asTeamId('alpha');

const team = { id: teamId, name: 'Alpha', memberAgents: ['claude-headless'], repos: [] };

const member = (id: string, agent: string, kind: 'agent' | 'plugin'): ContainerSnapshot => ({
  team: teamId,
  id: asMemberId(id),
  name: id,
  agent,
  kind,
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
});

const echo = member('Echo', 'plugin:sample-echo', 'plugin');
const mailer = member('Mailer', 'plugin:mailer', 'plugin');
const dev = member('Dev', 'claude-headless', 'agent');

/** What the log projection answers for a team whose runs carried no usage. */
const noUsage = {
  available: false,
  missing: 'No completed or failed run on the log carries tokensIn/tokensOut',
} as unknown as TeamTokenTotals;

/** One open workflow whose spend was never measured, against an instance limit. */
function oneOpenUnmeasured(): TeamWorkflows {
  const started = new Date(Date.now() - 60_000).toISOString();
  return {
    available: true,
    openCount: 1,
    totalCount: 1,
    earliestStartedAt: started,
    serverNow: new Date().toISOString(),
    missing: null,
    workflows: [{
      correlationId: 7,
      subject: 'Transform the text',
      startedAt: started,
      endedAt: null,
      spend: { tokensSpent: null, partial: true },
      members: [],
    }],
  } as unknown as TeamWorkflows;
}

let wrapper: VueWrapper | undefined;

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  resetBody();
});

async function mountStrip(containers: ContainerSnapshot[]) {
  setActivePinia(createPinia());
  useConsoleStore().$patch({
    teams: [team],
    activeTeamId: teamId,
    overviewLanded: true,
    workflowSpendLimit: 50_000_000,
  });

  wrapper = mount(TeamKpiStrip, {
    attachTo: document.body,
    props: { teamId, containers, usage: noUsage, timing: null, workflows: oneOpenUnmeasured(), findings: [], clockOffset: 0 },
  });
  await flushPromises();
  return wrapper;
}

async function openTokensDialog(strip: VueWrapper): Promise<string> {
  await strip.find('.team-kpi--tokens').trigger('click');
  await flushPromises();
  return document.body.querySelector('.token-dialog-card')?.textContent?.replace(/\s+/g, ' ') ?? '';
}

describe('the team KPI card on a plugin-only team', () => {
  it('says none on the Tokens tile, not unavailable', async () => {
    const strip = await mountStrip([echo, mailer]);

    expect(strip.find('.team-kpi--tokens').text()).toContain('none');
    expect(strip.find('.team-kpi--tokens').text()).not.toContain('unavailable');
  });

  it('gives no advice about usage capture, and says why there are no tokens', async () => {
    const dialog = await openTokensDialog(await mountStrip([echo, mailer]));

    expect(dialog).not.toContain('Unavailable');
    expect(dialog).not.toContain('Configure usage capture');
    expect(dialog).toContain('Plugins run no model, so this team uses no tokens.');
  });

  it('draws no budget line or bar on the workflow tile', async () => {
    const strip = await mountStrip([echo]);

    expect(strip.find('.team-kpi-budget').exists()).toBe(false);
    expect(strip.find('.team-kpi-budget-bar').exists()).toBe(false);
  });
});

describe('the team KPI card on a team with an agent', () => {
  it('still says unavailable and points to usage capture', async () => {
    const strip = await mountStrip([dev, echo]);

    expect(strip.find('.team-kpi--tokens').text()).toContain('unavailable');
    expect(await openTokensDialog(strip)).toContain('Configure usage capture in the Agents screen.');
  });

  it('still draws the budget line', async () => {
    const strip = await mountStrip([dev, echo]);

    expect(strip.find('.team-kpi-budget').exists()).toBe(true);
  });
});
