/**
 * TEAM CONNECTIONS: the member every team has, running the platform's built-in plugin
 * (`TeamConnectionsMember` in the Host). It has no settings of its own - its settings icon opens
 * Team settings -> Connections - and nobody removes it, so it has no bin. The board shows it only
 * while the team has a connection, because the Host's snapshot list leaves it out otherwise.
 */
import type { ContainerSnapshot, InstalledPlugin } from '../api/types';

/** What it runs. */
export const TEAM_CONNECTIONS_AGENT = 'plugin:team-connections';

/** Whether `snapshot` is a team's Team Connections member: by what it runs, which nobody can change. */
export function isTeamConnections(snapshot: Pick<ContainerSnapshot, 'agent'>): boolean {
  return snapshot.agent === TEAM_CONNECTIONS_AGENT;
}

/** Whether a plugin is the platform's own: never offered in Add member or the Marketplace, never removed. */
export function isBuiltInPlugin(plugin: Pick<InstalledPlugin, 'builtIn'>): boolean {
  return plugin.builtIn === true;
}
