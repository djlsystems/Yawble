import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * ONE TAB STRIP. Every tab strip in the app is `DialogTabs`, so a strip wider than its dialog
 * always shows an arrow at each end instead of cutting a tab off with nothing saying there is more
 * (Team settings' Environment tab was cut off that way), and every dialog's tabs line up and are
 * coloured alike. A bare `<q-tabs>` anywhere else is refused.
 *
 * That each dialog is sized so its tabs fit at its usual width is a browser measurement, not
 * something jsdom can lay out: Team settings is `os-dialog-lg` for that reason.
 */
const sourceDirectory = join(import.meta.dirname, '..', '..');

function vueFiles(directory: string, prefix = ''): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    if (entry.isDirectory()) return entry.name === '__tests__' ? [] : vueFiles(join(directory, entry.name), `${prefix}${entry.name}/`);
    return entry.name.endsWith('.vue') ? [`${prefix}${entry.name}`] : [];
  });
}

const withoutComments = (source: string) =>
  source.replace(/\/\*[\s\S]*?\*\//g, ' ').replace(/<!--[\s\S]*?-->/g, ' ');

const files = vueFiles(sourceDirectory)
  .map((name) => ({ name, source: withoutComments(readFileSync(join(sourceDirectory, name), 'utf8')) }));

describe('the tab strip', () => {
  it('finds the dialogs that have tabs', () => {
    // A scan that matches nothing passes the refusal below.
    const users = files.filter(({ source }) => source.includes('<DialogTabs')).map(({ name }) => name);

    expect(users).toEqual(expect.arrayContaining([
      'components/TeamSettingsDialog.vue',
      'components/CreateTeamDialog.vue',
      'components/TenantSettingsDialog.vue',
      'components/ProfileDialog.vue',
    ]));
  });

  it('is DialogTabs everywhere: no bare q-tabs outside it', () => {
    const bare = files
      .filter(({ name, source }) => name !== 'components/DialogTabs.vue' && /<q-tabs\b/.test(source))
      .map(({ name }) => name);

    expect(bare, 'use <DialogTabs> so the tabs overflow and look like every other dialog').toEqual([]);
  });

  it('stays left-aligned at every width and shows arrows when it overflows, on touch screens too', () => {
    const strip = files.find(({ name }) => name === 'components/DialogTabs.vue')!.source;
    const tag = strip.match(/<q-tabs\b[^>]*>/)![0];

    for (const attribute of ['align="left"', ':breakpoint="0"', 'outside-arrows', 'mobile-arrows', 'no-caps', 'active-color="primary"']) {
      expect(tag, attribute).toContain(attribute);
    }
  });
});
