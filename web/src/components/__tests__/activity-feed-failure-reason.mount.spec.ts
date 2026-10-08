// @vitest-environment happy-dom
//
// A FAILED RUN'S FEED LINE SAYS WHY, AS ITS CARD DOES. The row is clamped to two lines, so the
// reason sits right after the class badge; the class's own sentence follows it, as on the card.
import { beforeEach, describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { asMemberId, asTeamId, type ContainerSnapshot, type Message } from '../../api/types';
import { containerMark } from '../../lib/teamKpis';

import ActivityFeed from '../ActivityFeed.vue';
import '../../test/mountQuasar';

const reason = 'asked to fail: the printer is out of paper';

// The terminal row the Host writes for a plugin whose result record said `ok: false`: the
// plugin's error leads the output, there is no launch error, and nobody classified it.
const failedRow: Message = {
  seq: 77,
  type: 'agentContainer.failed',
  payload: JSON.stringify({ exitCode: 1, output: reason, launchError: null, failureClass: 'unknown' }),
  source: 'Mail/Echo',
  correlationId: 77,
  causationSeq: 76,
  depth: 1,
  occurredAt: '2026-10-07T10:00:00Z',
};

// The same run as the card reads it, from the snapshot.
const snapshot = {
  team: asTeamId('Mail'),
  id: asMemberId('Echo'),
  name: 'Echo',
  kind: 'plugin',
  failed: reason,
  failureClass: 'unknown',
} as unknown as ContainerSnapshot;

beforeEach(() => setActivePinia(createPinia()));

describe('ActivityFeed: a failed plugin run', () => {
  it('says the reason its card says, right after the class', () => {
    const wrapper = mount(ActivityFeed, { props: { feed: [failedRow], team: 'Mail', name: 'Echo', sinceSeq: 0 } });
    const line = wrapper.find('.feed-line').text();
    const mark = containerMark(snapshot);

    expect(mark?.kind).toBe('failed');
    expect(line.startsWith(`[unknown] ${(mark as { reason: string }).reason}`)).toBe(true);
  });
});
