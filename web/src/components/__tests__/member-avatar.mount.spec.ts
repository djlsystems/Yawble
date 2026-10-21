// @vitest-environment happy-dom
//
// THE MEMBER'S AVATAR AND ITS STATE RING ON THE BOARD AND THE KANBAN. The member card wears it at
// its upper left in place of the old drag icon, as the handle that moves the card on a board and a
// plain face off one; the Kanban card of a member wears the same tile at its upper left and none
// when nobody is assigned. The ring is the member's state, in its token; a plugin is a diamond of
// initials; and before the sheet loads an agent shows its initials.
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { mount, type VueWrapper } from '@vue/test-utils';

import MemberAvatar from '../MemberAvatar.vue';
import ContainerCard from '../ContainerCard.vue';
import KanbanCardComponent from '../KanbanCard.vue';
import { avatarIndex, setAvatarSheet } from '../../lib/avatars';
import { useConsoleStore } from '../../stores/console';
import { useKanbanStore } from '../../stores/kanban';
import type { KanbanCard } from '../../api/kanban';
import { asMemberId, asTeamId, type ContainerSnapshot, type Team } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

const snapshot = (over: Partial<ContainerSnapshot> = {}): ContainerSnapshot => ({
  team: asTeamId('alpha'),
  id: asMemberId('Developer'),
  name: 'Developer',
  agent: 'echo',
  state: 'Idle',
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
  ...over,
});

const card = (over: Partial<KanbanCard> = {}): KanbanCard => ({
  id: '4100_Developer',
  workflowSeq: 4100,
  team: 'alpha',
  member: 'Developer',
  item: null,
  title: 'Bind the domain',
  body: '',
  status: 'running',
  laneId: 'todo',
  progress: [],
  createdAt: '2026-10-16T01:00:00Z',
  updatedAt: '2026-10-16T01:31:00Z',
  awaitingManager: false,
  paused: false,
  ...over,
});

let wrapper: VueWrapper | undefined;

beforeEach(() => {
  setActivePinia(createPinia());
  setAvatarSheet(null, 1024, 1024);
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  setAvatarSheet(null, 0, 0);
  resetBody();
});

const avatar = () => wrapper!.get('[data-test="member-avatar"]');

describe('the avatar', () => {
  it('wears the member\'s own tile in its state ring, in the state\'s token', () => {
    wrapper = mount(MemberAvatar, { props: { team: 'alpha', member: 'Developer', name: 'Developer', state: 'running' } });
    expect(avatar().attributes('data-avatar')).toBe(String(avatarIndex('alpha', 'Developer', 100)));
    expect(avatar().classes()).toContain('member-avatar--running');
    expect(avatar().attributes('style')).toContain('--avatar-ring: var(--os-state-running)');
    expect(wrapper.get('.member-avatar-face').attributes('style')).toContain('/avatars/avatars.jpg');
  });

  it('draws needs you in the needs-you token', () => {
    wrapper = mount(MemberAvatar, { props: { team: 'alpha', member: 'Developer', name: 'Developer', state: 'decision' } });
    expect(avatar().attributes('style')).toContain('var(--os-state-needs-you)');
  });

  it('is a diamond of initials in the same ring for a plugin', () => {
    wrapper = mount(MemberAvatar, {
      props: { team: 'alpha', member: 'NamecheapDns', name: 'Namecheap DNS', state: 'failed', kind: 'plugin' },
    });
    expect(avatar().classes()).toEqual(expect.arrayContaining(['member-avatar--plugin', 'member-avatar--failed']));
    expect(avatar().attributes('data-avatar')).toBe('-1');
    expect(wrapper.get('.member-avatar-initials').text()).toBe('ND');
  });

  it('shows an agent\'s initials until the sheet has loaded', () => {
    setAvatarSheet(null, 0, 0);
    wrapper = mount(MemberAvatar, { props: { team: 'alpha', member: 'DeveloperIda', name: 'DeveloperIda', state: 'idle' } });
    expect(wrapper.get('.member-avatar-initials').text()).toBe('DI');
  });
});

describe('the member card', () => {
  it('wears the avatar at its upper left, in the ring of the member\'s state, with no drag icon', () => {
    wrapper = mount(ContainerCard, {
      props: { snapshot: snapshot({ state: 'Running' }), feed: [] },
      global: { stubs: { ActivityFeed: true, MemberSettingsDialog: true, TriggersDialog: true, LiveViewDialog: true } },
    });
    expect(avatar().attributes('data-avatar')).toBe(String(avatarIndex('alpha', 'Developer', 100)));
    expect(avatar().attributes('data-state')).toBe('running');
    expect(wrapper.html()).not.toContain('drag_indicator');
  });
});

describe('the Kanban card', () => {
  it('wears its member\'s avatar at its upper left, in the same ring the member card draws', () => {
    const team = { id: 'alpha', name: 'Alpha', containers: [snapshot({ blocked: 'waiting on a key' })] } as unknown as Team;
    useConsoleStore().teams = [team];
    const ring = useKanbanStore().memberRing(card());
    expect(ring).toEqual({ state: 'blocked', kind: 'agent' });

    wrapper = mount(KanbanCardComponent, { props: { card: card(), ring } });
    const head = wrapper.get('.k-card-head');
    expect(head.element.firstElementChild?.getAttribute('data-test')).toBe('member-avatar');
    expect(avatar().attributes('data-avatar')).toBe(String(avatarIndex('alpha', 'Developer', 100)));
    expect(avatar().attributes('data-state')).toBe('blocked');
  });

  it('wears none when nobody is assigned', () => {
    expect(useKanbanStore().memberRing(card({ member: null }))).toBeNull();
    wrapper = mount(KanbanCardComponent, { props: { card: card({ member: null }), ring: null } });
    expect(wrapper.find('[data-test="member-avatar"]').exists()).toBe(false);
  });
});
