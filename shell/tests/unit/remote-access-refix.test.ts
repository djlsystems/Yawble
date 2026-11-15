// @vitest-environment happy-dom
import { mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import { defineComponent, h, nextTick, ref, withDirectives } from 'vue';
import { applyRemoteAccess, READ_ONLY_TITLE, vChange } from '../../src/renderer/src/remoteAccess';

// B025 re-verify (card 159): the tester's break tests for the finding 1 fix (card 157). Fakes only: a tiny screen with
// one v-change button whose own :disabled and :title the test drives.

afterEach(() => applyRemoteAccess('full'));

function screen(initial: { disabled?: unknown; title?: string; show?: boolean } = {}) {
  const disabled = ref<unknown>(initial.disabled ?? false);
  const title = ref<string | undefined>(initial.title);
  const show = ref(initial.show ?? true);
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
  return { w, btn, disabled, title, show, label };
}

describe('the screen\'s :disabled changes while access is read', () => {
  it('enabled -> disabled while read: disabled after going back to full', async () => {
    const s = screen({ disabled: false });
    applyRemoteAccess('read');
    s.disabled.value = true;
    await nextTick();
    expect(s.btn().disabled).toBe(true);
    applyRemoteAccess('full');
    expect(s.btn().disabled).toBe(true);
  });

  it('disabled -> enabled while read: still disabled while read, enabled after going back to full', async () => {
    const s = screen({ disabled: true });
    applyRemoteAccess('read');
    s.disabled.value = false;
    await nextTick();
    expect(s.btn().disabled).toBe(true);
    applyRemoteAccess('full');
    expect(s.btn().disabled).toBe(false);
  });

  it('an unrelated re-render while read keeps the button disabled and gives back the screen\'s value on full', async () => {
    const s = screen({ disabled: false });
    applyRemoteAccess('read');
    s.label.value = 'b';
    await nextTick();
    expect(s.btn().disabled).toBe(true);
    applyRemoteAccess('full');
    expect(s.btn().disabled).toBe(false);
  });

  it.each([[''], [true], ['disabled']])('disabled=%j counts as disabled', async (v) => {
    const s = screen({ disabled: v });
    applyRemoteAccess('read');
    applyRemoteAccess('full');
    expect(s.btn().disabled).toBe(true);
  });

  it.each([[false], [null], [undefined], [0]])('disabled=%j counts as enabled', async (v) => {
    const s = screen({ disabled: v });
    applyRemoteAccess('read');
    applyRemoteAccess('full');
    expect(s.btn().disabled).toBe(false);
  });
});

describe('several read/full flips in a row', () => {
  it('keeps following the screen and the access through each flip', async () => {
    const s = screen({ disabled: false, title: 'Send it' });
    for (let i = 0; i < 5; i++) {
      applyRemoteAccess('read');
      expect(s.btn().disabled).toBe(true);
      expect(s.btn().title).toBe(READ_ONLY_TITLE);
      applyRemoteAccess('read'); // read twice: no change
      applyRemoteAccess('full');
      expect(s.btn().disabled).toBe(i % 2 === 1);
      expect(s.btn().title).toBe('Send it');
      applyRemoteAccess('full'); // full twice: no change
      expect(s.btn().disabled).toBe(i % 2 === 1);
      s.disabled.value = i % 2 === 0; // next round's screen value
      await nextTick();
    }
  });

  it('a button with no title of its own gets no title back after the flips', () => {
    const s = screen({ disabled: false });
    applyRemoteAccess('read');
    applyRemoteAccess('full');
    applyRemoteAccess('read');
    applyRemoteAccess('full');
    expect(s.btn().hasAttribute('title')).toBe(false);
  });
});

describe('a button that mounts while access is read', () => {
  it.each([[false], [true]])('screen disabled=%j: disabled with the reason while read, the screen\'s value on full', async (d) => {
    const s = screen({ disabled: d, title: 'Own', show: false });
    applyRemoteAccess('read');
    s.show.value = true;
    await nextTick();
    expect(s.btn().disabled).toBe(true);
    expect(s.btn().title).toBe(READ_ONLY_TITLE);
    applyRemoteAccess('full');
    expect(s.btn().disabled).toBe(d);
    expect(s.btn().title).toBe('Own');
  });

  it('a button unmounted while read is no longer touched; a new one mounted later starts right', async () => {
    const s = screen({ disabled: false, show: true });
    applyRemoteAccess('read');
    const old = s.btn();
    s.show.value = false;
    await nextTick();
    applyRemoteAccess('full');
    expect(old.disabled).toBe(true); // detached, left alone
    s.show.value = true;
    await nextTick();
    expect(s.btn().disabled).toBe(false);
  });
});

describe('the screen\'s :title changes while access is read (same kind of fault as finding 1, for titles)', () => {
  it('gives back the screen\'s current title, not the one from before read', async () => {
    const s = screen({ disabled: false, title: 'Before' });
    applyRemoteAccess('read');
    s.title.value = 'After';
    await nextTick();
    expect(s.btn().title).toBe(READ_ONLY_TITLE);
    applyRemoteAccess('full');
    expect(s.btn().title).toBe('After');
  });
});
