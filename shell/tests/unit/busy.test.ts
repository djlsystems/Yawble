// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { defineComponent, h } from 'vue';
import { describe, expect, it } from 'vitest';
import BusyLabel from '../../src/renderer/src/components/BusyLabel.vue';
import AgentTile from '../../src/renderer/src/components/AgentTile.vue';
import { useBusy } from '../../src/renderer/src/busy';

// The one "action in progress" pattern: useBusy() holds which actions are pending, BusyLabel shows the shared spinner
// and the present-participle text inside the button. No core: the action is a promise the test settles.
function deferred<T = void>() {
  let resolve!: (v: T) => void; let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

describe('useBusy', () => {
  it('marks a key pending while its action runs and clears it on success', async () => {
    const b = useBusy();
    const d = deferred<number>();
    const p = b.run('save', () => d.promise);
    expect(b.is('save')).toBe(true);
    expect(b.any()).toBe(true);
    expect(b.is('other')).toBe(false);
    d.resolve(7);
    expect(await p).toBe(7);
    expect(b.is('save')).toBe(false);
    expect(b.any()).toBe(false);
  });

  it('clears the key when the action fails (the error still reaches the caller)', async () => {
    const b = useBusy();
    const d = deferred();
    const p = b.run('save', () => d.promise);
    d.reject(new Error('nope'));
    await expect(p).rejects.toThrow('nope');
    expect(b.is('save')).toBe(false);
  });

  it('holds every button of one thing while one runs, and says which one was clicked', async () => {
    const b = useBusy();
    const d = deferred();
    void b.run('ask-3', () => d.promise, 'decline');
    expect(b.is('ask-3')).toBe(true); // every button of ask 3 is held
    expect(b.is('ask-3', 'decline')).toBe(true); // the clicked one spins
    expect(b.is('ask-3', 'approve')).toBe(false);
    expect(await b.run('ask-3', async () => 'second', 'approve')).toBeUndefined(); // another button of it: ignored
    d.resolve();
    await flushPromises();
    expect(b.is('ask-3')).toBe(false);
  });

  it('ignores a second run of a key that is still pending (a double click)', async () => {
    const b = useBusy();
    const d = deferred();
    let calls = 0;
    const fn = () => { calls++; return d.promise; };
    void b.run('send', fn);
    const second = b.run('send', fn);
    expect(calls).toBe(1);
    expect(await second).toBeUndefined();
    d.resolve();
    await flushPromises();
    void b.run('send', fn);
    expect(calls).toBe(2);
  });
});

describe('BusyLabel', () => {
  it('shows the button label when idle and the spinner with its text when busy', async () => {
    const w = mount(BusyLabel, { props: { busy: false, text: 'Saving…' }, slots: { default: 'Save' } });
    expect(w.text()).toBe('Save');
    expect(w.find('.btn-spinner').exists()).toBe(false);
    await w.setProps({ busy: true });
    expect(w.text()).toBe('Saving…');
    expect(w.get('[data-test=btn-spinner]').classes()).toContain('btn-spinner');
    expect(w.get('.btn-spinner').attributes('aria-hidden')).toBe('true');
  });

  it('in a button driven by useBusy: disabled + spinner while pending, restored after, a double click does nothing', async () => {
    const d = deferred();
    let calls = 0;
    const Btn = defineComponent({
      setup() {
        const busy = useBusy();
        const save = () => busy.run('save', () => { calls++; return d.promise; });
        return () => h('button', { 'data-test': 'save', disabled: busy.is('save'), onClick: save },
          [h(BusyLabel, { busy: busy.is('save'), text: 'Saving…' }, () => 'Save')]);
      },
    });
    const w = mount(Btn);
    await w.get('[data-test=save]').trigger('click');
    expect(w.get('[data-test=save]').attributes('disabled')).toBeDefined();
    expect(w.get('[data-test=save]').text()).toBe('Saving…');
    await w.get('[data-test=save]').trigger('click');
    expect(calls).toBe(1);
    d.resolve();
    await flushPromises();
    expect(w.get('[data-test=save]').attributes('disabled')).toBeUndefined();
    expect(w.get('[data-test=save]').text()).toBe('Save');
    expect(w.find('.btn-spinner').exists()).toBe(false);
  });

  it('AgentTile uses it for Terminal / Test (no spinner of its own)', () => {
    const w = mount(AgentTile, { props: {
      title: 'Claude', cliId: 'claude', custom: false, starting: 'test',
      detected: { cliId: 'claude', found: true, launcherPath: '/c', executablePath: '/c', version: '1', installKind: 'native', readOnlyFolders: [], configFolders: [], problems: [], installHelp: '' },
    } });
    expect(w.findComponent(BusyLabel).exists()).toBe(true);
    expect(w.get('[data-test=test-claude]').text()).toBe('Starting…');
    expect(w.get('[data-test=test-claude] [data-test=btn-spinner]').exists()).toBe(true);
    expect(w.get('[data-test=terminal-claude]').text()).toBe('Terminal');
  });
});
