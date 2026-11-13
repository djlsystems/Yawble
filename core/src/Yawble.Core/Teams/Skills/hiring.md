---
name: hiring
description: How a Manager builds its team - which agent CLIs to hire, names, jobs, and how many members.
roles: [manager]
---
# Hiring

You build your own team with two tools:

- `hiring` lists every agent CLI preset (claude, grok, codex, copilot, antigravity) with `ready` and a reason.
  A CLI is ready when the person has installed it on this computer and signed in to it in their own terminal.
  Hire only ready CLIs; never ask the person to paste a key or token into anything.
- `hire` adds a member: `name`, `cli` (default: your own), and `job`, the member's standing job on the team.
  Name them for the job, a space, then a first name: `Developer Priya`, `Tester Wren`, `Researcher Amara`. The job
  word is constant across every member of that job. Choose a different first name every time, never one copied from
  these instructions, never one starting with the job's letter, and never a slot like `dev1`.
- `hire` answers with the member's id, the name without its space (`DeveloperPriya`): it names the member's folder
  and its branch `members/developerpriya`. Address the member by that id, every time.

## Good practice

- Hire for the work, not ahead of it. Most tasks need one developer and one tester; a larger one may need
  two developers on separate files.
- Mixing CLIs is fine and often useful: a tester on a different CLI than the developer reviews with fresh eyes.
  If a CLI is not ready, use another.
- Write the job as "<Role>: <what they own>", e.g. `Developer: writes the console app and its unit tests` or
  `Tester: builds the developer's branch, runs it, and reports exactly what passed and failed`.
- A new member starts idle with an empty clone of the team's repository. It does nothing until you `tell` it a
  card, so hire, then tell, then end your turn.
- Names are permanent for the team; pick a new name rather than reusing one for a different job.
