// @vitest-environment happy-dom
//
// `lib/triggers` is already tested, thoroughly, and this spec adds nothing to that. It asks the one
// question those tests cannot: IS THE COMPONENT WIRED TO IT?
//
// `triggerDraftProblem` decides whether Save is enabled, and `triggerFieldProblems` - the same rule,
// by field - decides what is said under each input - and the component's own comment records what happens when that wiring is subtly wrong:
// a completeness check in the component compared `string | null` against `''`, `null === ''` is
// false, and the result was "a dead button and no reason given". Every test stayed green, because
// a source scan can see that `:disable` appears near a button and cannot see what it means.
import { afterEach, describe, expect, it } from 'vitest';
import TriggerEditDialog from '../TriggerEditDialog.vue';
import { draftForCreate, triggerDraftProblem, triggerFieldProblems } from '../../lib/triggers';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import * as probe from '../../test/formProbe';

afterEach(resetBody);

const baseProps = { trigger: null, container: 'Manager', events: [], saving: false };

function button(label: string): HTMLButtonElement {
  const found = [...document.body.querySelectorAll('button')]
    .find((candidate) => candidate.textContent?.trim().includes(label));

  if (!found) throw new Error(`no button labelled "${label}" in the rendered dialog`);

  return found as HTMLButtonElement;
}

function field(label: string): HTMLInputElement {
  const found = [...document.body.querySelectorAll('input')]
    .find((candidate) => candidate.closest('.q-field')?.textContent?.includes(label));

  if (!found) throw new Error(`no field labelled "${label}" in the rendered dialog`);

  return found;
}

/**
 * The instruction box carries a `hint` and no `label`, so there is no text to find it by. It is the
 * only textarea in the dialog, which is a weaker handle than a label and is said out loud here
 * rather than hidden inside a selector: if a second one is ever added, this throws instead of
 * silently typing into the wrong box.
 */
function theOnlyTextarea(): HTMLTextAreaElement {
  const found = [...document.body.querySelectorAll('textarea')];

  if (found.length !== 1) throw new Error(`expected exactly one textarea, found ${found.length}`);

  return found[0]!;
}

/** Types into a Quasar field the way a person does, so the component's own watcher sees it. */
async function type(element: HTMLInputElement | HTMLTextAreaElement, value: string): Promise<void> {
  element.value = value;
  element.dispatchEvent(new Event('input', { bubbles: true }));
  await new Promise((resolve) => setTimeout(resolve, 0));
}

describe('TriggerEditDialog, mounted', () => {
  /** A plugin is handed the same text as its work, as a command its skill documents. */
  it('calls the box a Command for a plugin member and names the plugin\'s skill', async () => {
    const wrapper = await mountDialog(TriggerEditDialog, {
      ...baseProps, container: 'Echo', memberKind: 'plugin', memberAgent: 'plugin:sample-echo',
    });

    expect(bodyText()).toContain('Command');
    expect(bodyText()).toContain("Sent to the plugin as its instruction, in the plugin's own command syntax: see its skill plugin-sample-echo.");
    expect(bodyText()).not.toContain('What the member is told when this fires.');

    wrapper.unmount();
  });

  it('keeps Instruction and its hint for an agent member', async () => {
    const wrapper = await mountDialog(TriggerEditDialog, baseProps);

    expect(bodyText()).toContain('Instruction');
    expect(bodyText()).toContain('What the member is told when this fires.');
    expect(bodyText()).not.toContain("the plugin's own command syntax");

    wrapper.unmount();
  });

  it('opens on a draft the lib function rejects, and disables Save', async () => {
    // Asserted rather than assumed: if a fresh draft ever became valid, every case below would be
    // testing the enabled path while claiming to test the disabled one.
    expect(triggerDraftProblem(draftForCreate('Manager'))).not.toBeNull();

    const wrapper = await mountDialog(TriggerEditDialog, baseProps);

    expect(button('Save').hasAttribute('disabled')).toBe(true);

    wrapper.unmount();
  });

  it('shows the lib function\'s sentence under the field it is about, not in a banner', async () => {
    const wrapper = await mountDialog(TriggerEditDialog, baseProps);

    // Nothing is said about a brand-new draft: every field is `lazy-rules`, so a message waits until
    // the field has been left.
    expect(document.body.querySelector('.q-field--error')).toBeNull();

    await type(field('Name'), 'Nightly sweep');
    await type(theOnlyTextarea(), '   ');
    await probe.leave(theOnlyTextarea());

    const problem = triggerFieldProblems({ ...draftForCreate('Manager'), name: 'Nightly sweep' }).instruction;

    expect(problem).toBeDefined();
    expect(bodyText()).toContain(problem!);
    expect(document.body.querySelector('.q-banner')).toBeNull();
    expect(button('Save').hasAttribute('disabled')).toBe(true);

    wrapper.unmount();
  });

  it('refuses an Every count below 1 under the field', async () => {
    const wrapper = await mountDialog(TriggerEditDialog, baseProps);

    await type(field('Name'), 'Nightly sweep');
    await type(theOnlyTextarea(), 'Sweep the board.');
    await probe.type('Every', '0');
    await probe.blur('Every');

    expect(probe.hasError('Every')).toBe(true);
    expect(button('Save').hasAttribute('disabled')).toBe(true);

    wrapper.unmount();
  });

  it('refuses a raw offset as a cron timezone, which the server cannot look up', async () => {
    const trigger = {
      name: 'Nightly',
      container: 'Manager',
      instruction: 'Sweep.',
      kind: 'cron',
      expression: '0 0 2 * * *',
      timezone: 'UTC',
      idleOnly: true,
      enabled: true,
    };
    const wrapper = await mountDialog(TriggerEditDialog, { ...baseProps, trigger });

    expect(button('Save').hasAttribute('disabled')).toBe(false);

    await probe.type('Timezone (IANA)', '+05:00');
    await probe.blur('Timezone (IANA)');

    expect(probe.hasError('Timezone (IANA)')).toBe(true);
    expect(button('Save').hasAttribute('disabled')).toBe(true);

    await probe.type('Timezone (IANA)', 'Europe/London');

    expect(button('Save').hasAttribute('disabled')).toBe(false);

    wrapper.unmount();
  });

  it('enables Save and emits the draft once the problem is gone', async () => {
    const wrapper = await mountDialog(TriggerEditDialog, baseProps);

    await type(field('Name'), 'Nightly sweep');
    await type(theOnlyTextarea(), 'Sweep the board.');

    expect(button('Save').hasAttribute('disabled')).toBe(false);

    button('Save').click();
    await probe.settle();

    expect(wrapper.emitted('save')).toHaveLength(1);

    wrapper.unmount();
  });
});
