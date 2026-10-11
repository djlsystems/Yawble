import { watch } from 'vue';
import { storeToRefs } from 'pinia';
import { useConsoleStore } from '../stores/console';
import { useSessionStore } from '../stores/session';
import { AgentsAction } from './agentInstall';
import { refreshAgentInstallations, useAgentInstallations } from './useAgentInstallations';
import { NoTeamHint, Ribbon, needsActiveWorkTeam, type RibbonItem, type RibbonTab } from './ribbon';

/**
 * Shared ribbon state for the desktop strip and the mobile drawer.
 *
 * Both surfaces render the SAME ribbon - `Ribbon`, the one constant in `lib/ribbon.ts`. A
 * hand-written list of menu items would be a second source of truth for the toolbar. The
 * disabled rule lives here for the same reason: `team-...` must mean the same thing in the
 * hamburger as it does on the strip. There is no tier rule: every signed-in person is an
 * administrator, so every item is enabled the moment there is a session.
 */
export function useRibbon() {
  const board = useConsoleStore();
  const { choosableTeams, activeTeamId, activeWorkTeamId } = storeToRefs(board);
  const session = useSessionStore();

  const ribbon = Ribbon;

  /**
   * The Agent prerequisite mark, shared by both surfaces.
   *
   * WATCHED RATHER THAN FETCHED ON MOUNT, because the session arrives asynchronously: a fetch fired
   * at mount would ask as an anonymous caller on a cold load, get a 401, and settle on an empty
   * list - so the badge would be permanently absent for the person it is for, with nothing
   * failing. `immediate` covers the already-signed-in case, where there is no transition left to
   * observe.
   */
  const { badge } = useAgentInstallations();

  watch(
    () => session.user !== null,
    (signedIn) => {
      if (!signedIn) return;

      void refreshAgentInstallations();
    },
    { immediate: true },
  );

  /**
   * The heading inside a block, above its items - distinct from the small label underneath, which
   * names the block itself and never changes. For the active-team block the heading is the team, so
   * the commands under it never point at something you have to remember; the label under it still
   * reads "Active Team", so the block is identifiable when no team exists at all.
   */
  function titleOf(tab: RibbonTab) {
    // The LABEL, not the id - this heading is the one place the active team is named to a person, so
    // it is the one place a rename has to show. `activeTeamLabel` falls back to the id while the team
    // list is empty, which is every frame before the first overview lands.
    if (tab.titleFrom === 'active-team') return board.activeTeamLabel || tab.title || '';

    return tab.title ?? '';
  }

  /**
   * A command that acts on the active team cannot do anything while there is none - a fresh instance
   * has no teams at all, and Settings opening onto an empty dialog is worse than a button that says
   * it is unavailable. Named by prefix so a new `team-` command in the file inherits the rule.
   */
  function needsTeam(item: RibbonItem) {
    return needsActiveWorkTeam(item.action, activeWorkTeamId.value);
  }

  /** The item's own description, or why it is disabled. The only reason an item is ever
   *  disabled is a missing active team, and a greyed button that does not say so reads as broken:
   *  the heading above it says "No team", which names the state but not the way out. */
  function tooltipFor(item: RibbonItem) {
    return needsTeam(item) ? NoTeamHint : item.tooltip;
  }

  /**
   * The badge for ONE ribbon item, or null.
   *
   * IT IS KEYED ON THE ACTION, which is a name this repository writes down twice - the `Ribbon`
   * constant and MainLayout's handler. Naming an ACTION is nothing like naming an Agent: an action
   * is the ribbon's own vocabulary, where a preset belongs to whoever edits the catalog and may be
   * relabelled at any time.
   *
   * Both surfaces call this. A mark on the strip and none in the hamburger is worse than no mark
   * at all, because it teaches a
   * person that the absence of one means something.
   */
  function badgeFor(item: RibbonItem) {
    // Built-ins come from the build, so there is no seed drift to mark: the only badge
    // is the install mark on `admin-agents`.
    if (item.action === AgentsAction) return badge.value;

    return null;
  }

  return {
    board,
    teams: choosableTeams,
    activeTeamId,
    activeWorkTeamId,
    ribbon,
    titleOf,
    needsTeam,
    tooltipFor,
    badgeFor,
  };
}
