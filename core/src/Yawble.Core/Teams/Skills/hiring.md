---
name: hiring
description: How a Manager builds its team - the hire list, which agent CLIs to hire, names, jobs, and how many members.
roles: [manager]
---
# Hiring

You build your own team with two tools:

- `hiring` shows who you may hire.
  - When the person set a hire list for your team, it lists its rows: `role`, `description` (the job a member of
    that role gets), `agent`, `model`, and whether the agent is `ready` on this computer now. The list is a hard limit:
    hire only those roles.
  - Without a hire list, it lists every agent CLI (claude, grok, codex, copilot, antigravity, and the person's custom
    agents) with `ready` and a reason.
  - An agent is ready when the person has installed it on this computer and signed in to it in their own terminal.
    Hire only ready agents; never ask the person to paste a key or token into anything.
- `hire` adds a member.
  - With a hire list: `role` (one of the list's, e.g. `role: "Developer"`). The row gives the agent, the model and the
    job; leave `cli` and `job` out (a different one is refused, and so is a role not on the list: the refusal names the
    roles you may hire). Put what the member is to do in the card you `tell` it.
  - Without a hire list: `name`, `cli` (default: your own), and `job`, the member's standing job on the team. You may
    also give `role` (a role of the person's catalog): the member gets that role's job when you give none.
  - The name is the job word alone, one word: `Developer`, `Tester`, `Researcher` (with a role, leave `name` out: it is
    the role). The app adds a first name from the person's name list, one no member of any team has
    (`Developer Priya`). The job word is constant across every member of that job; a job is always one word. Never a
    slot like `dev1`.
- If no free name is left on the list, `hire` is refused and says which country's names to use: call `hire` again
  with the job, a space, then a first name common in that country that no member of any team has (`Developer Amara`).
  A full name whose first name a member already has is refused the same way: choose another.
- `hire` answers with the member's id, the name without its space (`DeveloperPriya`): it names the member's folder
  and its branch `members/<team>/developerpriya` (`<team>` is the team's slug, as in `team/<team>`). Address the member by that id, every time.

## How many

- Hire for the cards you have now, not ahead of them. Split the work into independent cards that do not share files,
  then hire as many members of a role as there are independent cards for that role ready now.
- Stay within the computer's run slots (`status` ends with how many are in use of the limit): more members than slots
  only wait in the queue. Only one full test suite or build runs at a time (the heavy-work lease), so cards that each
  need one take turns anyway.
- Cards that depend on each other run in order with the members you have; adding people does not speed them up.
- A member you have no card for yet is not hired.

## Good practice

- Mixing CLIs is fine and often useful: a tester on a different CLI than the developer reviews with fresh eyes.
  If a CLI is not ready, use another (with a hire list, only one the list gives).
- Without a hire list, write the job as "<Role>: <what they own>", e.g. `Developer: writes the console app and its unit
  tests` or `Tester: builds the developer's branch, runs it, and reports exactly what passed and failed`.
- A new member starts idle with an empty clone of the team's repository. It does nothing until you `tell` it a
  card, so hire, then tell, then end your turn.
- Names are permanent for the team; pick a new name rather than reusing one for a different job.
