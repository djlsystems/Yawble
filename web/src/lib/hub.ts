import * as signalR from '@microsoft/signalr';
import type { CapacitySample, ContainerSnapshot, Team } from '../api/types';

const DefaultReconnectDelaysMs = [0, 2000, 10000, 30000] as const;
const PostBudgetReconnectDelayMs = 5000;

export interface HubHandlers {
  onContainerChanged: (snapshot: ContainerSnapshot) => void;

  /**
   * Fired after the connection comes back. The caller must RESYNCHRONISE here -
   * see the note below, it is the whole reason this callback exists.
   */
  onReconnected: () => void;

  onConnectedChanged: (connected: boolean) => void;

  /**
   * A JoinTeam invoke failed - during the initial connect or after a reconnect. Unlike a missed
   * `containerChanged` push, which the next change repairs on its own, nothing retries this: the
   * connection stays open and reports "live" while the group for this team was never joined, so
   * the board silently stops updating. That is indistinguishable from a team where nothing is
   * happening, which is the exact symptom per-team groups exist to remove - so the caller must
   * turn this into something the person looking at the board can see.
   */
  onJoinFailed: () => void;

  /**
   * This PERSON's current team moved - from another device, or from
   * `harness team switch` inside the Concierge.
   *
   * `team` is null for none. Delivered to the person's own group rather than a team's, because the
   * teams it moves between are exactly the groups this connection may not be in.
   */
  onCurrentTeamChanged: (change: { team: string | null; name: string | null }) => void;

  /** A team summary changed - pause/resume and any other team-scoped update that must repaint live. */
  onTeamChanged: (team: Team) => void;

  /** A team entered this person's visible list. The browser refetches the list rather than trusting a second copy. */
  onTeamCreated: (change: { team: string }) => void;

  /** A team left this person's visible list. Delivered to the person's own group, never a team group. */
  onTeamDeleted: (change: { team: string }) => void;

  /**
   * The kanban projection moved - a card was created, changed lane, or was edited by a human.
   *
   * OPTIONAL, AND THAT IS THE MODULE SEAM. The board plugs into the hub the console already has
   * rather than opening a second one; a build with the kanban module removed simply passes no
   * handler, and a Host that predates the event never raises it. Either way this file behaves
   * exactly as it did before.
   *
   * `team` is null for a change that is not scoped to one team. The payload deliberately carries
   * no card: the board is a projection and the client refetches it, so a push is a nudge rather
   * than a second delivery of the state.
   */
  onKanbanChanged?: (change: { team: string | null }) => void;

  /**
   * One capacity sample, every few seconds, to people only (the Host pushes it to its people group,
   * which a machine principal never joins). OPTIONAL by the same seam as `onKanbanChanged`.
   */
  onCapacityChanged?: (sample: CapacitySample) => void;
}

/**
 * One multiplexed connection carrying every container's activity.
 *
 * A browser allows about six concurrent connections per origin, and a stream per
 * container would exhaust that as soon as a team got interesting - which is the
 * argument for a hub rather than per-AC streams.
 *
 * The hub carries NOTHING inbound except which group a connection belongs to.
 * Instructions still go through the API, so there is one way IN; joining only
 * decides which of the API's own outputs this socket is allowed to overhear.
 *
 * @param activeTeam Read at connect time and again after every reconnect, rather
 * than taken once as a fixed string - a caller can switch teams while the
 * connection is live, and the value at construction would go stale the first
 * time that happened.
 */
export async function connectHub(
  handlers: HubHandlers,
  activeTeam: () => string,
): Promise<signalR.HubConnection> {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl('/hub/containers')
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: (context) =>
        nextHubReconnectDelayMs(context.previousRetryCount),
    })
    .build();

  // Caught here rather than left to propagate: a rejection out of connectHub would abandon the
  // CONNECTION too (the caller's `await connectHub(...)` never resolves into a handle it can hold
  // or later stop), for a failure that is specifically about the JOIN. handlers.onJoinFailed is
  // what turns this into something visible instead of an unhandled rejection nobody sees.
  const join = async (team: string): Promise<void> => {
    try {
      await joinTeam(connection, team);
    } catch {
      handlers.onJoinFailed();
    }
  };

  // Registered BEFORE start(). A handler added afterwards can miss messages that
  // arrive during the handshake.
  connection.on('containerChanged', handlers.onContainerChanged);
  connection.on('currentTeamChanged', handlers.onCurrentTeamChanged);
  connection.on('teamChanged', handlers.onTeamChanged);
  connection.on('teamCreated', handlers.onTeamCreated);
  connection.on('teamDeleted', handlers.onTeamDeleted);

  // Registered only when the caller wants it, so a build without the kanban module subscribes to
  // nothing extra and a Host without the event raises nothing this listens for.
  if (handlers.onKanbanChanged) connection.on('kanbanChanged', handlers.onKanbanChanged);
  if (handlers.onCapacityChanged) connection.on('capacityChanged', handlers.onCapacityChanged);

  connection.onreconnecting(() => handlers.onConnectedChanged(false));

  connection.onclose(() => handlers.onConnectedChanged(false));

  // withAutomaticReconnect restores the CONNECTION, never the snapshots missed
  // while it was down - and group membership lives on the connection ID, which
  // automatic reconnect replaces. Without rejoining here, a laptop that slept
  // wakes reporting "live" while receiving nothing at all: the same silent-stale
  // failure the resync below exists for, one layer lower.
  connection.onreconnected(async () => {
    await join(activeTeam());
    await joinMe(connection);
    handlers.onConnectedChanged(true);
    handlers.onReconnected();
  });

  await connection.start();

  // Joining is an AUTHORITY decision on the server, so a fresh
  // connection holds no group membership at all until this succeeds.
  await join(activeTeam());
  await joinMe(connection);
  handlers.onConnectedChanged(true);

  return connection;
}

/**
 * Joins the group this PERSON's own pushes go to. Rejoined after a reconnect for the same reason
 * team groups are: group membership lives on the connection id, which automatic reconnect replaces.
 *
 * SWALLOWED rather than reported. This carries a convenience - the tab strip following a switch
 * made somewhere else - and a person whose socket cannot join it still has a working board, where
 * a failed TEAM join means the board silently stops updating. Different failures, different noise.
 */
async function joinMe(connection: signalR.HubConnection): Promise<void> {
  try {
    await connection.invoke('JoinMe');
  } catch {
    // Nothing. See above.
  }
}

/**
 * Joins the group a team's snapshots are published to. A no-op for an empty team
 * name - a fresh instance with no teams yet has nothing to join.
 *
 * Left to reject rather than caught here: this function has no opinion on what a failure means to
 * the person looking at the board, so every caller in this module routes it through `join`'s catch
 * above or `boardStore.switchHubTeam`'s, both of which do.
 */
export async function joinTeam(connection: signalR.HubConnection, team: string): Promise<void> {
  if (!team) return;
  await connection.invoke('JoinTeam', team);
}

/** The other half of a team switch. Only narrows what a connection receives, so
 * there is no authority check to fail on the server and none to anticipate here. */
export async function leaveTeam(connection: signalR.HubConnection, team: string): Promise<void> {
  if (!team) return;
  await connection.invoke('LeaveTeam', team);
}

/**
 * SignalR's default reconnect budget ends after 0s, 2s, 10s and 30s. That is a sensible first
 * sweep and a bad terminal state for a board whose whole point is to stay live: a Host restart
 * longer than forty-two seconds leaves the browser permanently stale until a reload nobody is told
 * to do. So we keep the default cadence, then continue every few seconds until the transport
 * returns. This changes only TRANSPORT recovery - an authority-level `JoinTeam` refusal still
 * routes through `onJoinFailed` above and never becomes a reconnect loop.
 */
export function nextHubReconnectDelayMs(previousRetryCount: number): number {
  return DefaultReconnectDelaysMs[previousRetryCount] ?? PostBudgetReconnectDelayMs;
}
