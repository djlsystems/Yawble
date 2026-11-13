---
name: writing-skills
description: The quality bar for writing or reviewing a skill, such as one the person adds in Settings, then Skills.
roles: [manager, member]
---
# Writing skills

A skill is a procedure an agent loads when it needs it (`skills_list`, then `skills_get`). The person can add
their own in the app under Settings, then Skills. A good skill captures something reusable, not the story of
one run.

## Shape

```
---
name: <kebab-case-name>
description: <one line: when to load it>
roles: [manager] | [member] | [manager, member]
---
# Title
```

## Checklist

- **The description says when to use it.** It is all an agent sees before loading, so name the situation
  ("Use when testing a web app in a browser"), not the topic.
- **Roles are chosen on purpose.** A skill is offered only to the roles it names; a Manager does not write code,
  so code procedures are usually for members.
- **The body is short, ordered and runnable.** Steps in the order they happen, exact commands, real tool
  names. Cut background the agent does not need to act.
- **Assumptions are explicit.** Which tools must be installed, which network hosts must be allowed, which
  environment variables must be set, which folder to work in.
- **Risky steps name their safeguards.** Before anything that deletes, overwrites or reaches outside the
  team, say what to check first and when to stop and ask (`needs_decision`).
- **It says what to report.** What a good hand-back from this procedure contains.
- **It matches what the app really does.** No tools, folders or features that do not exist; no credentials
  or secret values in the text, ever.

## Reviewing one

Read it as the agent that will load it: could you follow it with nothing else? Try the commands. If a step is
vague, a name is wrong, or it contradicts the role texts or another skill, fix it or say exactly what is wrong.
