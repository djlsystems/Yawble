# sample-echo-go

The Go template for a plugin member: the same behaviour and skill as `sample-echo` (the .NET
template), written in Go. Start a connector to a REST API, a cloud, a database, a queue or mail from
here: see "Choosing a language" in `docs/plugins.md`.

- **Self-contained.** `main.go` uses the standard library only, and `build.sh` builds it with
  `CGO_ENABLED=0` into one static binary per processor, so the plugin needs nothing from the image.
  A Go library the plugin uses is compiled into the binary; nothing is ever added to the image for a
  plugin.
- **Both processors.** `build.sh` builds `bin/linux-x64/sample-echo-go` (`GOARCH=amd64`) and
  `bin/linux-arm64/sample-echo-go` (`GOARCH=arm64`); the manifest's `platforms` map names each, and
  the Host runs the one for its own processor.
- **No `requires`.** A static Go binary needs no runtime from the image.

Build a version folder and install it:

```sh
samples/plugins/sample-echo-go/build.sh ~/plugins-build/sample-echo-go/0.1.0
yawble plugin install ~/plugins-build/sample-echo-go/0.1.0
```

Built inside the instance (a team's worktree, the Concierge's workspace), install it where it is:

```sh
yawble plugin install --from-instance /data/teams/<team>/repos/<repo>/<tree>/build/sample-echo-go/0.1.0
```

`PluginGoTemplateEndToEndTests` builds both binaries, installs the folder and runs it on the Host's
processor. See `docs/plugins.md` for the manifest, the protocol and hiring.
