// @vitest-environment happy-dom
import { defineAsyncComponent, h } from 'vue';
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import PageLoadFailed from '../../src/renderer/src/components/PageLoadFailed.vue';

describe('a page whose file cannot be loaded', () => {
  it('says so with a Reload button instead of rendering nothing', async () => {
    const Page = defineAsyncComponent({ loader: () => Promise.reject(new Error('Failed to fetch dynamically imported module')), errorComponent: PageLoadFailed });
    const w = mount({ render: () => h(Page) });
    await flushPromises();
    await flushPromises();
    expect(w.get('[data-test=page-load-failed]').text()).toContain('This page could not be loaded.');
    expect(w.find('[data-test=page-load-reload]').exists()).toBe(true);
  });

  it('Reload uses the desktop window Reload command, as Ctrl+R does', async () => {
    const commands: string[] = [];
    (window as any).yawble = { windowCommand: (c: string) => commands.push(c), call: async () => ({}), onEvent: () => () => {} };
    try {
      const w = mount(PageLoadFailed);
      await w.get('[data-test=page-load-reload]').trigger('click');
      expect(commands).toEqual(['reload']);
    } finally { delete (window as any).yawble; }
  });
});
