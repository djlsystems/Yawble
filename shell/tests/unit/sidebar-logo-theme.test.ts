import { describe, expect, it } from 'vitest';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

// The sidebar's wordmark: the light theme shows the dark-lettered logo, the dark theme the white-lettered one.
const src = join(__dirname, '../../src/renderer/src');
const sidebar = readFileSync(join(src, 'components/Sidebar.vue'), 'utf8');

describe('the sidebar logo follows the theme', () => {
  it('has both logos, same size', () => {
    for (const f of ['yawble-logo-light.png', 'yawble-logo-dark.png']) {
      const png = readFileSync(join(src, 'assets', f));
      expect(existsSync(join(src, 'assets', f))).toBe(true);
      // IHDR: width and height at bytes 16 and 20.
      expect([png.readUInt32BE(16), png.readUInt32BE(20)]).toEqual([319, 96]);
    }
  });

  it('shows the light one by default and the dark one under data-theme="dark"', () => {
    expect(sidebar).toMatch(/class="logo on-light" :src="wordmark"/);
    expect(sidebar).toMatch(/class="logo on-dark" :src="wordmarkDark"/);
    expect(sidebar).toContain('.logo.on-dark { display: none; }');
    expect(sidebar).toContain(':root[data-theme="dark"] .logo.on-light { display: none; }');
    expect(sidebar).toContain(':root[data-theme="dark"] .logo.on-dark { display: block; }');
    // Never :global(...) around the theme: scoped CSS then compiles the rule to the bare `:root[data-theme=dark]`,
    // which hides the whole window in dark mode.
    expect(sidebar).not.toMatch(/:global\(:root/);
  });
});
