import { describe, expect, it, vi } from 'vitest';

const subscribed: string[] = [];

vi.mock('@microsoft/signalr', () => {
  const connection = {
    on: (name: string) => subscribed.push(name),
    onreconnecting: () => undefined,
    onreconnected: () => undefined,
    onclose: () => undefined,
    start: () => Promise.resolve(),
    invoke: () => Promise.resolve(),
  };
  class HubConnectionBuilder {
    withUrl() {
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    build() {
      return connection;
    }
  }
  return { HubConnectionBuilder };
});

import { connectHub } from '../hub';

/**
 * Two windows on one instance - a duplicated tab, another device - never move each other. Every
 * window still tells the server which team it is on, so the Concierge reads the newest click, but
 * no window listens for that write coming back from another.
 */
describe('one window never moves another', () => {
  it('subscribes to no current-team announcement', async () => {
    const nothing = () => undefined;
    await connectHub(
      {
        onContainerChanged: nothing,
        onReconnected: nothing,
        onConnectedChanged: nothing,
        onJoinFailed: nothing,
        onTeamChanged: nothing,
        onTeamCreated: nothing,
        onTeamDeleted: nothing,
        onKanbanChanged: nothing,
        onCapacityChanged: nothing,
      },
      () => '',
    );

    expect(subscribed).toContain('containerChanged');
    expect(subscribed).not.toContain('currentTeamChanged');
  });
});
