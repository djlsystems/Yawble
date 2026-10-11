// @vitest-environment happy-dom
//
// PAUSED READS ON THE CARD AS WELL AS ON THE TILE.
//
// The acceptance is that a workflow which has hit its budget says so wherever a person is looking,
// and the board is the other place they are looking. A card whose workflow is paused is not
// "queued" in any sense a reader would act on - nothing will move it until somebody resumes.
//
// **A FLAG, NOT A STATUS.** Folding pause into `KanbanStatus` would
// erase a member's own `blocked` mark (which `container.started` resets and pause does not), and
// would need a new lane and colour in every template. `awaitingManager` is the shape the board
// already has for "a fact about this card that is not its run outcome", and `paused` sits beside
// it and needs no template change at all.
import { afterEach, describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';

import KanbanCard from '../KanbanCard.vue';
import '../../test/mountQuasar';
import type { KanbanCard as Card } from '../../api/kanban';

function card(over: Partial<Card> = {}): Card {
  return {
    id: '2316',
    workflowSeq: 2226,
    team: 'alpha',
    member: 'DeveloperPetra',
    item: 5,
    title: 'Read spec, routes and tests',
    body: '',
    status: 'queued',
    laneId: 'todo',
    progress: [],
    createdAt: '2026-09-20T00:00:00Z',
    updatedAt: '2026-09-20T00:00:00Z',
    awaitingManager: false,
    paused: false,
    ...over,
  };
}

let mounted: ReturnType<typeof mount> | null = null;

afterEach(() => {
  mounted?.unmount();
  mounted = null;
});

function render(props: Card): HTMLElement {
  mounted = mount(KanbanCard, { props: { card: props } });

  return mounted.element as HTMLElement;
}

describe('a card whose workflow is paused says so', () => {
  it('shows a paused mark', () => {
    const el = render(card({ paused: true }));

    expect(el.querySelector('.k-card-paused')).not.toBeNull();
    expect(el.textContent).toContain('paused');
  });

  /** And nothing at all when it is not - a mark that is always there is a mark nobody reads. */
  it('shows nothing for a card nobody paused', () => {
    const el = render(card({ paused: false }));

    expect(el.querySelector('.k-card-paused')).toBeNull();
  });

  /**
   * IT DOES NOT REPLACE THE CARD'S STATUS. A paused card is still queued, running or blocked, and
   * losing that word would cost the reader the state the card is actually in.
   */
  it('keeps the card’s own status word beside it', () => {
    const el = render(card({ paused: true, status: 'blocked' }));

    expect(el.querySelector('.k-card-status')?.textContent?.trim()).not.toBe('');
    expect(el.querySelector('.k-card-paused')).not.toBeNull();
  });

  /** Nor the awaiting-manager overlay: the two are independent facts and a card can carry both. */
  it('renders beside the awaiting-manager mark rather than instead of it', () => {
    const el = render(card({ paused: true, awaitingManager: true }));

    expect(el.querySelector('.k-card-paused')).not.toBeNull();
    expect(el.querySelector('.k-card-awaiting')).not.toBeNull();
  });
});
