import { vi } from 'vitest';
import { asDocumentsFolderKey, asTeamId, type DocumentEntry, type DocumentsFolder } from '../api/types';

/**
 * A FAKE DOCUMENTS HOST AT THE FETCH BOUNDARY, for the Documents explorer's mount specs.
 *
 * `fetch` is stubbed, never `api/documents`: the explorer's own client code builds every URL and
 * body, and the specs assert on what actually crossed the boundary. That is also how "Paste, Move
 * to… and a drop send the same request" is checked - by comparing requests, which a spy on a
 * module export cannot see from inside a composable's closure.
 *
 * The shapes are the 3.2 contract's: `GET /api/documents` answers `{ folders, root }`; a listing is
 * an array of entries; move, copy and rename answer `{ results }`, or 409 with `clashes` or
 * `results`; upload answers the entry, `{ skipped, path }`, or 409 with `clashes`.
 */

export interface DocumentsCall {
  method: string;
  url: string;
  route: 'root' | 'list' | 'move' | 'copy' | 'rename' | 'upload' | 'folders' | 'delete' | 'other';
  folder: string;
  query: URLSearchParams;
  /** The JSON body, or the multipart form's fields as an object. */
  body: unknown;
}

export interface Reply {
  status: number;
  body?: unknown;
}

export const aFolder = (
  team: string,
  label: string,
  { exists = true, retired = false, folder = team, entries = 2 } = {},
): DocumentsFolder => ({
  folder: asDocumentsFolderKey(folder),
  team: asTeamId(team),
  label,
  exists,
  retired,
  entries,
  modifiedAt: '2026-09-01T00:00:00Z',
});

export const aFile = (path: string, size = 12, modifiedAt = '2026-09-01T00:00:00Z'): DocumentEntry => ({
  name: path.slice(path.lastIndexOf('/') + 1),
  path,
  isFolder: false,
  size,
  modifiedAt,
  children: 0,
});

export const aDir = (path: string, children = 1): DocumentEntry => ({
  name: path.slice(path.lastIndexOf('/') + 1),
  path,
  isFolder: true,
  size: 0,
  modifiedAt: '2026-09-01T00:00:00Z',
  children,
});

/** Alpha (live, with reports), Beta (live, empty), Gone (a deleted team) and a superseded Alpha. */
export const StandardFolders = (): DocumentsFolder[] => [
  aFolder('alpha', 'Alpha', { entries: 3 }),
  aFolder('beta', 'Beta', { entries: 0 }),
  aFolder('gone', 'Gone', { exists: false }),
  aFolder('alpha', 'Alpha', { retired: true, folder: 'alpha~2026' }),
];

export const StandardListings = (): Record<string, DocumentEntry[]> => ({
  'alpha:': [aDir('reports', 4), aFile('notes.md', 40)],
  'alpha:reports': [aDir('reports/2026', 0), aFile('reports/a.md', 100), aFile('reports/b.pdf', 2048), aFile('reports/c.png', 50)],
  'alpha:reports/2026': [],
  'beta:': [],
  'gone:': [aFile('left.md'), aFile('over.txt')],
  'alpha~2026:': [aFile('old.md')],
});

const leaf = (path: string) => path.slice(path.lastIndexOf('/') + 1);
const join = (parent: string, name: string) => (parent ? `${parent}/${name}` : name);

function respond(reply: Reply) {
  const text = reply.body === undefined ? '' : JSON.stringify(reply.body);

  return {
    ok: reply.status < 400,
    status: reply.status,
    statusText: '',
    json: async () => reply.body,
    text: async () => text,
  };
}

function formFields(form: FormData): Record<string, unknown> {
  const fields: Record<string, unknown> = {};
  form.forEach((value, name) => {
    fields[name] = typeof value === 'string' ? value : (value as File).name;
  });

  return fields;
}

export function documentsServer(
  options: { folders?: DocumentsFolder[]; listings?: Record<string, DocumentEntry[]>; root?: string } = {},
) {
  const folders = options.folders ?? StandardFolders();
  const listings = options.listings ?? StandardListings();
  const root = options.root ?? '/data/documents';
  const calls: DocumentsCall[] = [];
  const queued = new Map<string, Reply[]>();

  /** The next answer to a route; answers queue in order, then the default applies. */
  function reply(route: DocumentsCall['route'], status: number, body?: unknown) {
    const list = queued.get(route) ?? [];
    list.push(body === undefined ? { status } : { status, body });
    queued.set(route, list);
  }

  function defaultReply(call: DocumentsCall): Reply {
    switch (call.route) {
      case 'root':
        return { status: 200, body: { folders, root } };
      case 'list': {
        const path = call.query.get('path') ?? '';
        if (call.query.get('recursive') === 'true') {
          const files = Object.entries(listings)
            .filter(([key]) => key.startsWith(`${call.folder}:`))
            .flatMap(([, entries]) => entries.filter((entry) => !entry.isFolder && entry.path.startsWith(path)));
          return { status: 200, body: files };
        }
        return { status: 200, body: listings[`${call.folder}:${path}`] ?? [] };
      }
      case 'move':
      case 'copy': {
        const body = call.body as { to: { path: string }; items: { path: string }[] };
        return {
          status: 200,
          body: { results: body.items.map((item) => ({ from: item.path, to: join(body.to.path, leaf(item.path)), outcome: 'done' })) },
        };
      }
      case 'rename': {
        const body = call.body as { items: { path: string; name: string }[] };
        return {
          status: 200,
          body: {
            results: body.items.map((item) => ({
              from: item.path,
              to: join(item.path.includes('/') ? item.path.slice(0, item.path.lastIndexOf('/')) : '', item.name),
              outcome: 'done',
            })),
          },
        };
      }
      case 'upload': {
        const fields = call.body as { file: string; path: string };
        return { status: 200, body: aFile(join(fields.path, fields.file)) };
      }
      case 'folders':
        return { status: 200, body: aDir((call.body as { path: string }).path, 0) };
      case 'delete':
        return { status: 204 };
      default:
        return { status: 404, body: { error: 'No such route in the fake.' } };
    }
  }

  const fetch = vi.fn(async (input: unknown, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? 'GET';
    const parsed = new URL(url, 'http://test');
    const team = parsed.pathname.match(/^\/api\/teams\/([^/]+)\/documents(\/[a-z]+)?$/);

    let route: DocumentsCall['route'] = 'other';
    if (parsed.pathname === '/api/documents') route = 'root';
    else if (team) {
      const suffix = team[2] ?? '';
      if (suffix === '' && method === 'GET') route = 'list';
      else if (suffix === '' && method === 'DELETE') route = 'delete';
      else if (['/move', '/copy', '/rename', '/upload', '/folders'].includes(suffix)) route = suffix.slice(1) as DocumentsCall['route'];
    }

    let body: unknown = undefined;
    if (init?.body instanceof FormData) body = formFields(init.body);
    else if (typeof init?.body === 'string') body = JSON.parse(init.body);

    const call: DocumentsCall = {
      method,
      url,
      route,
      folder: team ? decodeURIComponent(team[1]!) : '',
      query: parsed.searchParams,
      body,
    };
    calls.push(call);

    const next = queued.get(route)?.shift();

    return respond(next ?? defaultReply(call));
  });

  vi.stubGlobal('fetch', fetch);

  return {
    fetch,
    calls,
    reply,
    folders,
    listings,
    /** Every call to one route, in order. */
    callsTo: (route: DocumentsCall['route']) => calls.filter((call) => call.route === route),
  };
}
