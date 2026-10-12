// @vitest-environment happy-dom
//
// MARKETPLACE, THE FIRST OPEN'S EDGES (the tester's probes): the dialog closed while the first
// refresh is out and opened again; the catalog GET answering late, so the refresh must come after
// it and win; Refresh pressed while the first refresh is out; a failed GET that leaves the first
// refresh for a later open; and a failed manual Refresh that keeps the list.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SolutionsLauncher from '../SolutionsLauncher.vue';
import type { MarketplaceCatalog, MarketplacePackage } from '../../api/types';
import { resetFirstOpenRefresh } from '../../lib/marketplaceRefresh';
import { bodyFind, mountDialog, resetBody } from '../../test/mountQuasar';
import { settle } from '../../test/formProbe';
import { fakeHost, reply, sent, wizardRoutes, type Call } from '../../test/solutionFixtures';

function pkg(id: string, name: string): MarketplacePackage {
  return {
    id,
    name,
    kind: 'solution',
    summary: `${name} does its one job.`,
    description: '',
    version: '1.0.0',
    needs: [],
    catalogNeeds: { connections: [], secrets: [], inputs: [], runtimes: [] },
    installed: false,
    installedVersion: null,
    installedOn: [],
    updateAvailable: false,
  };
}

const Mail = pkg('mail', 'Mail');
const WhoAmI = pkg('whoami', 'WhoAmI');
const read = (packages: MarketplacePackage[]): MarketplaceCatalog => ({ checked: true, reason: null, checkedAt: '2026-10-09T08:00:00Z', packages });

let calls: Call[] = [];
// Requests held back until the spec releases them, by "METHOD url".
const held = new Map<string, Array<() => void>>();
const release = (key: string) => held.get(key)?.shift()?.();

function serve(answers: { get: () => Response; refresh: () => Response }, hold: string[] = []) {
  calls = [];
  held.clear();
  const host = fakeHost(
    [
      (call) => (call.method === 'GET' && call.url === '/api/marketplace' ? answers.get() : undefined),
      (call) => (call.method === 'POST' && call.url === '/api/marketplace/refresh' ? answers.refresh() : undefined),
      ...wizardRoutes(),
    ],
    calls,
  );
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, init?: RequestInit) => {
      const key = `${init?.method ?? 'GET'} ${url}`;
      const answer = host(url, init);
      if (!hold.includes(key)) return answer;
      return new Promise<Response>((resolve) => {
        held.set(key, [...(held.get(key) ?? []), () => void answer.then(resolve)]);
      });
    }),
  );
}

let warn: ReturnType<typeof vi.spyOn>;
let error: ReturnType<typeof vi.spyOn>;

beforeEach(() => {
  resetFirstOpenRefresh();
  warn = vi.spyOn(console, 'warn');
  error = vi.spyOn(console, 'error');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  resetBody();
  resetFirstOpenRefresh();
});

async function openTab(name: string) {
  bodyFind(`[data-solutions-tab="${name}"]`)!.click();
  await settle();
}

const shown = () => [...document.body.querySelectorAll('[data-catalog-package]')].map((tile) => tile.getAttribute('data-catalog-package'));
const refreshLoading = () => bodyFind('[data-catalog-refresh]')!.classList.contains('q-btn--loading')
  || bodyFind('[data-catalog-refresh] .q-spinner') !== null;
const posts = () => sent(calls, 'POST', '/api/marketplace/refresh').length;

describe('Marketplace, the first open: edges', () => {
  it('closed while the first refresh is out and opened again: no second refresh, no error', async () => {
    let refreshed = false;
    serve({ get: () => reply(200, read(refreshed ? [Mail, WhoAmI] : [Mail])), refresh: () => ((refreshed = true), reply(200, read([Mail, WhoAmI]))) }, ['POST /api/marketplace/refresh']);

    const wrapper = await mountDialog(SolutionsLauncher);
    await settle();
    expect(posts()).toBe(1);

    await wrapper.setProps({ modelValue: false });
    await settle();
    release('POST /api/marketplace/refresh');
    await settle();

    await wrapper.setProps({ modelValue: true });
    await settle();
    await openTab('get-started');

    expect(posts()).toBe(1);
    expect(sent(calls, 'GET', '/api/marketplace')).toHaveLength(2);
    // The Host kept the refreshed catalog; the reopened Browse reads it with its GET.
    expect(shown()).toEqual(['mail', 'whoami']);
    expect(refreshLoading()).toBe(false);
    expect(bodyFind('[data-catalog-unreachable]')).toBeNull();
    expect(error).not.toHaveBeenCalled();
    expect(warn).not.toHaveBeenCalled();
  });

  it('reopened before the first refresh answers: one refresh, and its late answer breaks nothing', async () => {
    serve({ get: () => reply(200, read([Mail])), refresh: () => reply(200, read([Mail, WhoAmI])) }, ['POST /api/marketplace/refresh']);

    const wrapper = await mountDialog(SolutionsLauncher);
    await settle();
    await wrapper.setProps({ modelValue: false });
    await settle();
    await wrapper.setProps({ modelValue: true });
    await settle();
    await openTab('get-started');

    expect(posts()).toBe(1);
    expect(shown()).toEqual(['mail']);
    expect(refreshLoading()).toBe(false);

    release('POST /api/marketplace/refresh');
    await settle();

    expect(posts()).toBe(1);
    expect(shown()).toEqual(['mail']);
    expect(error).not.toHaveBeenCalled();
    expect(warn).not.toHaveBeenCalled();
  });

  it('asks for the refresh only after the GET answers, so a late GET never overwrites the refreshed catalog', async () => {
    serve({ get: () => reply(200, read([Mail])), refresh: () => reply(200, read([Mail, WhoAmI])) }, ['GET /api/marketplace']);

    await mountDialog(SolutionsLauncher);
    await settle();
    await openTab('get-started');

    expect(posts()).toBe(0);
    expect(refreshLoading()).toBe(true);
    expect(shown()).toEqual([]);

    release('GET /api/marketplace');
    await settle();

    expect(posts()).toBe(1);
    expect(shown()).toEqual(['mail', 'whoami']);
    expect(refreshLoading()).toBe(false);
  });

  it('Refresh pressed while the first refresh is out sends nothing more; the first answer is shown', async () => {
    serve({ get: () => reply(200, read([Mail])), refresh: () => reply(200, read([Mail, WhoAmI])) }, ['POST /api/marketplace/refresh']);

    await mountDialog(SolutionsLauncher);
    await settle();
    await openTab('get-started');
    expect(refreshLoading()).toBe(true);

    bodyFind('[data-catalog-refresh]')!.click();
    await settle();
    expect(posts()).toBe(1);

    release('POST /api/marketplace/refresh');
    await settle();
    expect(shown()).toEqual(['mail', 'whoami']);
    expect(refreshLoading()).toBe(false);
  });

  it('a GET that fails asks no refresh and leaves the first refresh to the next open', async () => {
    let up = false;
    serve({ get: () => (up ? reply(200, read([Mail])) : reply(502, { error: 'The Host could not be reached' })), refresh: () => reply(200, read([Mail, WhoAmI])) });

    const wrapper = await mountDialog(SolutionsLauncher);
    await settle();
    expect(posts()).toBe(0);

    up = true;
    await wrapper.setProps({ modelValue: false });
    await settle();
    await wrapper.setProps({ modelValue: true });
    await settle();
    await openTab('get-started');

    expect(posts()).toBe(1);
    expect(shown()).toEqual(['mail', 'whoami']);
  });

  it('a failed manual Refresh keeps the list shown, with the sentence above it', async () => {
    let fail = false;
    serve({ get: () => reply(200, read([Mail])), refresh: () => (fail ? reply(502, { error: 'The catalog address did not answer' }) : reply(200, read([Mail, WhoAmI]))) });

    await mountDialog(SolutionsLauncher);
    await settle();
    await openTab('get-started');
    expect(shown()).toEqual(['mail', 'whoami']);

    fail = true;
    bodyFind('[data-catalog-refresh]')!.click();
    await settle();

    expect(posts()).toBe(2);
    expect(bodyFind('[data-catalog-unreachable]')?.textContent?.trim()).toBe('Could not reach the package catalog: The catalog address did not answer.');
    expect(shown()).toEqual(['mail', 'whoami']);

    fail = false;
    bodyFind('[data-catalog-refresh]')!.click();
    await settle();
    expect(bodyFind('[data-catalog-unreachable]')).toBeNull();
  });
});
