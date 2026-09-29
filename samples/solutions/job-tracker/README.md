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
