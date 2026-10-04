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
| `microsoft` | Built in: the Microsoft identity platform, PKCE, or a sign-in with a code (device flow) for a public client; the tenant follows who can sign in (`common` by default). | the `offline_access` scope | The ID token's `preferred_username` |
| `custom` | Anything else that speaks OAuth 2.0 authorization code with PKCE: you give the authorization URL, token URL, an optional userinfo URL and default scopes. | whatever the service needs, in its scopes | userinfo, when given |

Each provider needs its **client** - a client ID and a client secret you register at the provider -
entered once, in the Connections dialog (**Set up client**). The secret is stored encrypted with the
instance's Data Protection keys (`<dataRoot>/keys`); it is never in plain text, never in the
environment, and never returned by any route: the dialog shows only that it is **set**. Changing it
means typing a new one.

## Connecting an account: the guided path

Start here. Admin → **Connections** → **Add connection** opens a dialog in three steps:

1. **Service** - Google or Microsoft. The dialog shows what will be asked, in words, each line naming
   the plugin that wants it ("Read, change and send your Gmail - mailer"); the raw scope is behind
   **details**, and a scope the Host has no words for reads as itself. Every line is ticked; untick
   any you do not want. Nothing is typed: the scopes come from the installed plugins' connection
   slots (`GET /api/connections/needs?provider=<id>`, or `&plugin=<id>&slot=<name>` for one slot).
2. **Set up the app** - only when the provider's client is not set up yet: the provider's setup
   guide as a checklist, with links into the provider's console and buttons to copy each value (the
   redirect URI, the scopes, the APIs). The guide is served with the provider by
   `GET /api/connections/providers` (`guide.steps`), so the dialog and this page say the same thing.
3. **Sign in** - the provider's consent page, then back to the dialog, where you may name the
   connection.

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
   Authorized redirect URIs. When the address you are using is one Google will refuse (an IP address,
   http on anything but localhost, a name with no public top-level domain), this step says so first:
   open the web UI at `http://localhost:<port>` on the machine running the container and register
   that, or connect with `yawble connect`.
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
   work or school tenant an admin may need to **grant consent** before anyone can sign in.
4. **Paste the Application (client) ID** - checked as a GUID before saving and again by the Host; for
   *only my organisation*, the Directory (tenant) ID too, also checked as a GUID. Saved with
   `PUT /api/connections/providers/microsoft` `{ clientId, audience: "common" | "organizations" |
   "tenant", tenantId? }`, which stores no secret (and clears one set before): the token exchange
   and every refresh send no `client_secret`. A `clientSecret` with `audience` is refused.

The **Sign in** step then shows a code and a link: open the link, enter the code, approve. The dialog
moves on by itself.

The redirect app with a client secret is unchanged, under **Advanced** (the `tenant` field and the
secret); the long form is under [Registering a Microsoft client](#registering-a-microsoft-client).

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
  connection? }`, the connection only when `done`. Closing the dialog cancels nothing; reading again
  gives the same state. A flow is kept an hour past its expiry, and not across a Host restart.
- `GET /api/connections/flows/open` (people only) lists the caller's own sign-ins with a code still
  waiting and not past their expiry, soonest to expire first, so a reopened dialog picks one back up:
  `[{ flowId, provider, userCode, verificationUri, expiresAt, state: "waiting" }]`. Another person's
  are never listed, and neither the device code nor any secret or token is in it.
- Approval runs the same completion as the web flow: the account from the ID token or userinfo, a
  reconnect must be the same account, the scopes merged, the tokens stored as ciphertext with the
  tenant row in the same transaction. Refusal and expiry store nothing.

Pinned by `ConnectionsTests` (`A_device_sign_in_answers_only_the_code_and_link_and_its_device_code_reaches_no_answer_row_or_file`,
`Device_polling_waits_the_providers_interval_and_five_seconds_more_after_each_slow_down`,
`An_approved_device_sign_in_stores_the_connection_with_its_tenant_row`,
`A_refused_device_sign_in_ends_with_a_sentence_and_stores_nothing`,
`An_expired_device_sign_in_ends_with_a_sentence_and_stores_nothing`,
`Another_person_reads_a_device_flow_exactly_as_a_missing_one`, `A_machine_principal_cannot_read_a_device_flow`,
`A_person_lists_only_their_own_waiting_device_sign_ins_and_never_a_device_code`,
`A_machine_principal_cannot_list_open_device_sign_ins`,
`A_device_reconnect_that_signs_in_as_another_account_is_refused_and_changes_nothing`,
`A_microsoft_public_client_is_saved_with_no_secret_and_its_exchange_and_refresh_send_none`,
`The_microsoft_tenant_follows_who_can_sign_in`, `A_provider_with_no_device_endpoint_refuses_a_device_sign_in_with_a_sentence`
and the CLI exchange tests below), `ConnectionNeedsTests.The_microsoft_guide_sets_up_a_public_client_signed_in_with_a_code`
and, in the web, `web/src/components/__tests__/connect-dialog-device.mount.spec.ts`.

No step of either guide carries a secret: the secret is pasted by you, stored encrypted, and never
shown again.

## Advanced: the full form

Admin → **Connections** → **Advanced** holds the Providers tab and the Connect form as they were:
custom providers, free-text scopes, the Microsoft tenant, the client secret. Use it for a custom
provider, for scopes no installed plugin asks for, or to set a client by hand.

1. Admin → **Connections** → **Advanced** → **Connect an account**.
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

### Signing in with a code from the CLI

```
yawble connect microsoft --device --scopes https://graph.microsoft.com/Mail.Read --name "Outlook"
```

`--device` prints the code and the link and waits for the sign-in to finish: no port, no loopback
listener, nothing to register. It is refused with a sentence for a provider with no device endpoint.
Through the request file below it sends `{ op: "start", flow: "device", ... }`, answered
`start: { flowId, userCode, verificationUri, expiresAt }`, then `{ op: "flow", flowId }`, answered
`flow: { state, sentence, connection? }` (404 for a flow that is not the operator's, exactly as for a
missing one). For a provider with no device endpoint the start answers status 400 with the sentence in
`error`. The device code is in no report and no file under the data root. Pinned by `ConnectionsTests`
(`The_cli_device_flow_starts_and_reads_through_the_operator_exchange`,
`The_cli_device_code_reaches_no_exchange_answer_and_no_file_under_the_data_root`,
`The_cli_reads_a_persons_device_flow_exactly_as_a_missing_one`,
`The_cli_device_start_for_a_provider_with_no_device_endpoint_answers_400_with_the_sentence`) and, in the CLI,
`cli/internal/cli/connect_device_test.go`.

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

The guided path (above) registers a public client signed in with a code, and needs only steps 1, 5
and 6 here plus **Authentication → Allow public client flows: Yes**. What follows is the Advanced
redirect app with a client secret, unchanged.

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
- A connection made from a slot's **Connect** is bound through the same route a person's binding
  uses, `PUT /api/teams/{team}/members/{member}/plugin-settings` with `connections: { <slot>: <id> }`:
  the same scope check, the same `member.connections-changed` row in the same transaction. Pinned by
  `ConnectionsTests.A_connection_just_signed_in_from_a_slot_binds_through_the_settings_route_and_one_missing_scopes_is_refused_there`.
- Before an install, nothing of a package's plugin is installed, so the plan names what each connection
  input's slot takes: `personConnections: [{ member, slot, description, required, plugin, providers,
  scopes }]`, `scopes` keyed by provider as the manifest declares them. Pinned by
  `SolutionCheckTests.Each_connection_input_carries_its_slots_plugin_providers_and_scopes_before_any_install`.
- An unbound required slot blocks the member's runs with a sentence naming the slot.
- Connecting, reconnecting and binding take effect without a restart.
