---
name: test-credentials
description: How to sign in to and test against anything that needs a credential - only the team's test variables, never a pasted or printed secret.
roles: [manager, member]
---
# Test credentials

The person gives the team test accounts and keys as team environment variables (in the app: the team's
Manage, then Environment). They reach every member's run as ordinary environment variables. They are test
values only, and they are the only credentials you use.

## Find what you have

List the names, not the values:

```powershell
Get-ChildItem env: | Select-Object -ExpandProperty Name
```

Look before you conclude something is missing. The card, or your Manager, should name the variables to use.

## The rules

1. Read a credential from its variable (`$env:TEST_API_KEY`) at the moment you use it. Never print it, echo it,
   or write it out: not in a report, a commit, a commit message, a test fixture, a log you keep, or
   `YAWBLE_TEAM_DOCS`. Name the variable instead.
2. Never ask the person to paste a key, password or token into a chat, a card, a document or a file. The only
   place a credential goes is the team's Environment.
3. Never invent an account or create a second one to get past a sign-in. An account one run makes up is
   invisible to everyone else and cannot be recovered.
4. If the thing you test is already set up with an account the team does not have, stop and say so; do not
   guess passwords or reset it.
5. Code you write reads credentials from the environment too. Keep `.env` files and the like out of commits.
6. If the service's host is refused by the network, that is the person's network policy, not a fault in the
   credential: say which host you need (see `dev-tools`).

## If a credential is missing

Call `needs_decision` naming exactly what is needed, for example: "Add a team environment variable
`TEST_SHOP_PASSWORD` (a test account's password for the staging shop) under the team's Manage, then
Environment." Then carry on with any work that does not need it, or end your turn. Never ask for the value
itself.

A Manager writing a card names the variable to use (`$env:TEST_SHOP_USER`), never its value.
