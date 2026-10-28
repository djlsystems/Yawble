// @vitest-environment happy-dom
//
// A TABLE'S BOOL COLUMN SAYS WHAT IT IS FOR (D17). The row editor gives every text, enum and number
// column its description behind an info icon; a bool column's switch had only its label, so a
// manifest's description for, say, web-credentials' "Active" and "Try again" reached nobody.
import { afterEach, describe, expect, it } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import PluginTableField from '../PluginTableField.vue';
import type { PluginTableColumn } from '../../api/types';
import { bodyFind, helpTexts, resetBody } from '../../test/mountQuasar';
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
      column({ name: 'name', type: 'string', required: true, description: 'The row name.' }),
      column({ name: 'active', type: 'bool', label: 'Active', default: true, description: 'Off skips this site on every run.' }),
      column({ name: 'retry', type: 'bool', label: 'Try again', description: 'On asks the plugin to check this site again.' }),
    ],
  },
});

afterEach(resetBody);

describe('PluginTableField row editor', () => {
  it("shows a bool column's description beside its switch, as the other fields do", async () => {
    const wrapper = mount(PluginTableField, {
      props: { name: 'entries', field: entries, label: 'Entries', modelValue: [], editable: true },
      attachTo: document.body,
    });
    await flushPromises();

    (wrapper.find('[data-table-add]').element as HTMLElement).click();
    await flushPromises();

    const editor = bodyFind('[data-table-editor]')!;
    expect(editor).not.toBeNull();
    const help = helpTexts(editor);
    expect(help).toContain('Off skips this site on every run.');
    expect(help).toContain('On asks the plugin to check this site again.');

    // The help sits next to its own switch, not inside it: a tap on the icon must not flip the switch.
    const active = editor.querySelector('[data-editor-bool="active"]')!;
    expect(active.querySelector('[data-editor-cell="active"]')).not.toBeNull();
    expect(helpTexts(active)).toEqual(['Off skips this site on every run.']);
    expect(active.querySelector('[data-editor-cell="active"] [data-help]')).toBeNull();
    wrapper.unmount();
  });
});
