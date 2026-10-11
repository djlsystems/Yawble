---
name: job-search-playbook
description: Use when working the Job Tracker team's search - how postings move on the tracker and what a draft letter must hold.
roles: manager member
---

# Job search playbook

The tracker site's `jobs` collection is the one list of postings. Each document is a posting as the
Scout found it, plus what the team has added:

| Field | Set by | Meaning |
|---|---|---|
| `status` | Scout, Writer, the page | `new`, `applying` (the person pressed Apply), `drafted`, `sent`, `skipped` |
| `fit` | Writer | Two lines on how well the resume fits the posting. |
| `draft` | Writer | The path of the draft letter in the team's documents, once written. |

## A draft letter

- One page, addressed to the company by name.
- Opens with the role and where the person saw it.
- Three short paragraphs: why this role, the two resume items that fit it best, and a close.
- Saved as `Drafts/<job id>.md` in the team's documents. Nobody on the team sends it: the person does.

## What never happens

- No member applies, emails or posts anywhere. The page's Apply button asks for a draft, nothing more.
- No member edits the resume. If it looks out of date, say so to the Coordinator.
