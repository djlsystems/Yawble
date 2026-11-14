// @vitest-environment happy-dom
import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import BoardCardItem from '../../src/renderer/src/board/BoardCardItem.vue';
import type { BoardCard } from '../../src/shared/api';

// The Kanban card shows the heavy-work lease of its member on the card it works on: held or waited for. Fakes only.
const card = (lease?: BoardCard['lease']): BoardCard => ({
  id: 7, team: 'payments', teamId: 1, member: 'DeveloperPriya', item: 'B007', title: 'Run the suite', body: '', status: 'running',
  lane: 'in-progress', placed: false, awaitingManager: false, workflow: 12, createdAt: '2026-11-08T10:00:00Z', updatedAt: '2026-11-08T10:00:00Z',
  trailCount: 0, ...(lease ? { lease } : {}),
});

describe('Kanban card: the heavy-work lease', () => {
  it('says Heavy work while the member holds it, with what for in the tooltip', () => {
    const w = mount(BoardCardItem, { props: { card: card({ state: 'holding', what: 'full test suite', position: null }) } });
    const chip = w.get('[data-test=card-lease]');
    expect(chip.text()).toBe('Heavy work');
    expect(chip.attributes('title')).toContain('Holds the heavy-work lease: full test suite');
    expect(chip.classes()).toContain('held');
  });

  it('says Waiting for heavy work with the queue position while it waits', () => {
    const w = mount(BoardCardItem, { props: { card: card({ state: 'waiting', what: 'build', position: 2 }) } });
    const chip = w.get('[data-test=card-lease]');
    expect(chip.text()).toBe('Waiting for heavy work (2)');
    expect(chip.attributes('title')).toContain('position 2: build');
    expect(chip.classes()).toContain('queued');
    expect(chip.classes()).not.toContain('waiting'); // not the red "awaiting Manager" style
  });

  it('shows nothing when the member has no lease (or the core is older)', () => {
    expect(mount(BoardCardItem, { props: { card: card() } }).find('[data-test=card-lease]').exists()).toBe(false);
  });
});
