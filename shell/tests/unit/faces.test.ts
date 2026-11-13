// @vitest-environment happy-dom
import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import { avatarIndex, displayName, initials, memberStateOf, setAvatarSheet, tileStyle } from '../../src/renderer/src/faces/faces';
import MemberAvatar from '../../src/renderer/src/faces/MemberAvatar.vue';

describe('faces', () => {
  it('deals each member the same tile every time, ignoring case, and spreads members out', () => {
    expect(avatarIndex('payments', 'DeveloperPriya', 100)).toBe(avatarIndex('Payments', 'developerpriya', 100));
    const tiles = new Set(['Manager', 'DeveloperPriya', 'TesterWren', 'ResearcherAmara', 'DeveloperKofi'].map((m) => avatarIndex('payments', m, 100)));
    expect(tiles.size).toBeGreaterThan(3);
    expect(avatarIndex('payments', 'Manager', 0)).toBe(-1);
  });

  it('names and initials from member ids', () => {
    expect(initials('DeveloperPriya')).toBe('DP');
    expect(initials('Developer Priya')).toBe('DP');
    expect(initials('Manager')).toBe('M');
    expect(initials('')).toBe('?');
    expect(displayName('DeveloperPriya')).toBe('Developer Priya');
  });

  it('says the loudest state first', () => {
    expect(memberStateOf({ state: 'running', attention: 'decision' })).toBe('decision');
    expect(memberStateOf({ state: 'idle', attention: 'blocked' })).toBe('blocked');
    expect(memberStateOf({ state: 'idle', attention: 'failed' })).toBe('failed');
    expect(memberStateOf({ state: 'running', attention: null })).toBe('running');
    expect(memberStateOf({ state: 'queued' })).toBe('waiting');
    expect(memberStateOf({ state: 'idle' })).toBe('idle');
  });

  it('shows a tile of the sheet once it has loaded, initials before, and the state on the ring', () => {
    const before = mount(MemberAvatar, { props: { team: 'payments', member: 'DeveloperPriya', state: 'running' } });
    expect(before.text()).toBe('DP');
    expect(before.classes()).toContain('is-running');
    setAvatarSheet(null, 1024, 1024);
    const after = mount(MemberAvatar, { props: { team: 'payments', member: 'DeveloperPriya', state: 'decision' } });
    expect(after.find('.initials').exists()).toBe(false);
    expect(after.get('.face').attributes('style')).toContain('background-image');
    expect(after.attributes('data-state')).toBe('decision');
    expect(tileStyle(0, 10, 10).backgroundPosition).toMatch(/^0\.549\d*% 0\.549\d*%$/);
  });
});
