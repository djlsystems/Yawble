import { describe, expect, it } from 'vitest';
import { nextHubReconnectDelayMs } from '../hub';

describe('hub reconnect policy', () => {
  it('keeps SignalR trying after the default budget is exhausted', () => {
    expect(nextHubReconnectDelayMs(0)).toBe(0);
    expect(nextHubReconnectDelayMs(1)).toBe(2000);
    expect(nextHubReconnectDelayMs(2)).toBe(10000);
    expect(nextHubReconnectDelayMs(3)).toBe(30000);

    // The built-in policy would stop here and leave the board stale until reload. This one keeps
    // asking the transport to reconnect, while authority-level JoinTeam failures still surface
    // through `onJoinFailed` instead of looping here.
    expect(nextHubReconnectDelayMs(4)).toBe(5000);
    expect(nextHubReconnectDelayMs(9)).toBe(5000);
  });
});
