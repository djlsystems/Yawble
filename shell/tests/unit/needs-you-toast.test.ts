import { describe, expect, it, vi } from 'vitest';

// B143 (card 613): the OS notification "N things waiting for you" names the newest item and its team (app.overview newest).
vi.mock('electron', () => ({ app: {}, Menu: {}, nativeImage: {}, Tray: class {} }));
const { needsYouToast } = await import('../../src/main/background');

describe('the needs-you notification', () => {
  it('names the newest item, its kind and its team', () => {
    expect(needsYouToast(3, { kind: 'question', title: 'Ship the release today?', team: 'payments', at: '2026-11-24T10:00:00Z' }))
      .toBe('3 things waiting for you. Newest: a question from payments: Ship the release today?');
    expect(needsYouToast(1, { kind: 'approval', title: 'api:twilio CreateMessage', team: 'Alpha' }))
      .toBe('1 thing waiting for you. Newest: a tool call to approve from Alpha: api:twilio CreateMessage');
    expect(needsYouToast(2, { kind: 'host', title: 'pypi.org', team: 'web' })).toBe('2 things waiting for you. Newest: a host to allow from web: pypi.org');
    expect(needsYouToast(2, { kind: 'tool', title: 'nodejs-lts', team: 'web' })).toBe('2 things waiting for you. Newest: a tool to install from web: nodejs-lts');
    expect(needsYouToast(2, { kind: 'folder', title: 'C:\\data', team: 'web' })).toBe('2 things waiting for you. Newest: a folder request from web: C:\\data');
  });

  it('leaves the team out when there is none, and cuts a long title', () => {
    expect(needsYouToast(2, { kind: 'question', title: 'Which?', team: null })).toBe('2 things waiting for you. Newest: a question: Which?');
    const long = 'x'.repeat(200);
    expect(needsYouToast(2, { kind: 'other', title: long, team: 'a' })).toBe(`2 things waiting for you. Newest: a request from a: ${'x'.repeat(120)}…`);
  });

  it('without a newest item (an older core, or only reconnects) lists the kinds as before', () => {
    const before = '2 things waiting for you (questions, approvals, hosts agents ask to reach, developer tools agents ask for, connections to reconnect).';
    expect(needsYouToast(2)).toBe(before);
    expect(needsYouToast(2, null)).toBe(before);
    expect(needsYouToast(1, null)).toBe(before.replace('2 things', '1 thing'));
  });
});
