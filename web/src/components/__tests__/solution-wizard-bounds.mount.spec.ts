// @vitest-environment happy-dom
//
// THE SOLUTION WIZARD AND A BOUNDED NUMBER SETTING (B001W). A person-only number setting whose
// plugin declares `min`, `max` or `integer` says its bounds in its hint; a value outside them (or not
// whole) is refused inline and Next stays off until it is fixed - unlike a skipped required setting,
// which installs and leaves the team blocked, an out-of-range value is one the Host refuses. When the
// Host refuses anyway, its sentence is shown.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

import SolutionWizard from '../SolutionWizard.vue';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, hasError, isDisabled, settle, type } from '../../test/formProbe';
import { Folder, fakeHost, hostPlan, reply, sent, steps, wizardRoutes, type Call, type Route } from '../../test/solutionFixtures';

let calls: Call[] = [];

/** Job Tracker's plan with `limit` bounded as its manifest now declares it. */
function boundedPlan() {
  const plan = hostPlan();
  plan.personSettings = plan.personSettings.map((setting) =>
    setting.setting === 'limit' ? { ...setting, min: 1, max: 50, integer: true } : setting,
  );
  return plan;
}

function serve(extra: Route[] = []) {
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          ...extra,
          (call) =>
            call.url === '/api/solutions/install'
              ? reply(200, { ok: true, team: 'job-tracker', teamName: 'Job Tracker', version: '1.1.0', missing: [], steps: steps(8) })
              : undefined,
          (call) => (call.url === '/api/teams/job-tracker/solution' ? reply(200, { team: 'job-tracker', missing: [] }) : undefined),
          ...wizardRoutes({ plan: boundedPlan() }),
        ],
        calls,
      ),
    ),
  );
}

beforeEach(() => {
  setActivePinia(createPinia());
  calls = [];
  serve();
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function yourPart() {
  const wrapper = await mountDialog(SolutionWizard, { folder: Folder }, { pinia: false });
  await settle();
  for (let index = 0; index < 2; index++) {
    button('Next').click();
    await settle();
  }
  expect(bodyFind('[data-step="inputs"]')).not.toBeNull();
  return wrapper;
}

const limit = () => bodyFind('[data-person-setting="Scout/limit"]')!;

describe('Solution wizard - a bounded number setting', () => {
  it('says its bounds in the hint', async () => {
    await yourPart();

    expect(limit().textContent).toContain('Between 1 and 50, whole numbers only.');
    expect(bodyFind('[data-person-setting="Scout/region"]')!.textContent).not.toContain('Between');
  });

  it('refuses an out-of-range or non-whole value inline and holds Next; in range, it installs with it', async () => {
    await yourPart();

    await type('Scout: limit', '0');
    expect(hasError('Scout: limit')).toBe(true);
    expect(limit().textContent).toContain('0 is out of range for limit: it must be at least 1.');
    expect(isDisabled('Next')).toBe(true);

    button('Next').click();
    await settle();
    expect(bodyFind('[data-step="inputs"]')).not.toBeNull();

    await type('Scout: limit', '80');
    expect(limit().textContent).toContain('80 is out of range for limit: it must be at most 50.');

    await type('Scout: limit', '2.5');
    expect(limit().textContent).toContain('limit takes whole numbers only; 2.5 is not one.');

    await type('Scout: limit', '30');
    expect(hasError('Scout: limit')).toBe(false);
    expect(isDisabled('Next')).toBe(false);

    button('Next').click();
    await settle();
    button('Install').click();
    await settle();

    expect(sent(calls, 'POST', '/api/solutions/install')[0]!.body).toMatchObject({ settings: { Scout: { limit: 30 } } });
  });

  it("shows the Host's refusal sentence when the install is refused", async () => {
    const sentence = "The Scout setting limit must be at most 50; 60 is out of range. Nothing was installed.";
    calls = [];
    serve([(call) => (call.url === '/api/solutions/install' ? reply(400, { error: sentence }) : undefined)]);
    await yourPart();

    button('Next').click();
    await settle();
    button('Install').click();
    await settle();

    expect(bodyFind('[data-install-error]')!.textContent).toContain(sentence);
  });
});
