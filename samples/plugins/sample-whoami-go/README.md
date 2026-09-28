# sample-whoami-go

The end-to-end check for **connections** (`docs/connections.md`): a plugin that binds one Google
connection, sends the access token the Host hands it on stdin to Google's userinfo endpoint, and
reports the account and the scopes. It holds no OAuth code at all: no client, no refresh token, no
authorize step. Built from `sample-echo-go`, and self-contained the same way (standard library only,
one static binary per processor, no `requires`).

The manifest declares one slot:

```json
"connections": {
  "account": {
    "description": "The Google account to report on.",
    "providers": ["google"],
    "scopes": { "google": ["openid", "email"] },
    "required": true
  }
}
```

Build a version folder and install it:

```sh
samples/plugins/sample-whoami-go/build.sh ~/plugins-build/sample-whoami-go/0.1.0
yawble plugin install ~/plugins-build/sample-whoami-go/0.1.0
```

Then connect a Google account (Admin, Connections, or `yawble connect google --scopes openid,email`),
hire a member on `plugin:sample-whoami-go`, pick that connection for the `account` slot, and send the
member any instruction. Its output is:

```
account you@example.com
scopes openid email
```

`userinfoUrl` (set by a person only) points the token at a test double instead of Google; leave it
at its default otherwise. `go test ./...` in this folder runs the sample against a local double.
