---
name: integrations
description: How to use the person's own tools (gh, az, MCP servers, APIs) offered to your team - check cli_commands or action list first, give a reason, wait to be woken, and report what is refused.
roles: [manager, member]
---
# Integrations

Integrations are the person's own tools, offered to your team and reached as tools on the app's MCP: `gh`, `az`,
`<server>__<tool>` for an MCP server's tools, `api_<name>` for each of the person's APIs, `web_<name>` for each of their websites, plus `pr_open`, `pr_status`, `cli_help` and `cli_commands`.

## How to use them

- Call `cli_commands` first. It shows each command's state for your team: what runs, what asks the person first,
  and what is refused. The person may allow even a destructive command; a command on their never list is always
  refused. Use `cli_help` when you need a command's usage.
- Always give a one-line `reason`: what the call does and, if it changes something, how to roll it back.
- A call that asks returns at once. The person chooses: approve once, always allow, keep asking, decline or never.
  The choice is theirs, not yours. You are woken with the result: do not ask again.
- You never receive the person's sign-in or tokens, only the output of the call.
- If something your work needs is refused, say so in your report rather than working around it.

## APIs

- Each API is one tool, `api_<name>`, taking an `action`, its `params` and a `_reason`.
- Start with `action list` (optionally `params.filter`) to see the actions your team may use, then
  `action describe` with `params.action` for that action's parameters. Neither is a call and neither asks.
- Path, query and header parameters go in `params` by name; a request body goes in `params.body`.
- The app signs the call in on the person's computer: you never see a key or token, and you cannot add one.
- An answer saying a connection needs reconnecting or that the API needs a connection means stop using that API and
  tell the Manager (Managers: tell the person); do not retry.
- An action can also be refused for missing access: the person signed the API in with OAuth without the permission that action needs. Stop
  using that action and tell the Manager (Managers: tell the person) that the API needs signing in again in Settings; never ask for a
  token or try to sign in yourself.

## MCP servers

- A remote MCP server's tools look the same as a local one's: `<server>__<tool>`, with a `_reason`, ruled on and logged
  the same way. You do not need to know where the server runs.
- An answer saying the server "needs you to sign in again" means stop using that server and tell the Manager (Managers:
  tell the person); do not retry. The person signs in again in the app.
- You never receive a server's token, key or sign-in: the app adds it on the person's computer.

## Websites

- Each website is one tool, `web_<name>`, with six fixed actions: open, read and screenshot, and click, fill and submit.
  Use `action list` and `action describe` the same way as for an API.
- The app opens the page in its own browser window on the person's computer, signed in as the person if they gave the
  site a login. Pages outside the site's own address are stopped: do not try to leave the site.
- Click, fill and submit may be off for your team or ask the person first; a call that asks returns at once and you are
  woken with the answer. Always say in the `_reason` what the click or form does.
- If a website says you are signed out, stop and tell the Manager (Managers: tell the person); never try to sign in yourself.
  Do not type a username or password into a page. The person signs in again in the app.
