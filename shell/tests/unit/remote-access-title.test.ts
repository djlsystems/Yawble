// @vitest-environment happy-dom
import { mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import { defineComponent, h, nextTick, ref, withDirectives } from 'vue';
import { applyRemoteAccess, READ_ONLY_TITLE, vChange } from '../../src/renderer/src/remoteAccess';

// B025 re-verify (card 164): the tester's break tests for the finding 2 fix (card 163) - the screen's own :title on a
// v-change button must come back as the screen has it NOW when access goes from read back to full. Fakes only.

afterEach(() => applyRemoteAccess('full'));

function screen(initial: { title?: unknown; show?: boolean; disabled?: boolean } = {}) {
  const title = ref<unknown>(initial.title);
  const show = ref(initial.show ?? true);
  const disabled = ref(initial.disabled ?? false);
  const label = ref('a');
  const C = defineComponent({
    setup: () => () =>
      h('div', [
        h('span', label.value),
        show.value
          ? withDirectives(h('button', { 'data-test': 'b', disabled: disabled.value, title: title.value }, 'go'), [[vChange]])
          : null,
      ]),
  });
  const w = mount(C, { attachTo: document.body });
  const btn = () => w.find('[data-test="b"]').element as HTMLButtonElement;
  const attr = () => btn().getAttribute('title');
  return { w, btn, attr, title, show, disabled, label };
}

describe('the screen\'s :title changes while access is read', () => {
  it('A -> B while read: shows the read-only title, then B on full', async () => {
    const s = screen({ title: 'A' });
    applyRemoteAccess('read');
    s.title.value = 'B';
    await nextTick();
    expect(s.attr()).toBe(READ_ONLY_TITLE);
    applyRemoteAccess('full');
    expect(s.attr()).toBe('B');
  });

  it('A -> cleared while read: no title attribute on full', async () => {
    const s = screen({ title: 'A' });
    applyRemoteAccess('read');
    s.title.value = undefined;
    await nextTick();
    expect(s.attr()).toBe(READ_ONLY_TITLE);
    applyRemoteAccess('full');
    expect(s.btn().hasAttribute('title')).toBe(false);
  });

  it('none -> set while read: the new title on full', async () => {
    const s = screen({});
    applyRemoteAccess('read');
    s.title.value = 'New';
    await nextTick();
    expect(s.attr()).toBe(READ_ONLY_TITLE);
    applyRemoteAccess('full');
    expect(s.attr()).toBe('New');
  });

  it('set then cleared while read (two renders): no title on full', async () => {
    const s = screen({});
    applyRemoteAccess('read');
    s.title.value = 'X';
    await nextTick();
    s.title.value = null;
    await nextTick();
    expect(s.attr()).toBe(READ_ONLY_TITLE);
    applyRemoteAccess('full');
    expect(s.btn().hasAttribute('title')).toBe(false);
  });

  it('cleared then set again while read: the last title on full', async () => {
    const s = screen({ title: 'A' });
    applyRemoteAccess('read');
    s.title.value = undefined;
    await nextTick();
    s.title.value = 'C';
    await nextTick();
    applyRemoteAccess('full');
    expect(s.attr()).toBe('C');
  });

  it('A -> B -> A while read: A on full', async () => {
    const s = screen({ title: 'A' });
    applyRemoteAccess('read');
    s.title.value = 'B';
    await nextTick();
    s.title.value = 'A';
    await nextTick();
    applyRemoteAccess('full');
    expect(s.attr()).toBe('A');
  });

  it('an unrelated re-render while read keeps the read-only title and gives back the screen\'s on full', async () => {
    const s = screen({ title: 'A' });
    applyRemoteAccess('read');
    s.label.value = 'b';
    await nextTick();
    expect(s.attr()).toBe(READ_ONLY_TITLE);
    applyRemoteAccess('full');
    expect(s.attr()).toBe('A');
  });

  it('title and disabled both changed while read: both come back as the screen has them', async () => {
    const s = screen({ title: 'A', disabled: false });
    applyRemoteAccess('read');
    s.title.value = 'Busy';
    s.disabled.value = true;
    await nextTick();
    applyRemoteAccess('full');
    expect(s.attr()).toBe('Busy');
    expect(s.btn().disabled).toBe(true);
  });

  it('an empty-string title is kept as title=""', async () => {
    const s = screen({ title: '' });
    applyRemoteAccess('read');
    applyRemoteAccess('full');
    expect(s.attr()).toBe('');
  });
});

describe('several read/full flips with title changes', () => {
  it('follows the screen through each flip, changing in read and in full', async () => {
    const s = screen({ title: 't0' });
    for (let i = 1; i <= 5; i++) {
      applyRemoteAccess('read');
      expect(s.attr()).toBe(READ_ONLY_TITLE);
      s.title.value = i % 2 ? `r${i}` : undefined;
      await nextTick();
      expect(s.attr()).toBe(READ_ONLY_TITLE);
      applyRemoteAccess('full');
      if (i % 2) expect(s.attr()).toBe(`r${i}`);
      else expect(s.btn().hasAttribute('title')).toBe(false);
      s.title.value = `f${i}`;
      await nextTick();
      expect(s.attr()).toBe(`f${i}`);
    }
  });

  it('read -> full -> read with a title change still pending (no render in between): the new title at the end', async () => {
    const s = screen({ title: 'A' });
    applyRemoteAccess('read');
    s.title.value = 'B'; // not rendered yet
    applyRemoteAccess('full');
    applyRemoteAccess('read');
    await nextTick();
    expect(s.attr()).toBe(READ_ONLY_TITLE);
    applyRemoteAccess('full');
    expect(s.attr()).toBe('B');
  });

  it('a title change pending when access goes full is rendered right after', async () => {
    const s = screen({ title: 'A' });
    applyRemoteAccess('read');
    s.title.value = 'B';
    applyRemoteAccess('full');
    await nextTick();
    expect(s.attr()).toBe('B');
  });

  it('read twice and full twice in a row changes nothing extra', async () => {
    const s = screen({ title: 'A' });
    applyRemoteAccess('read');
    applyRemoteAccess('read');
    expect(s.attr()).toBe(READ_ONLY_TITLE);
    applyRemoteAccess('full');
    applyRemoteAccess('full');
    expect(s.attr()).toBe('A');
  });
});

describe('mount and unmount while read', () => {
  it('mounted while already read with a title: read-only title, then its own on full', async () => {
    applyRemoteAccess('read');
    const s = screen({ title: 'Mine' });
    expect(s.attr()).toBe(READ_ONLY_TITLE);
    expect(s.btn().disabled).toBe(true);
    applyRemoteAccess('full');
    expect(s.attr()).toBe('Mine');
    expect(s.btn().disabled).toBe(false);
  });

  it('mounted while already read with no title: no title on full', async () => {
    applyRemoteAccess('read');
    const s = screen({});
    expect(s.attr()).toBe(READ_ONLY_TITLE);
    applyRemoteAccess('full');
    expect(s.btn().hasAttribute('title')).toBe(false);
  });

  it('mounted while read, title changed while still read: the new title on full', async () => {
    applyRemoteAccess('read');
    const s = screen({ title: 'Mine' });
    s.title.value = 'Changed';
    await nextTick();
    applyRemoteAccess('full');
    expect(s.attr()).toBe('Changed');
  });

  it('unmounted while read, remounted while read with a different title: that title on full', async () => {
    const s = screen({ title: 'A' });
    applyRemoteAccess('read');
    s.show.value = false;
    await nextTick();
    s.title.value = 'B';
    s.show.value = true;
    await nextTick();
    expect(s.attr()).toBe(READ_ONLY_TITLE);
    applyRemoteAccess('full');
    expect(s.attr()).toBe('B');
  });

  it('unmounted while read, full, then remounted: its own title, no read-only leftovers', async () => {
    const s = screen({ title: 'A' });
    applyRemoteAccess('read');
    s.show.value = false;
    await nextTick();
    applyRemoteAccess('full');
    s.show.value = true;
    await nextTick();
    expect(s.attr()).toBe('A');
    expect(s.btn().disabled).toBe(false);
    expect(s.btn().dataset.readOnly).toBeUndefined();
  });

  it('two buttons keep their own titles apart', async () => {
    const a = screen({ title: 'one' });
    const b = screen({ title: 'two' });
    applyRemoteAccess('read');
    b.title.value = 'TWO';
    await nextTick();
    applyRemoteAccess('full');
    expect(a.attr()).toBe('one');
    expect(b.attr()).toBe('TWO');
  });
});
