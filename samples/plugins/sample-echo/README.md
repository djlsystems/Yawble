# sample-echo

The proof-of-concept plugin member, and the .NET template. See `docs/plugins.md` for the manifest,
the protocol and how to install it and hire it into a team; `sample-echo-go` is the same plugin in
Go, the default language for a connector (see "Choosing a language" there).

- **Self-contained.** The image guarantees the .NET runtime, Node and Python 3, and nothing else.
  Everything else this plugin needs lives in its own folder: `dotnet publish` puts every NuGet
  package it uses into `lib/` beside `SampleEcho.dll`. Nothing a plugin needs is ever added to the
  image.
- **`requires`.** The manifest declares `"requires": ["dotnet"]`: the runtime it needs from the
  image. The Host refuses the plugin, naming the runtime, when that runtime is not on its path.
- **One launcher for both processors.** A framework-dependent build runs unchanged on x64 and arm64,
  so `sample-echo` (the launcher) serves both and the manifest has no `platforms`.
