// @vitest-environment happy-dom
//
// AGENT EDIT, MOUNTED CLOSED AND THEN OPENED: every field carries a rule from `lib/rules`, and Save
// follows those rules rather than a check of its own. The rules themselves are pinned in
// `rules.spec.ts`; what these cases pin is that the dialog WIRES them - a sentence under the field,
// a dead Save, and no `save` emitted.
import { afterEach, describe, expect, it } from 'vitest';
import { flushPromises, type VueWrapper } from '@vue/test-utils';

import AgentEditDialog from '../AgentEditDialog.vue';
import type { Agent } from '../../api/types';
import { USAGE_FORMATS } from '../../lib/rules';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';

afterEach(resetBody);

function open(agent: Agent | null = null, error = '') {
  return mountDialog(AgentEditDialog, { agent, busy: false, error });
}

function field(wrapper: VueWrapper, label: string) {
  const found = wrapper.findAllComponents({ name: 'QInput' })
    .find((input) => String(input.props('label')).startsWith(label));

  if (!found) throw new Error(`no ${label} input in the rendered dialog`);

  return found;
}

function saveButton(): HTMLButtonElement {
  const found = [...document.body.querySelectorAll('button')]
    .find((candidate) => candidate.textContent?.trim() === 'Save');

  if (!found) throw new Error('no Save button in the rendered dialog');

  return found as HTMLButtonElement;
}

/** Quasar validates on a zero-delay debounce after the model changes. */
async function settle() {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 10));
  await flushPromises();
}

async function fillValid(wrapper: VueWrapper) {
  await field(wrapper, 'Name').setValue('codex-headless');
  await field(wrapper, 'Executable').setValue('codex');
  await settle();
}

describe('AgentEditDialog validation', () => {
  it('disables Save while the form is empty and enables it with valid input', async () => {
    const wrapper = await open();

    expect(saveButton().hasAttribute('disabled')).toBe(true);

    await fillValid(wrapper);

    expect(saveButton().hasAttribute('disabled')).toBe(false);
  });

  it('refuses an illegal name with the identifier sentence', async () => {
    const wrapper = await open();
    await fillValid(wrapper);

    await field(wrapper, 'Name').setValue('my agent');
    await settle();

    expect(bodyText()).toContain('Names use letters, digits, - and _, up to 32 characters.');
    expect(saveButton().hasAttribute('disabled')).toBe(true);
  });

  it('refuses a HARNESS_ environment name with a sentence rather than dropping it', async () => {
    const wrapper = await open();
    await fillValid(wrapper);

    await field(wrapper, 'Environment').setValue('HARNESS_TEAM=x');
    await settle();

    expect(bodyText()).toContain("Names beginning HARNESS_ are the platform's own");
    expect(saveButton().hasAttribute('disabled')).toBe(true);
    expect(wrapper.emitted('save')).toBeUndefined();
  });

  it('names every bad environment line, not only the first', async () => {
    const wrapper = await open();
    await fillValid(wrapper);

    await field(wrapper, 'Environment').setValue('GOOD=1\nnot a pair\nHARNESS_X=2');
    await settle();

    expect(bodyText()).toContain('Line 2 needs the form NAME=value.');
    expect(bodyText()).toContain('Line 3:');
  });

  it('offers exactly the server usage formats, as a select', async () => {
    const wrapper = await open();

    const select = wrapper.findAllComponents({ name: 'QSelect' })
      .find((candidate) => String(candidate.props('label')).startsWith('Usage format'));

    expect(select, 'no usage format select').toBeTruthy();

    const values = (select!.props('options') as Array<{ value: string }>).map((option) => option.value);

    expect(values).toEqual(['', ...USAGE_FORMATS]);
    expect(bodyText()).not.toContain('codex-json');
  });

  it('marks a stored usage format the server cannot parse', async () => {
    await open({
      name: 'old',
      mode: 'Headless',
      launch: { fileName: 'codex', arguments: [], usageFormat: 'codex-json' },
    } as Agent);
    await settle();

    expect(saveButton().hasAttribute('disabled')).toBe(true);
  });

  it('refuses an install link that is not an http URL, and a timeout below 1', async () => {
    const wrapper = await open();
    await fillValid(wrapper);

    await field(wrapper, 'Install link').setValue('docs.example.com');
    await settle();

    expect(bodyText()).toContain('Use an absolute http or https URL');
    expect(saveButton().hasAttribute('disabled')).toBe(true);

    await field(wrapper, 'Install link').setValue('https://docs.example.com');
    await field(wrapper, 'Run timeout').setValue('0');
    await settle();

    expect(bodyText()).toContain('Use a whole number, 1 or more.');
    expect(saveButton().hasAttribute('disabled')).toBe(true);

    await field(wrapper, 'Run timeout').setValue('600');
    await settle();

    expect(saveButton().hasAttribute('disabled')).toBe(false);
  });

  it('emits the rebuilt Agent on submit', async () => {
    const wrapper = await open();
    await fillValid(wrapper);

    saveButton().click();
    await settle();

    const saved = wrapper.emitted('save')?.[0]?.[0] as Agent | undefined;

    expect(saved?.name).toBe('codex-headless');
    expect(saved?.launch?.fileName).toBe('codex');
  });

  it('marks the name field when the server refuses the name', async () => {
    const wrapper = await open(null, 'Two Agents share a name. Names are compared without regard to case.');

    expect(field(wrapper, 'Name').props('error')).toBe(true);
    expect(bodyText()).toContain('Two Agents share a name.');
  });
});

describe('AgentEditDialog mode', () => {
  /** A person reads "Concierge"; the wire value stays `Interactive`. */
  it('labels the Interactive mode Concierge and keeps the wire value', async () => {
    const wrapper = await open({
      name: 'claude-tui',
      mode: 'Interactive',
      launch: { fileName: 'claude', arguments: [] },
    } as Agent);

    const select = wrapper.findAllComponents({ name: 'QSelect' })
      .find((candidate) => candidate.props('label') === 'Mode')!;

    expect(select.props('options')).toEqual([
      { label: 'Headless', value: 'Headless' },
      { label: 'Concierge', value: 'Interactive' },
    ]);
    expect(select.props('modelValue')).toBe('Interactive');
    expect(bodyText()).not.toContain('Interactive');
  });
});
