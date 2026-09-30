// @vitest-environment happy-dom
//
// THE SOLUTION WIZARD: THE SECRETS A PACKAGE BINDS. "Your part" lists each by its key name with
// whether the Host has it set - never a value - what it is for, and, when unset, that its source
// fails until it is set and the exact way to set it. A secret for a source the person leaves off is
// not needed, and follows the person's choice. The result lists the keys still unset.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { VueWrapper } from '@vue/test-utils';
import { QSelect } from 'quasar';

import SolutionWizard from '../SolutionWizard.vue';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle } from '../../test/formProbe';
import { Folder, fakeHost, hostSecrets, reply, steps, wizardRoutes, type Call } from '../../test/solutionFixtures';

let calls: Call[] = [];

beforeEach(() => {
  setActivePinia(createPinia());
  calls = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          (call) =>
            call.url === '/api/solutions/install'
              ? reply(200, {
                  ok: true,
                  team: 'job-tracker',
                  teamName: 'Job Tracker',
                  version: '1.1.0',
                  missing: [],
                  steps: steps(8),
                  secrets: hostSecrets().map((secret) => ({ ...secret, needed: secret.key !== 'THEMUSE_API_KEY' })),
                  unset: ['USAJOBS_API_KEY'],
                })
              : undefined,
          (call) => (call.url === '/api/teams/job-tracker/solution' ? reply(200, { team: 'job-tracker', missing: [] }) : undefined),
          ...wizardRoutes({ secrets: hostSecrets() }),
        ],
        calls,
      ),
    ),
  );
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

const secret = (key: string) => bodyFind(`[data-secret="${key}"]`)!;

const selectLabelled = (wrapper: VueWrapper, label: string) =>
  wrapper.findAllComponents(QSelect).find((select) => select.props('label') === label)!;

describe('Solution wizard - secrets', () => {
  it('lists each secret by key name: set, unset with how to set it, and not needed', async () => {
    await yourPart();

    expect(bodyFind('[data-secrets]')).not.toBeNull();

    const set = secret('ADZUNA_APP_ID');
    expect(set.dataset.secretState).toBe('set');
    expect(set.textContent).toContain('Your Adzuna application id.');
    expect(set.textContent).toContain('Set on this Host.');

    const unset = secret('USAJOBS_API_KEY');
    expect(unset.dataset.secretState).toBe('unset');
    expect(unset.textContent).toContain('not set');
    expect(unset.textContent).toContain('its source fails until it is set');
    expect(unset.textContent).toContain('yawble secret set USAJOBS_API_KEY (it prompts for the value), then yawble up to restart the Host');

    // THE MUSE'S SOURCE IS NOT TICKED (sources starts empty): not needed, and says why.
    const notNeeded = secret('THEMUSE_API_KEY');
    expect(notNeeded.dataset.secretState).toBe('not-needed');
    expect(notNeeded.textContent).toContain("Not needed: Scout's sources leaves other off.");
    expect(notNeeded.textContent).not.toContain('yawble secret set');
  });

  it('follows the person ticking the source a secret belongs to', async () => {
    const wrapper = await yourPart();

    selectLabelled(wrapper, 'Scout: sources').vm.$emit('update:modelValue', ['other']);
    await settle();

    expect(secret('THEMUSE_API_KEY').dataset.secretState).toBe('unset');
    expect(secret('THEMUSE_API_KEY').textContent).toContain('yawble secret set THEMUSE_API_KEY');
  });

  it('the result lists the keys still unset, each with the way to set it', async () => {
    await yourPart();
    button('Next').click();
    await settle();
    button('Install').click();
    await settle();
    await settle();

    const list = bodyFind('[data-unset-secrets]')!;
    expect(list).not.toBeNull();
    expect(list.textContent).toContain('still not set on the Host');
    const row = bodyFind('[data-unset-secret="USAJOBS_API_KEY"]')!;
    expect(row.textContent).toContain('yawble secret set USAJOBS_API_KEY (it prompts for the value), then yawble up to restart the Host');
    expect(bodyFind('[data-unset-secret="ADZUNA_APP_ID"]')).toBeNull();
    expect(bodyFind('[data-unset-secret="THEMUSE_API_KEY"]')).toBeNull();
  });
});
