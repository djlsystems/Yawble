# Cloud Link

> Cloud Link is a preview.

Cloud Link lets you view and steer Yawble on your computer from a browser somewhere else. You attach the computer
to a cloud site, by default [cloud.yawble.ai](https://cloud.yawble.ai/), and open it there. The browser shows the
app's own screens: Concierge, Teams, Board, Backlog, Documents, Monitor and most of Settings.

The app works fully without it. Nothing is attached until you pair.

## Pairing

1. In the app, open **Settings > Cloud Link**.
2. Check the **Cloud site address**. It is `https://cloud.yawble.ai/` unless you type another. The address must use
   `https`; plain `http` is accepted only for a site on this computer.
3. Press **Pair**. The app asks the site for a pairing code, shows it, and opens the site's pair page in your
   browser with the code filled in.
4. On the site, sign in (or create an account), choose a workgroup and attach the computer. The code works for
   10 minutes.
5. The app shows **Connected**. You can close the dialog.

You never paste anything into the app. If the code expires before it is used, press **Pair again** for a new one.
**Cancel** stops a pairing in progress.

Once attached, the Cloud Link page shows the site, the name the computer has there, its organization and group,
and who is viewing.

## How it connects

- The desktop app makes one outbound WebSocket connection to the site. It opens no listening port and needs no
  inbound firewall rule.
- The site relays each viewer's calls over that connection. The app answers them with the same handlers as the
  desktop window, after the checks below.
- While someone is viewing, the app sends its live updates (terminal output, state changes) up the same connection.
- The computer's id and secret from pairing are kept in the app's data folder. They are never shown.
- When the connection drops, the app reconnects on its own, waiting a little longer each time (up to 30 seconds).
  The status reads **Not connected (retrying)** meanwhile.
- The app's state reads one of: **Not attached**, **Pairing**, **Connecting…**, **Connected**,
  **Not connected (retrying)**.

## What a remote viewer can do

A remote viewer can do what you can do in the window, with the exceptions below. That includes reading and telling
teams, answering their questions, the board and backlog, approving integration requests, answering network
requests (allowing a host an agent was refused, like any other policy change), running the guardrail check, and
changing agents and teams. A network request shows in the remote view's **Needs you** with the same four buttons; if
the computer refuses the answer, the item says why and to answer it in the window. A tool request shows there too,
with **Not now** only: it says to install it in the Yawble window on the computer.

Calls from a viewer run as that viewer. Every remote call that changes something, and every refused call, is
written to `cloud-link-audit.log` in the app's data folder, with the time, the viewer and the method.

## What only the person at the computer can do

These are refused over Cloud Link. The viewer sees the reason.

| Area | Refused remotely |
|---|---|
| Cloud Link | Changing its settings, detaching, pairing. |
| Credentials | Entering or removing model credentials. Their status and mode can be read. |
| Connections | Seeing, adding, changing or removing any Connection. |
| Sign-ins | OAuth sign-ins, website logins, MCP server sign-ins, finding a provider's sign-in addresses, opening a terminal to sign a CLI in. |
| APIs | Adding, removing or discovering an API (each of these makes the computer fetch addresses). |
| MCP servers | Importing servers from config files on the computer; adding or changing a remote server, its address, transport, sign-in or secrets; reading a remote server's tools. |
| Agent terminals | An agent's **Terminal** / **Test** session. It is started, typed into and stopped in the window only, and its output is not sent up. |
| Backups | **Back up now** and **Restore**. The list of backups can be read. |
| Host tools | **Install** (and **Try again**) for a tool an agent asked for: winget runs on the computer, so it is clicked there. The requests can be listed and answered **Not now**. |
| Companion | Pairing, approving, listing or revoking `yawble` companion clients. Nothing of the companion is sent up. |

### Host terminals

A host terminal is a shell on your computer, outside any sandbox. They are off for Cloud Link by default. While they
are off, a viewer also cannot:

- open or use a host terminal;
- add or change an MCP server (that defines a program the computer runs);
- define a host task;
- detect, enable, discover or save rules for a CLI the app does not know and that is not already an integration on
  this computer.

Turn on **Allow host terminals from the site** in Settings > Cloud Link to lift these. It is meant for signing in to
agent CLIs remotely.

### Members' code

**Show members' code to cloud viewers** is on by default. It lets viewers use the read-only code browser of each
member's working copy. Files that may hold secrets are never shown. Turn it off to refuse every code browser call
from the site.

### Concierge over Cloud Link

A Concierge message sent from the site starts a turn that counts as remote. In such a turn the Concierge cannot:

- create a team, or add or remove a team repository;
- close a workflow;
- delete, prune, push or sync branches, or open or merge a pull request;
- archive or delete a team, or set team environment variables;
- apply doctor fixes.

It also cannot take a "yes" sent from the site as confirmation for an action that needs one. It keeps its read
tools and the backlog and board actions.

## Turning it off

- **Detach this computer** in Settings > Cloud Link closes the link, tells the site, and deletes the computer's id
  and secret.
- Detaching the computer on the site has the same effect: the app notices, forgets the link and shows
  **Not attached**.
- To attach again, pair again.

See also: [Features](features.md), [Connections and integrations](connections-and-integrations.md).
