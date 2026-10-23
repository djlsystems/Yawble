// @vitest-environment happy-dom
//
// THE SOLUTION WIZARD: EACH PLUGIN MEMBER'S SECRET FIELDS. "Your part" lists each by the field's
// label (else its name in words) and its member - never a key name - with whether it is set, and,
// when unset, that its source fails until it is set. Each needed field offers one box for that
// member's OWN value: a password box, or a text box for a field declared `masked: false`. What is
// typed rides Install as `secretValues`; no key is written. A secret for a source the person leaves
// off is not needed, and follows the person's choice. The result lists the fields still unset
// (`unsetFields`), to be set in the member's settings.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { VueWrapper } from '@vue/test-utils';
import { QSelect } from 'quasar';

import SolutionWizard from '../SolutionWizard.vue';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, field, settle, type } from '../../test/formProbe';
import { Folder, fakeHost, hostSecrets, reply, steps, wizardRoutes, type Call } from '../../test/solutionFixtures';

let calls: Call[] = [];

const Value = 'usajobs-0123456789abcdef';

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
                  unsetFields: [{ member: 'Scout', field: 'usajobsApiKey', label: 'USAJOBS API key' }],
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

async function installNow() {
  button('Next').click();
  await settle();
  button('Install').click();
  await settle();
  await settle();
}

const secret = (field: string) => bodyFind(`[data-secret="Scout/${field}"]`)!;

const selectLabelled = (wrapper: VueWrapper, label: string) =>
  wrapper.findAllComponents(QSelect).find((select) => select.props('label') === label)!;

describe('Solution wizard - secrets', () => {
  it("lists each secret field by its label and member, never a key name: set, unset, and not needed", async () => {
    await yourPart();

    const section = bodyFind('[data-secrets]')!;
    expect(section).not.toBeNull();
    for (const key of ['ADZUNA_APP_ID', 'USAJOBS_API_KEY', 'THEMUSE_API_KEY']) expect(section.textContent).not.toContain(key);

    const set = secret('adzunaAppId');
    expect(set.dataset.secretState).toBe('set');
    expect(set.textContent).toContain('Adzuna app ID');
    expect(set.textContent).toContain('Scout');
    expect(set.querySelector('[data-secret-sentence]')?.textContent).toContain('Set.');

    const unset = secret('usajobsApiKey');
    expect(unset.dataset.secretState).toBe('unset');
    expect(unset.textContent).toContain('USAJOBS API key');
    expect(unset.textContent).toContain('not set');
    expect(unset.textContent).toContain('its source fails until it is set');
    expect(unset.textContent).not.toContain('secret set');

    // THE MUSE'S SOURCE IS NOT TICKED (sources starts empty): not needed, and says why.
    const notNeeded = secret('themuseApiKey');
    expect(notNeeded.dataset.secretState).toBe('not-needed');
    expect(notNeeded.textContent).toContain("Not needed: Scout's sources leaves other off.");
    expect(notNeeded.querySelector('[data-secret-value-input]')).toBeNull();
  });

  it('follows the person ticking the source a secret belongs to', async () => {
    const wrapper = await yourPart();

    selectLabelled(wrapper, 'Scout: sources').vm.$emit('update:modelValue', ['other']);
    await settle();

    expect(secret('themuseApiKey').dataset.secretState).toBe('unset');
    expect(secret('themuseApiKey').querySelector('[data-secret-value-input]')).not.toBeNull();
  });

  it("offers a password box for a masked field and a text box for an unmasked one, and sends what is typed with Install as that member's own", async () => {
    await yourPart();

    expect(field('USAJOBS API key').getAttribute('type')).toBe('password');
    expect(field('USAJOBS API key').value).toBe('');
    expect(field('Adzuna app ID').getAttribute('type')).toBe('text');

    await type('USAJOBS API key', Value);
    expect(secret('usajobsApiKey').querySelector('[data-secret-sentence]')?.textContent).toBe('Saved for this member when you press Install.');

    await installNow();

    const install = calls.find((call) => call.url === '/api/solutions/install')!;
    expect((install.body as { secretValues?: unknown }).secretValues).toEqual({ Scout: { usajobsApiKey: Value } });
    // No key is written from the wizard: the value is the member's own.
    expect(calls.filter((call) => call.url.startsWith('/api/secrets/'))).toHaveLength(0);
    expect(document.body.textContent).not.toContain(Value);
  });

  it('sends no secretValues when nothing is typed', async () => {
    await yourPart();
    await installNow();

    const install = calls.find((call) => call.url === '/api/solutions/install')!;
    expect(install.body).not.toHaveProperty('secretValues');
  });

  it('the result lists each needed field still unset by its label and member, never a key name', async () => {
    await yourPart();
    await installNow();

    const list = bodyFind('[data-unset-secrets]')!;
    expect(list).not.toBeNull();
    expect(list.textContent).toContain("set it in the member's settings");
    const row = bodyFind('[data-unset-secret="Scout/usajobsApiKey"]')!;
    expect(row.textContent).toContain('USAJOBS API key · Scout');
    expect(list.textContent).not.toContain('USAJOBS_API_KEY');
    expect(bodyFind('[data-unset-secret="Scout/adzunaAppId"]')).toBeNull();
  });
});
