// @vitest-environment happy-dom
//
// THE CONTROL PANEL'S CONTROLS SECTION (B001J), each through an EXISTING route: pause and resume the
// team, Run now per schedule (and only a schedule), each trigger's on/off and daily cap, the settings
// the package lists first, "All settings" with the person-only ones, and the connection bindings.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SolutionPanel from '../SolutionPanel.vue';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { fakeHost, reply, sent, type Call, type Route } from '../../test/solutionFixtures';
import { openSection, panelRead, panelRoutes } from '../../test/solutionPanelFixtures';
import type { SolutionPanel as PanelShape } from '../../api/types';

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

  it('says a refused control in its own words', async () => {
    serve([(call) => (call.url === '/api/teams/job-tracker/pause' ? reply(403, { error: 'Only a person may pause a team.' }) : undefined)]);
    await controls();
    await click('[data-control-pause]');

    expect(bodyFind('[data-control-problem]')?.textContent).toBe('Only a person may pause a team.');
  });
});
