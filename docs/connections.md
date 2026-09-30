# Connections

A **connection** is one person's account at an OAuth service - a Gmail mailbox, an Outlook mailbox, a
Google Drive, Microsoft Graph - held by the Host so that plugin members can act on it. The Host holds
the provider's OAuth client and each account's refresh token, refreshes and rotates the tokens, and
hands a plugin only a fresh access token on each run. No plugin writes OAuth code, and no long-lived
credential ever leaves the Host.

- **Where:** Admin → **Connections** in the web UI, or `yawble connect` on the operator's computer.
- **Who:** people only. No agent tool, skill, environment variable or route gives an agent a token.
- **Not the agent home:** an account signed in for the Concierge in `/data/agent-home` (a CLI's
  connectors, MCP servers) reaches the Concierge only; members are launched without it. Anything a
  member should act on belongs here. See [architecture.md](architecture.md#the-shared-agent-home).
- **For plugin authors:** declare a slot in the manifest and read the token on stdin; see
  [plugins.md](plugins.md#connections-oauth-accounts).

## Providers

| Provider | What it is | Refresh token from | Account name from |
|---|---|---|---|
| `google` | Built in: Google's authorization and token endpoints, PKCE. | `access_type=offline`, `prompt=consent` | The ID token's `email`, else userinfo |
| `microsoft` | Built in: the Microsoft identity platform (`common` tenant), PKCE. | the `offline_access` scope | The ID token's `preferred_username` |
| `custom` | Anything else that speaks OAuth 2.0 authorization code with PKCE: you give the authorization URL, token URL, an optional userinfo URL and default scopes. | whatever the service needs, in its scopes | userinfo, when given |

Each provider needs its **client** - a client ID and a client secret you register at the provider -
entered once, in the Connections dialog (**Set up client**). The secret is stored encrypted with the
instance's Data Protection keys (`<dataRoot>/keys`); it is never in plain text, never in the
environment, and never returned by any route: the dialog shows only that it is **set**. Changing it
means typing a new one.

## Connecting an account from the web

1. Admin → **Connections** → **Connect an account**.
2. Pick the provider. If its client is not set up yet, the dialog asks for it first.
3. Choose the scopes. A plugin's slot lists the scopes it needs; connect with at least those.
4. The browser goes to the provider's consent page and comes back to `/api/connections/callback` on
   the Host, which checks the `state`, exchanges the code with the PKCE verifier and stores the
   connection.

The dialog shows the **exact redirect URI** to register at the provider for the address you are using
right now - `<the address in your browser>/api/connections/callback`. The provider must accept it:

- reached at `http://127.0.0.1:<port>` or `http://localhost:<port>`, register that;
- reached through a tunnel (`https://…`), register the tunnel's address, or use the CLI flow below,
  which never needs the Host's own address at the provider.

Each connection lists its provider, account, granted scopes, when it was connected and last
refreshed, its status (`ok`, or `needs reconnect` with the provider's reason) and the members that use
it. On each one:

- **Reconnect** - the same account again, with the new scopes added to the old. It clears
  `needs reconnect`.
- **Rename** - the name members and the CLI show.
- **Disconnect** - refused while any member uses it, naming them; unbind those first. It revokes the
  grant at the provider where the provider supports that, and deletes the tokens.

## Connecting an account from the CLI

For an instance reached through a tunnel, or at any address the provider will not accept as a
redirect, connect from the operator's computer:

```
yawble connect google --scopes openid,email,https://mail.google.com/ --name "Work mail"
yawble connect microsoft --scopes offline_access,https://outlook.office.com/IMAP.AccessAsUser.All
yawble connect list
yawble connect remove "Work mail"
```

`yawble connect <provider>`:

1. asks the Host to start a flow and gets back the authorization URL and `state`;
2. listens on a free port of this computer's `127.0.0.1` and opens the browser with the redirect
   `http://127.0.0.1:<port>/` - for `microsoft`, `http://localhost:<port>/`, the form Entra takes
   (`--port` picks the port, for a client that needs it registered exactly);
3. catches the code the provider sends back to that listener;
4. hands the code (with its `state`) to the Host, which holds the PKCE verifier from step 1 and does
   the exchange. **The client secret
   never leaves the Host**, and no token reaches this computer.

Without a browser on this computer, `connect` prints the URL to open by hand; the redirect still has
to reach this computer's `127.0.0.1`.

### How the CLI reaches the Host

The same way the other commands that talk to a running instance do (`yawble plugin install
--from-instance`, `yawble plugin list`): **through the container engine**, never over HTTP.

- `yawble` finds the running instance's container (`yawble up` first if it is not running) and
  runs a short script in it with `podman exec` / `docker exec`.
- The script writes a request file, `/data/connections/.connect`, from the request handed to it on
  **stdin** (it can carry the authorization code, so it is never on a command line). The folder and
  file belong to the Host's own user and nobody else can read them - no agent sees a request.
- The Host answers each request in `/data/connections/.connect-report.json`; the CLI waits for the
  answer to its own request (a random id in both) and withdraws a request no Host answered.
- Nothing is signed in and no port of the instance is used: whoever can run the engine on that
  computer already controls the instance. The Host records what was done in the tenant log.
- An image from before connections does not answer; the command says so after 20 seconds.
  `yawble update` brings the instance current.

## Registering a Google client

1. In the [Google Cloud console](https://console.cloud.google.com/), pick or create a project.
2. **APIs & Services → Library**: enable the APIs the plugins will use (Gmail API, Google Drive
   API, ...).
3. **Google Auth Platform → Branding** (the OAuth consent screen): the app's name and your support
   email. **Audience**: *External* unless every account is in your own Workspace (*Internal*).
4. **Data Access**: add the scopes the plugins ask for.
5. **Clients → Create client**:
   - **Desktop app** if you connect with `yawble connect`, or reach the instance at
     `http://127.0.0.1:<port>`: Google accepts any loopback port for it, so nothing is registered.
   - **Web application** if people connect from the web UI through a tunnel: add the redirect URI the
     Connections dialog shows (`https://<your address>/api/connections/callback`) under **Authorized
     redirect URIs**. A web client accepts only the URIs registered, port included, so use
     `yawble connect --port <n>` with `http://127.0.0.1:<n>` registered as well if you also use the CLI.
6. Copy the client ID and secret into Admin → Connections → **Set up client** for `google`.

### In production: so refresh tokens do not expire after 7 days

While an *External* app's publishing status is **Testing**, Google expires every refresh token it
issues after **7 days**, and every connection then turns `needs reconnect`. Publish it:

- **Google Auth Platform → Audience → Publishing status → Publish app**, to **In production**.
- An app asking for sensitive or restricted scopes (Gmail, Drive) that Google has not verified shows
  an "unverified app" warning on the consent page and is limited to 100 accounts. For an instance's
  own accounts that is enough: on the warning, **Advanced → Go to <app> (unsafe)**. Verification is
  only needed to go past that.
- An *Internal* app (Workspace only) has no Testing status and no 7-day limit.

## Registering a Microsoft client

1. In the [Microsoft Entra admin center](https://entra.microsoft.com/), **App registrations → New
   registration**.
2. **Supported account types**: *Accounts in any organizational directory and personal Microsoft
   accounts* for the built-in provider (it uses the `common` tenant).
3. **Redirect URI**: platform **Web**, and the URI the Connections dialog shows. For the CLI flow add
   `http://localhost` under the same platform **Web** - Entra ignores the port on a loopback
   redirect, so one entry covers every port `yawble connect` picks, and `yawble connect microsoft`
   sends `http://localhost:<port>/` for that reason. Keep it under **Web**: the Host sends the client
   secret, and Entra refuses a client secret on a redirect registered under a public-client
   platform.
4. **Certificates & secrets → New client secret**. Copy its **Value** (not its ID) at once; Entra
   never shows it again. Note when it expires, and set a new one in the Connections dialog before it
   does.
5. **API permissions**: add the delegated permissions the plugins ask for (for mail:
   `IMAP.AccessAsUser.All`, `SMTP.Send`), and `offline_access`.
6. Copy the **Application (client) ID** and the secret into Admin → Connections → **Set up client**
   for `microsoft`.

Microsoft rotates the refresh token on use; the Host stores each new one before it hands out the
access token, and serialises the refreshes of one connection so two members sharing it never race.

## When a connection needs reconnecting

A refresh the provider refuses (`invalid_grant`: revoked, expired, the password changed, a Google app
still in Testing after 7 days) marks the connection `needs reconnect` with the provider's reason. Every
run of a member bound to it is blocked with a sentence naming the connection and saying to reconnect
it from Admin → Connections. **Reconnect** there, or `yawble connect <provider> --name <name>` again,
clears it, and the next run goes ahead.

## Binding a connection to a member

A plugin member's settings - at hire, and later in Member settings - show a picker per slot the
manifest declares, listing the connections of the providers the slot allows.

- The member stores the connection's **id**, never a token. Cloning a team carries the binding.
- Only a person binds. A Manager or Concierge hire may name a connection only when a person has
  already bound that same connection to a member of the same team.
- A connection lacking a scope the slot asks for is refused at binding, offering **Reconnect** with
  the missing scope.
- An unbound required slot blocks the member's runs with a sentence naming the slot.
- Connecting, reconnecting and binding take effect without a restart.
