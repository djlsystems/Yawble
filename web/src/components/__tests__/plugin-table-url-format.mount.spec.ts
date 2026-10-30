// @vitest-environment happy-dom
//
// A `format: "url"` BOX GIVES A BARE ADDRESS https:// ON LEAVING IT (22750), so the person sees what
// will be saved before pressing Add or Done. A scheme is kept as typed, junk is left to fail the
// plugin's check, and Other hosts (no format) is never given a scheme.
import { afterEach, describe, expect, it } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import PluginTableField from '../PluginTableField.vue';
import type { PluginTableColumn, PluginTableRow } from '../../api/types';
import { leave } from '../../test/formProbe';
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
      column({ name: 'siteUrl', type: 'string', label: 'Site URL', format: 'url' }),
      column({ name: 'hosts', type: 'string', label: 'Other hosts' }),
    ],
  },
});

afterEach(resetBody);

const box = (name: string) =>
  bodyFind('[data-table-editor]')!.querySelector(`[data-editor-cell="${name}"] input, input[data-editor-cell="${name}"]`) as HTMLInputElement;

async function type(name: string, value: string, andLeave = true) {
  const input = box(name);
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await flushPromises();
  if (andLeave) {
    await leave(input);
  }
}

async function openAdd(rows: PluginTableRow[] = []) {
  const wrapper = mount(PluginTableField, {
    props: { name: 'entries', field: entries, label: 'Entries', modelValue: rows, editable: true, saveLabel: 'Save' },
    attachTo: document.body,
  });
  await flushPromises();
  (wrapper.find('[data-table-add]').element as HTMLElement).click();
  await flushPromises();
  return wrapper;
}

const applyRow = async (wrapper: Awaited<ReturnType<typeof openAdd>>) => {
  (bodyFind('[data-table-editor]')!.querySelector('[data-table-apply]') as HTMLElement).click();
  await flushPromises();
  const emitted = wrapper.emitted('update:modelValue')!;
  return (emitted[emitted.length - 1]![0] as PluginTableRow[]).at(-1)!;
};

describe('PluginTableField url column', () => {
  it.each([
    ['mysite.com', 'https://mysite.com'],
    ['www.mysite.com', 'https://www.mysite.com'],
    ['https://mysite.com/login', 'https://mysite.com/login'],
    ['http://mysite.com', 'http://mysite.com'],
    ['not a url', 'not a url'],
    ['foo bar.com', 'foo bar.com'],
    ['javascript:alert(1)', 'javascript:alert(1)'],
    ['https://', 'https://'],
  ])('shows %j as %j on leaving the box, and adds it so', async (typed, shown) => {
    const wrapper = await openAdd();
    await type('name', 'site1');
    await type('siteUrl', typed);
    expect(box('siteUrl').value).toBe(shown);
    expect((await applyRow(wrapper)).siteUrl).toBe(shown);
    wrapper.unmount();
  });

  it('never gives Other hosts a scheme', async () => {
    const wrapper = await openAdd();
    await type('name', 'site1');
    await type('hosts', 'login.mysite.com www.mysite.com');
    expect(box('hosts').value).toBe('login.mysite.com www.mysite.com');
    expect((await applyRow(wrapper)).hosts).toBe('login.mysite.com www.mysite.com');
    wrapper.unmount();
  });

  it('gives a bare address its scheme on Add even when the box was never left', async () => {
    const wrapper = await openAdd();
    await type('name', 'site1');
    await type('siteUrl', 'mysite.com', false);
    expect((await applyRow(wrapper)).siteUrl).toBe('https://mysite.com');
    wrapper.unmount();
  });
});
