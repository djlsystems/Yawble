// @vitest-environment happy-dom
//
// THE COMMAND PALETTE: Ctrl+K (and `/`) opens it from the board and never from inside a text field;
// Enter on each kind of row lands where the row says; Escape closes it. The backlog list is read
// when it opens and kept while it is open. Matching itself is `lib/__tests__/palette.spec.ts`.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const { backlogItems, listLiveOutcomes, follow } = vi.hoisted(() => ({
  backlogItems: vi.fn(),
  listLiveOutcomes: vi.fn(),
  follow: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  backlogItems,
}));

vi.mock('../../api/outcomes', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listLiveOutcomes,
}));

vi.mock('../../lib/consoleLinks', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  follow,
}));

import CommandPalette from '../CommandPalette.vue';
import { useConsoleStore } from '../../stores/console';
import { useKanbanStore } from '../../stores/kanban';
import { useWorkflowNavStore } from '../../stores/workflowNav';
import { memberHref } from '../../lib/consoleLinks';
import { itemLabel } from '../../lib/backlog';
import { resetBody } from '../../test/mountQuasar';
import type { ContainerSnapshot, Team, TeamId } from '../../api/types';

function member(team: string, id: string, name = id): ContainerSnapshot {
  return {
    team: team as TeamId,
    id,
    name,
    agent: 'claude',
    state: 'Idle',
    queueDepth: 0,
    ceiling: 1,
    subscribes: [],
    currentCorrelation: null,
  } as unknown as ContainerSnapshot;
}

function team(id: string, name: string, members: ContainerSnapshot[]): Team {
  return {
    id: id as TeamId,
    name,
    concierge: null,
    memberAgents: null,
    additionalInstructions: null,
    root: null,
    containers: members,
  } as Team;
}

const alpha = team('alpha', 'Alpha', [member('alpha', 'Manager'), member('alpha', 'mailer', 'Mail Sorter')]);
const beta = team('beta', 'Beta Squad', [member('beta', 'Manager')]);

const items = [
  { id: 17, title: 'Sort the inbound mail', state: 'ready', team: null, teamName: null, teamGone: false, body: '' },
  { id: 545, title: 'Index the archive', state: 'pending', team: null, teamName: null, teamGone: false, body: '' },
];

let wrapper: VueWrapper | null = null;
let board: HTMLButtonElement;

async function mountPalette(activeTeamId: TeamId | '' = 'alpha' as TeamId) {
  setActivePinia(createPinia());
  const console_ = useConsoleStore();
  console_.$patch({ teams: [alpha, beta], activeTeamId, overviewLanded: true, view: 'board' });

  // A control on the board, where focus sits when nobody is typing.
  board = document.createElement('button');
  board.textContent = 'a member card';
  document.body.appendChild(board);

  wrapper = mount(CommandPalette, {
    attachTo: document.body,
    props: {
      open: false,
      'onUpdate:open': (value: boolean) => wrapper?.setProps({ open: value }),
    },
  });
  await flushPromises();
  return wrapper;
}

/** Whether the palette is open, as its `open` model says. */
function isOpen(palette: VueWrapper): boolean | undefined {
  return (palette.props() as { open?: boolean }).open;
}

function press(target: EventTarget, init: KeyboardEventInit) {
  const event = new KeyboardEvent('keydown', { bubbles: true, cancelable: true, ...init });
  target.dispatchEvent(event);
  return event;
}

function input(): HTMLInputElement {
  const found = document.body.querySelector<HTMLInputElement>('[data-test="command-palette-input"] input, input[data-test="command-palette-input"]');
  if (!found) throw new Error('the palette has no text box');
  return found;
}

function rows(): string[] {
  return [...document.body.querySelectorAll('[role="option"]')].map((row) => row.getAttribute('data-key') ?? '');
}

async function type(text: string) {
  const box = input();
  box.value = text;
  box.dispatchEvent(new Event('input', { bubbles: true }));
  await flushPromises();
}

async function enter() {
  press(input(), { key: 'Enter' });
  await flushPromises();
}

async function openWithShortcut() {
  press(board, { key: 'k', ctrlKey: true });
  await flushPromises();
}

beforeEach(() => {
  backlogItems.mockReset().mockResolvedValue(items);
  listLiveOutcomes.mockReset().mockResolvedValue([{ id: 'o-1', name: 'Faster replies', status: 'active' }]);
  follow.mockReset();
  vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('no network in this spec'))));
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = null;
  vi.unstubAllGlobals();
  resetBody();
});

describe('the shortcut', () => {
  it('Ctrl+K from the board opens the palette, and reads the backlog once as it opens', async () => {
    const palette = await mountPalette();
    expect(isOpen(palette)).toBe(false);

    const event = press(board, { key: 'k', ctrlKey: true });
    await flushPromises();

    expect(event.defaultPrevented).toBe(true);
    expect(isOpen(palette)).toBe(true);
    expect(input()).toBeTruthy();
    expect(backlogItems).toHaveBeenCalledTimes(1);

    // Cached while open: typing reads nothing again.
    await type('mail');
    await type(itemLabel(17));
    expect(backlogItems).toHaveBeenCalledTimes(1);
  });

  it('Cmd+K and a bare slash open it from the board too', async () => {
    const palette = await mountPalette();
    press(board, { key: 'k', metaKey: true });
    await flushPromises();
    expect(isOpen(palette)).toBe(true);

    await palette.setProps({ open: false });
    await flushPromises();
    press(board, { key: '/' });
    await flushPromises();
    expect(isOpen(palette)).toBe(true);
  });

  it('does not open from inside a text field, a textarea or contenteditable', async () => {
    const palette = await mountPalette();
    const field = document.createElement('input');
    const area = document.createElement('textarea');
    const editable = document.createElement('div');
    editable.setAttribute('contenteditable', 'true');
    document.body.append(field, area, editable);

    for (const target of [field, area, editable]) {
      for (const key of [{ key: '/' }, { key: 'k', ctrlKey: true }]) {
        const event = press(target, key);
        await flushPromises();
        expect(event.defaultPrevented).toBe(false);
        expect(isOpen(palette)).toBe(false);
      }
    }
    expect(backlogItems).not.toHaveBeenCalled();
  });
});

describe('Escape closes it', () => {
  it('Escape in the box closes the palette', async () => {
    const palette = await mountPalette();
    await openWithShortcut();
    expect(isOpen(palette)).toBe(true);

    press(input(), { key: 'Escape' });
    await flushPromises();
    expect(isOpen(palette)).toBe(false);
  });
});

describe('Enter on each kind of row lands where it says', () => {
  it('a team opens its board', async () => {
    const palette = await mountPalette();
    const console_ = useConsoleStore();
    console_.view = 'kanban';
    await openWithShortcut();
    await type('beta squad');
    expect(rows()[0]).toBe('team:beta');

    await enter();
    expect(console_.view).toBe('board');
    expect(console_.activeTeamId).toBe('beta');
    expect(isOpen(palette)).toBe(false);
  });

  it('a backlog item, found by its citation, opens the item', async () => {
    await mountPalette();
    await openWithShortcut();
    await type(itemLabel(545).toLowerCase());
    expect(rows()).toEqual(['item:545']);
    expect(document.body.textContent).toContain(itemLabel(545));
    expect(document.body.textContent).toContain('pending');

    await enter();
    const nav = useWorkflowNavStore();
    expect(nav.backlogOpen).toBe(true);
    expect(nav.backlogItem).toBe(545);
  });

  it('a member, as team / member, follows the member deep link', async () => {
    await mountPalette();
    await openWithShortcut();
    await type('alpha sorter');
    expect(rows()).toEqual(['member:alpha/mailer']);
    expect(document.body.textContent).toContain('Alpha / Mail Sorter');

    await enter();
    expect(follow).toHaveBeenCalledWith(memberHref('alpha', 'mailer'));
  });

  it('the views show Teams, Kanban and Monitor', async () => {
    await mountPalette();
    const console_ = useConsoleStore();
    const kanban = useKanbanStore();

    await openWithShortcut();
    await type('monitor');
    await enter();
    expect(console_.view).toBe('monitor');

    await openWithShortcut();
    await type('kanban');
    expect(rows()[0]).toBe('view:kanban');
    await enter();
    expect(console_.view).toBe('kanban');
    expect(kanban.filters.team).toBe('alpha');

    await openWithShortcut();
    await type('teams');
    expect(rows()[0]).toBe('view:teams');
    await enter();
    expect(console_.view).toBe('teams');
  });

  it('a ribbon command runs as the ribbon runs it, and a team- command is not offered without a team', async () => {
    const palette = await mountPalette();
    await openWithShortcut();
    await type('new team');
    await enter();
    expect(palette.emitted('action')).toEqual([['admin-new-team']]);

    await openWithShortcut();
    await type('reset');
    expect(rows()).toEqual(['action:team-reset']);
    palette.unmount();
    wrapper = null;
    resetBody();

    await mountPalette('');
    await openWithShortcut();
    await type('reset');
    expect(rows()).toEqual([]);
    expect(document.body.textContent).toContain('Nothing matches.');
  });

  it('an outcome opens the outcome', async () => {
    const palette = await mountPalette();
    await openWithShortcut();
    await type('faster');
    expect(rows()).toEqual(['outcome:o-1']);
    await enter();
    expect(palette.emitted('outcome')).toEqual([['o-1']]);
  });

  it('arrows move the row Enter opens', async () => {
    const palette = await mountPalette();
    await openWithShortcut();
    await type('manager');
    expect(rows()).toEqual(['member:alpha/Manager', 'member:beta/Manager']);

    press(input(), { key: 'ArrowDown' });
    await flushPromises();
    expect(document.body.querySelector('[aria-selected="true"]')?.getAttribute('data-key')).toBe('member:beta/Manager');
    await enter();
    expect(follow).toHaveBeenCalledWith(memberHref('beta', 'Manager'));
    expect(palette.emitted('action')).toBeUndefined();
  });
});
