import { computed, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useConsoleStore } from '../stores/console';
import { useKanbanStore } from '../stores/kanban';
import { placeFromQuery, placeOf, queryFor, samePlace, type ConsolePlace } from '../lib/consolePlace';

/**
 * THE BROWSER'S BACK AND FORWARD MOVE BETWEEN THE CONSOLE'S VIEWS. The Teams table, a team's board
 * and the Kanban were switched in the store alone, so the address never changed and Back left the
 * Console altogether. Now each switch a person makes is one history entry (`?view=teams`,
 * `?team=<id>`, `?view=kanban`), and an address that names another place is applied through the
 * store's own actions - the ones the tabs call - so the remembered tabs and the team the Concierge
 * follows move exactly as a click moves them.
 *
 * TWO WATCHES AND NO FLAG. The store's place changing writes the address when they differ; the
 * address changing applies it to the store when they differ. Each write makes the two equal, so the
 * other watch sees nothing to do. Writing the address the first time, or correcting it, REPLACES:
 * a reload or a team that is gone adds no step to go back through.
 *
 * Only on `/console`: the same page renders under the Solutions addresses, which are their own.
 */
export function useConsoleHistory() {
  const route = useRoute();
  const router = useRouter();

  // A Console mounted with no router (a component test) has no address to keep.
  if (!route || !router) return;

  const board = useConsoleStore();
  const kanban = useKanbanStore();

  const onConsole = () => route.path === '/console';
  const storePlace = computed(() => placeOf(board.view, board.activeTeamId));
  let written = false;

  // A MOVE THE PERSON DID NOT MAKE WRITES NO ADDRESS. The Concierge switching the current team, or
  // the board correcting a team that is gone, moves the store too - and any change of address
  // closes every open dialog, so writing one there would close a dialog a person is typing in. The
  // address is left as it was; the person's next switch writes theirs.
  const platformActions = new Set(['applyCurrentTeam', 'applyPendingCurrentTeam', 'reconcileActiveTeam']);
  let platformPlace: ConsolePlace | null | undefined;
  board.$onAction(({ name, after }) => {
    if (!platformActions.has(name)) return;
    const before = storePlace.value;
    after(() => {
      if (!samePlace(before, storePlace.value)) platformPlace = storePlace.value;
    });
  });

  function write(place: ConsolePlace, replace: boolean) {
    const target = { path: '/console', query: queryFor(place, route.query) };
    void (replace ? router.replace(target) : router.push(target));
  }

  function apply(place: ConsolePlace) {
    if (place.kind === 'teams') {
      board.showTeamsView();
      return;
    }

    if (place.kind === 'kanban') {
      board.view = 'kanban';
      board.rememberTabs();
      void kanban.load();
      return;
    }

    // A TEAM IS ONLY KNOWN ONCE THE LIST HAS LANDED; the watch on it below applies it then.
    if (!board.overviewLanded) return;

    if (board.choosableTeams.some((team) => team.id === place.team)) {
      board.showBoard();
      board.setActiveTeam(place.team as typeof board.activeTeamId);
      return;
    }

    // GONE, OR NEVER THIS BROWSER'S: the Teams table, and the address corrected rather than kept.
    board.showTeamsView();
    write({ kind: 'teams' }, true);
  }

  watch(
    storePlace,
    (place) => {
      if (!onConsole() || place === null) return;
      // Not before the first write: the place the board opens on is written once, replacing.
      if (written && platformPlace !== undefined && samePlace(place, platformPlace)) {
        platformPlace = undefined;
        return;
      }
      platformPlace = undefined;
      if (samePlace(place, placeFromQuery(route.query))) {
        written = true;
        return;
      }
      // AN ADDRESS THAT NAMES A PLACE IS APPLIED BEFORE IT IS EVER WRITTEN OVER: an opened bookmark
      // waits for the team list, and the reload's restored tabs must not replace it meanwhile.
      if (!written && placeFromQuery(route.query) !== null) return;
      write(place, !written);
      written = true;
    },
    { immediate: true },
  );

  watch(
    () => [route.path, route.query] as const,
    () => {
      if (!onConsole()) return;
      const wanted = placeFromQuery(route.query);
      if (wanted === null) {
        if (storePlace.value !== null) write(storePlace.value, true);
        return;
      }
      if (samePlace(wanted, storePlace.value)) written = true;
      else apply(wanted);
    },
    { immediate: true },
  );

  watch(
    () => board.overviewLanded,
    (landed) => {
      if (!landed || !onConsole()) return;
      const wanted = placeFromQuery(route.query);
      if (wanted !== null && !samePlace(wanted, storePlace.value)) apply(wanted);
    },
  );
}
