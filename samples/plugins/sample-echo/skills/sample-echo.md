---
name: plugin-sample-echo
description: Use when you need a deterministic stand-in member that transforms text - for trying out plugin members.
roles: manager member
---

# Sample Echo (plugin member)

A plugin member, not an agent: it runs no model and reads no prompt. Send it work with `tell`,
exactly as to any member; its result comes back as its `completed` row, and wakes you like any
member's.

## What to send

The instruction text is the input. Each instruction in one batch is handled in order:

- plain text - transformed by the member's `mode`: `upper` (default) or `reverse`;
- `handback:<text>` - hands `<text>` back, then transforms it;
- `block:<text>` - reports that item as blocked with `<text>`;
- `fail:<text>` - fails the run with `<text>`;
- `sleep:<seconds>` - waits, for trying Stop;
- `publish:<type>` - also publishes an event of that type, for trying what the platform refuses.

## What comes back

`output` is the transformed lines, one per instruction, and `token length N` when the member has a
`token` secret bound. Each run that completes also publishes `plugin.sample-echo.done`, carrying
`length` (the output's length), in the same workflow: a trigger on that type can wake another
member with `{event.length}`.
