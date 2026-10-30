// @vitest-environment happy-dom
//
// A ROW EDITOR'S SECRET LINE NAMES THE EDITOR'S OWN BUTTON (22718). Typing a password in "Add a row
// to Entries" said "Saved when you press Save." - the form's button - in a dialog whose only button
// is Add. The line now names whichever button the dialog shows: Add for a new row, Done for an edit.
import { afterEach, describe, expect, it } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import PluginTableField from '../PluginTableField.vue';
import type { PluginTableColumn } from '../../api/types';
import { bodyFind, resetBody } from '../../test/mountQuasar';
import { hostField } from '../../test/pluginFixtures';

const column = (c: Partial<PluginTableColumn> & Pick<PluginTableColumn, 'name' | 'type'>): PluginTableColumn => ({
  label: null,
  description: '',
  required: false,
  default: null,
  enum: null,
  min: null,
  max: null,
  integer: false,
  readOnly: false,
  masked: null,
  ...c,
});

const entries = hostField({
  type: 'table',
  description: 'Sites to sign in to.',
  table: {
    key: 'name',
    maxRows: 10,
    rowActions: [],
    columns: [
      column({ name: 'name', type: 'string', required: true }),
      column({ name: 'password', type: 'secret', label: 'Password' }),
    ],
  },
});

const states = { entries: { site1: { password: { set: true as const, setBy: 'Ada', setAt: '2026-10-01T09:00:00Z' } } } };

afterEach(resetBody);

function mountTable(rows: Record<string, unknown>[] = [], savedRows: string[] = []) {
  return mount(PluginTableField, {
    // The form's own button is Save: the editor must not borrow it.
    props: { name: 'entries', field: entries, label: 'Entries', modelValue: rows, editable: true, saveLabel: 'Save', states, savedRows },
    attachTo: document.body,
  });
}

const editor = () => bodyFind('[data-table-editor]')!;
const applyButton = () => editor().querySelector('[data-table-apply]')!.textContent!.trim();
const secretLine = () => editor().querySelector('[data-editor-secret="password"] .os-text-muted')!.textContent!.trim();

async function typePassword(value: string) {
  const input = editor().querySelector('[data-secret-value-input] input, input[data-secret-value-input]') as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await flushPromises();
}

describe('PluginTableField row editor secret words', () => {
  it('the add dialog says Saved when you press Add.', async () => {
    const wrapper = mountTable();
    await flushPromises();
    (wrapper.find('[data-table-add]').element as HTMLElement).click();
    await flushPromises();

    expect(applyButton()).toBe('Add');
    expect(secretLine()).toBe('Not set.');
    await typePassword('hunter2-secret');
    expect(secretLine()).toBe(`Saved when you press ${applyButton()}.`);
    expect(secretLine()).toBe('Saved when you press Add.');
    wrapper.unmount();
  });

  it('the edit dialog names its own button, Done, for a typed and a cleared secret', async () => {
    const wrapper = mountTable([{ name: 'site1' }], ['site1']);
    await flushPromises();
    (wrapper.find('[data-table-edit]').element as HTMLElement).click();
    await flushPromises();

    expect(applyButton()).toBe('Done');
    expect(secretLine()).toMatch(/^Saved by Ada on /);
    await typePassword('hunter2-secret');
    expect(secretLine()).toBe('Saved when you press Done.');

    await typePassword('');
    const clear = [...editor().querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Clear') as HTMLElement;
    clear.click();
    await flushPromises();
    expect(secretLine()).toBe(`Cleared when you press ${applyButton()}.`);
    expect(secretLine()).toBe('Cleared when you press Done.');
    wrapper.unmount();
  });

  it("the table's own cell still names the form's button", async () => {
    const wrapper = mountTable();
    await flushPromises();
    (wrapper.find('[data-table-add]').element as HTMLElement).click();
    await flushPromises();
    (editor().querySelector('[data-editor-cell="name"] input, input[data-editor-cell="name"]') as HTMLInputElement).value = 'site2';
    editor().querySelector<HTMLInputElement>('[data-editor-cell="name"] input, input[data-editor-cell="name"]')!.dispatchEvent(new Event('input'));
    await typePassword('hunter2-secret');
    (editor().querySelector('[data-table-apply]') as HTMLElement).click();
    await flushPromises();
    expect(wrapper.find('[data-table-secret-cell]').text()).toBe('Saved on Save');
    wrapper.unmount();
  });
});
