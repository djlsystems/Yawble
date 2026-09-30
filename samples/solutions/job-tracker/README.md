# Job Tracker

A sample solution package: a team that finds job postings, tracks them on a page, and drafts a cover
letter when you press **Apply**. It is the worked example in [docs/solutions.md](../../../docs/solutions.md)
and the package the solution tests install.

| Part | What it is |
|---|---|
| `solution.json` | The team: three members, five triggers, one skill, one site, what you provide. |
| `plugins/job-board/` | A stand-in job board plugin (Python, no network): reads `postings.json`, writes matches to the tracker and publishes `plugin.job-board.posting-found`. |
| `skills/job-search-playbook.md` | The team's own skill, offered to its Manager and members only. |
| `sites/tracker/` | The page: the postings, with **Apply** and **Not for me**. |
| `tools/make-cover-letter.py` | The first-draft generator the Writer runs as `{solution}/make-cover-letter.py`. |

## What you are asked for

- **Resume** (required): your reference resume, `.docx` or PDF, uploaded to the team's `Resume` folder.
- **The Scout's sources** (optional): which boards it may read. Until you tick `sample`, it reads none.

## The page fills right after install

"Scan for postings" sets `runAtInstall`, so the install runs it once as soon as its last step
succeeds, and then every hour as usual. With a source ticked, the tracker page fills right after
install instead of an hour later; the install's result says "Scan for postings ran now".

## Secrets

The install binds the Scout's five secrets by key name; you do not bind them by hand. Each is
needed only when you tick its source, and the install shows which the Host already has set.

| Key | For |
|---|---|
| `ADZUNA_APP_ID`, `ADZUNA_APP_KEY` | `adzuna` |
| `USAJOBS_API_KEY`, `USAJOBS_USER_AGENT` | `usajobs` |
| `THEMUSE_API_KEY` | `themuse` |

To set one, the operator runs `yawble secret set ADZUNA_APP_ID` (it prompts for the value) and then
`yawble up` to restart the Host. A key left unset is not an error: that source fails until it is
set. This sample's board is a stand-in and calls none of them; it says which ticked board has no keys.

## Check it

```
yawble solution check samples/solutions/job-tracker
```

## Version 1.1.0

`../job-tracker-1.1.0.overlay/` holds only what 1.1.0 changes. Copy this folder, then copy the overlay
over the copy, to get 1.1.0 (`SolutionSamples.JobTracker("1.1.0")` in the tests does exactly that):

```
cp -r samples/solutions/job-tracker /tmp/job-tracker-1.1.0
cp -r samples/solutions/job-tracker-1.1.0.overlay/. /tmp/job-tracker-1.1.0/
```
