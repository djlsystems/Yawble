// @vitest-environment happy-dom
//
// THE CONTROL PANEL'S CONTROLS SECTION, each through an EXISTING route: pause and resume the
// team, Run now per schedule (and only a schedule), each trigger's on/off and daily cap, the settings
// the package lists first, "All settings" with the person-only ones, and the connection bindings.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SolutionPanel from '../SolutionPanel.vue';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { fakeHost, reply, sent, type Call, type Route } from '../../test/solutionFixtures';
import { HtmlLooking, openSection, panelRead, panelRoutes, panelTrigger } from '../../test/solutionPanelFixtures';
import type { SolutionPanel as PanelShape } from '../../api/types';
import { localTime } from '../../lib/localTime';

// The re-read after Run now waits a few milliseconds here instead of seconds, and gives up sooner.
vi.mock('../../lib/solutionPanel', async (actual) => ({
  ...(await actual<typeof import('../../lib/solutionPanel')>()),
  RunWatchEveryMs: 5,
  RunWatchTries: 3,
}));

let calls: Call[] = [];
let read: PanelShape;

const ok: Route = (call) => {
  if (call.method === 'POST' && /\/api\/teams\/job-tracker\/(pause|resume)$/.test(call.url)) return reply(200, {});
  if (call.method === 'PATCH' && call.url.startsWith('/api/teams/job-tracker/triggers/')) return reply(200, {});
  if (call.method === 'PUT' && call.url.endsWith('/plugin-settings')) return reply(204);
  if (call.method === 'POST' && call.url === '/api/teams/job-tracker/triggers/trg_scan/run')
    return reply(200, { outcome: 'fired', reason: null, seq: 77, trigger: {} });
  return undefined;
};

function serve(extra: Route[] = []) {
  calls = [];
  vi.stubGlobal('fetch', vi.fn(fakeHost([...extra, ok, ...panelRoutes(() => read)], calls)));
}

beforeEach(() => {
  read = panelRead();
  serve();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function controls() {
  const wrapper = await mountDialog(SolutionPanel, { team: 'job-tracker' });
  await settle();
  await openSection('controls');
  return wrapper;
}

/** QInput puts its attributes on the native input. */
const capBox = () => bodyFind('[data-control-trigger="trg_scan"] input[data-trigger-cap]') as HTMLInputElement;

const click = async (selector: string) => {
  bodyFind(selector)!.click();
  await settle();
};

describe('the control panel: Controls', () => {
  it('pauses the team, and offers resume once it is paused', async () => {
    await controls();

    expect(bodyFind('[data-control-pause]')?.textContent).toContain('Pause the team');
    read = panelRead({ paused: true, state: { kind: 'paused', reason: null } });
    await click('[data-control-pause]');

    expect(sent(calls, 'POST', '/api/teams/job-tracker/pause')).toHaveLength(1);
    expect(bodyFind('[data-control-pause]')?.textContent).toContain('Resume the team');

    await click('[data-control-pause]');
    expect(sent(calls, 'POST', '/api/teams/job-tracker/resume')).toHaveLength(1);
  });

  it('offers Run now on a schedule only, fires it through the run route and says so', async () => {
    await controls();

    expect(bodyFind('[data-control-trigger="trg_apply"] [data-run-now]')).toBeNull();
    await click('[data-control-trigger="trg_scan"] [data-run-now]');

    expect(sent(calls, 'POST', '/api/teams/job-tracker/triggers/trg_scan/run')).toHaveLength(1);
    expect(bodyFind('[data-control-notice]')?.textContent).toBe('Scan for postings is running now.');
  });

  it('shows the run Run now started once it finishes, without Refresh', async () => {
    const before = panelRead();
    const finished: PanelShape = {
      ...before,
      status: '5 new jobs · last checked 2026-09-30T10:00:00Z',
      members: before.members.map((member) =>
        member.member === 'scout' ? { ...member, lastRun: { seq: 42, at: '2026-09-30T10:00:00Z', outcome: 'completed' } } : member,
      ),
      recentRuns: [
        { ...before.recentRuns[0]!, seq: 42, endedAt: '2026-09-30T10:00:00Z', output: '5 new postings' },
        ...before.recentRuns,
      ],
    };
    // The run ends a moment after the route answered: the read as it answers still shows the old
    // runs, every read after that has the finished one.
    let readsSinceRun = -1;
    serve([
      (call) => {
        if (call.method === 'POST' && call.url === '/api/teams/job-tracker/triggers/trg_scan/run') readsSinceRun = 0;
        if (call.method === 'GET' && call.url === '/api/teams/job-tracker/solution/panel' && readsSinceRun >= 0)
          return reply(200, readsSinceRun++ === 0 ? before : finished);
        return undefined;
      },
    ]);
    await controls();
    expect(bodyFind('[data-panel-status]')?.textContent).not.toContain('5 new jobs');

    await click('[data-control-trigger="trg_scan"] [data-run-now]');
    await vi.waitFor(() => expect(bodyFind('[data-panel-status]')?.textContent).toBe(`5 new jobs · last checked ${localTime(Date.parse('2026-09-30T10:00:00Z'), { date: true })}`));

    await new Promise((resolve) => setTimeout(resolve, 60));
    await settle();
    // It stopped at the read that showed the new run: the one as the route answered, and one more.
    expect(readsSinceRun).toBe(2);
    await openSection('results');
    expect(bodyFind('[data-run="42"] [data-run-output]')?.textContent).toBe('5 new postings');
  });

  it('stops re-reading after a bounded number of tries when no new run shows', async () => {
    await controls();
    const reads = () => sent(calls, 'GET', '/api/teams/job-tracker/solution/panel').length;
    const readsBefore = reads();
    await click('[data-control-trigger="trg_scan"] [data-run-now]');

    // One read as the route answers, then the three bounded tries - and no more.
    await vi.waitFor(() => expect(reads()).toBe(readsBefore + 1 + 3));
    await new Promise((resolve) => setTimeout(resolve, 60));
    await settle();

    expect(reads()).toBe(readsBefore + 1 + 3);
  });

  it('says a skipped Run now as skipped, with why', async () => {
    serve([
      (call) =>
        call.url === '/api/teams/job-tracker/triggers/trg_scan/run'
          ? reply(200, { outcome: 'capped', reason: 'its daily cap is reached', seq: null, trigger: {} })
          : undefined,
    ]);
    await controls();
    await click('[data-control-trigger="trg_scan"] [data-run-now]');

    expect(bodyFind('[data-control-notice]')?.textContent).toBe('Scan for postings was skipped: its daily cap is reached.');
  });

  it('turns a trigger off through its PATCH route', async () => {
    await controls();
    await click('[data-control-trigger="trg_scan"] [data-trigger-enabled]');

    const patch = sent(calls, 'PATCH', '/api/teams/job-tracker/triggers/trg_scan');
    expect(patch.map((call) => call.body)).toEqual([{ enabled: false }]);
  });

  it("changes a trigger's daily cap through its PATCH route, and clears it with an empty box", async () => {
    await controls();

    const input = capBox();
    input.value = '50,000';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
    await click('[data-control-trigger="trg_scan"] [data-trigger-cap-save]');

    const input2 = capBox();
    input2.value = '';
    input2.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
    await click('[data-control-trigger="trg_scan"] [data-trigger-cap-save]');

    const patch = sent(calls, 'PATCH', '/api/teams/job-tracker/triggers/trg_scan');
    expect(patch.map((call) => call.body)).toEqual([{ dailyTokenCap: 50000 }, { dailyTokenCap: null }]);
  });

  it("says a trigger's daily cap saved, like every other Save on the panel, until it is edited again", async () => {
    await controls();
    expect(bodyFind('[data-trigger-cap-saved]')).toBeNull();

    const input = capBox();
    input.value = '250,000';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
    await click('[data-control-trigger="trg_scan"] [data-trigger-cap-save]');

    expect(bodyFind('[data-control-trigger="trg_scan"] [data-trigger-cap-saved]')?.textContent?.trim()).toBe('Saved: 250,000 tokens a day.');

    const again = capBox();
    again.value = '300,000';
    again.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
    expect(bodyFind('[data-trigger-cap-saved]')).toBeNull();
  });

  it('says nothing saved when the cap is refused', async () => {
    serve([(call) => (call.method === 'PATCH' ? reply(400, { error: 'The cap was refused.' }) : undefined)]);
    await controls();

    const input = capBox();
    input.value = '250,000';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();
    await click('[data-control-trigger="trg_scan"] [data-trigger-cap-save]');

    expect(bodyFind('[data-trigger-cap-saved]')).toBeNull();
    expect(bodyFind('[data-control-problem]')?.textContent).toContain('The cap was refused.');
  });

  it('refuses a cap that is not a whole number before sending it', async () => {
    await controls();

    const input = capBox();
    input.value = 'lots';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await settle();

    expect(bodyText()).toContain('A whole number of tokens, or empty for no cap.');
    expect(bodyFind('[data-control-trigger="trg_scan"] [data-trigger-cap-save]')?.hasAttribute('disabled')).toBe(true);
  });

  it('shows the settings the package lists first, and only those, before All settings', async () => {
    await controls();

    const listed = bodyFind('[data-listed-settings="scout"]')!;
    expect(listed.querySelector('[data-setting="keywords"]')).not.toBeNull();
    expect(listed.querySelector('[data-setting="region"]')).toBeNull();

    const section = bodyFind('[data-section="controls"]')!;
    const html = section.innerHTML;
    expect(html.indexOf('data-listed-settings')).toBeLessThan(html.indexOf('data-all-settings'));
  });

  it('lists every setting under All settings, the person-only one included', async () => {
    await controls();
    await click('[data-all-settings] .q-item');

    const all = bodyFind('[data-all-settings-member="scout"]')!;
    expect(all.querySelector('[data-setting="keywords"]')).not.toBeNull();
    const region = all.querySelector('[data-setting="region"]')!;
    expect(region.querySelector('[data-person-only]')?.textContent).toContain('Set by a person only');
  });

  it("saves a listed setting with the member's whole body, so nothing else it holds is lost", async () => {
    await controls();

    const listed = bodyFind('[data-listed-settings="scout"]')!;
    const chip = listed.querySelector<HTMLElement>('[data-chip="dotnet"] .q-chip__icon--remove')!;
    chip.click();
    await settle();
    await click('[data-listed-settings="scout"] [data-settings-save]');

    const put = sent(calls, 'PUT', '/api/teams/job-tracker/members/scout/plugin-settings');
    expect(put.map((call) => call.body)).toEqual([
      { config: { region: 'EU' }, secrets: { apiKey: 'JOB_BOARD_KEY' }, connections: {} },
    ]);
    expect(bodyFind('[data-settings-saved]')).not.toBeNull();
  });

  it("says the Host's refusal of a setting", async () => {
    serve([
      (call) =>
        call.method === 'PUT' && call.url.endsWith('/plugin-settings')
          ? reply(400, { error: 'keywords: at most 10 entries.' })
          : undefined,
    ]);
    await controls();
    await click('[data-listed-settings="scout"] [data-settings-save]');

    expect(bodyFind('[data-settings-problem]')?.textContent).toBe('keywords: at most 10 entries.');
  });

  it('shows the connection bindings with a picker per slot', async () => {
    await controls();

    const slots = bodyFind('[data-connections-member="scout"]')!;
    expect(slots.textContent).toContain('Scout');
    expect(slots.querySelector('.q-select')).not.toBeNull();
    expect(slots.querySelector('[data-setting]')).toBeNull();
  });

  it('lays the triggers out as tiles on the shared grid, each control inside its own tile', async () => {
    await controls();

    const grid = bodyFind('[data-control-triggers]')!;
    expect(grid.classList).toContain('os-tiles');
    expect([...grid.children].map((el) => el.getAttribute('data-control-trigger'))).toEqual(['trg_scan', 'trg_apply']);
    for (const el of grid.children) expect(el.classList).toContain('os-tile');

    const scan = bodyFind('[data-control-trigger="trg_scan"]')!;
    for (const control of ['[data-trigger-enabled]', 'input[data-trigger-cap]', '[data-trigger-cap-save]', '[data-run-now]', '[data-trigger-details]']) {
      const el = scan.querySelector(control);
      expect(el, control).not.toBeNull();
      expect(el!.closest('[data-control-trigger]')).toBe(scan);
    }

    const source = readFileSync(join(import.meta.dirname, '../SolutionPanel.vue'), 'utf8');
    expect(/\.solution-trigger-tiles[^{]*\{[^}]*--os-tile-min:\s*min\(\d+rem, 100%\)/.test(source)).toBe(true);
  });

  it("shows a long instruction's first line on its tile, and the whole of it in Details", async () => {
    const tail = 'A SENTENCE ONLY DETAILS SHOWS.';
    const instruction = `Scan the job boards for new postings.\n${tail}`;
    read = panelRead({
      triggers: [panelTrigger({ id: 'trg_scan', packageName: 'Scan for postings', packageKind: 'schedule', instruction, runNow: true, filter: 'payload.kind == "apply"' })],
    });
    await controls();

    const line = bodyFind('[data-control-trigger="trg_scan"] [data-trigger-instruction]')!;
    expect(line.textContent).toBe('Scan the job boards for new postings.');
    expect(bodyFind('[data-control-trigger="trg_scan"]')!.textContent).not.toContain(tail);

    await click('[data-control-trigger="trg_scan"] [data-trigger-details]');
    const details = bodyFind('[data-trigger-details-dialog]')!;
    expect(details.classList).toContain('os-dialog-md');
    expect(details.querySelector('[data-trigger-details-instruction]')?.textContent).toBe(instruction);
    expect(details.querySelector('[data-trigger-fact="Filter"]')?.textContent).toBe('payload.kind == "apply"');
    expect(details.querySelector('[data-trigger-fact="Fires"]')).not.toBeNull();
  });

  it('clips a long one-line instruction on the tile with CSS, and keeps newlines in Details', async () => {
    const long = 'Read every posting on the board and '.repeat(12).trim();
    read = panelRead({
      triggers: [panelTrigger({ id: 'trg_scan', packageName: 'Scan for postings', packageKind: 'schedule', instruction: long })],
    });
    await controls();

    const line = bodyFind('[data-control-trigger="trg_scan"] [data-trigger-instruction]')!;
    // The string is never cut: the clip is the stylesheet's.
    expect(line.textContent).toBe(long);
    expect(line.classList).toContain('solution-trigger-instruction');

    const source = readFileSync(join(import.meta.dirname, '../SolutionPanel.vue'), 'utf8');
    const rule = (selector: string) => new RegExp(`\\${selector}\\s*\\{[^}]*\\}`).exec(source)?.[0] ?? '';
    expect(rule('.solution-trigger-instruction')).toMatch(/-webkit-line-clamp:\s*2/);
    expect(rule('.solution-trigger-instruction')).toMatch(/(^|[^-])line-clamp:\s*2/m);
    expect(rule('.solution-trigger-instruction')).toMatch(/overflow:\s*hidden/);
    expect(rule('.solution-trigger-instruction-whole')).toMatch(/white-space:\s*pre-wrap/);
  });

  it("renders an instruction that looks like HTML as its characters in Details", async () => {
    read = panelRead({
      triggers: [panelTrigger({ id: 'trg_scan', packageName: 'Scan for postings', packageKind: 'schedule', instruction: HtmlLooking })],
    });
    await controls();
    await click('[data-control-trigger="trg_scan"] [data-trigger-details]');

    const details = bodyFind('[data-trigger-details-dialog]')!;
    expect(details.querySelector('[data-trigger-details-instruction]')?.textContent).toBe(HtmlLooking);
    expect(details.querySelector('img')).toBeNull();
    expect(details.querySelector('b')).toBeNull();
  });

  it('says a refused control in its own words', async () => {
    serve([(call) => (call.url === '/api/teams/job-tracker/pause' ? reply(403, { error: 'Only a person may pause a team.' }) : undefined)]);
    await controls();
    await click('[data-control-pause]');

    expect(bodyFind('[data-control-problem]')?.textContent).toBe('Only a person may pause a team.');
  });
});
