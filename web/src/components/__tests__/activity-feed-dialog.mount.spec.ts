// @vitest-environment happy-dom
//
// A MEMBER CARD'S ROW, READ IN FULL. A click (or a tap) on a row opens a dialog with the whole
// message; Newer and Older step through the card's rows in the order the card lists them (newest
// first) without closing it; hovering opens nothing. The workflow button and the install link keep
// their own clicks.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount, type VueWrapper } from '@vue/test-utils';

// Following a workflow tells the server which one the person is steering; nothing here needs it.
vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  publishSteering: vi.fn(),
}));
import { createPinia, setActivePinia } from 'pinia';
import type { Message } from '../../api/types';

import ActivityFeed from '../ActivityFeed.vue';
import { useConsoleStore } from '../../stores/console';
import { bodyFind, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';

const progress = (seq: number, status: string): Message => ({
  seq,
  type: 'agentContainer.progress',
  payload: JSON.stringify({ status }),
  source: 'Alpha/Worker',
  correlationId: 7,
  causationSeq: null,
  depth: 1,
  occurredAt: '2026-09-29T10:00:00Z',
});

// Newest first, as the card lists them.
const feed = [progress(30, 'third: the newest'), progress(20, 'second'), progress(10, 'first: the oldest')];

let wrapper: VueWrapper | null = null;

function mountFeed(rows: Message[]) {
  wrapper = mount(ActivityFeed, { props: { feed: rows }, attachTo: document.body });
  return wrapper;
}

function rows() {
  return wrapper!.findAll('.q-item').filter((row) => row.attributes('aria-label')?.startsWith('Open '));
}

const dialog = () => bodyFind('[data-feed-dialog]');
const body = () => bodyFind('[data-feed-body]')?.textContent ?? '';
const position = () => bodyFind('[data-feed-position]')?.textContent?.trim() ?? '';
const previous = () => bodyFind('[data-feed-previous]') as HTMLButtonElement;
const next = () => bodyFind('[data-feed-next]') as HTMLButtonElement;

beforeEach(() => setActivePinia(createPinia()));

afterEach(() => {
  wrapper?.unmount();
  wrapper = null;
  resetBody();
});

describe('a member card row opens its message in a dialog', () => {
  it('opens nothing on hover, and the full message on a click', async () => {
    mountFeed(feed);

    await rows()[1]!.trigger('mouseenter');
    await settle();
    expect(dialog()).toBeNull();

    await rows()[1]!.trigger('click');
    await settle();

    expect(dialog()).not.toBeNull();
    expect(body()).toContain('second');
    expect(position()).toBe('2 of 3');
  });

  it('steps to the newer row with Newer and the older row with Older, and stops at each end', async () => {
    mountFeed(feed);
    await rows()[1]!.trigger('click');
    await settle();

    // Named for where they go, not for a direction the reader has to translate.
    expect(previous().textContent).toContain('Newer');
    expect(next().textContent).toContain('Older');

    previous().click();
    await settle();
    expect(body()).toContain('third: the newest');
    expect(position()).toBe('1 of 3');
    expect(previous().disabled || previous().getAttribute('aria-disabled') === 'true').toBe(true);

    next().click();
    await settle();
    next().click();
    await settle();
    expect(body()).toContain('first: the oldest');
    expect(position()).toBe('3 of 3');
    expect(next().disabled || next().getAttribute('aria-disabled') === 'true').toBe(true);
  });

  it('steps with the Left and Right keys too', async () => {
    mountFeed(feed);
    await rows()[0]!.trigger('click');
    await settle();

    dialog()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
    await settle();
    expect(body()).toContain('second');

    dialog()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }));
    await settle();
    expect(body()).toContain('third: the newest');
  });

  it('stays on the message being read when a new one arrives at the top', async () => {
    const view = mountFeed(feed);
    await rows()[1]!.trigger('click');
    await settle();

    await view.setProps({ feed: [progress(40, 'arrived while reading'), ...feed] });
    await settle();

    expect(body()).toContain('second');
    expect(position()).toBe('3 of 4');
  });

  it('keeps showing a message that falls out of the card, with nothing to step to', async () => {
    const view = mountFeed(feed);
    await rows()[2]!.trigger('click');
    await settle();

    await view.setProps({ feed: feed.slice(0, 2) });
    await settle();

    expect(body()).toContain('first: the oldest');
    expect(position()).toBe('');
    expect(next().disabled || next().getAttribute('aria-disabled') === 'true').toBe(true);
  });

  it('follows the workflow from its button without opening the dialog', async () => {
    mountFeed(feed);

    (wrapper!.find('[aria-label="Follow workflow 7"]').element as HTMLButtonElement).click();
    await settle();

    expect(useConsoleStore().highlightedWorkflow).toBe(7);
    expect(dialog()).toBeNull();
  });

  it('shows the message text as text, never as markup', async () => {
    mountFeed([progress(50, '<img src=x onerror="alert(1)"> not markup')]);
    await rows()[0]!.trigger('click');
    await settle();

    expect(body()).toContain('<img src=x');
    expect(bodyFind('[data-feed-body] img')).toBeNull();
  });
});
