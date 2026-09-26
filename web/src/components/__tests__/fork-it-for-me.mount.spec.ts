// @vitest-environment happy-dom
//
/**
 * FORK IT FOR ME: the person gives only the upstream, GitHub makes the fork, and the
 * dialog gets both URLs back. An organisation is passed only when typed. A refusal is shown as the
 * server said it - GitHub's sentence and what the token needs - and nothing is added.
 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  forkUpstream: vi.fn(),
}));

import ForkItForMe from '../ForkItForMe.vue';
import { forkUpstream } from '../../api/client';
import '../../test/mountQuasar';

beforeEach(() => {
  vi.mocked(forkUpstream).mockReset();
});

async function forkWith(upstream: string, organisation = '') {
  const wrapper = mount(ForkItForMe);
  const inputs = wrapper.findAll('input');
  await inputs[0]!.setValue(upstream);
  await inputs[1]!.setValue(organisation);
  const button = wrapper.findAll('button').find((b) => b.text().includes('Fork it for me'))!;
  await button.trigger('click');
  await flushPromises();
  return wrapper;
}

describe('Fork it for me', () => {
  it('asks for the fork in the token account and hands both URLs back', async () => {
    vi.mocked(forkUpstream).mockResolvedValue({
      forkUrl: 'https://github.com/fork-owner/Widget',
      forkOwner: 'fork-owner',
      upstreamUrl: 'https://github.com/project/Widget',
    });

    const wrapper = await forkWith('https://github.com/project/Widget');

    expect(forkUpstream).toHaveBeenCalledWith('https://github.com/project/Widget', '');
    expect(wrapper.emitted('forked')?.[0]?.[0]).toMatchObject({
      forkUrl: 'https://github.com/fork-owner/Widget',
      upstreamUrl: 'https://github.com/project/Widget',
    });
  });

  it('passes the organisation only when one is typed', async () => {
    vi.mocked(forkUpstream).mockResolvedValue({
      forkUrl: 'https://github.com/some-org/Widget', forkOwner: 'some-org', upstreamUrl: 'https://github.com/project/Widget',
    });

    await forkWith('https://github.com/project/Widget', 'some-org');

    expect(forkUpstream).toHaveBeenCalledWith('https://github.com/project/Widget', 'some-org');
  });

  it("shows GitHub's refusal and adds nothing", async () => {
    vi.mocked(forkUpstream).mockRejectedValue(new Error(
      'The fork was not made. GitHub said: HTTP 403: Resource not accessible by personal access token. '
      + 'Contributor mode needs a classic GitHub token with the public_repo scope.',
    ));

    const wrapper = await forkWith('https://github.com/project/Widget');

    expect(wrapper.find('.fork-it-error').text()).toContain('Resource not accessible by personal access token');
    expect(wrapper.find('.fork-it-error').text()).toContain('public_repo');
    expect(wrapper.emitted('forked')).toBeUndefined();
  });
});
