// @vitest-environment happy-dom
//
// NEW TEAM FITS THE WINDOW: General with two agents and the instructions box shows whole, with no
// scroll bar, at 1280x783 and the common laptop sizes. The panels took the window's height less a
// fixed 15rem, which is more than the dialog's own title, tabs and buttons need, so General
// scrolled with room to spare. Now the card is a column that gives the panels whatever its title,
// tabs and buttons leave, and the instructions box opens shorter; below that room the panel still
// scrolls inside the dialog.
//
// happy-dom lays nothing out, so this pins the shape that makes it fit; the measured heights at
// each window size were taken in a browser.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

const { fileSystemRoots, getInstanceId, listCatalog } = vi.hoisted(() => ({
  getInstanceId: vi.fn(),
  fileSystemRoots: vi.fn(),
  listCatalog: vi.fn(),
}));

vi.mock('quasar', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useQuasar: () => ({ notify: vi.fn() }),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getInstanceId,
  fileSystemRoots,
  listCatalog,
}));

import CreateTeamDialog from '../CreateTeamDialog.vue';
import { useConsoleStore } from '../../stores/console';
import { useSessionStore } from '../../stores/session';
import { remember } from '../../lib/newTeamDefaults';
import { mountDialog, resetBody } from '../../test/mountQuasar';

beforeEach(() => {
  localStorage.clear();
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('unexpected fetch')));
  getInstanceId.mockResolvedValue('instance-a');
  fileSystemRoots.mockResolvedValue({ roots: [] });
  listCatalog.mockResolvedValue({
    agents: [{ name: 'claude-headless', mode: 'Headless' }, { name: 'codex-headless', mode: 'Headless' }],
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function open() {
  remember({ managerAgent: 'claude-headless', memberAgents: ['claude-headless', 'codex-headless'], root: null, repos: [] }, 'instance-a');
  setActivePinia(createPinia());
  useConsoleStore().$patch({ teams: [], overviewLanded: true });
  useSessionStore().$patch({ user: { id: 'u1', email: 'admin@example.com' } });

  return mountDialog(CreateTeamDialog, {}, { pinia: false });
}

/** The component's own scoped rules, as written. */
function scopedStyle(): string {
  const source = readFileSync(join(import.meta.dirname, '..', 'CreateTeamDialog.vue'), 'utf8');

  return source.slice(source.lastIndexOf('<style scoped>'));
}

function rule(css: string, selector: string): string {
  // At the start of a line: `.create-panels {` is also the tail of a longer selector.
  const at = css.indexOf(`\n${selector} {`);
  if (at < 0) throw new Error(`no ${selector} rule`);

  return css.slice(at, css.indexOf('}', at)).replace(/\s+/g, ' ');
}

describe('New team fits the window', () => {
  it('is a column whose panels take what the title, tabs and buttons leave', async () => {
    await open();

    const panels = document.querySelector('.create-panels')!;
    const card = panels.closest('.q-card')!;
    expect(card.classList.contains('create-card')).toBe(true);
    expect(panels.parentElement!.tagName).toBe('FORM');
    expect(panels.parentElement!.parentElement).toBe(card);

    const css = scopedStyle();
    expect(rule(css, '.create-card')).toContain('display: flex');
    expect(rule(css, '.create-card')).toContain('flex-direction: column');
    expect(rule(css, '.create-card > form')).toContain('display: flex');
    expect(rule(css, '.create-card > form')).toContain('min-height: 0');
    // Shrinks to the room left, and no longer by the fixed window-height reserve of `.os-tab-panels`.
    expect(rule(css, '.create-panels')).toContain('flex: 0 1 auto');
    expect(rule(css, '.create-panels')).toContain('max-height: none');
  });

  it('opens the instructions box five lines tall, so General fits with two agents listed', async () => {
    await open();

    expect(document.querySelectorAll('.create-panels .q-list .q-item')).toHaveLength(2);
    const instructions = document.querySelector<HTMLTextAreaElement>('[data-section="team-instructions"] textarea')!;
    expect(instructions.style.minHeight).toBe('5em');
  });
});
