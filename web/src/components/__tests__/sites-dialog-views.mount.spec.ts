// @vitest-environment happy-dom
//
// ADMIN > SITES, its Data and Versions views. A collection's documents read as fields and values,
// with the JSON one Show JSON switch away, as Admin > Log reads its details. The Versions table
// fits its dialog - no action is cut off and nothing scrolls sideways - and keeps its Version
// column and Live badge through a roll back. An unpublished site is put back live with "Publish
// v<n>" for its newest version, and rolled back to an older one.
//
// THE HOST HERE KEEPS STATE: a roll back moves `live`, so what the dialog shows after it is what the
// Host answers next, as in the product.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

import SitesDialog from '../SitesDialog.vue';
import type { Site, SiteDetail, SiteDocument, SiteVersion } from '../../api/sites';
import { useConsoleStore } from '../../stores/console';
import type { Team } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle } from '../../test/formProbe';

const order: SiteDocument = {
  collection: 'orders',
  id: 'o1',
  doc: {
    customer: 'Ana <b>Lopez</b>',
    total: 42.5,
    paid: true,
    note: null,
    tags: ['rush', 'gift'],
    address: { city: 'Lyon', zip: '69001' },
  },
  updatedAt: '2026-09-28T11:00:00Z',
  updatedBy: 'visitor',
};

const listed: SiteDocument = {
  collection: 'orders',
  id: 'o2',
  doc: ['not', 'an', 'object'],
  updatedAt: '2026-09-28T12:00:00Z',
  updatedBy: 'visitor',
};

function version(n: number, live: boolean): SiteVersion {
  return {
    version: n,
    live,
    publishedAt: `2026-09-2${n}T10:00:00Z`,
    publishedBy: 'dana@example.com',
    source: 'documents/sites/shop/files/a-rather-long-source-folder-name/',
    files: 3,
    bytes: 1024,
  };
}

/** The Host's state: which version is live, null when unpublished. */
let live: number | null = 3;
const posts: { url: string; body: unknown }[] = [];

function site(): Site {
  return {
    team: 'alpha',
    name: 'shop',
    liveVersion: live,
    publishedAt: live === null ? null : '2026-09-28T10:00:00Z',
    publishedBy: live === null ? null : 'dana@example.com',
    createdAt: '2026-09-01T10:00:00Z',
    createdBy: 'dana@example.com',
    documents: 2,
    dataBytes: 900,
    url: '/sites/alpha/shop/',
  };
}

function detail(): SiteDetail {
  return {
    site: site(),
    versions: [3, 2, 1].map((n) => version(n, n === live)),
    collections: ['orders'],
    url: '/sites/alpha/shop/',
  };
}

function reply(status: number, body?: unknown) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function host(url: string, init?: RequestInit): Response {
  const method = init?.method ?? 'GET';

  if (method === 'GET' && url === '/api/sites') return reply(200, [site()]);
  if (method === 'GET' && url === '/api/teams/alpha/sites/shop') return reply(200, detail());
  if (method === 'GET' && url === '/api/teams/alpha/sites/shop/data/orders') return reply(200, [order, listed]);
  if (method === 'POST' && url === '/api/teams/alpha/sites/shop/rollback') {
    const body = JSON.parse(String(init?.body)) as { version: number };
    posts.push({ url, body });
    live = body.version;
    return reply(200, site());
  }

  return reply(404, { error: `No route for ${method} ${url}.` });
}

beforeEach(() => {
  setActivePinia(createPinia());
  useConsoleStore().$patch({
    teams: [{ id: 'alpha', name: 'Alpha Team' }] as unknown as Team[],
    overviewLanded: true,
  });

  live = 3;
  posts.length = 0;
  localStorage.clear();
  vi.stubGlobal('fetch', vi.fn((url: string, init?: RequestInit) => Promise.resolve(host(url, init))));
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  resetBody();
});

async function open() {
  await mountDialog(SitesDialog, {}, { pinia: false });
  await settle();
}

async function openView(label: 'Versions of shop' | 'Data of shop') {
  await open();
  (bodyFind(`[aria-label="${label}"]`) as HTMLButtonElement).click();
  await settle();
}

/** A document's fields as `field: value` lines, in order. */
function fields(id: string): string[] {
  const doc = bodyFind(`[data-site-doc="${id}"]`)!;
  return Array.from(doc.querySelectorAll('[data-site-doc-field]')).map((row) => {
    const name = row.querySelector('dt')?.textContent?.trim();
    const value = row.querySelector('dd')?.textContent?.trim();
    return `${name}: ${value}`;
  });
}

const source = (file: string) => readFileSync(join(import.meta.dirname, file), 'utf8');

describe('a site\'s data', () => {
  it('reads each document as its fields and values, with no JSON until Show JSON', async () => {
    await openView('Data of shop');
    button('orders').click();
    await settle();

    expect(fields('o1')).toEqual([
      'customer: Ana <b>Lopez</b>',
      'total: 42.5',
      'paid: yes',
      'note: nothing',
      'tags: rush, gift',
      'address.city: Lyon',
      'address.zip: 69001',
    ]);
    expect(bodyFind('[data-site-doc="o1"] pre')).toBeNull();
    // Whatever the site stored is text, never markup.
    expect(document.body.querySelector('[data-site-doc] b')).toBeNull();
  });

  it('shows every document\'s JSON behind one Show JSON switch, as Admin > Log does', async () => {
    await openView('Data of shop');
    button('orders').click();
    await settle();

    const toggle = bodyFind('[data-site-detail] [data-show-json]')!;
    expect(toggle.textContent).toContain('Show JSON');

    toggle.click();
    await settle();

    expect(bodyFind('[data-site-doc="o1"] pre')?.textContent).toBe(JSON.stringify(order.doc, null, 2));
    expect(bodyFind('[data-site-doc="o1"] [data-site-doc-field]')).toBeNull();
  });

  it('shows a document that is not an object as its JSON either way', async () => {
    await openView('Data of shop');
    button('orders').click();
    await settle();

    expect(bodyFind('[data-site-doc="o2"] pre')?.textContent).toBe(JSON.stringify(listed.doc, null, 2));
  });
});

describe('the Versions table', () => {
  it('fits its dialog: no column a person can widen past it, actions never wrap or clip, long text wraps, nothing scrolls sideways', async () => {
    await openView('Versions of shop');

    const table = bodyFind('[data-site-versions]')!;
    expect(table).not.toBeNull();
    // A pinned column width (a resize remembered in this browser) is what pushed the actions out
    // of the dialog, so this table is laid out by the browser every time.
    expect(table.querySelectorAll('.os-col-resizer')).toHaveLength(0);

    const actions = table.querySelectorAll('td.site-version-action');
    expect(actions.length).toBe(3);

    // happy-dom lays nothing out, so the rules that do it are pinned.
    const vue = source('../SitesDialog.vue');
    const rule = (selector: string) =>
      vue.match(new RegExp(`\\n${selector.replace(/[.()]/g, '\\$&')} \\{([^}]*)\\}`))?.[1] ?? '';

    expect(rule('.site-version-action')).toMatch(/white-space:\s*nowrap;/);
    expect(rule('.site-version-action')).toMatch(/width:\s*1%;/);
    expect(rule('.site-versions td')).toMatch(/overflow-wrap:\s*anywhere;/);
    expect(rule('.site-versions')).toMatch(/overflow-x:\s*hidden;/);
  });

  it('keeps the Version column and moves the Live badge on a roll back, without reopening', async () => {
    await openView('Versions of shop');

    button('Roll back to v1').click();
    await settle();

    expect(posts).toEqual([{ url: '/api/teams/alpha/sites/shop/rollback', body: { version: 1 } }]);

    const row = (n: number) => bodyFind(`[data-site-version="${n}"]`)!;
    expect(row(1).querySelector('td')?.textContent).toContain('v1');
    expect(row(1).querySelector('[data-site-version-live]')?.textContent).toContain('Live');
    expect(row(3).querySelector('[data-site-version-live]')).toBeNull();
    expect(row(3).querySelector('td')?.textContent?.trim()).toBe('v3');
    expect(() => button('Roll back to v1')).toThrow();
    expect(button('Roll back to v3')).toBeTruthy();
  });
});

describe('an unpublished site', () => {
  it('offers Publish for its newest version and Roll back for the older ones', async () => {
    live = null;
    await openView('Versions of shop');

    expect(button('Publish v3')).toBeTruthy();
    expect(button('Roll back to v2')).toBeTruthy();
    expect(button('Roll back to v1')).toBeTruthy();
    expect(() => button('Roll back to v3')).toThrow();
    expect(bodyText()).not.toContain('Live');
  });

  it('publishes its newest version again with Publish, and then offers it no action', async () => {
    live = null;
    await openView('Versions of shop');

    button('Publish v3').click();
    await settle();

    expect(posts).toEqual([{ url: '/api/teams/alpha/sites/shop/rollback', body: { version: 3 } }]);
    expect(bodyFind('[data-site-version="3"] [data-site-version-live]')).not.toBeNull();
    expect(() => button('Publish v3')).toThrow();
    expect(button('Roll back to v2')).toBeTruthy();
  });

  it('a published site offers no Publish at all', async () => {
    await openView('Versions of shop');

    expect(bodyText()).not.toContain('Publish v');
  });
});
