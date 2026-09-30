// @vitest-environment happy-dom
//
// A CARD WHOSE RUN MET A TOOL THE PLATFORM DID NOT GIVE IT SAYS SO, AND A CALL LOUDER THAN AN OFFER.
//
// The server's per-run check puts `foreignTools` on the card: `called`, `offered` or `notMeasured`.
// A call is the filled negative badge, an offer the warning one, a run that could not be checked a
// quiet outline - never nothing, since unknown is not clean. A clean card, and a card from an older
// server that sends no field, shows none.
import { afterEach, describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';

import KanbanCard from '../KanbanCard.vue';
import '../../test/mountQuasar';
import type { KanbanCard as Card } from '../../api/kanban';

function card(over: Partial<Card> = {}): Card {
  return {
    id: '4101',
    workflowSeq: 4100,
    team: 'alpha',
    member: 'DeveloperIlse',
    item: 51,
    title: 'Flag foreign tools',
    body: '',
    status: 'completed',
    laneId: 'done',
    progress: [],
    createdAt: '2026-09-30T00:00:00Z',
    updatedAt: '2026-09-30T00:00:00Z',
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

describe('a card whose run met a foreign tool says so', () => {
  it('marks a call, loudest', () => {
    const el = render(card({ foreignTools: 'called' }));

    const badge = el.querySelector('.k-card-foreign-called');
    expect(badge).not.toBeNull();
    expect(badge!.textContent).toContain('foreign tool called');
    expect(el.querySelector('.k-card-foreign-offered')).toBeNull();
  });

  it('marks an offer, below a call', () => {
    const el = render(card({ foreignTools: 'offered' }));

    expect(el.querySelector('.k-card-foreign-offered')!.textContent).toContain('foreign tool offered');
    expect(el.querySelector('.k-card-foreign-called')).toBeNull();
  });

  it('marks a run that could not be checked, rather than showing it clean', () => {
    const el = render(card({ foreignTools: 'notMeasured' }));

    expect(el.querySelector('.k-card-foreign-unmeasured')!.textContent).toContain('tools not measured');
  });

  it('shows nothing for a clean card, or one from a server that sends no field', () => {
    expect(render(card({ foreignTools: null })).querySelector('.k-card-foreign')).toBeNull();
    mounted?.unmount();
    expect(render(card()).querySelector('.k-card-foreign')).toBeNull();
  });
});
