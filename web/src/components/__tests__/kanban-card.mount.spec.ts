// @vitest-environment happy-dom
//
// WHAT A CARD SAYS ABOUT ITS OWN IDENTITY, MOUNTED.
//
// Its neighbour `kanban-board.mount.spec.ts` mounts the card inside the board. This file is about
// what a person reads on the card itself: without the card id, every card in one workflow reads as
// the same `#2226`.
//
// THE ID IS THE HANDLE EVERY AGENT USES. The `tell` and `kanban` MCP tools address a card by id,
// and a member's progress line quotes it back ("B000H card 2237: read spec"). The workflow seq is
// what GROUPS cards and stays. Both are asserted here, in one element, so a change that showed one
// INSTEAD of the other is caught rather than read as tidying.
import { afterEach, describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';

import KanbanCard from '../KanbanCard.vue';
// Importing this module is what installs the Quasar plugin - it registers a `beforeAll` hook at
// MODULE level, so the import itself is the setup.
import '../../test/mountQuasar';
import type { KanbanCard as Card } from '../../api/kanban';

function card(over: Partial<Card> & { id: string; workflowSeq: number }): Card {
  return {
    team: 'alpha',
    member: 'DeveloperInes',
    item: 5,
    title: 'Read spec, routes and tests',
    body: '',
    status: 'queued',
    laneId: 'todo',
    progress: [],
    createdAt: '2026-09-18T00:00:00Z',
    updatedAt: '2026-09-18T00:00:00Z',
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

describe('a card names itself', () => {
  it('shows the linked platform backlog item with the B prefix', () => {
    const el = render(card({ id: '2316', workflowSeq: 2226, item: 17 }));

    // `B000H`, NOT `BH`: the id is Crockford base 32, zero-padded to four.
    expect(el.querySelector('.k-card-item')?.textContent?.trim()).toBe('B000H');
  });

  /**
   * A PLANNED CARD: its id is the seq of the row that planned it, and it is NOT the workflow seq.
   * Three cards planned under workflow 2226 read `#2226` three times over; the id is the only
   * thing that tells them apart, and it is what `--card` takes.
   */
  it('shows the card id beside the workflow number for a planned card', () => {
    const el = render(card({ id: '2316', workflowSeq: 2226 }));

    const meta = el.querySelector('.k-card-meta');

    expect(meta).not.toBeNull();
    expect(meta!.textContent).toContain('card 2316');
    expect(meta!.textContent).toContain('#2226');
  });

  /**
   * A CARD MADE FROM AN INSTRUCTION is keyed `(correlation, member)` and its id carries the member,
   * `2310_Manager`. It has to read as an id rather than as a rendering fault - which is why it is
   * labelled `card` and sits in its own element, not glued onto the workflow number.
   */
  it('shows a composite instruction-card id as it is, labelled as the card', () => {
    const el = render(card({ id: '2310_Manager', workflowSeq: 2310, member: 'Manager' }));

    const id = el.querySelector('.k-card-id');

    expect(id).not.toBeNull();
    expect(id!.textContent?.trim()).toBe('card 2310_Manager');

    // AND THE WORKFLOW NUMBER IS STILL THERE. Both, never one instead of the other: the workflow is
    // what groups the cards, and a card that lost it could no longer be found among its siblings.
    expect(el.querySelector('.k-card-workflow')?.textContent).toContain('#2310');
  });
});
