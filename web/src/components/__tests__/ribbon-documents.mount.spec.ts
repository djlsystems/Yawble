// @vitest-environment happy-dom
//
// DOCUMENTS ON BOTH RIBBON SURFACES, as a plain button.
//
// `lib/__tests__/ribbon.spec.ts` pins the CONSTANT: which tab the item is in, what its action is
// called, and that neither disable predicate applies to it. What it cannot see is whether the two
// components honour any of that.
//
// THE DRAWER IS TESTED BESIDE THE STRIP, NOT AFTER IT. A ribbon change is easy to ship on the
// desktop half only, and an affordance present on one surface and absent on the other teaches a
// person that its absence means something.
//
// ---
// WHY `api/documents` IS NOT MOCKED HERE. It is the only code that knows a URL; mocking it would
// make this spec assert that the component renders whatever the mock returns, in whatever shape,
// while the real paths could answer 404. A fixture chooses which half of a contract you can see.
//
// So this file proves only what it actually can:
// the item is on both surfaces, it is enabled with no active team, and it emits its action. The
// PATHS are pinned where they live, in `lib/__tests__/documents-api.spec.ts`, against no mock at
// all.
// ---
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import type { Component } from 'vue';

// The two ribbon badges fetch on a session watcher the moment `useRibbon()` runs. Stubbed rather
// than left to reach an unmocked `fetch`, which in happy-dom is a real socket and rejects AFTER
// the assertions - failing the FILE while every case in it passes.
vi.mock('../../lib/useAgentInstallations', () => ({
  useAgentInstallations: () => ({ badge: { value: null } }),
  refreshAgentInstallations: vi.fn(),
}));

import RibbonBar from '../RibbonBar.vue';
import RibbonMobileMenu from '../RibbonMobileMenu.vue';
import { DocumentsAction, TeamDocumentsAction } from '../../lib/ribbon';
import { useConsoleStore } from '../../stores/console';
import { asTeamId } from '../../api/types';
// Importing this module is what installs the Quasar plugin - it registers a `beforeAll` hook at
// MODULE level, so the import itself is the setup.
import '../../test/mountQuasar';

/**
 * The strip renders the item as a button, the drawer as a row. Same item, same state, two
 * controls - which is the pair this file exists to keep honest.
 *
 * THE COMPONENT NAME IS PART OF THE CASE. `QBtnDropdown`/`QExpansionItem` here would now match
 * nothing, and a finder that throws is the only reason that reads as a failure rather than as a
 * skipped assertion.
 */
const Surfaces: [string, Component, string][] = [
  ['RibbonBar', RibbonBar, 'QBtn'],
  ['RibbonMobileMenu', RibbonMobileMenu, 'QItem'],
];

beforeEach(() => {
  vi.clearAllMocks();
  setActivePinia(createPinia());
});

afterEach(() => {
  document.body.innerHTML = '';
});

async function render(component: Component): Promise<VueWrapper> {
  const wrapper = mount(component, { attachTo: document.body });
  await flushPromises();

  return wrapper as VueWrapper;
}

/**
 * Found by its TEXT rather than by position, so reordering the strip does not silently point this
 * file at the team switcher - which is the other control of the same type beside it.
 *
 * BY TEXT AND NOT BY THE `label` PROP, because the two surfaces disagree: the strip's QBtn takes
 * one and the drawer's QItem renders a child QItemLabel instead. Asking for a prop the drawer
 * never had is how the drawer half of a pair like this quietly stops being tested.
 *
 * `includes`, NOT `startsWith`. Material Icons are a LIGATURE FONT: the glyph is produced from the
 * icon's name as ordinary text inside the element, so this control's `textContent` reads
 * `folder_openDocuments` and every row in the drawer is prefixed by its own icon name. Nothing
 * renders wrongly and nothing logs; a `startsWith` finder simply matches nothing, and the failure
 * it produces - "no Documents QItem rendered" - describes a component that is on screen.
 */
function documentsControls(wrapper: VueWrapper, type: string) {
  return wrapper.findAllComponents({ name: type }).filter((candidate) => candidate.text().includes('Documents'));
}

/**
 * PROJECTS › DOCUMENTS, the last of the two. Active Team › Documents comes first on both surfaces
 * (the Active Team block is before Projects); it is the shortcut to the active team's folder, and
 * the cases below it are about the door to every folder.
 */
function documentsControl(wrapper: VueWrapper, type: string) {
  const found = documentsControls(wrapper, type).at(-1);

  if (!found) throw new Error(`no Documents ${type} rendered`);

  return found;
}

describe.each(Surfaces)('%s, with no active team', (_name, component, type) => {
  /**
   * THE GATING DECISION, MADE REAL IN THE TEMPLATE. No active-team gate: the documents root is the
   * TENANT's, so that gate would hide every other team's folder - including the ones whose team is gone, which matter most.
   *
   * No active team is seeded, deliberately: that is the state a fresh instance is in.
   */
  it('leaves Documents enabled, like the backlog beside it', async () => {
    const wrapper = await render(component);

    expect(documentsControl(wrapper, type).props('disable')).toBe(false);
  });

  /** Under PROJECTS beside Backlog, and not under the heading that names the active team. */
  it('renders Documents after Backlog rather than under the active team', async () => {
    const text = (await render(component)).text();

    expect(text).toContain('Documents');
    expect(text.lastIndexOf('Documents')).toBeGreaterThan(text.indexOf('Backlog'));
  });

  /**
   * ONE ARGUMENT. The row does not say WHOSE folder; the dialog chooses its own default.
   *
   * ASSERTED AS THE WHOLE EMITTED TUPLE rather than on its first element, so a second argument
   * creeping back reddens this rather than passing unnoticed.
   */
  it('emits the action alone, with no team beside it', async () => {
    const wrapper = await render(component);

    await documentsControl(wrapper, type).trigger('click');
    await flushPromises();

    expect(wrapper.emitted('action')).toEqual([[DocumentsAction]]);
  });

  /**
   * THE STRIP READS NOTHING. Fetching the folder list on every open would be a call per hover on
   * a control most people never use - and the list is the one the dialog fetches the moment it
   * opens.
   *
   * Pinned by the absence of a `fetch` rather than by mocking the module: with no mock at all, any
   * call the ribbon makes would reach happy-dom's real socket, and this is the assertion that says
   * it must not.
   */
  /**
   * THE SHORTCUT IS GATED; THE DOOR IS NOT. Active Team › Documents opens the active team's own
   * folder, so with no active team it is disabled - and Projects › Documents beside it is not.
   */
  it('disables Active Team › Documents with no active team, and enables it with one', async () => {
    const without = await render(component);
    const controls = documentsControls(without, type);
    expect(controls).toHaveLength(2);
    expect(controls[0]!.props('disable')).toBe(true);
    expect(controls[1]!.props('disable')).toBe(false);
    without.unmount();

    const board = useConsoleStore();
    board.teams = [{ id: 'alpha', name: 'Alpha' }] as never;
    board.activeTeamId = asTeamId('alpha');
    const withTeam = await render(component);
    const shortcut = documentsControls(withTeam, type)[0]!;
    expect(shortcut.props('disable')).toBe(false);

    await shortcut.trigger('click');
    await flushPromises();
    expect(withTeam.emitted('action')).toEqual([[TeamDocumentsAction]]);
  });

  it('makes no request of its own to draw the item', async () => {
    const fetching = vi.spyOn(globalThis, 'fetch');

    await render(component);

    const documents = fetching.mock.calls
      .map(([input]) => String(input))
      .filter((url) => url.includes('document'));

    expect(documents).toEqual([]);
  });
});
