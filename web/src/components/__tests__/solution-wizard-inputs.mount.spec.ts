// @vitest-environment happy-dom
//
// THE SOLUTION WIZARD, STEP 3: YOUR PART. What only a person provides: each person-only setting as
// an input of its type starting at its default (a choice list, text, a number); a connection picker
// per connection input, offering the preview's connections; an upload box per document input naming
// its folder, description and "required". Skipping a required one is allowed, with a sentence that
// the team will show as blocked, naming it. What was chosen is what the install sends.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { VueWrapper } from '@vue/test-utils';
import { QFile, QSelect } from 'quasar';

import SolutionWizard from '../SolutionWizard.vue';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, field, settle, type } from '../../test/formProbe';
import {
  Folder,
  fakeHost,
  hostPlan,
  installedRow,
  reply,
  sent,
  steps,
  updatePreview,
  wizardRoutes,
  type Call,
} from '../../test/solutionFixtures';
import type { SolutionKept } from '../../api/types';

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
              ? reply(200, { ok: true, team: 'job-tracker', teamName: 'Job Tracker', version: '1.1.0', missing: [], steps: steps(8) })
              : undefined,
          (call) => (call.url === '/api/teams/job-tracker/documents/upload' ? reply(200, { name: 'cv.pdf', path: 'Resume/cv.pdf' }) : undefined),
          (call) => (call.url === '/api/teams/job-tracker/solution' ? reply(200, { team: 'job-tracker', missing: [] }) : undefined),
          ...wizardRoutes(),
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

const selectLabelled = (wrapper: VueWrapper, label: string) =>
  wrapper.findAllComponents(QSelect).find((select) => select.props('label') === label)!;

describe('Solution wizard - Your part', () => {
  it('renders each person-only setting by its type, at its default, with its description', async () => {
    const wrapper = await yourPart();

    const sources = selectLabelled(wrapper, 'Scout: sources');
    expect((sources.props('options') as { value: string }[]).map((option) => option.value)).toEqual(['sample', 'other']);
    expect(sources.props('multiple')).toBe(true);
    expect(sources.props('modelValue')).toEqual([]);

    expect((field('Scout: region') as HTMLInputElement).value).toBe('');
    expect((field('Scout: limit') as HTMLInputElement).value).toBe('20');
    expect((field('Scout: limit') as HTMLInputElement).type).toBe('number');

    expect(bodyFind('[data-person-setting="Scout/region"]')!.textContent).toContain('Where to look.');
    expect(bodyFind('[data-person-setting="Scout/region"]')!.textContent).toContain('Required.');
    expect(bodyFind('[data-person-setting="Scout/limit"]')!.textContent).toContain('Default: 20.');
  });

  it("offers the preview's connections for each connection input", async () => {
    const wrapper = await yourPart();

    const picker = selectLabelled(wrapper, 'Connection for Scout: mail');
    expect(picker.props('options')).toEqual([{ value: 'conn-1', label: 'Work mail - dana@example.com (google)' }]);
    expect(bodyFind('[data-connection-input="Scout/mail"]')!.textContent).toContain('Where postings are emailed from.');
  });

  it('shows an upload box per document input with its folder, description and "required"', async () => {
    const wrapper = await yourPart();

    const box = bodyFind('[data-document-input="Resume"]')!;
    expect(box.textContent).toContain('Upload to Resume/');
    expect(box.textContent).toContain('Your reference resume, .docx or PDF.');
    expect(box.querySelector('[data-required]')!.textContent).toContain('required');
    expect(wrapper.findAllComponents(QFile)).toHaveLength(1);
  });

  it('allows skipping a required input, saying the team will show as blocked and naming each', async () => {
    const wrapper = await yourPart();

    const skipped = bodyFind('[data-skipped-required]')!;
    expect(skipped.textContent).toContain('the Scout setting region');
    expect(skipped.textContent).toContain('the Scout connection mail');
    expect(skipped.textContent).toContain('a document in Resume/');
    expect(skipped.textContent).toContain('the team will show as blocked');

    await type('Scout: region', 'Europe');
    selectLabelled(wrapper, 'Connection for Scout: mail').vm.$emit('update:modelValue', 'conn-1');
    wrapper.findAllComponents(QFile)[0]!.vm.$emit('update:modelValue', new File(['cv'], 'cv.pdf'));
    await settle();

    expect(bodyFind('[data-skipped-required]')).toBeNull();

    // Skipping is not refused: Next still leads to Install.
    button('Next').click();
    await settle();
    expect(bodyFind('[data-step="install"]')).not.toBeNull();
  });

  it('sends what was chosen: changed settings, the bound connection; then uploads the document into its folder', async () => {
    const wrapper = await yourPart();

    await type('Scout: region', 'Europe');
    selectLabelled(wrapper, 'Scout: sources').vm.$emit('update:modelValue', ['sample']);
    selectLabelled(wrapper, 'Connection for Scout: mail').vm.$emit('update:modelValue', 'conn-1');
    const resume = new File(['cv'], 'cv.pdf');
    wrapper.findAllComponents(QFile)[0]!.vm.$emit('update:modelValue', resume);
    await settle();

    button('Next').click();
    await settle();
    button('Install').click();
    await settle();

    expect(sent(calls, 'POST', '/api/solutions/install')[0]!.body).toEqual({
      folder: Folder,
      teamName: 'Job Tracker',
      localRepository: true,
      // `limit` stayed at its default, so it is not sent.
      settings: { Scout: { region: 'Europe', sources: ['sample'] } },
      connections: { Scout: { mail: 'conn-1' } },
    });

    const upload = sent(calls, 'POST', '/api/teams/job-tracker/documents/upload')[0]!;
    const form = upload.body as FormData;
    expect(form.get('path')).toBe('Resume');
    expect((form.get('file') as File).name).toBe('cv.pdf');
    expect(bodyText()).toContain('Uploaded cv.pdf to Resume/.');
  });
});

describe('Solution wizard - Your part, on an update', () => {
  const kept: SolutionKept = {
    settings: [
      { member: 'Scout', setting: 'sources', value: ['sample'] },
      { member: 'Scout', setting: 'region', value: 'Europe' },
      { member: 'Scout', setting: 'limit', value: null },
    ],
    connections: [{ member: 'Scout', slot: 'mail', connection: 'conn-1' }],
    documents: [{ folder: 'Resume', files: ['resume.pdf'] }],
  };

  async function updateYourPart(answer: SolutionKept) {
    calls = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(
        fakeHost(
          [
            (call) =>
              call.url === '/api/solutions/preview' && (call.body as { team?: string }).team === 'job-tracker'
                ? reply(200, updatePreview('job-tracker', hostPlan(), answer))
                : undefined,
            (call) =>
              call.url === '/api/solutions/update'
                ? reply(200, { ok: true, team: 'job-tracker', teamName: 'Job Tracker', version: '1.1.0', missing: [], steps: steps(8) })
                : undefined,
            (call) => (call.url === '/api/teams/job-tracker/solution' ? reply(200, { team: 'job-tracker', missing: [] }) : undefined),
            ...wizardRoutes({ installed: [installedRow('job-tracker', 'Job Tracker', '1.0.0')] }),
          ],
          calls,
        ),
      ),
    );

    const wrapper = await mountDialog(SolutionWizard, { folder: Folder }, { pinia: false });
    await settle();
    (bodyFind('[data-update-team="job-tracker"] .q-radio') as HTMLElement).click();
    await settle();
    for (let index = 0; index < 2; index++) {
      button('Next').click();
      await settle();
    }
    expect(bodyFind('[data-step="inputs"]')).not.toBeNull();
    return wrapper;
  }

  it("shows what the update keeps - settings, the binding, the documents present - instead of asking again", async () => {
    const wrapper = await updateYourPart(kept);

    expect(bodyFind('[data-person-setting="Scout/sources"] [data-kept-setting]')!.textContent).toContain('kept: sample');
    expect(bodyFind('[data-person-setting="Scout/region"] [data-kept-setting]')!.textContent).toContain('kept: Europe');
    expect(bodyFind('[data-person-setting="Scout/limit"] [data-kept-setting]')!.textContent).toContain('kept: not set');
    expect(wrapper.findAllComponents(QSelect).find((select) => select.props('label') === 'Scout: sources')).toBeUndefined();
    expect(bodyFind('[data-connection-input="Scout/mail"] [data-kept-connection]')!.textContent).toContain(
      'kept: Work mail - dana@example.com (google)',
    );
    expect(bodyFind('[data-document-input="Resume"] [data-kept-files]')!.textContent).toContain('Already in Resume/: resume.pdf.');

    // Nothing is missing, so nothing warns that the team will be blocked.
    expect(bodyFind('[data-skipped-required]')).toBeNull();

    button('Next').click();
    await settle();
    button('Update').click();
    await settle();

    // A kept member's settings and bindings are the Host's to keep, so none is sent.
    expect(sent(calls, 'POST', '/api/solutions/update')[0]!.body).toEqual({ folder: Folder, team: 'job-tracker', settings: {}, connections: {} });
  });

  it('still warns about a required document folder that is empty, or a kept slot left unbound', async () => {
    await updateYourPart({ ...kept, connections: [{ member: 'Scout', slot: 'mail', connection: null }], documents: [{ folder: 'Resume', files: [] }] });

    expect(bodyFind('[data-connection-input="Scout/mail"] [data-kept-connection]')!.textContent).toContain('kept: not connected');
    expect(bodyFind('[data-kept-files]')).toBeNull();
    const skipped = bodyFind('[data-skipped-required]')!;
    expect(skipped.textContent).toContain('the Scout connection mail');
    expect(skipped.textContent).toContain('a document in Resume/');
    expect(skipped.textContent).not.toContain('the Scout setting region');
  });
});
