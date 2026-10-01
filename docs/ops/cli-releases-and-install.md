# Releasing and installing the operator CLI

The operator CLI, `yawble`, lives in `cli/`. There is no hosted CI. **Image and CLI carry one
version number and one tag,** `v<yyyy.mm.dd.N>`, and one command releases both:
`scripts\release.ps1` (see [releases-and-updates.md](releases-and-updates.md)).

1. It runs the web, .NET and CLI suites, tags `v<yyyy.mm.dd.N>` (N counted over this
   repository's tags), and pushes `ghcr.io/djlsystems/yawble:<yyyy.mm.dd.N>` (and `latest`)
   as one manifest for linux/amd64 and linux/arm64.
2. **As its last step** it runs, from `cli/`:

   ```sh
   scripts/release.sh v2026.09.24.1
   ```

   which builds the CLI pinned to the image of that same version and creates the tag's GitHub
   Release: generated notes plus the CLI archives. Run it by hand only to finish a release whose
   last step failed; if the Release already exists it replaces the assets instead. It refuses
   when that image is not on GHCR, so a CLI never pins an image that does not exist, and it never
   pins `latest`.

`cli/scripts/release.sh` cross-compiles for linux, darwin and windows on amd64 and arm64 with
`CGO_ENABLED=0`, stamps the version, commit and image tag into `internal/buildinfo` with
`-ldflags -X`, and attaches these assets to the Release:

```
yawble_<version>_linux_amd64.tar.gz                                             yawble_<version>_windows_amd64.zip
yawble_<version>_linux_arm64.tar.gz     yawble_<version>_darwin_arm64.tar.gz    yawble_<version>_windows_arm64.zip
checksums.txt                           (sha256, one line per asset)
```

macOS is Apple silicon only for now, because Intel Macs are not tested yet: no `darwin_amd64` archive is built, and `install.sh` and `yawble up` refuse an Intel Mac.

## Installing

```sh
curl -fsSL https://raw.githubusercontent.com/djlsystems/Yawble/main/cli/scripts/install.sh | sh    # Linux, macOS
```

```powershell
irm https://raw.githubusercontent.com/djlsystems/Yawble/main/cli/scripts/install.ps1 | iex          # Windows
```

Each script reads this repository's releases list and takes the highest regular release (the
list is not in release order, so the versions are compared; drafts are skipped). With
`--prerelease` it takes the highest release, a pre-release included:

```sh
curl -fsSL https://raw.githubusercontent.com/djlsystems/Yawble/main/cli/scripts/install.sh | sh -s -- --prerelease
```

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/djlsystems/Yawble/main/cli/scripts/install.ps1))) -Prerelease
```

`irm | iex` cannot pass a switch, which is why the PowerShell form runs the script as a script
block. With no regular release published, the plain form says no release is available and prints
the `--prerelease` form. The script downloads the matching archive and
`checksums.txt`, verifies the checksum, and places the binary (`~/.local/bin`, or
`%LOCALAPPDATA%\Programs\yawble` plus the user Path on Windows). `yawble update --cli` reads the
same list the same way (`yawble update --prerelease` for pre-releases), and never moves to a
version older than the one running.

The scripts only install; they never start the instance, so they run unattended. The first
`yawble up` afterwards, at a terminal, shows what the container engine has (memory and CPUs,
from `docker info` or the Podman machine), proposes the container's memory and CPUs, and asks for
each (Enter accepts, a value above the engine's or below the floor is refused naming the bound, and one above the engine's
says how to give that engine more: the Podman machine's `podman machine set`, Docker Desktop's
Settings > Resources, or on Linux that there is nothing to enlarge);
`yawble up --yes`, or `up` without a terminal, takes the proposal without asking and says what it
chose. See [cli/README.md](../../cli/README.md#what-the-first-up-asks-the-containers-memory-and-cpus).

### A private fork or package

When the repository or its image package is private, set `GH_TOKEN` (or `GITHUB_TOKEN`) to a token with the `repo`
scope (raw files and release assets) and `read:packages` (the ghcr.io image; `write:packages`
includes it), and fetch the script with it:

```sh
export GH_TOKEN=$(gh auth token)
curl -fsSL -H "Authorization: Bearer $GH_TOKEN" https://raw.githubusercontent.com/djlsystems/Yawble/main/cli/scripts/install.sh | sh
```

```powershell
$env:GH_TOKEN = gh auth token
irm -Headers @{ Authorization = "Bearer $env:GH_TOKEN" } https://raw.githubusercontent.com/djlsystems/Yawble/main/cli/scripts/install.ps1 | iex
```

With a token, the scripts and `yawble update --cli` read the Release through the API
(the newest in `releases` or `releases/tags/<tag>`, then each asset's API URL with
`Accept: application/octet-stream`), and follow the redirect to storage themselves, in a request
without the token. `yawble up` and `yawble update` run `podman login ghcr.io -u x-access-token
--password-stdin` (Docker the same) before a pull from ghcr.io, with the token on stdin; no other
registry is sent it. Without a token nothing changes. For the public repository and package no token is needed.

`YAWBLE_GITHUB_API` (default `https://api.github.com`) points both scripts at another API; it
exists so the token path can be exercised against a local fake.

A build nobody released reports `yawble dev` and `no image pinned`, and `yawble up` refuses to
run until `yawble config set image <reference>` names one. That is deliberate: a guessed image
is how a CLI and a Host that never ran together would meet.
