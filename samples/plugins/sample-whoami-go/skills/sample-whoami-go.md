---
name: plugin-sample-whoami-go
description: Use when you need to check that a Google connection works - it reports which account a plugin member acts as, and with which scopes.
roles: manager member
---

# Sample Who Am I, Go (plugin member)

A plugin member, not an agent: it runs no model and reads no prompt. Send it any instruction with
`tell`; the text is ignored. Each run asks Google who the bound connection's account is and reports
it.

## What comes back

`output` is two lines:

- `account <email>` - the account Google says the access token belongs to;
- `scopes <scope> <scope> ...` - the scopes the connection was granted.

When no connection is bound, or the connection needs reconnecting, the platform blocks the run
before it starts, with a sentence naming the slot or the connection. A person fixes it in Admin,
Connections, or in the member's settings. Never ask anybody for a token.
