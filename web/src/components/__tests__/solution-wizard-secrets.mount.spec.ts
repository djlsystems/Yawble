// @vitest-environment happy-dom
//
// THE SOLUTION WIZARD: EACH PLUGIN MEMBER'S SECRET FIELDS. "Your part" lists each by the field's
// label (else its name in words) and its member - never a key name - with whether it is set, and,
// when unset, that its source fails until it is set. Each needed field offers one box for that
// member's OWN value: a password box, or a text box for a field declared `masked: false`. What is
// typed rides Install as `secretValues`; no key is written. A secret for a source the person leaves
// off is not needed, and follows the person's choice. The result lists the fields still unset
// (`unsetFields`) under each member, with a link to that member's settings by its container.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { VueWrapper } from '@vue/test-utils';
import { QSelect } from 'quasar';

import SolutionWizard from '../SolutionWizard.vue';
import { useConsoleStore } from '../../stores/console';
import { asMemberId, asTeamId, type ContainerSnapshot, type Team } from '../../api/types';
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
                  // The container differs from the package member name: only `container` is the address.
                  unsetFields: [
                    { member: 'Scout', container: 'scout-2', field: 'usajobsApiKey', label: 'USAJOBS API key' },
                    { member: 'Scout', container: 'scout-2', field: 'contactEmail', label: null },
                  ],
                })
              : undefined,
          (call) => (call.url === '/api/teams/job-tracker/solution' ? reply(200, { team: 'job-tracker', missing: [] }) : undefined),
          (call) =>
            call.url === '/api/teams/job-tracker/members/scout-2/plugin-settings'
              ? reply(200, { team: 'job-tracker', member: 'scout-2', plugin: 'job-scout', version: '1.0.0', config: {}, secrets: {}, fields: {}, secretFields: {} })
              : undefined,
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

  it('the result lists each needed field still unset by its label under its member, never a key name', async () => {
    await yourPart();
    await installNow();

    const list = bodyFind('[data-unset-secrets]')!;
    expect(list).not.toBeNull();
    expect(list.textContent).toContain("set it in the member's settings");
    const group = bodyFind('[data-unset-member="scout-2"]')!;
    expect(group.textContent).toContain('Scout');
    expect(group.querySelector('[data-unset-secret="Scout/usajobsApiKey"]')?.textContent).toBe('USAJOBS API key');
    // A field with no label is named in words.
    expect(group.querySelector('[data-unset-secret="Scout/contactEmail"]')?.textContent).toBe(', Contact email');
    expect(list.textContent).not.toContain('USAJOBS_API_KEY');
    expect(bodyFind('[data-unset-secret="Scout/adzunaAppId"]')).toBeNull();
  });

  it("each unset member's Settings opens that member's settings by the container it was hired as", async () => {
    await yourPart();
    await installNow();

    const scout = {
      team: asTeamId('job-tracker'),
      id: asMemberId('scout-2'),
      name: 'Scout',
      agent: 'plugin:job-scout',
      kind: 'plugin',
      state: 'Idle',
      queueDepth: 0,
      ceiling: 16,
      subscribes: [],
      currentCorrelation: null,
      sinceSeq: 0,
    } as unknown as ContainerSnapshot;
    const board = useConsoleStore();
    board.teams = [{ id: asTeamId('job-tracker'), name: 'Job Tracker', memberAgents: [], containers: [scout] } as unknown as Team];
    board.managerName = asMemberId('Manager');

    (bodyFind('[data-unset-settings="scout-2"]') as HTMLElement).click();
    await settle();
    await settle();

    expect(bodyFind('[data-unset-settings-problem]')).toBeNull();
    expect(calls.some((call) => call.url === '/api/teams/job-tracker/members/scout-2/plugin-settings')).toBe(true);
    expect(calls.some((call) => call.url.includes('/members/Scout/'))).toBe(false);
    expect(bodyFind('[data-member-plugin-settings]')).not.toBeNull();
  });
});
