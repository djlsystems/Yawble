# The management link

An instance can be attached to a management service that you run: you give the instance the
service's URL and a one-time token, it spends the token for a credential, and from then on it holds
one outbound WebSocket to the service and reports its status over it. The instance always connects
out; nothing listens for the service. An instance that is never attached behaves exactly as before.
This page is the wire format, version 1, that a service is built against; the records are
`src/Harness.Contracts/Link/LinkMessages.cs`, and every example uses `https://manage.example.com`.

## Attach

```
POST https://manage.example.com/api/link/attach
Content-Type: application/json

{ "token": "<one-time token>", "version": "1.2.3", "commit": "68b0123" }
```

A `200` answer carries the link's credential and the names to show:

```json
{ "credential": "<credential>", "serviceName": "Manage", "instanceName": "office" }
```

Any other answer (not `2xx`) carries one sentence, which the instance shows to the person verbatim:

```json
{ "error": "That token has already been used." }
```

The instance stores the credential only encrypted, and never prints, logs or reports it. A failed
attach stores nothing and leaves an earlier link as it was.

## The link

```
GET wss://manage.example.com/api/link
Authorization: Bearer <credential>
Sec-WebSocket-Protocol: yawble-link.1
```

- The scheme is `wss`. Plain `ws` is allowed only to a loopback or private address, the same rule a
  worker's connection to control follows.
- The subprotocol is `yawble-link.1`.
- A `401` on the upgrade means the credential is refused: the instance stops retrying, as for close
  code `4401`.

## Frames

Each WebSocket message is UTF-8 JSON text holding one object, discriminated by `type`. Names are
camelCase; times are ISO 8601 with an offset.

### Instance to service

| `type`      | Fields                                   | When                                         |
|-------------|------------------------------------------|----------------------------------------------|
| `hello`     | `version`, `commit`, `startedAt`         | First, on every connection.                  |
| `status`    | `seq`, `at`, `summary`                   | On connect and whenever the summary changes. |
| `heartbeat` | `at`                                     | Every `heartbeatSeconds`.                    |

```json
{ "type": "hello", "version": "1.2.3", "commit": "68b0123", "startedAt": "2026-11-02T09:30:00+00:00" }
{ "type": "status", "seq": 7, "at": "2026-11-02T09:31:00+00:00", "summary": { ... } }
{ "type": "heartbeat", "at": "2026-11-02T09:31:15+00:00" }
```

`seq` only grows; a service keeps the `status` with the highest `seq`. A status is sent at most once
every 2 seconds, always the latest state, and once again after a reconnect.

The `summary`:

```json
{
  "health": "healthy",
  "version": "1.2.3",
  "workers": [ { "name": "worker-1", "state": "connected" } ],
  "teams": [
    { "id": "team-1", "name": "Core", "status": "running", "running": 2, "idle": 1, "blocked": 0, "needsYou": 1 }
  ],
  "needsYou": 1
}
```

- `health` is `healthy` or `unhealthy`.
- A worker's `state` is `connected` or `dropped`.
- A team's `status`, `running`, `idle`, `blocked` and `needsYou` are the figures its row in the Teams
  list shows; the top-level `needsYou` is the header's Needs-you count.

### Service to instance

| `type`     | Fields                                               | When                                        |
|------------|------------------------------------------------------|---------------------------------------------|
| `welcome`  | `serviceName`, `instanceName`, `heartbeatSeconds`    | In answer to `hello`.                       |
| `refused`  | `reason`                                             | The link is refused; the service then closes it. |

```json
{ "type": "welcome", "serviceName": "Manage", "instanceName": "office", "heartbeatSeconds": 15 }
{ "type": "refused", "reason": "This instance was detached." }
```

`heartbeatSeconds` is 5 to 300; when it is absent it is 15.

The service cannot ask the instance to do anything in version 1. A frame whose `type` the instance
does not know is ignored, and logged once per type.

## Close codes

| Code   | Meaning                          | The instance                                      |
|--------|----------------------------------|---------------------------------------------------|
| `4401` | Credential unknown or revoked.   | Stops retrying until it is attached again.        |
| `4400` | Protocol error.                  | Retries with backoff.                             |
| other  | Anything else, or a dropped link.| Retries with backoff.                             |

Backoff starts at 1 second and doubles to 60 seconds, with 20 % jitter either way.

## What the summary never carries

The summary is built from an explicit list of fields: the ones above, which the Teams list and the
live-status dropdown already show. It never carries documents, transcripts, prompts, file paths,
connection values or secrets.

## Reserved

Frame types whose names begin with `relay.` are reserved for a later version. A version 1 instance
ignores them, as it ignores any type it does not know.
