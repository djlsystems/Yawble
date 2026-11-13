// @vitest-environment happy-dom
import { mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import { defineComponent, h, ref } from 'vue';
import Modal from '../../src/renderer/src/components/Modal.vue';

const esc = () => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

describe('Escape closes dialogs', () => {
  afterEach(() => { document.body.innerHTML = ''; });

  it('closes a dialog', () => {
    const w = mount(Modal, { props: { title: 'One' }, attachTo: document.body });
    esc();
    expect(w.emitted('close')).toHaveLength(1);
    w.unmount();
  });

  it('closes only the top dialog when one is open over another, one per press', async () => {
    const closed: string[] = [];
    const App = defineComponent(() => {
      const open = ref(['under', 'over']);
      return () => h('div', open.value.map((t) => h(Modal, { key: t, title: t, onClose: () => { closed.push(t); open.value = open.value.filter((x) => x !== t); } })));
    });
    const w = mount(App, { attachTo: document.body });
    esc();
    await w.vm.$nextTick();
    expect(closed).toEqual(['over']);
    esc();
    await w.vm.$nextTick();
    expect(closed).toEqual(['over', 'under']);
    w.unmount();
  });

  it('leaves an inline panel (a Settings tab) alone, and ignores an Escape something else already handled', () => {
    const inline = mount(Modal, { props: { title: 'Tab', inline: true }, attachTo: document.body });
    esc();
    expect(inline.emitted('close')).toBeUndefined();
    const w = mount(Modal, { props: { title: 'One' }, attachTo: document.body });
    const e = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
    e.preventDefault();
    document.dispatchEvent(e);
    expect(w.emitted('close')).toBeUndefined();
    inline.unmount(); w.unmount();
  });

  it('no longer reacts once closed', () => {
    const w = mount(Modal, { props: { title: 'One' }, attachTo: document.body });
    w.unmount();
    esc(); // must not throw or reach the unmounted dialog
    expect(w.emitted('close')).toBeUndefined();
  });
});
