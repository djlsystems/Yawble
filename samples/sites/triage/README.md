# Triage: a sample site

A list over the `items` collection with **Done** and **Assign** actions. It is the end-to-end check
for sites and the worked example in the `building-sites` skill; see [docs/sites.md](../../../docs/sites.md).

The page never changes an item itself. A click posts an action to the team's log, an event trigger
wakes a member, the member's run updates the item with the `site` tool, and the page shows the change
on its next read (every five seconds, and on focus).

| File | What it is |
|---|---|
| `index.html` | The page. It includes `/sites/_sdk/site.js` and `app.js`; nothing inline, because the site policy allows no inline script or style. |
| `app.js` | Reads `items` with `site.data.list`, renders every field with `textContent`, and posts `done` `{id}` and `assign` `{id, assignee}` with `site.action`. |
| `site.css` | Its styles. |

An item is a document in `items`:

```json
{ "title": "Printer on 3 is jammed", "detail": "Reported by the front desk.", "status": "open", "assignee": "" }
```

## Publish it

From an agent on the team, with the `site` tool (the folder is this one, in the member's worktree):

    site  action: create   site: triage
    site  action: publish  site: triage  folder: <worktree>/samples/sites/triage
    site  action: show     site: triage

`show` answers the versions and the path a signed-in person opens, `/sites/<team>/triage/`. It is also
listed in Admin → Sites, with Open.

## Seed it

With the tool:

    site  action: data  op: put  site: triage  collection: items  id: t1  doc: {"title":"Printer on 3 is jammed","status":"open"}

Or from a plugin member of the same team, one record per line on its stdout:

    {"t":"site.put","site":"triage","collection":"items","id":"t1","doc":{"title":"Printer on 3 is jammed","status":"open"}}

## Wire the trigger

A person adds an event trigger in the team's Triggers dialog:

- event type `site.action`, filter `siteAction eq triage/done` (or `site eq triage` for both actions);
- the member to wake, and an instruction such as

      A person marked {event.payload} done on the triage site ({event.by}). Set that item's status to
      "done" with the site tool: read it with data get, then data put it back with "status":"done".

`{event.payload}` is the action's payload, `{"id":"t1"}`. An `assign` trigger does the same with
`siteAction eq triage/assign` and sets `assignee`.
