// @vitest-environment happy-dom
//
// ADMIN > SITES. Every site with its team, live version, who published it and when, and its data
// size; Open in a new tab; Versions and rolling back; Unpublish; Delete, which asks with the Host's
// own sentence and sends `confirm=true` only after the person agrees; and a collection's documents,
// shown as text and never as markup. Filtered to one team when opened from Active Team.
//
// THE MOCK IS OF `fetch`, not of `api/sites`: what is pinned here is the request each action sends -
// the path, the method, `?confirm=true` and the rollback body - as well as what the dialog makes of
// the answers. What the Host does is pinned server-side.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

import SitesDialog from '../SitesDialog.vue';
import type { Site, SiteDetail, SiteDocument } from '../../api/sites';
import { useConsoleStore } from '../../stores/console';
import type { Team } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle } from '../../test/formProbe';

const shop: Site = {
  team: 'alpha',
  name: 'shop',
  liveVersion: 3,
  publishedAt: '2026-09-28T10:00:00Z',
  publishedBy: 'dana@example.com',
  createdAt: '2026-09-01T10:00:00Z',
  createdBy: 'dana@example.com',
  documents: 12,
  dataBytes: 5 * 1024 * 1024,
  url: '/sites/alpha/shop/',
};

const draft: Site = {
  team: 'beta',
  name: 'draft',
  liveVersion: null,
  publishedAt: null,
  publishedBy: null,
  createdAt: '2026-09-02T10:00:00Z',
  createdBy: 'eli@example.com',
  documents: 1,
  dataBytes: 900,
  url: '/sites/beta/draft/',
};

const shopDetail: SiteDetail = {
  site: shop,
  versions: [
    { version: 3, live: true, publishedAt: '2026-09-28T10:00:00Z', publishedBy: 'dana@example.com', source: 'site/', files: 4, bytes: 2048 },
    { version: 2, live: false, publishedAt: '2026-09-20T10:00:00Z', publishedBy: 'dana@example.com', source: 'site/', files: 3, bytes: 1024 },
  ],
  collections: ['orders', 'notes'],
  url: '/sites/alpha/shop/',
};

const hostile: SiteDocument = {
  collection: 'notes',
  id: 'n1',
  doc: { text: '<img src=x onerror=alert(1)>' },
  updatedAt: '2026-09-28T11:00:00Z',
  updatedBy: 'visitor',
};

const DeleteSentence = 'Deleting shop removes 3 versions and 12 documents, and cannot be undone.';

interface Call {
  method: string;
  url: string;
  body: unknown;
}

let calls: Call[] = [];

function reply(status: number, body?: unknown) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

/** A Host that answers the sites routes and records every request. */
function host(url: string, init?: RequestInit): Response {
  const method = init?.method ?? 'GET';
  calls.push({ method, url, body: init?.body ? JSON.parse(String(init.body)) : undefined });

  if (method === 'GET' && url === '/api/sites') return reply(200, [shop, draft]);
  if (method === 'GET' && url === '/api/teams/alpha/sites/shop') return reply(200, shopDetail);
  if (method === 'GET' && url === '/api/teams/alpha/sites/shop/data/notes') return reply(200, [hostile]);
  if (method === 'POST' && url.endsWith('/rollback')) return reply(200, { ...shop, liveVersion: 2 });
  if (method === 'POST' && url.endsWith('/unpublish')) return reply(200, { ...shop, liveVersion: null });
  if (method === 'DELETE' && url === '/api/teams/alpha/sites/shop') return reply(409, { error: DeleteSentence });
  if (method === 'DELETE' && url === '/api/teams/alpha/sites/shop?confirm=true') return new Response(null, { status: 204 });

  return reply(404, { error: `No route for ${method} ${url}.` });
}

beforeEach(() => {
  setActivePinia(createPinia());
  useConsoleStore().$patch({
    teams: [{ id: 'alpha', name: 'Alpha Team' }, { id: 'beta', name: 'Beta Team' }] as unknown as Team[],
    overviewLanded: true,
  });

  calls = [];
  vi.stubGlobal('fetch', vi.fn((url: string, init?: RequestInit) => Promise.resolve(host(url, init))));
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  resetBody();
});

async function open(props: Record<string, unknown> = {}) {
  const wrapper = await mountDialog(SitesDialog, props, { pinia: false });
  await settle();
  return wrapper;
}

function row(key: string) {
  return bodyFind(`[data-site="${key}"]`);
}

function action(label: string) {
  return bodyFind(`[aria-label="${label}"]`) as HTMLButtonElement;
}

const sent = (method: string) => calls.filter((call) => call.method === method);

describe('Admin > Sites', () => {
  it('lists every site with its team, live version, publisher and data size', async () => {
    await open();

    const live = row('alpha/shop')!.textContent ?? '';
    expect(live).toContain('shop');
    expect(live).toContain('Alpha Team');
    expect(live).toContain('v3');
    expect(live).toContain(new Date('2026-09-28T10:00:00Z').toLocaleString());
    expect(live).toContain('by dana@example.com');
    expect(live).toContain('12 documents');
    expect(live).toContain('5.0 MB');

    const unpublished = row('beta/draft')!.textContent ?? '';
    expect(unpublished).toContain('Beta Team');
    expect(unpublished).toContain('Not published');
    expect(unpublished).toContain('1 document');
    expect(unpublished).toContain('900 B');
  });

  it('opens a published site in a new tab, and offers nothing to open or unpublish when it is not', async () => {
    const opened = vi.spyOn(window, 'open').mockReturnValue(null);
    await open();

    action('Open shop').click();
    expect(opened).toHaveBeenCalledWith('/sites/alpha/shop/', '_blank', 'noopener');

    expect(action('Open draft').hasAttribute('disabled')).toBe(true);
    expect(action('Unpublish draft').hasAttribute('disabled')).toBe(true);
  });

  it('shows the Host\'s own sentence before deleting, and sends confirm=true only once agreed', async () => {
    await open();

    action('Delete shop').click();
    await settle();

    expect(bodyFind('[data-site-delete-sentence]')?.textContent).toBe(DeleteSentence);
    expect(sent('DELETE').map((call) => call.url)).toEqual(['/api/teams/alpha/sites/shop']);

    button('Cancel').click();
    await settle();
    expect(sent('DELETE')).toHaveLength(1);

    action('Delete shop').click();
    await settle();
    button('Delete shop').click();
    await settle();

    expect(sent('DELETE').map((call) => call.url)).toEqual([
      '/api/teams/alpha/sites/shop',
      '/api/teams/alpha/sites/shop',
      '/api/teams/alpha/sites/shop?confirm=true',
    ]);
    expect(bodyFind('[data-site-delete]')).toBeNull();
  });

  it('lists the kept versions and rolls back to the chosen one, never offering the live one', async () => {
    await open();

    action('Versions of shop').click();
    await settle();

    expect(bodyFind('[data-site-version="3"]')?.textContent).toContain('Live');
    expect(() => button('Roll back to v3')).toThrow();

    button('Roll back to v2').click();
    await settle();

    expect(sent('POST')).toEqual([{ method: 'POST', url: '/api/teams/alpha/sites/shop/rollback', body: { version: 2 } }]);
  });

  it('unpublishes with a POST', async () => {
    await open();

    action('Unpublish shop').click();
    await settle();

    expect(sent('POST')).toEqual([{ method: 'POST', url: '/api/teams/alpha/sites/shop/unpublish', body: {} }]);
  });

  it('shows a refusal in the Host\'s own words', async () => {
    await open();
    vi.mocked(fetch).mockImplementationOnce(() => Promise.resolve(reply(409, { error: 'shop is not published.' })));

    action('Unpublish shop').click();
    await settle();

    expect(bodyFind('[data-sites-problem]')?.textContent).toBe('shop is not published.');
  });

  it('shows a collection\'s documents as text, never as markup', async () => {
    await open();

    action('Data of shop').click();
    await settle();
    button('notes').click();
    await settle();

    const doc = bodyFind('[data-site-doc="n1"]')!;
    expect(doc.textContent).toContain('visitor');
    expect(doc.querySelector('pre')?.textContent).toBe(JSON.stringify(hostile.doc, null, 2));
    expect(bodyText()).toContain('<img src=x onerror=alert(1)>');
    expect(document.body.querySelector('img')).toBeNull();
  });

  it('opened for a team, lists only that team\'s sites until All teams', async () => {
    await open({ team: 'alpha' });

    expect(bodyText()).toContain('Sites · Alpha Team');
    expect(row('alpha/shop')).not.toBeNull();
    expect(row('beta/draft')).toBeNull();

    button('All teams').click();
    await settle();

    expect(row('beta/draft')).not.toBeNull();
  });
});
