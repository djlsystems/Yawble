// @vitest-environment happy-dom
//
// THE TAB STRIP MEASURES AGAIN ONCE IT IS ITS REAL SIZE. QTabs measures its tabs as it mounts, which
// inside a dialog is during the opening scale-up, so tabs that overflow read as fitting and the
// arrows stay hidden. DialogTabs asks it to measure again (QTabs re-measures whenever
// `outside-arrows` changes, so the strip turns it off and on within one tick) when a transition or
// animation around it ends. happy-dom lays nothing out, so this pins that the request is made and
// that the strip settles with its arrows outside, not the browser's measurement.
import { afterEach, describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { defineComponent, h, nextTick, ref } from 'vue';
import { QTab, QTabs } from 'quasar';
import '../../test/mountQuasar';
import DialogTabs from '../DialogTabs.vue';

const Host = defineComponent({
  setup() {
    const tab = ref('one');
    return () => h('div', { class: 'around' }, [
      h(DialogTabs, { modelValue: tab.value, 'onUpdate:modelValue': (v: string) => (tab.value = v) }, () => [
        h(QTab, { name: 'one', label: 'One' }),
        h(QTab, { name: 'two', label: 'Two' }),
      ]),
    ]);
  },
});

function outsideArrowsSeen(wrapper: ReturnType<typeof mount>) {
  const tabs = wrapper.findComponent(QTabs);
  const seen: boolean[] = [];
  tabs.vm.$watch(() => tabs.props('outsideArrows'), (value: boolean) => seen.push(value));
  return seen;
}

describe('the tab strip measures again', () => {
  let wrapper: ReturnType<typeof mount> | null = null;
  afterEach(() => wrapper?.unmount());

  it('when the dialog around it finishes opening, and settles with its arrows outside', async () => {
    wrapper = mount(Host, { attachTo: document.body });
    await nextTick();
    const seen = outsideArrowsSeen(wrapper);

    wrapper.find('.around').element.dispatchEvent(new Event('transitionend', { bubbles: true }));
    await nextTick();
    await nextTick();

    expect(seen).toEqual([false, true]);
    expect(wrapper.findComponent(QTabs).props('outsideArrows')).toBe(true);
  });

  it('when an animation around it ends too, and not for one that ends elsewhere', async () => {
    wrapper = mount(Host, { attachTo: document.body });
    await nextTick();
    const seen = outsideArrowsSeen(wrapper);

    const elsewhere = document.createElement('div');
    document.body.appendChild(elsewhere);
    elsewhere.dispatchEvent(new Event('transitionend', { bubbles: true }));
    await nextTick();
    expect(seen).toEqual([]);

    wrapper.find('.around').element.dispatchEvent(new Event('animationend', { bubbles: true }));
    await nextTick();
    await nextTick();
    expect(seen).toEqual([false, true]);
    elsewhere.remove();
  });
});
