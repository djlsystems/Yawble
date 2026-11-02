# Connections

A **connection** is one credential the Host holds so that plugin members can act with it: an account
at an OAuth service (a Gmail mailbox, an Outlook mailbox, a Google Drive, Microsoft Graph), a mailbox
with an app password, a website, an API key, or a username and password. Connections are one store,
and every connection is **known by its name**. For an OAuth account the Host holds the provider's
OAuth client and the account's refresh token, refreshes and rotates the tokens, and hands a plugin
only a fresh access token on each run. No plugin writes OAuth code, and no long-lived credential ever
leaves the Host.

## Making a connection wherever one is asked for

**A connection can be made wherever one is asked for.** Every place that chooses a connection - a
plugin member's slot (at hire, in Member settings, in a solution install), Team settings → Connections
and New team → Connections, the guided setup's Advanced… inputs, and Admin → MCP servers (a web
address's API key, each environment variable's connection) - lists the existing connections that place
accepts, and its **last entry is always New connection...**. It opens the connection wizard over the
dialog it is in, offering only the kinds that place accepts; nothing typed underneath is lost. On save
the new connection is chosen at once, the list is read again, and whatever depends on the choice
follows it (an MCP server's Field lists the new connection's fields). Cancel leaves the select and the
form as they were. A sign-in that leaves the page (Google's guided one) opens in another tab and waits;
a Microsoft one shows its code in place. An MCP variable's New connection... opens at API key's
**Other**, so a person names their own fields (`key`, `autoSave`, `endpoint`).

**New places asking for a connection use `ConnectionSelect`** (`web/src/components/ConnectionSelect.vue`):
it takes the accepted kinds and providers (or a slot, and the team's assigned connections), and
optionally a slot's need as ConnectDialog takes it, v-models the chosen connection's id, and emits
`created` with a new connection. No new Host route is involved: the wizard saves through the routes
Admin → Connections uses.

## Kinds

Each connection has one kind, and each kind its own fields:

| Kind (`kind`) | What it holds |
|---|---|
| **Sign-in** (`oauth`) | An account at Microsoft or Google (or a custom OAuth service): its scopes, and for Microsoft its directory. See below. |
| **Mailbox** (`imap`) | A preset (Gmail, iCloud, Yahoo, Namecheap Private Email, Other), the username, the app password and the servers. See [A mailbox with an app password](#a-mailbox-with-an-app-password-imap). |
| **Website** (`website`) | A site the platform's own browser signs in to - with a password (and an optional two-step code), a token, or a session you keep with **Sign in** - and a page that shows it is signed in. See [Websites](#websites). |
| **API key** (`apikey`) | A preset naming the fields a service needs - Stripe: key; Namecheap: API user, API key, whitelisted IP; Twilio: account SID, API key SID, API key secret; Other: fields you name - each masked or not as the preset says. |
| **Username and password** (`login`) | A username, a password and an optional URL, for anything else. |

**The name is the key.** Every connection has a name no other connection has (ignoring case), and
teams and members refer to it by that name. An internal id stays the same for the connection's life,
so a rename breaks nothing. You may change the account behind a name - a password, a key, a username,
even another Microsoft or Google account on Reconnect - and every team and member using it gets the
new value at its next run, with no warning. Every change writes its tenant row in the same
transaction, naming what changed and who changed it, never a value.

Every secret - a refresh token, an app password, a masked field - is Data Protection ciphertext under
`<dataRoot>/keys`. No route, tenant row, log, report or file carries a value: a masked field says only
that it is `set`.

- **Where:** Admin → **Connections** in the web UI, or `yawble connect` on the operator's computer.
- **Who:** people only. No agent tool, skill, environment variable or route gives an agent a token.
- **Not the agent home:** an account signed in for the Concierge in `/data/agent-home` (a CLI's
  connectors, MCP servers) reaches the Concierge only; members are launched without it. Anything a
  member should act on belongs here. See [architecture.md](architecture.md#the-shared-agent-home).
- **For plugin authors:** declare a slot in the manifest and read the token on stdin; see
  [plugins.md](plugins.md#connections-oauth-accounts).
- **A mailbox with an app password** (kind `imap`) is a connection too, with no OAuth at all: see
  [A mailbox with an app password](#a-mailbox-with-an-app-password-imap).

## Providers

| Provider | What it is | Refresh token from | Account name from |
|---|---|---|---|
| `google` | Built in: Google's authorization and token endpoints, PKCE. | `access_type=offline`, `prompt=consent` | The ID token's `email`, else userinfo |
| `microsoft` | Built in: the Microsoft identity platform, PKCE, or a sign-in with a code (device flow) for a public client; the tenant follows who can sign in (`common` by default). | the `offline_access` scope | The ID token's `preferred_username` |
| `custom` | Anything else that speaks OAuth 2.0 authorization code with PKCE: you give the authorization URL, token URL, an optional userinfo URL and default scopes. | whatever the service needs, in its scopes | userinfo, when given |

Microsoft needs no client of your own when the instance has a **default Microsoft client**: the
public, multi-tenant app the operator CLI names on the control container (`HARNESS_MICROSOFT_CLIENT_ID`;
the Host names no ID of its own). `yawble up` passes the published app's ID; `yawble config set
microsoftClientId <id>` names another, and an empty value passes none. While no Microsoft client
is saved, Microsoft is that app: audience `common`, a sign-in with a code, and no secret on any
request. A client a person saves wins over it. With no variable (or an empty one), Microsoft is not set
up until a person saves a client. `GET /api/connections/providers` answers `clientSource`: `default`
while the instance's app is in use (its `clientId` is not answered), `saved` for a person's client,
null with none. The browser sign-in is refused for the default app, which registers no redirect URI;
it signs in with a code.

### Undoing a saved Microsoft or Google client

A client saved on a service's **App** step wins over the instance's own app until a person clears it.
On that same step of Add connection (Sign-in → the service → **Next**), a built-in provider with a
saved client offers **Use Yawble's app** (Microsoft, on an instance with an app of its own) or
**Remove this app** (Google, or Microsoft on an instance with none), naming the connections that
signed in through the saved app and so need **Reconnect** afterwards. Once cleared, the step says
which app is in use now; Google then shows its guide and holds **Next** until an app is saved.

- `DELETE /api/connections/providers/{id}/client` (people only, `google` or `microsoft`) forgets the
  saved client ID and secret with a `connections.provider-cleared` tenant row in the same
  transaction; when the row cannot be written nothing changes (500). Microsoft then signs in with the
  instance's app again (`clientSource: "default"`), or reads not set up without one; Google reads not
  set up. It answers `{ provider, reconnect: [{ id, name, account }] }`: the provider as the list
  shows it and its sign-in connections (the Host does not record which client each signed in
  through, so every one is named). 409 when nothing is saved; a custom provider answers 400 - remove
  it instead.
- `DELETE /api/connections/providers/{id}` on a built-in still answers 400, "clear its client instead".
- The provider list answers `instanceClient` for Microsoft: whether the instance was started with an
  app of its own, which signs in again once a saved client is cleared.

### A directory ID saved as the client ID

A Directory (tenant) ID pasted into **Client ID** makes every Microsoft sign-in fail on Microsoft's
own page (AADSTS700016), which the Host never sees. So:

- A Microsoft client save whose `clientId` equals its own `tenantId` (the App step) or `tenant`
  (the route's other field), or any connection's directory, is refused with one sentence naming both fields
  (`clientId` and the Directory (tenant) ID), and nothing is saved.
- When Microsoft answers a saved client's code start, a poll or a browser sign-in's
  code exchange with AADSTS700016 or `unauthorized_client` (what a directory's own endpoints answer),
  or AADSTS50059 (what `common` answers for an app it cannot find), the sign-in ends with one sentence
  (`ConnectionProviders.MicrosoftUnknownClient`): the saved app's client ID is not an app Microsoft
  knows, it is often a directory ID pasted in the wrong box, so use the instance's own app instead
  or set the app up again. Never the code alone. The console offers **Use Yawble's app** (or
  **Remove this app**) under it.

Every other provider needs its **client** - a client ID and a client secret you register at the
provider - entered once, on its own **App** step of Add connection (Google's, or a custom service's
**Name the service** step). The secret is stored encrypted with the
instance's Data Protection keys (`<dataRoot>/keys`); it is never in plain text, never in the
environment, and never returned by any route: the step says only that it is **set**, and its field
starts empty. Left empty on save, the stored secret is kept; changing it means typing a new one.

## Connecting an account: the guided path

Admin → **Connections** has one button for every kind: **Add connection**. There is no second door -
no Advanced, no Providers tab, no Connect an account; a slot's **Connect**, the solution wizard and
**New connection...** open this same dialog, narrowed to the kinds the place takes. It first asks
what you are connecting - a **Sign-in** (Microsoft, Google or another OAuth service), a **Mailbox**, a
**Website**, an **API key** or a **Username and password** - and then shows only that kind's fields. A
Sign-in goes on to Microsoft, Google, each custom OAuth service already named and **Another OAuth
service**; a website, an API key and a username and password to their own form (see
[Websites, API keys, usernames and passwords](#websites-api-keys-usernames-and-passwords)). A Mailbox -
and a plugin slot's **Connect** for a slot that takes only sign-ins and mailboxes - opens one screen of
provider tiles, each saying its way in: **Gmail** (app password), **Microsoft
(Outlook, Microsoft 365, Azure)** (sign in with a code), **iCloud** (app-specific password),
**Yahoo** (app password), **Namecheap Private Email** (app password) and **Other** (server
settings). A mailbox tile opens the mailbox form with that provider's hints - for Gmail, links to
2-Step Verification and App passwords and "paste the 16 characters"; for iCloud, account.apple.com,
a password shown once and a user name that is often the part before the @; for Yahoo, Account
security → Generate app password; for Namecheap Private Email, webmail → Settings → Security Center
→ App passwords, a password shown once and the full address as the user name - see
[A mailbox with an app password](#a-mailbox-with-an-app-password-imap). Gmail through a Google app of
your own is the link **Sign in through a Google app of your own…** under the mailbox tiles (and in
Gmail's hints, for an account that cannot make an app password); it goes to Google's sign-in, whose
App step follows. A slot's Connect shows only the kinds the slot takes, a mailbox (`imap`) included.

Every OAuth service - Google, Microsoft, a custom one - then goes in three steps, and its app is
always the second, before sign-in:

1. **Service** - Google, Microsoft or a custom service. The dialog shows what will be asked, in words, each line naming
   the plugin that wants it ("Read, change and send your Gmail - mailer"); the raw scope is behind
   **details**, and a scope the Host has no words for reads as itself. Every line is ticked; untick
   any you do not want. Nothing is typed: the scopes come from the installed plugins' connection
   slots (`GET /api/connections/needs?provider=<id>`, or `&plugin=<id>&slot=<name>` for one slot).
   When a Microsoft connection's scopes span several APIs, the lines are grouped under each API's
   name (Azure management, Azure SQL, Azure Storage, Key Vault, Log Analytics, Microsoft Graph),
   from the read's `microsoftApis`; one sign-in asks for every ticked line.
2. **App** - the service's app, a step of its own. It says which app signs in: for Microsoft,
   **Yawble's app** (the instance's default client, `clientSource: "default"`) or the client you
   saved; for Google, the client you saved, or that there is none yet. With a saved client the step
   offers **Use Yawble's app** or **Remove this app** (see
   [Undoing a saved Microsoft or Google client](#undoing-a-saved-microsoft-or-google-client)).
   **Next** goes on to Sign in while an app is in use. Setting up an app of your own is this step too
   - **Set up an app of your own** while one is in use (for Microsoft, also to sign in to one
   organisation's directory only), shown at once when there is none: the provider's setup
   guide as a checklist, with links into the provider's console and buttons to copy each value (the
   redirect URI, the scopes, the APIs). It says first that this is a one-time setup and roughly how
   long it takes (Google about 20 to 30 minutes, Microsoft about 10), that the tick boxes only mark
   your own progress, and, for Google, that a project you already have is fine: the APIs and scopes
   it keeps for other things stay as they are. The guide is served with the provider by
   `GET /api/connections/providers` (`guide.steps`), so the dialog and this page say the same thing.
   Saving the client goes on to Sign in.
   A **custom OAuth service**'s second step is **Name the service** instead: its name, authorization
   URL, token URL, optional userinfo URL and default scopes, and its client ID and secret, with the
   exact redirect URI to register at the service (see
   [A custom OAuth service](#a-custom-oauth-service-and-scopes-no-plugin-asks-for)).
3. **Sign in** - the scopes the installed plugins ask for stay listed and ticked from the Service
   step; **More scopes (optional)** adds any scope no plugin asks for. Then the provider's consent
   page (Microsoft: a code, below), and back to the dialog, where you may name the connection.

### Google's setup steps

1. **Project** - create one in the Google Cloud console, or pick one. Type its project id into the
   dialog and every later link opens in it.
2. **APIs** - turn on the APIs the scopes need, one link per API: Gmail (`gmail.googleapis.com`),
   Drive (`drive.googleapis.com`), Calendar (`calendar-json.googleapis.com`), Sheets
   (`sheets.googleapis.com`), Docs (`docs.googleapis.com`), People (`people.googleapis.com`).
3. **Branding and audience** - the app's name and a support email, Audience **External**, then
   **Publish app**. Left in Testing, Google ends the sign-in after 7 days (see below). Signing in,
   Google shows "Google hasn't verified this app": that is expected for your own app - choose
   **Advanced → Go to <app>**.
4. **Data access** - add exactly the scopes the dialog lists, each one copyable.
5. **Client** - type **Web application**, with the redirect URI the dialog shows registered under
   Authorized redirect URIs. When the address you are using is one Google will refuse (an IP address
   other than loopback, http on anything but localhost, a name with no public top-level domain), this
   step says so first, and the warning is shown again beside **Save client**: open the web UI at
   `http://localhost:<port>` on the machine running the container and register that, or connect with
   `yawble connect`. `http://127.0.0.1:<port>` and `http://localhost:<port>` are both accepted -
   Google exempts loopback from its no-IP-address and https rules - so neither warns; register
   exactly the one in your browser's address bar, as Google treats them as different URIs.
6. **Paste the client ID and secret** - the ID must end in `.apps.googleusercontent.com`, checked
   before saving and again by the Host. Saved with `PUT /api/connections/providers/google`.

### Microsoft's setup steps

Microsoft's guided app is a **public client**: you sign in with a code, so there is no client secret
to copy, rotate or watch expire, and no redirect URI - it works the same from localhost, a LAN address
or a tunnel.

1. **Register the app and choose who can sign in** - the link opens Entra's new registration. The
   *Supported account types* you pick there is the choice you make in the dialog, and it sets the
   tenant: *personal and any work account* → `common`, *work accounts only* → `organizations`, *only my
   organisation* → your Directory (tenant) ID. Leave Redirect URI empty.
2. **Allow public client flows** - under Authentication, set **Allow public client flows: Yes**.
3. **API permissions** - add the delegated Microsoft Graph permissions the ticked scopes need, plus
   `offline_access`, each one copyable (`https://graph.microsoft.com/Mail.Read` is `Mail.Read`). In a
   work or school tenant an admin may need to **grant consent** before anyone can sign in. A slot
   naming other Microsoft APIs needs their delegated permissions too, under **APIs my organization
   uses**: Azure Service Management `user_impersonation` (Azure management), Azure SQL Database
   `user_impersonation`, Azure Storage `user_impersonation`, Azure Key Vault `user_impersonation`,
   Log Analytics API `Data.Read`. One sign-in covers them all; see
   [Several Microsoft APIs](plugins.md#several-microsoft-apis-from-one-connection).
4. **Paste the Application (client) ID** - checked as a GUID before saving and again by the Host; for
   *only my organisation*, the Directory (tenant) ID too, also checked as a GUID. Saved with
   `PUT /api/connections/providers/microsoft` `{ clientId, audience: "common" | "organizations" |
   "tenant", tenantId? }`, which stores no secret (and clears any secret already set): the token exchange
   and every refresh send no `client_secret`. A `clientSecret` with `audience` is refused.

The **Sign in** step then shows a code and a link: open the link, enter the code, approve. The dialog
moves on by itself. When the ticked permissions span more than one Microsoft API, the sign-in takes
**two codes**, and the dialog says so before it starts and numbers them ("Code 1 of 2"): Microsoft
redeems a code for one API only, so the first code approves every permission and the second, which
asks no new questions, finishes the sign-in. Microsoft's page may say it can be closed after the
first code; come back to the dialog for the second. When the account's organisation lets only an admin approve the app, Microsoft's
refusal is shown as one sentence - an admin of that organisation approves the app, then you sign in
again - never as an error code alone (`ConnectionProviders.MicrosoftAdminApproval`; the Host words the
refusals carrying `AADSTS65001`, `AADSTS90094`, `AADSTS90095`, `AADSTS900941`, `AADSTS90099`,
`admin_consent_required` or `consent_required`, and the dialog words them alike whatever reaches it).

### A Microsoft connection's own directory

The **Sign in** step has an optional field, **Directory (tenant) ID or domain**. A personal Microsoft
account that owns an Azure subscription needs it: such an account belongs to the subscription's
directory only as an external user, and signed in through the provider's tenant (`common` for the
instance's app) it gets tokens from its personal home, which Azure management and the directory's
other APIs refuse. Type the subscription's directory ID - in the Azure portal, **Microsoft Entra ID**,
**Overview**, the Tenant ID (`xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`) - or one of its domains
(`example.onmicrosoft.com`). A work or school account leaves it empty. If Microsoft says the username
may be incorrect, the account is not in the directory the sign-in used: name that directory here and
sign in again.

- When Microsoft refuses an account as not in the directory the sign-in used - `AADSTS50020`,
  `AADSTS50034`, `AADSTS51004` or `AADSTS90072` in Microsoft's published Entra error list - the code
  sign-in, the browser sign-in and the operator CLI's `connect` end `refused` with one sentence
  (`ConnectionProviders.MicrosoftNotInDirectory`): the account was not found in that directory, a
  personal Microsoft account that owns an Azure subscription signs in through that subscription's
  directory (its ID under Directory), and a work account in that directory works too. Never the code
  or Microsoft's own words, and nothing is stored.
- Microsoft's own sign-in page often turns such an account away itself ("This username may be
  incorrect") and the code just runs out: a Microsoft code that expires says so on a second line
  after the expiry sentence (`ConnectionProviders.MicrosoftExpiredNotInDirectory`).

- The connection then signs in, redeems its code, refreshes and gets each API's token at
  `https://login.microsoftonline.com/<directory>/oauth2/v2.0/...`, with the same client (the
  instance's app or one you saved). It is sent as `tenant` on `POST /api/connections/start`, for the
  code and the browser sign-in alike (Add connection's Microsoft sign-in is the code); Reconnect and
  a slot's Reconnect offer the same field.
- A value that is neither a directory ID nor a domain (`common` and `organizations` included) is
  refused with one sentence naming the field, shown at the field, and nothing is stored.
- Each connection with a directory names it in Admin → **Connections** (`tenant` on
  `GET /api/connections`; `null` without one). A directory is not a secret.
- **Reconnect** shows the connection's directory: left as it is, it is kept (no `tenant` is sent);
  changed, the new one is used; emptied, it is cleared (`tenant: ""`). Reconnect as another account
  is accepted: the connection keeps its name and id and holds the new account, which every member
  bound to it uses at its next run, and its `connections.reconnected` row names both accounts
  (`account`, `fromAccount`). The same account in two directories is two connections.
- A slot's **Reconnect** for a Microsoft connection - in a member's slot picker (a chosen connection
  lacking a scope, a refused binding) and in the solution wizard (Your part and the install step's
  refusal) - shows the same optional Directory field, with the username hint under it, and signs in
  with a code in place. What is typed is sent as `tenant`; left empty, none is sent and the connection
  keeps its directory. A connection made with none then signs in through `common`, where a personal
  Microsoft account cannot get Azure tokens and Microsoft says "This username may be incorrect" -
  so type the subscription's directory there.
- From the operator's computer: `yawble connect microsoft --device --tenant <directory ID or domain>`.

Add connection's Microsoft App step sets up the public client signed in with a code. A redirect app
with a client secret has no screen of its own in the web: see
[Registering a Microsoft client](#registering-a-microsoft-client).

### Signing in with a code (the device flow)

Any provider with a device authorization endpoint (Microsoft's:
`https://login.microsoftonline.com/{tenant}/oauth2/v2.0/devicecode`) offers it; the provider list says
which with `deviceFlow`.

- `POST /api/connections/start` with `flow: "device"` asks the provider for a code and answers only
  `{ flowId, userCode, verificationUri, expiresAt }`. The **device code stays on the Host**, in memory
  only, spent once and bound to the person who started it, as the web flow keeps its PKCE verifier: it
  never reaches the browser, a row, a log or a file.
- The Host polls the token endpoint (`grant_type=urn:ietf:params:oauth:grant-type:device_code`) at the
  provider's `interval`, 5 s longer after each `slow_down`, until the person approves, refuses
  (`access_denied`), or the code expires (`expired_token`, or its `expiresAt` passes).
- `GET /api/connections/flows/{flowId}` (people only, the starter only: anyone else gets exactly what
  a missing flow gets) answers `{ state: "waiting" | "done" | "refused" | "expired", sentence,
  userCode, verificationUri, expiresAt, connection? }`, the code being the one to enter now and the
  connection only when `done`. Closing the dialog, or Back, cancels nothing; reading again gives the
  same state. A flow is kept an hour past its expiry, and not across a Host restart.
- **Cancel**, beside the code wherever one is shown (Add connection, a Connections tile's Reconnect,
  a slot picker's Reconnect, the solution wizard), is `DELETE /api/connections/flows/{flowId}` (people
  only, the starter only: anyone else gets what a missing flow gets). The flow leaves the Host at once:
  its device code is forgotten, its poll stops, and nothing is stored - even if the person approves at
  Microsoft afterwards. It is gone from `flows/open` and reads 404 from then on. 204; a flow that
  already ended, or whose sign-in is being stored, answers 409 with its `state`. Add connection's
  "the sign-in you started earlier" says Cancel ends it.
- **A second code.** When Microsoft takes the first code but will not redeem it for a sign-in naming
  more than one API (`AADSTS28000`), the Host asks at once for a second code naming the first API's
  scopes and the sign-in scopes. The same flow stays `waiting` with the new `userCode`,
  `verificationUri` and `expiresAt`, and its sentence says Microsoft needs a second code to finish
  the sign-in and asks no new questions. A refused or expired second code ends the flow with nothing
  stored; there is never a third. The Host then redeems its one refresh token for each API (see
  [Several Microsoft APIs](plugins.md#several-microsoft-apis-from-one-connection)).
- `GET /api/connections/flows/open` (people only) lists the caller's own sign-ins with a code still
  waiting and not past their expiry, soonest to expire first, so a reopened dialog picks one back up:
  `[{ flowId, provider, userCode, verificationUri, expiresAt, state: "waiting" }]`. Another person's
  are never listed, and neither the device code nor any secret or token is in it.
- Approval runs the same completion as the web flow: the account from the ID token or userinfo, a
  reconnect taking that account under the same name, the scopes merged, the tokens stored as ciphertext with the
  tenant row in the same transaction. Refusal and expiry store nothing.

No step of either guide carries a secret: the secret is pasted by you, stored encrypted, and never
shown again.

## A custom OAuth service, and scopes no plugin asks for

A service that is neither Microsoft nor Google, but speaks OAuth 2.0 authorization code with PKCE, is
added on the same flow:

1. Admin → **Connections** → **Add connection** → **Sign-in** → **Another OAuth service** → **Next**.
2. **Name the service**: its name, **Authorization URL**, **Token URL**, an optional **Userinfo URL**
   (where the account's name is read), **Default scopes** (asked on every connection of it), and the
   **Client ID** and **Client secret** you registered there. **Save and continue** saves it
   (`PUT /api/connections/providers/custom-<name>`) and goes on to Sign in. A service already named
   has its own tile on the Service step and opens this step filled in - all but the secret, which is
   kept when left empty - with **Remove this service** while no connection signs in through it.
3. **Sign in**: type any scopes it needs beyond its defaults under **More scopes (optional)**. The
   browser goes to the service's consent page and comes back to `/api/connections/callback` on the
   Host, which checks the `state`, exchanges the code with the PKCE verifier and stores the
   connection.

**More scopes (optional)** is on every service's Sign in step, Microsoft's and Google's too: the
scopes the installed plugins ask for stay listed and ticked, and what is typed there is asked for
besides them.

The Name the service step shows the **exact redirect URI** to register at the service for the address you are using
right now - `<the address in your browser>/api/connections/callback`. The provider must accept it:

- reached at `http://127.0.0.1:<port>` or `http://localhost:<port>`, register that;
- reached through a tunnel (`https://…`), register the tunnel's address, or use the CLI flow below,
  which never needs the Host's own address at the provider.

Each connection lists its provider, account, what its granted scopes allow - in words, once each
(`email` and `userinfo.email` are one line; the API answers them as `permissions`) - when it was
connected and last refreshed, its status (`ok`, or `needs reconnect` with the provider's reason) and the members that use
it. On each one:

- **Reconnect** - the account again, or another account under the same name. A short dialog first says what it will ask for: what the
  connection already has and, when an installed plugin's slot needs more of that provider, that it
  adds those. The provider's answer comes back to Admin → Connections, saying it worked or what
  failed. A Microsoft connection was made with a code (its app is a public client with no redirect
  URI) and reconnects with one: the dialog shows the code and the link to enter it at, and closes by
  itself on the same message. It clears `needs reconnect`.
- **Edit** (a mailbox, a website, an API key, a username and password) - change what is behind the
  name; see [Websites, API keys, usernames and passwords](#websites-api-keys-usernames-and-passwords).
- **Rename** - the name teams, members and the CLI use. No other connection may have it (ignoring
  case): `PATCH /api/connections/{id}` answers 409 otherwise. The id stays, so every binding stays.
- **Delete** - asks first. The dialog lists each team and member using the connection and says their
  runs stop until another is chosen; **Delete and unbind** confirms. The connection, what the Host
  holds for it and every member's binding of it then go in one transaction, with a
  `connections.disconnected` row and a `member.connections-changed` row per member - or none of it
  does, when a row cannot be written. Each member that used it is marked blocked with a sentence naming
  the connection, so Needs you lists it, and its runs stop at the missing slot until a person binds
  another. Last, the grant is revoked at the provider where the provider supports that.
  `DELETE /api/connections/{id}` without `unbind=true` answers 409 naming the users (`usedBy`) and
  deletes nothing; with it, it deletes and unbinds.

The Connections screen lists every connection with its kind, its health and, read only, the teams and
members using it. It assigns nothing to a team.

## Connecting an account from the CLI

For an instance reached through a tunnel, or at any address the provider will not accept as a
redirect, connect from the operator's computer:

```
yawble connect google --scopes openid,email,https://mail.google.com/ --name "Work mail"
yawble connect microsoft --scopes offline_access,https://outlook.office.com/IMAP.AccessAsUser.All
yawble connect list
yawble connect remove "Work mail"
yawble connect remove "Work mail" --unbind
```

`connect list` shows each connection's kind, and a credential's fields with a masked one only as
`set`. `connect remove` names the members using a connection and deletes nothing; `--unbind` confirms,
deleting it and unbinding them. A name is matched ignoring case, as the Host matches it.

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

### Signing in with a code from the CLI

```
yawble connect microsoft --device --scopes https://graph.microsoft.com/Mail.Read --name "Outlook"
```

`--device` prints the code and the link and waits for the sign-in to finish: no port, no loopback
listener, nothing to register. When Microsoft asks for a second code, it prints the new code and the
sentence and keeps waiting. It is refused with a sentence for a provider with no device endpoint.
Through the request file below it sends `{ op: "start", flow: "device", ... }`, answered
`start: { flowId, userCode, verificationUri, expiresAt }`, then `{ op: "flow", flowId }`, answered
`flow: { state, sentence, connection? }` (404 for a flow that is not the operator's, exactly as for a
missing one). For a provider with no device endpoint the start answers status 400 with the sentence in
`error`. The device code is in no report and no file under the data root.

`--tenant <directory ID or domain>` signs the connection in through its own directory (see
[A Microsoft connection's own directory](#a-microsoft-connections-own-directory)): it puts `tenant`
on the start, and without the flag no `tenant` is sent, so a reconnect (`--name` of an existing
connection) keeps the connection's directory. A value the Host refuses is said in its sentence.

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
- A request the Host does not answer within 20 seconds is withdrawn, and the command says so. An
  instance whose image is older than the CLI may not answer at all; `yawble update` brings both to
  one version.

## Registering a Google client

The guided path above walks these same steps; this is the long form, including the Desktop app
client for the CLI.

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
6. Copy the client ID and secret into Admin → Connections → **Add connection** → **Sign-in** →
   Google → **Next**: the App step (**Set up an app of your own**, when an app is already saved).

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

The guided path (above) registers a public client signed in with a code, and needs only steps 1, 5
and 6 here plus **Authentication → Allow public client flows: Yes**, its client ID pasted on
Microsoft's **App** step of Add connection. What follows is a redirect app with a client secret,
which the web has no screen for: it is saved with `PUT /api/connections/providers/microsoft`
`{ clientId, clientSecret, tenant? }`.

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
   never shows it again. Note when it expires, and save a new one before it does.
5. **API permissions**: add the delegated permissions the plugins ask for (for mail:
   `IMAP.AccessAsUser.All`, `SMTP.Send`), and `offline_access`.
6. Save the **Application (client) ID** and the secret with `PUT /api/connections/providers/microsoft`.

Microsoft rotates the refresh token on use; the Host stores each new one before it hands out the
access token, and serialises the refreshes of one connection so two members sharing it never race.

## A mailbox with an app password (IMAP)

The simplest way to connect a mailbox. Gmail, iCloud, Yahoo and Namecheap Private Email each issue an **app password**: a
password for one program, made in a minute in the account's security settings, that you can revoke at
any time without changing your own. No OAuth client, Google review or client secret is involved.
Microsoft (Outlook, Microsoft 365) does not accept password access; connect it as above.

**Make the app password first**, at the provider:

- **Gmail** - it needs 2-Step Verification on the Google account; then Google Account → Security →
  App passwords (https://support.google.com/accounts/answer/185833).
- **iCloud** - it needs two-factor authentication on the Apple Account; then account.apple.com →
  Sign-In and Security → App-Specific Passwords (https://support.apple.com/en-us/102654).
- **Yahoo** - Account security → Generate app password (https://help.yahoo.com/kb/SLN15241.html).
- **Namecheap Private Email** - Private Email webmail → Settings → Security Center → App passwords
  (https://www.namecheap.com/support/knowledgebase/article.aspx/10816/2306/new-how-to-use-app-passwords-for-private-email/).
  The username is the full address.

**Then add it:** Admin → **Connections** → **Add connection**, choose the mailbox's provider - Gmail,
iCloud, Yahoo, Namecheap Private Email or Other - and fill in:

| Field | What goes in |
|---|---|
| Email address | The mailbox's address: the connection's account. |
| Username | What the mail server signs in with: usually the address (the default). iCloud's IMAP takes the part before the @; with a username that has no @, the Host signs in to SMTP with the full address, as Apple's settings say. |
| App password | The app password you just made. Write-only: the connection afterwards shows only that a password is set. |
| IMAP server, port, security | Filled in from the provider's published settings and editable; empty for Other. Security is `TLS` (encrypted from the first byte) or `STARTTLS`. |
| SMTP server, port, security | The same, for sending. |
| Name (optional) | What the connection is called in Connections and a slot's picker; the address when left empty. |

| Preset | IMAP | SMTP | Published at |
|---|---|---|---|
| Gmail | imap.gmail.com:993 TLS | smtp.gmail.com:465 TLS | https://developers.google.com/workspace/gmail/imap/imap-smtp |
| iCloud | imap.mail.me.com:993 TLS | smtp.mail.me.com:587 STARTTLS | https://support.apple.com/en-us/102525 |
| Yahoo | imap.mail.yahoo.com:993 TLS | smtp.mail.yahoo.com:465 TLS | https://help.yahoo.com/kb/SLN4075.html |
| Namecheap Private Email | mail.privateemail.com:993 TLS | mail.privateemail.com:465 TLS | https://www.namecheap.com/support/knowledgebase/article.aspx/1179/2175/general-private-email-configuration-for-mail-clients-and-mobile-devices |
| Other | typed | typed | your provider's help |

**The login test.** When you save, the Host logs in to the IMAP server and authenticates to the SMTP
server with exactly these settings, over a socket from the Host (no program is started), and answers
in one sentence: "Connected: 1,240 messages in Inbox", or what to fix - "The incoming (IMAP) server
refused the password. For Gmail, make an app password: it needs 2-Step Verification.", "Could not reach
imap.example.com on port 993." A mailbox whose first login is refused, or whose server cannot be
reached, **is not saved**. Save shows the sentence under the form; fix what it names and Save again.

**What the password is used for, and where it is.** Only to sign in to that mailbox's IMAP and SMTP
servers: by the Host, for the login test and before a run, and by a plugin member a person bound to
it, which receives it on stdin for that run (see
[plugins.md](plugins.md#connections-oauth-accounts)). It is stored only as Data Protection ciphertext
under `<dataRoot>/keys`, like a refresh token; no route, tenant row, log, diagnostic, report or file
carries it, and everything the plugin writes has it redacted. A connection answers `passwordSet`,
never the password. Disconnecting deletes it.

**Before a run** the Host trusts the mailbox's last good login for 10 minutes; after that it logs in
again first. A login the server **refuses** later - the app password was revoked, the account's
password changed - marks the connection `needs reconnect` with that sentence, and every run of a member
bound to it is blocked with a sentence naming the connection and Admin → Connections. **Update
password** there logs in with the new one and, only when that works, stores it and clears the mark. A
server that cannot be reached blocks only that run.

The routes (people only): `GET /api/connections/imap/presets`, `POST /api/connections/imap`,
`PUT /api/connections/{id}/password`; rename and disconnect are the routes every connection uses.
Each write - add, update password, rename, disconnect, a member's binding - lands with its
`tenant_events` row in the same transaction, or does not happen.

## Websites, API keys, usernames and passwords

Admin → **Connections** → **Add connection** → **Website**, **API key** or **Username and password**.
Each asks for a **Name** - required, and no other connection's - and then only its kind's fields:

- **API key** - choose the service first, then its fields: **Stripe** (Secret key, masked);
  **Namecheap** (API user, shown; API key, masked; Whitelisted IP, shown - the public IPv4 address
  Yawble calls Namecheap from, which must be on the key's whitelist); **Twilio** (Account SID and API key SID, shown;
  API key secret, masked); **Other** - **Add a field** for each one the service needs, its name and
  value, each **Masked** unless you turn it off.
- **Username and password** - Username, Password (masked) and an optional URL (`http://` or
  `https://`).
- **Website** - see [Websites](#websites) below.

**The account** a credential shows on its tile (and hands a run as `account`): a username and
password's username, a website's host, an API key's first unmasked field that is not a setting -
Namecheap's API user, Twilio's Account SID. A setting is not an account: a field a preset types as a
switch, or an Other field whose value is `true` or `false` (an `autoSave`), so an Other key whose
unmasked fields are all settings shows no account line. A key whose fields are all masked, such as
Stripe's, shows the service's name.

A masked value is at least 8 characters, so it can be kept out of everything a plugin writes. It is
stored only as Data Protection ciphertext - all of a connection's masked values as one - and never
answered: a field says `set`. Nothing is signed in to on Save; **Test connection** tries them when you
ask (see [Testing a connection](#testing-a-connection)), and the service the values are for checks them
when a run uses them.

**Edit** on the connection's tile shows the same form: an unmasked field with its value, a masked one
empty. Leave a masked field empty to keep it, or type a new value to replace it. The name and id stay,
so every team and member using the connection gets the new value at its next run, and the
`connections.credentials-changed` row names the fields that changed (`changed: ["password"]`), never a
value. A mailbox's **Edit** changes its address, username, servers or app password the same way, after
a login with the result.

A plugin slot takes one when its manifest's `providers` names `login` (a username and password) or
`apikey` (an API key); scopes do not apply. The run is handed
`{ kind, account, preset, fields: { <name>: value } }` for the slot on stdin, and every masked value
joins its redaction set.

The routes (people only): `GET /api/connections/credentials/presets`, `POST /api/connections/credentials`
`{ kind, preset?, name, fields: [{ name, value, masked? }] }`, `PUT /api/connections/{id}/credentials`
`{ fields }`, `PUT /api/connections/{id}/mailbox` `{ account?, username?, password?, imap?, smtp? }`.

## Websites

Admin → **Connections** → **Add connection** → **Website** asks for its **Name** and:

| Field | What it is |
|---|---|
| **Site URL** | The origin it signs in to, such as `https://app.example.com`. An address without `http://` or `https://` is given `https://`. |
| **Other hosts** | Other hosts the browser may go to for this site (pages and frames), such as `www.example.com`, separated by commas or spaces, kept as written. Images, scripts and fetches from other hosts still load. |
| **Method** | `password` fills the sign-in form; `token` sends a header; `interactive` uses the session you keep with **Sign in**. |
| **Sign-in URL** | For password: the sign-in page, a path on the site (`/login`) or a full URL on its hosts. Default: the Site URL. A bare address is given `https://`. |
| **Username**, **Password** | For password. The password is masked. |
| **TOTP secret** | Optional, for password: the two-step secret (base32), masked, so the browser types the code. |
| **Token**, **Token header** | For token. `Authorization` (the default) sends `Bearer <token>`; any other header sends the token as it is. The token is masked. |
| **Signed-in check URL**, **Signed-in check text** | A page only a signed-in user can see (default: the Site URL), and optionally text it must show, or a CSS selector written `css=<selector>`. A bare address is given `https://`. |
| **May act on pages** | Off unless you turn it on: with it off, a team member may open pages signed in and read them, never click, fill or submit. |
| **Saved session** | Kept by **Sign in**; never typed and never shown. |
| Advanced: **Username / Password / Submit button / Code field selector** | Optional CSS selectors, when the sign-in form's fields are not found on their own. |
| Advanced: **Language**, **Time zone** | What the browser says it reads and where it is, such as `en-GB, en` and `Europe/London` (a tag in another case runs as its canonical one: `en-gb` as `en-GB`). A session kept with Sign in uses those of the browser that signed in. Default: `en-US`, `UTC`. |

**Sign in**, on a website's tile, opens the platform's sign-in page for the connection in a new tab:
the site's Site URL in the platform's own Google Chrome, which you use as your own; **Done** keeps its
session (cookies and local storage, pruned to the site's hosts and the sign-in providers you passed
through) as the connection's saved session, with a `connections.signed-in` row naming how many hosts
it holds - never the session - and clears **Needs you**. Only a person can open it.

**One browser, one profile.** Sign in, Test connection and every Team Connections command on the
website run in the same browser - Google Chrome from the volume, run headed by a worker as its own
user - in the connection's own lasting profile on the worker's volume, kept where no agent can read it
and removed with the connection. See [decisions.md](decisions.md).

**A team uses it through Team Connections** (assign it to the team first), in the Web Credentials
plugin's words:

- **`check <name> [<path>] [expect "<text>"]`** opens the page (default: the check URL) signed in and
  answers its HTTP status, address, title, heading and text, with a screenshot saved in the team's
  documents as `Team Connections/<connection>-<time>.png`.
- **`session <name> [for <minutes>]`** (1 to 120, default 30) hands a short-lived session to the member
  that asked: a file in that member's own workspace,
  `.team-connections-sessions/<connection>-<random>.until-<yyyyMMddTHHmmssZ>.json`, deleted when it
  expires. For a browser session the file is a plain Playwright storage state - `{ "cookies": [...],
  "origins": [...] }` for the site's hosts and nothing else - so the member passes its path as it is:
  `browser.newContext({ storageState: '<path>' })`. The answer says so in those words, and names the
  site's hosts and the time it is good until: *"Portal: a browser session for portal.example.com is at
  Dev:.team-connections-sessions/portal-3f9a1c2b7d4e.until-20261102T153000Z.json, good until
  2026-11-02T15:30:00Z. It is a Playwright storageState file: pass its path as it is as `storageState`
  (for example `browser.newContext({ storageState: '<path>' })`). It is deleted when it expires."* A
  token website's file keeps its own shape - `{ "kind": "token", "header", "value", "hosts",
  "expiresAt" }` - and the answer names the header to send `value` in. The expiry is in the file's name,
  so a file is still deleted on time after the Host restarts: at its start the expired ones go and the
  rest are deleted when their time is up, and every handoff sweeps the folder too. A hand-off that could
  not be made (the member's workspace refused the file, say) is answered saying so.
  When one Team Connections run answers several members at once, each member is told only its own
  lines - its own file, never another member's; the run's own output, on its card and in its log,
  lists them all.
- **`act <name> <path> click "<selector>" | fill "<selector>" "<text>" | submit "<selector>"`**, steps
  joined by `then`, acts on the page and answers it as `check` does - only when **May act on pages** is
  on; otherwise it is refused with a sentence naming the switch.

Every command is held to the Site URL's host and the other hosts: a path elsewhere is refused before
the browser goes anywhere, and a redirect, a script or a click that leads elsewhere is stopped in the
browser. Every value of the connection - its password, token, TOTP secret, username and every part of
its session - is taken out of what comes back; no value is in a route, a row, a log, an answer, a
screenshot's name or a file other than the session handed over. Each command lands one
`connections.website-used` row (the command, the outcome, the sentence; for a session, `handoff`: whether
it was `made`, the member it was handed `to`, and why not - never a value or a path). A session the
browser fetched but that could not be handed over is outcome `not-handed-over`, never `ok`. A refused sign-in or an
expired session marks the website `needs reconnect` (Needs you, on every member using it), a CAPTCHA or
a device approval the same, saying you sign in yourself with **Sign in**. Needs you then lists the
connection once, naming the team and Team Connections; the run that asked waits for you, held by the
connection (its workflow reads Awaiting, not Failed), and while it needs you no command opens the
browser for it. A passing **Test connection**, or **Sign in** then **Done**, clears it and tells the
team's Manager once in each workflow it held up. A site that does not answer fails the run only and
marks nothing.

**Web Credentials entries come over.** At the first start with this release, every entry of every
member running the Web Credentials plugin, on every team, becomes a website connection named after its
team and its entry (`Alpha supplier-admin`), assigned to that team, with its fields, its plugin's
language and time zone, and its password, token, TOTP secret and saved session; one
`connections.web-credentials-carried-over` row names how many, and which plugin ids it carried. Both ids
the plugin runs under are carried: `web-credentials` (solution 1.0.0) and `web-credentials-plugin`
(solution 1.1.0 and the plugin 1.0.0). An instance that already came over with v2026.10.31.1 or earlier,
which carried `web-credentials` members only, carries its `web-credentials-plugin` members at its next
start, once; the connections the first run made, and anything a person changed or removed since, are left
as they are, and an entry whose `<team> <entry>` name a connection already has is not made again. The
plugin's own members, their hourly verify schedule and their `status.changed` event stay as they are.

## Testing a connection

**Test connection** says whether a connection works now, without hiring a member and running it. It
is in Add connection once the connection is saved, on the connection's tile in Admin → Connections,
and on its **Edit** screen. It tries what is **saved** - nothing typed and not yet saved is sent - and
answers in one sentence:

| Kind | What the test does |
|---|---|
| Sign-in (OAuth) | Refreshes the token and reads the account's profile once, at the provider's userinfo endpoint (a custom provider with none: the refresh alone). A refresh token the provider rotates is stored first. **Microsoft** also renews one token per API the connection holds, each with that API's scopes as a run asks for it, trying every one, and names each: "Azure management: a token was issued." or "Azure SQL: Microsoft refused (...)." Its profile read is a Graph call a token for the sign-in's scopes alone may not pass (a personal account that is a guest in the directory gets HTTP 401); while a token was issued, a profile not read is said - with the account named from the same refresh's ID token - and the test is still ok: it does not mark `needs reconnect`, keeps the stored access token and does not say Reconnect. A Microsoft test is refused only when the refresh itself is refused (that sentence says Reconnect) or every API it holds is refused; one API refused alone is named and the connection stays as it was. |
| Mailbox | Logs in to IMAP and authenticates to SMTP: the same login test as Save. |
| API key | Sends the one harmless request its preset declares: **Stripe** reads the balance (`GET /v1/balance`, the key as a bearer token); **Namecheap** reads the account's balances (`namecheap.users.getBalances`, with the Whitelisted IP as `ClientIp`; a `Status="ERROR"` answer is a refusal, with Namecheap's reason - an address not on the key's whitelist is `Invalid request IP`); **Twilio** reads the account (`GET /2010-04-01/Accounts/<Account SID>.json`, with the API key SID and secret). **Other** is tested only when it has a field named `url` and exactly one masked field: a GET of that address with the masked value as a bearer token. |
| Username and password | Only with a URL: a GET of it with HTTP Basic. A redirect - to a sign-in page, say - is not taken as a yes. With no URL there is nothing to test. |
| Website | Its **signed-in check**, in the platform's browser on a worker: a password website signs in first (typing its two-step code), a token website sends its header, an interactive one uses the session kept with **Sign in**; then the check page must answer and, when set, show the check text. A refused sign-in or an expired session marks it `needs reconnect`; a CAPTCHA or a device approval marks it saying you sign in yourself with **Sign in**; a site that does not answer says so and leaves its health as it was. |

A connection with no test - Other without a `url` or with more than one masked field, a
username and password with no URL - shows **no** Test connection button rather than a result nobody
measured; the connection answers `testable: false`.

**The answer is the connection's health**, shown where the Connections list shows it: "Connected:
Stripe accepted the secret key." leaves it `ok` (and clears an earlier mark); a refusal - "Stripe
refused the secret key (HTTP 401).", "The incoming (IMAP) server refused the password. ...", "Acme
refused to renew the sign-in (invalid_grant: ...). Reconnect it." - or a service that cannot be reached
marks it `needs reconnect` with that sentence, and every run of a member bound to it is blocked with it
until a later test passes, or a reconnect or password update clears it. Each test lands with a
`connections.tested` tenant row (the outcome and the sentence) in the same transaction.

**It runs as platform code on a worker**, never through a team or a plugin member, so a connection no
team uses can be tested. Control sends the worker what to try, sealed under the worker connection's
key, and the worker answers; a test no worker ran - none is connected, or it did not answer within 60
seconds - says so and records nothing. Nothing tests a connection on a schedule: a test runs only when
a person asks. No token, password or key is in the sentence, the connection, the tenant row or any
log: each value the test carried is taken out of its sentence, and a masked field stays set.

The route (people only): `POST /api/connections/{id}/test`, answering
`{ health: ok|needs-reconnect, sentence, connection }`.

## When a connection needs reconnecting

A refresh the provider refuses (`invalid_grant`: revoked, expired, the password changed, a Google app
still in Testing after 7 days) marks the connection `needs reconnect` with the provider's reason. Every
run of a member bound to it is blocked with a sentence naming the connection and saying to reconnect
it from Admin → Connections. **Reconnect** there, or `yawble connect <provider> --name <name>` again,
clears it, and the next run goes ahead.

**A plugin's run can mark it too.** A run handed a connection that fails because of it names the slot
and the kind on its result record - `"connection": "<slot>"` and `"connectionFailure"`, exactly
`refused`, `unreachable` or `sign-in` (see [plugins.md](plugins.md#when-a-connection-fails-the-run)):

- **`refused`** (a wrong password, a revoked token, an expired session, a key the service refuses) marks
  the connection `needs reconnect` with the run's sentence, and a `connections.needs-reconnect` row names
  the team, member, slot and kind.
- **`sign-in`** (the service asks for a challenge or a device approval) is the same mark, with a sentence
  that says the person signs in themselves. The Sign in action is a later release's.
- **`unreachable`** (the service is down, a timeout) fails only the run; the connection stays `ok`.
- A result naming no kind, or a slot the run was not handed, fails the run and marks nothing. The
  platform never reads the kind from the sentence.

**Whatever marked it, the connection holds up every member using it.** Each reads **Needs you** where its
status shows, on every team, and the header's **Needs you** lists the connection once (kind
`connection`), naming the teams and members it holds up and opening Admin → Connections; those members'
own failures are not listed beside it. An edit, a Reconnect, a new password or a Test connection that
passes clears it everywhere at once and tells each affected team's Manager, once, in each still-open
workflow one of its members' runs was held in, to send the work again. Pinned by `RunConnectionFailureTests`.

## Which teams may use a connection

A connection is set up once and used by as many teams as a person chooses. **Which teams may use it is a
person's decision, made where they manage a team**, and a plugin member picks only from what its team has
been given. A new team has none.

- **Team settings → Connections** lists the connections assigned to the team and, under each, the members
  of the team using it. **Add connection** picks an existing one, and its last entry, **New
  connection...**, opens the same wizard as Admin → Connections and assigns what it saves to this team;
  **Remove** takes one off the team.
- **New team → Connections** does the same while the team is created: the team starts with those.
- **Removing asks first**, naming the members that use it. Confirmed, the assignment and every binding of
  it on that team go in one transaction; each member unbound reads **Needs you** until a person binds
  another. The connection itself stays, and every other team keeps it.
- **Only a person assigns.** The routes are a person's only (`GET`/`POST /api/teams/{team}/connections`,
  `DELETE /api/teams/{team}/connections/{id}?unbind=true`, and `connections` on `POST /api/teams`, refused
  403 for an agent's create). No Manager, member or Concierge route or tool assigns a connection.
- **An agent's hire binds what the team was given.** A Manager's (or Concierge's) hire may bind any
  connection assigned to its own team - `hiring` lists them as `connections: [{ id, name, kind, provider }]`
  - and is refused one that is not. (This replaces the earlier rule, which let an agent reuse only a
  connection a person had already bound on a member of that team.)
- **A person's binding is also an assignment.** A person binding a connection not yet assigned to the team -
  New connection... from a member's slot, a hire, a solution install - assigns it in the same transaction,
  with its own row. So every binding is of a connection assigned to the member's team.
- **Lifecycle.** Cloning a team copies its assignments and its members' bindings, so the clone's plugin
  members run with the original's connections without a person touching them. An archived team keeps its
  assignments and bindings. A deleted team's assignments and bindings go with it; the connections stay.
  Deleting a connection removes it from every team.
- **Every assignment, removal and binding** writes its tenant row in the same transaction as the change, or
  nothing changes: `team.connection-assigned` (team, connection id, name, kind, and where it was made:
  `team settings`, `new team`, `member binding`, `hire`, `solution install` or `clone`),
  `team.connection-removed` (with the members unbound), `member.connections-changed`. No route, row, log or
  file carries a secret value.
- An existing volume's bindings are assigned to their teams when it is upgraded (schema step `auth-032`),
  so nothing bound before stops.

## Team Connections: asking about a team's connections

Every team has a member named **Team Connections** (the built-in plugin of that name, see
[plugins.md](plugins.md#team-connections)). It holds every connection assigned to its team - the
assignment above is the consent, and no binding is needed - and answers any member that `tell`s it:

- **`list`**: each connection assigned to the team, by name, kind and health (`ok` or
  `needs reconnect` with its reason), and whether it has a test.
- **`test <name>`**: that connection's own [Test connection](#testing-a-connection), by name, on a
  worker: the same test, the same health and the same `connections.tested` row as a person's. A
  preset with no test has no successful test.
- For a **website**, `check`, `session` and `act` - see [Websites](#websites).

It **never hands out a value**: no key, password, token or app password is in its answer, its rows or
any file, and it never reads a connection assigned only to another team. It is shown on the board and
in status only while the team has at least one connection; removing the last hides it again and keeps
its runs. Nobody hires, removes or renames it, and no member takes its name. Agents are told to ask it
for anything to do with a connection (the `manager` and `member` skills, and the prompts the platform gives the Manager and every member).

## Binding a connection to a member

A plugin member's settings - at hire (Add member), later in Member settings, and in a solution install -
show a picker per slot the manifest declares, listing **only the connections assigned to the member's
team that fit the slot**: its kind, its providers and its scopes. When exactly one fits, it is
preselected. **New connection...** opens the connection wizard and, on save, assigns the connection to
the team and binds it to the member in one go.

- The member stores the connection's **id**, never a token: a rename changes nothing for it, and a
  change to what is behind the name reaches it at its next run. Cloning a team carries the binding.
- A Manager or Concierge hire may bind any connection assigned to its own team, and is refused one
  that is not (see above).
- A connection lacking a scope the slot asks for is refused at binding, offering **Reconnect** with
  the missing scope. For a Microsoft connection the Reconnect has the optional Directory field (see
  [A Microsoft connection's own directory](#a-microsoft-connections-own-directory)), sent only when typed.
- A connection made from a slot's **Connect** is bound through the same route a person's binding
  uses, `PUT /api/teams/{team}/members/{member}/plugin-settings` with `connections: { <slot>: <id> }`:
  the same scope check, the same `member.connections-changed` row in the same transaction, and a
  `team.connection-assigned` row when it was not yet assigned to the team.
- Before an install, nothing of a package's plugin is installed, so the plan names what each connection
  input's slot takes: `personConnections: [{ member, slot, description, required, plugin, providers,
  scopes }]`, `scopes` keyed by provider as the manifest declares them. The install asks for each from
  the connections that fit, or offers New connection..., and assigns what is chosen to the new team.
- **A required slot with nothing bound** shows **Needs you** where the member's status shows (the
  snapshot's `needsPerson`). Its runs wait for a person, ending `needs-person` - a wait, never an
  error. A person's binding clears it and tells the Manager in each workflow that waited (the
  plugin-ready notice), so the work is sent again.
- Connecting, reconnecting, assigning and binding take effect without a restart.
