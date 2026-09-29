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
import { Folder, fakeHost, reply, sent, steps, wizardRoutes, type Call } from '../../test/solutionFixtures';

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
    expect(sources.props('options')).toEqual(['sample', 'other']);
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
