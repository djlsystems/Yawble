# Sites

A **site** is a small web page a team publishes and the platform serves and backs: a job tracker
with Apply buttons, a triage queue, an approval page. It has no server of its own, because the
platform is its backend:

- **static files** (HTML, JS, CSS, images, JSON) the Host serves;
- **a data store**: per site, named collections of JSON documents, shared by the page, the team's
  agents and the team's plugins;
- **actions**: a click on the page arrives in the team's log as a `site.action` event, and the
  ordinary event triggers turn it into work.

Nothing in a site runs on the Host.

## The model

| Thing | What it is |
|---|---|
| Name | A slug, unique within its team: 1-63 lower-case letters, digits and single hyphens. |
| Version | A copy of the published folder in `<dataRoot>/sites/<team>/<site>/v<n>/`. The numbers are never reused. |
| Live version | The one served, or none (unpublished). Only the database says which, so a version half copied, or a working copy an agent is still editing, is never what a person sees. |
| Collections | Named (a slug) sets of documents: `id` (1-128 of letters, digits, `.`, `_`, `-`) to a JSON value, with `updatedAt` and `updatedBy`. |

### Publishing

Publishing copies a folder the team may write - its documents folder, or anything under its team
folder (a worktree, a workspace) - into a new version, and makes that version live once the copy is
complete, in one transaction with its `site.published` row in the tenant log. The folder must hold
an `index.html` and must not hold a top-level `_api` (that path is the site's data and actions).
Symbolic links and names starting with `.` are not copied. A publish is bounded at 2 000 files and
100 MB, so publishing the wrong folder (a whole repository) is refused rather than copied.

The last five versions are kept, the live one always among them. **Rollback** makes an earlier kept
version live again (the one before the live one, or a version named). **Unpublish** stops serving
the site and keeps its files and data. **Delete** asks first: without confirmation it answers what
would be lost and changes nothing; confirmed, it removes the site, its versions and its data.
Deleting a team removes all of its sites and their data. Every one of these appends its
`tenant_events` row (`site.created`, `site.published`, `site.rolled-back`, `site.unpublished`,
`site.deleted`) in the same transaction as the change. Data writes are recorded on the document
itself (`updatedAt`, `updatedBy`), not in the tenant log, because a page may write on every click.

### Limits

Each is refused with a sentence that names it:

| Limit | Value |
|---|---|
| One document | 64 KB of JSON text |
| Documents in one collection | 10 000 |
| All the data of one site | 50 MB |
| One action's payload | 16 KB of JSON text |
| An action's name | a slug |

## How each browser request is authorized

An agent writes a site's pages, and an agent's pages may carry text it scraped from anywhere (a job
posting, an email). A script smuggled in that way must never run with the person's session, or it
could call the whole API as that person. So the session cookie authorizes exactly one site request,
the entry, and everything the page does after that is authorized by a **capability**.

| Request | Authorized by | Answer |
|---|---|---|
| `GET /sites/<team>/<site>/` (the entry) | The session cookie of a signed-in person. A member's key is refused (`HumansOnly`). | 302 to `/sites/<team>/<site>/_c/<capability>/`, `Cache-Control: no-store`. 401 with a sentence when nobody is signed in, 404 when there is no such site or it is not published. |
| `GET /sites/<team>/<site>/_c/<capability>/<path>` (the site's files) | The capability in the path, and nothing else. | The file, under the site policy below. |
| `GET …/_c/<capability>/_api/data/<collection>` | The capability. | The collection's documents. |
| `GET …/_c/<capability>/_api/data/<collection>/<id>` | The capability. | One document, or 404. |
| `POST …/_c/<capability>/_api/data/<collection>/<id>` | The capability. | Writes the body (the document's JSON, `text/plain`). |
| `POST …/_c/<capability>/_api/data/<collection>/<id>/delete` | The capability. | Deletes the document. |
| `POST …/_c/<capability>/_api/actions/<name>` | The capability. | Appends one `site.action` row. |
| `GET …/_c/<capability>/_api/whoami` | The capability. | `{"displayName": …}`: the part of the person's email before the `@`. Never the email, never a credential. |
| `GET /sites/_sdk/site.js` | Nobody: it is the same file for everyone and holds nothing. | The helper script. |

**The capability** is issued by the entry to the signed-in person for that one site. It is a Data
Protection payload under its own purpose, sealed with the instance's key ring: it names the team, the
site and the person, it cannot be altered, and it expires after an hour. It is carried as a path
segment, so a page's relative links, its images and the helper script's calls all inherit it with no
header and no cookie.

- It is checked against the route's team and site: a capability for another site, of this team or
  another, is refused (403).
- An altered or expired one is refused (401). A browser's own navigation with an expired one (a
  reload: `Sec-Fetch-Mode: navigate`, which a page's script cannot set) is sent back to the entry,
  which asks for the cookie and issues a new one.
- It is not a credential anywhere else. As an `X-Api-Key`, a bearer token or a cookie it is an
  unknown string, and every `/api` route and `/mcp` answers 401.
- It allows reading the site's files, reading and writing its data and posting its actions, and
  nothing more. The site must be live: an unpublished or deleted site refuses every capability.

**The cookie never authorizes site data or actions.** The capability routes never read it, and a
signed-in person's browser presenting the cookie with no valid capability is refused. It would not
arrive anyway: the cookie is `SameSite=Strict`, and a request made by an opaque origin is never
same-site.

The data and action calls are GETs and `text/plain` POSTs, so they are CORS *simple requests* and
need no preflight. Their answers carry `Access-Control-Allow-Origin: *` without credentials: the page
is in an opaque origin, so every call it makes is cross-origin, and what authorizes it is the
capability in the path, never anything ambient.

### The site policy

Every response under a valid capability carries this CSP **in place of** the app's, set through
`SecurityHeaders.PolicyOverride` and nothing else (`<files>` is
`<origin>/sites/<team>/<site>/_c/<capability>/`):

```
sandbox allow-scripts allow-forms; default-src 'none';
script-src <files> <origin>/sites/_sdk/site.js; style-src <files>; img-src <files>; font-src <files>;
connect-src <files>_api/; form-action 'none'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'
```

- `sandbox` with `allow-scripts` and `allow-forms` but **not** `allow-same-origin`: the page's script
  runs, in an **opaque origin**. It cannot read the app's cookie, storage or DOM.
- No `allow-top-navigation`, no `allow-popups` and so no `allow-popups-to-escape-sandbox`.
- Scripts, styles, images and fonts come from the site's own files under this capability only, plus
  the helper script. Nothing inline, no `data:`, nothing from anywhere else.
- `connect-src` is the site's own `_api/` under this capability: its data and actions, nothing else.
- `form-action 'none'`: a form is handled by script, and can never post to `/api` as the person.

The entry's redirect, a refused capability, the helper script and every other response keep the
app's own policy, which is unchanged. The sources carry the request's scheme and host, because a CSP
path needs an origin; under CSP Level 3 an `http:` source also matches the same host over `https:`,
so a TLS tunnel in front of the Host is covered, as long as it passes the browser's `Host` through.

### Why there is no localStorage

`localStorage`, `sessionStorage`, IndexedDB and cookies belong to an origin. A sandboxed page without
`allow-same-origin` has an opaque origin, which has no storage: the browser throws a `SecurityError`
when the page touches `localStorage`. That is the point of the sandbox - an origin with storage would
be the app's origin, and a page there could read the app's own storage and act with its session.
Keep state in the site's data store instead; it is shared with the team's agents and plugins, which
`localStorage` never could be.

## The helper script

Include it as a plain script, with no build step:

```html
<script src="/sites/_sdk/site.js"></script>
```

It defines `window.site`. Every call returns a Promise, and a refusal rejects with an `Error` whose
`message` is the platform's sentence and whose `status` is the HTTP status.

| Call | Answers |
|---|---|
| `site.data.list(collection)` | `[{ id, doc, updatedAt, updatedBy }]` |
| `site.data.get(collection, id)` | `{ id, doc, updatedAt, updatedBy }`, or `null` |
| `site.data.put(collection, id, doc)` | the document as written |
| `site.data.delete(collection, id)` | `true` when there was such a document |
| `site.action(name, payload)` | `{ seq }`, the `site.action` row's seq |
| `site.whoami()` | `{ displayName }` |

It reads the capability from the page's own address and sends it on every call; it sends no
credential.

### A complete small example

`index.html`:

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>Jobs</title>
  <link rel="stylesheet" href="site.css">
  <script src="/sites/_sdk/site.js"></script>
  <script src="app.js" defer></script>
</head>
<body>
  <h1>Jobs for <span id="who"></span></h1>
  <ul id="jobs"></ul>
</body>
</html>
```

`app.js`:

```js
async function render() {
  const list = document.getElementById('jobs');
  list.replaceChildren();

  for (const job of await site.data.list('jobs')) {
    const item = document.createElement('li');
    const title = document.createElement('span');
    title.textContent = job.doc.title;          // textContent, never innerHTML, for fetched text

    const apply = document.createElement('button');
    apply.textContent = job.doc.applied ? 'Applied' : 'Apply';
    apply.disabled = !!job.doc.applied;
    apply.addEventListener('click', async () => {
      await site.data.put('jobs', job.id, { ...job.doc, applied: true });
      await site.action('apply', { id: job.id });
      await render();
    });

    item.append(title, ' ', apply);
    list.append(item);
  }
}

site.whoami().then((me) => { document.getElementById('who').textContent = me.displayName; });
render();
window.addEventListener('focus', render);         // no push: re-read on focus or on an interval
```

A plugin that feeds the page writes `{"t":"site.put","site":"jobs","collection":"jobs","id":"j1","doc":{"title":"Engineer"}}`
on its stdout (see [plugins.md](plugins.md)).

## Actions and triggers

`site.action(name, payload)` appends ONE row to the team's log:

- type `site.action`, source `site:<team>/<site>`, and no causation, so it roots a new workflow as a
  trigger's fire does;
- payload `{ team, site, action, siteAction, payload, by, at }`: `siteAction` is `<site>/<action>`,
  `payload` is what the page sent, `by` is the signed-in person's email and `at` is when the Host
  received it.

An **event trigger** on `site.action` narrows with its filter:

- `site eq jobs` - every action on the `jobs` site;
- `siteAction eq jobs/apply` - only `apply` on `jobs`.

Its instruction can use the fields, for example `Apply for {event.payload} ({event.by})`. The
trigger's fire joins the action's workflow. An action widens nothing the team can do: it is an event,
and what follows is the ordinary trigger path. A plugin's outward-acting settings still need their
person-only allowlist.

## For agents: the `site` tool

A team's Manager, its members and the Concierge (which passes `team`) use the `site` MCP tool; the
built-in skill `building-sites` teaches it. A member or Manager acts on its own team's sites only.

| Tool call | What it does |
|---|---|
| `site action: create site: triage` | An empty site. |
| `site action: publish site: triage folder: <absolute path>` | A new version from the folder, live once copied. |
| `site action: list` | The team's sites. |
| `site action: show site: triage` | Its versions, collections, data size and the path a person opens. |
| `site action: rollback site: triage [version: n]` | An earlier kept version made live. |
| `site action: unpublish site: triage` | Stops serving it; keeps files and data. |
| `site action: data op: list\|get\|put\|delete site: triage collection: items [id: t1] [doc: {...}]` | The data store. |
| `site action: actions site: triage [take: 20]` | The recent `site.action` rows, read only. |

The tool has no delete: **deleting a site is a person's action**, in Admin → Sites.

### The routes behind it

The tool relays to these with the caller's own key; Admin → Sites calls them with the cookie.

| Route | Marker |
|---|---|
| `GET /api/teams/{team}/sites`, `GET …/sites/{site}`, `GET …/sites/{site}/data/{collection}[/{id}]`, `GET …/sites/{site}/actions?take=` | `Read` |
| `POST /api/teams/{team}/sites` `{name}`, `POST …/{site}/publish` `{folder}`, `POST …/{site}/rollback` `{version?}`, `POST …/{site}/unpublish`, `PUT …/{site}/data/{collection}/{id}` (the document's JSON), `DELETE …/{site}/data/{collection}/{id}` | `Sites` |
| `GET /api/sites` (every team, for Admin → Sites), `DELETE /api/teams/{team}/sites/{site}[?confirm=true]` | `HumansOnly` |

`Sites` is its own permit. A team's Manager, every agent member and the Concierge hold it by default;
`TeamGate` bounds a container to its own team, and another team's site answers what a missing team
does. The delete answers 409 with what would be lost until it is confirmed.

## Admin → Sites

The Admin group's **Sites** button lists every site across teams: name, team, live version,
published at and by, and data size. Each row has **Open** (a new tab, through the entry above),
**Versions** (roll back), **Unpublish** (keeps files and data), **Delete** (asks first, showing what
would be lost) and a read-only view of each collection. The Active Team group's **Sites** button opens
the same list filtered to that team.

## For plugins

A plugin member writes with the `site.put` and `site.delete` records, for its own team's sites only
(see [plugins.md](plugins.md)); another team's site, a missing one or a limit drops the record with
one progress warning.

## A complete example: `samples/sites/triage`

[`samples/sites/triage`](../samples/sites/triage) is a triage queue over the `items` collection with
**Done** and **Assign** actions, and the end-to-end check for everything above:

1. **Publish** from an agent: `site action: create site: triage`, then
   `site action: publish site: triage folder: <worktree>/samples/sites/triage`.
2. **Seed** from a plugin of the team, one record per line:
   `{"t":"site.put","site":"triage","collection":"items","id":"t1","doc":{"title":"Printer on 3 is jammed","status":"open"}}`
   (or `site action: data op: put …` from an agent).
3. **Wire** an event trigger in the team's Triggers dialog: event type `site.action`, filter
   `siteAction eq triage/done`, the member to wake, and an instruction such as
   `A person marked {event.payload} done on the triage site ({event.by}). Read that item with the site tool, set its status to "done" and put it back.`
4. **Click** Done on the page. One `site.action` row roots a workflow, the trigger wakes the member,
   its run writes `"status":"done"`, and the page shows the item under Done on its next read.

The page includes only `/sites/_sdk/site.js` and its own `app.js` and `site.css`, renders every
field with `textContent`, and keeps what it has asked for in memory, since it has no `localStorage`.
