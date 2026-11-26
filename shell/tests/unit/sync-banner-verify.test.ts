// @vitest-environment happy-dom
// The team page's TeamSyncBanner, fed the REAL core's
// answers. synctobase-*.json were written by the core's git previews verify tests, SyncToBase_* (GIT_PREVIEWS_FIXTURE_OUT set) through
// UiRpc's RpcDispatcher, exactly as a window gets them: behind.from = the team tip, behind.to = the base it is behind; with the
// branches level the core sends behind: null (never a behind with from / to null).
import { flushPromises, mount } from '@vue/test-utils';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import TeamSyncBanner from '../../src/renderer/src/components/TeamSyncBanner.vue';
import { closeCommit } from '../../src/renderer/src/commitView';
import { commitsInHeading } from '../../src/renderer/src/gitRange';
import { LOCAL_MARK } from '../../src/renderer/src/localMark';
import type { YawbleApi } from '../../src/shared/api';

const real = <T = any>(name: string): T => JSON.parse(readFileSync(path.join(__dirname, 'fixtures', 'git-previews', `${name}.json`), 'utf8'));

function fakeApi(answers: Record<string, (params: any) => unknown>) {
  const calls: { method: string; params: any }[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = answers[method];
      if (!answer) throw Object.assign(new Error(`fake: ${method} is not faked`), { code: 'method_not_found' });
      return structuredClone(answer(params)) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  } as YawbleApi;
  return { api, calls };
}

const team = { id: 7, name: 'Alpha', repoPath: null, members: 2, running: 0, queued: 0 } as any;
const wrappers: { unmount: () => void }[] = [];
afterEach(() => { closeCommit(); while (wrappers.length) wrappers.pop()!.unmount(); });
async function banner(answers: Record<string, (params: any) => unknown>) {
  const f = fakeApi(answers);
  const w = mount(TeamSyncBanner, { props: { api: f.api, team, pollMs: 1e9 }, attachTo: document.body });
  wrappers.push(w);
  await flushPromises();
  return { w, ...f };
}
const shas = (w: any, sel: string) => w.findAll(`${sel} [data-test=commit-list-row]`).map((r: any) => r.attributes('data-sha'));

describe('TeamSyncBanner on the real core\'s teams.syncToBase', () => {
  const behind = real('synctobase-behind');
  const range = real('synctobase-range');

  it('the real core sends from / to as full shas whenever behind is set (never null), and behind: null when level', () => {
    expect(behind.behind.from).toMatch(/^[0-9a-f]{40}$/);
    expect(behind.behind.to).toMatch(/^[0-9a-f]{40}$/);
    expect(behind.behind.from).not.toBe(behind.behind.to);
    expect(real('synctobase-level').behind).toBeNull();
    // the range of that exact pair lists the count (core asserted it equals git rev-list to ^from).
    expect(range.count).toBe(behind.behind.count);
    expect(range.commits.length).toBe(behind.behind.count);
  });

  it('the count is a link with the core\'s text, and opens git.commitRange with exactly the pair the core gave', async () => {
    const { w, calls } = await banner({ 'teams.syncToBase': () => behind, 'git.commitRange': () => range });
    const link = w.find('[data-test=team-behind-count]');
    expect(link.exists()).toBe(true);
    expect(link.text()).toBe('team/alpha is 2 commits behind trunk');
    await link.trigger('click');
    await flushPromises();
    const rc = calls.filter((c) => c.method === 'git.commitRange');
    expect(rc).toHaveLength(1);
    expect(rc[0].params).toEqual({ teamId: 7, from: behind.behind.from, to: behind.behind.to });
    expect(shas(w, '[data-test=team-behind-list]')).toEqual(range.commits.map((c: any) => c.sha));
    // no origin in this rig: no not-pushed mark.
    expect(w.find('[data-test=team-behind-list] [data-test=local-mark]').exists()).toBe(false);
    await link.trigger('click');
    expect(w.find('[data-test=team-behind-list]').exists()).toBe(false);
  });

  it('level (behind: null): no count, no link', async () => {
    const { w } = await banner({ 'teams.syncToBase': () => real('synctobase-level') });
    expect(w.find('[data-test=team-behind]').exists()).toBe(false);
    expect(w.find('[data-test=team-behind-count]').exists()).toBe(false);
  });

  it('Sync now on the real git.sync lists exactly the incoming commits under the core\'s team branch', async () => {
    const synced = real('sync-ff');
    let done = false;
    const { w } = await banner({
      'teams.syncToBase': () => (done ? real('synctobase-level') : behind),
      'git.sync': () => { done = true; return synced; },
    });
    await w.find('[data-test=team-sync-now]').trigger('click');
    await flushPromises();
    const came = w.find('[data-test=team-sync-came-in]');
    expect(came.text()).toContain(commitsInHeading(synced.incomingCount, 'team/alpha'));
    expect(shas(w, '[data-test=team-sync-came-in]')).toEqual(synced.incoming.map((c: any) => c.sha));
    // the count and its link are gone once the core says level.
    expect(w.find('[data-test=team-behind-count]').exists()).toBe(false);
  });

  // F4b (fixed, Ines 82de5bb): the banner asks git.status and marks the rows only when the repository has an origin.
  const marks = (w: any) => w.findAll('[data-test=team-sync-came-in] [data-test=commit-list-row]')
    .map((r: any) => r.find('[data-test=local-mark]').exists());
  async function syncWith(status: (() => unknown) | null, synced = real('sync-ff')) {
    const answers: Record<string, (params: any) => unknown> = { 'teams.syncToBase': () => behind, 'git.sync': () => synced };
    if (status) answers['git.status'] = status;
    const b = await banner(answers);
    await b.w.find('[data-test=team-sync-now]').trigger('click');
    await flushPromises();
    return b;
  }

  it('F4b, origin (real git.status status-ff-origin): every not-pushed row that came in carries the mark', async () => {
    const status = real('status-ff-origin');
    expect(status.origin).toBeTruthy();
    const synced = real('sync-ff');
    expect(synced.incoming.every((c: any) => c.notPushed)).toBe(true);
    const { w, calls } = await syncWith(() => status, synced);
    expect(calls.filter((c) => c.method === 'git.status').map((c) => c.params)).toContainEqual({ teamId: 7 });
    expect(marks(w)).toEqual([true, true]);
    expect(w.find('[data-test=team-sync-came-in]').text()).toContain(LOCAL_MARK.short);
  });

  it('F4b, origin, one row pushed: the mark is on the not-pushed row only', async () => {
    const synced = real('sync-ff');
    synced.incoming[1].notPushed = false;
    const { w } = await syncWith(() => real('status-ff-origin'), synced);
    expect(marks(w)).toEqual([true, false]);
  });

  it('F4b, origin null: no mark on any row', async () => {
    const { w } = await syncWith(() => ({ ...real<any>('status-ff-origin'), origin: null }));
    expect(marks(w)).toEqual([false, false]);
    expect(w.find('[data-test=team-sync-came-in] [data-test=local-mark]').exists()).toBe(false);
  });

  it('F4b, git.status fails: the list shows with no mark', async () => {
    const { w } = await syncWith(null);
    expect(marks(w)).toEqual([false, false]);
  });
});
