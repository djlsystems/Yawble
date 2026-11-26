// @vitest-environment happy-dom
import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import Sidebar from '../../src/renderer/src/components/Sidebar.vue';

// The sidebar's wordmark: the light theme shows the dark-lettered logo, the dark theme the white-lettered one.
const src = join(__dirname, '../../src/renderer/src');

describe('the sidebar logo follows the theme', () => {
  it('has both logos, same size', () => {
    for (const f of ['yawble-logo-light.png', 'yawble-logo-dark.png']) {
      const png = readFileSync(join(src, 'assets', f));
      expect(existsSync(join(src, 'assets', f))).toBe(true);
      // IHDR: width and height at bytes 16 and 20.
      expect([png.readUInt32BE(16), png.readUInt32BE(20)]).toEqual([319, 96]);
    }
  });

  it('draws both wordmarks, one marked for the light theme and one for the dark (the stylesheet shows one of them)', () => {
    const w = mount(Sidebar, { props: { view: 'backlog', conversations: [], teams: [], conversationId: null, teamId: null, slots: null, remote: false } });
    const light = w.get('[data-test=sidebar-logo]');
    const dark = w.get('[data-test=sidebar-logo-dark]');
    expect(light.classes()).toEqual(expect.arrayContaining(['logo', 'on-light']));
    expect(dark.classes()).toEqual(expect.arrayContaining(['logo', 'on-dark']));
    expect(light.attributes('src')).toContain('yawble-logo-light');
    expect(dark.attributes('src')).toContain('yawble-logo-dark');
    w.unmount();
  });
});
