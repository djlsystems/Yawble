import { watchPostEffect } from 'vue';
import { useRoute } from 'vue-router';
import { useConsoleStore } from '../stores/console';
import { consolePageName, titleFor } from '../router/pageTitle';

/**
 * THE TAB NAMES WHAT THE CONSOLE SHOWS: Teams, Kanban, or the team whose board is open. The router
 * names `/console` "Console" after every navigation; this runs after it (a post-flush effect) and on every
 * switch the store makes without one, such as the Concierge moving the current team.
 *
 * Only on `/console`: the Solutions addresses render the same page and keep their own names.
 */
export function useConsoleTitle() {
  const route = useRoute();
  // A Console mounted with no router (a component test) has no tab to name.
  if (!route) return;

  const board = useConsoleStore();
  watchPostEffect(() => {
    // Read every time, so a navigation re-runs it after the router's own title.
    void route.fullPath;
    if (route.path === '/console') document.title = titleFor(consolePageName(board.view, board.activeTeam?.name));
  });
}
