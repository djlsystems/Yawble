# Cut a release from this checkout: test, tag, push the image, then build the operator CLI in cli/
# at the SAME tag, pinned to that image, and publish ONE GitHub Release carrying the notes and the
# CLI archives. Core and CLI live in one repository and always carry one version number.
# There is no hosted CI, so this runs on the owner's machine, with the container running.
#
#   scripts/release.ps1 -DryRun   # checks, all three suites, and the version it would use; publishes nothing
#   scripts/release.ps1           # the same, then tag, push the tag, image to ghcr.io, CLI build, GitHub Release
#
# The version is yyyy.mm.dd.N, the tag v<version>. N counts that day's release tags on origin.
# Two images go to ghcr.io/<owner>/<repo>, both built from the one Containerfile: control as
# :<version> and :latest, the worker as :<version>-worker and :latest-worker (Get-ImageBuilds). The
# name is taken from the origin remote and lowercased. Pushing needs a gh token with write:packages:
#   gh auth refresh -s write:packages
param(
    [switch]$DryRun,
    # Publish the GitHub Release as a pre-release. Ask the owner which it is before every release.
    [switch]$Prerelease,
    # Build the image from scratch: no cached layers, and the base images pulled again. The cache
    # keeps the toolchain layers between releases, so its operating-system packages age; run with
    # -NoCache now and then (monthly, or for a security fix) to take the current ones.
    [switch]$NoCache,
    # The running container the Linux .NET suite is run in. Empty: the instance's first worker when it
    # has one (the control image carries no .NET SDK), else the single-process container.
    [string]$Container = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-functions.ps1')
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# 1. On main, clean, and equal to origin/main.
Assert-CleanMain $root
$head = (git rev-parse HEAD).Trim()
$originMain = @(git ls-remote origin refs/heads/main)
if ($LASTEXITCODE -ne 0 -or $originMain.Count -eq 0) { throw 'Could not read main from origin.' }
$originSha = ($originMain[0] -split '\s+')[0]
if ($head -ne $originSha) {
    throw "Refusing: HEAD is $head but origin/main is $originSha. Pull or push first."
}
$remote = Get-GitHubRepository (git config --get remote.origin.url)
if (-not $remote) { throw 'Refusing: the origin remote is not a GitHub repository.' }
$image = "ghcr.io/$($remote.Owner)/$($remote.Repo)"
Write-Host "Releasing $head from $($remote.Owner)/$($remote.Repo)."

# 1b. The CLI in cli/ is built with Go and sh. Asked before an hour of tests, because a release
# whose CLI cannot be built is a release without its installer.
$cliDir = Join-Path $root 'cli'
foreach ($tool in 'go', 'sh') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "Refusing: '$tool' is not on PATH; the CLI is built with it."
    }
}

if (-not $DryRun) {
    # Fail before an hour of tests, not after.
    Invoke-Checked 'gh auth status' { gh auth status | Out-Null }
    # 5.1 turns redirected stderr into errors under 'Stop'; this only reads it.
    $scopes = & { $ErrorActionPreference = 'Continue'; gh auth status 2>&1 | Out-String }
    if ($scopes -match 'Token scopes:' -and $scopes -notmatch 'write:packages') {
        throw 'Refusing: the gh token cannot push images. Run: gh auth refresh -s write:packages'
    }
    Invoke-Checked 'podman' { podman --version | Out-Null }
}

# The image is built for linux/amd64 AND linux/arm64 as one manifest, so the operator CLI's users
# on Apple Silicon and arm64 servers pull the same tag. The half that is not this machine's runs
# its runtime layer under emulation; without a registered handler the build fails a long way in
# with "exec format error", so ask first with the cheapest probe there is. Checked in a dry run
# too: it is the one prerequisite that is not a credential.
$platforms = 'linux/amd64,linux/arm64'
$foreign = if ((podman info --format '{{.Host.Arch}}').Trim() -eq 'arm64') { 'linux/amd64' } else { 'linux/arm64' }
function Test-Emulation {
    $answer = & { $ErrorActionPreference = 'Continue'; podman run --rm --platform $foreign docker.io/library/alpine uname -m 2>&1 | Out-String }
    return ($LASTEXITCODE -eq 0 -and $answer -match 'aarch64|x86_64')
}
if (-not (Test-Emulation)) {
    # The registration lasts until the Podman machine restarts, so the first release after a restart
    # needs it again. It is made here rather than asked for; a failure still stops the release.
    Write-Host "Registering the $foreign emulator in the Podman machine (it lasts until the machine restarts)"
    & { $ErrorActionPreference = 'Continue'; podman machine ssh -- sudo podman run --rm --privileged docker.io/multiarch/qemu-user-static --reset -p yes 2>&1 } | Out-Null
    if (-not (Test-Emulation)) {
        throw @"
Refusing: no emulator for $foreign is registered, so the multi-arch image cannot be built here, and
registering one did not work. Register it by hand in the Podman machine:
    podman machine ssh -- sudo podman run --rm --privileged docker.io/multiarch/qemu-user-static --reset -p yes
On Linux without a machine, run the same podman command with sudo on the host.
"@
    }
}

# 2a. Web suite, here.
Write-Host '== Web suite (local) =='
Push-Location (Join-Path $root 'web')
try {
    Invoke-Checked 'npm ci' { npm ci --no-audit --no-fund }
    Invoke-Checked 'The web suite' { npm test }
} finally {
    Pop-Location
}

# 2b. Linux .NET suite, in a scratch clone inside the running container, with TMPDIR in the
# scratch folder so its hosts do not share /tmp with the live one. The commit goes in as a
# bundle, so the container needs no access to origin. The test dll is run directly: `dotnet test`
# can report zero tests on this repository.
if (-not $Container) {
    $running = @(podman ps --format '{{.Names}}')
    $Container = if ($running -contains 'yawble-worker-1') { 'yawble-worker-1' } else { 'yawble' }
}
Write-Host "== .NET suite (scratch clone in container '$Container') =="
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$scratch = "/tmp/release-$stamp"
$bundle = Join-Path ([IO.Path]::GetTempPath()) "release-$stamp.bundle"
Invoke-Checked 'git bundle' { git bundle create $bundle HEAD }
try {
    Invoke-Checked 'Creating the scratch folder' { podman exec $Container mkdir -p "$scratch/tmp" }
    Invoke-Checked 'Copying the commit into the container' { podman cp $bundle "${Container}:$scratch/src.bundle" }
    # A worker container carries its own HARNESS_ settings (HARNESS_ROLE=worker, its key file, ...);
    # left set, every test Host would start as a worker, so all but the data root are cleared. The
    # clone, TMPDIR and the build output are opened to other users, so the root-only tests that
    # start control or a worker as another user can read them; stdin is closed.
    $suite = @(
        'set -e',
        'for v in $(env | grep -o ''^HARNESS_[A-Z_]*'' | grep -vx HARNESS_DATA_ROOT); do unset $v; done',
        'export ASPNETCORE_URLS=http://0.0.0.0:8080',
        "export TMPDIR=$scratch/tmp",
        "git clone -q $scratch/src.bundle $scratch/src",
        "cd $scratch/src",
        "git checkout -q --detach $head",
        'dotnet build tests/Harness.Tests/Harness.Tests.csproj -c Debug -nologo -v quiet',
        "chmod 1777 $scratch/tmp && chmod o+rx $scratch $scratch/src && chmod -R o+rX tests/Harness.Tests/bin",
        'dotnet tests/Harness.Tests/bin/Debug/net10.0/Harness.Tests.dll < /dev/null'
    ) -join "`n"
    Invoke-Checked 'The .NET suite' { podman exec $Container sh -c (ConvertTo-ShArgument $suite) }
} finally {
    Remove-Item -Force $bundle -ErrorAction SilentlyContinue
    podman exec $Container rm -rf $scratch | Out-Null
}

# 2c. The CLI suite, so a dry run answers for both halves.
Write-Host '== CLI suite (local) =='
Push-Location $cliDir
try {
    Invoke-Checked 'go vet' { go vet ./... }
    Invoke-Checked 'The CLI suite' { go test ./... }
} finally {
    Pop-Location
}

# 3. Today's next version, from the tags on origin: one tag names core and CLI.
$tags = @(git ls-remote --tags origin 'refs/tags/v*' | ForEach-Object { ($_ -split '\s+')[1] })
if ($LASTEXITCODE -ne 0) { throw 'Could not read the tags on origin.' }
$version = Get-NextReleaseVersion -Tags $tags -Date (Get-Date)
$tag = "v$version"

if ($DryRun) {
    Write-Host ''
    Write-Host "Dry run. Both suites passed. Next version: $version"
    Write-Host "Would tag      $tag on $head and push it to origin"
    foreach ($b in Get-ImageBuilds -Image $image -Version $version) {
        Write-Host "Would build    --target $($b.Target) at $tag for $platforms with --build-arg HARNESS_VERSION=$version HARNESS_COMMIT=$head"
        Write-Host "Would push     $($b.Ref) (one manifest, both platforms)"
    }
    foreach ($b in Get-ImageBuilds -Image $image -Version $version) { Write-Host "Would push     $($b.Latest)" }
    Write-Host "Would run      cli/scripts/release.sh ${tag}$(if ($Prerelease) { ' --prerelease' }): build the CLI pinned to $version, gh release create $tag with its archives$(if ($Prerelease) { ', as a pre-release' })"
    return
}

# 4 and 5. The tag is made locally first and the image built from it, so a build that fails
# publishes nothing. Then the tag and the image, in that order; the Release comes with the CLI.
Invoke-Checked 'git tag' { git tag -a $tag -m "Release $version" $head }
$tree = Join-Path ([IO.Path]::GetTempPath()) "release-$stamp"
try {
    Invoke-Checked 'git worktree add' { git worktree add --detach $tree $tag }
    # --format docker keeps HEALTHCHECK, as in dev-up.ps1. The Containerfile declares
    # HARNESS_VERSION (bare yyyy.mm.dd.N, no v) and HARNESS_COMMIT (full sha) and sets the
    # .version and .revision labels from them; only .source is added here.
    #
    # ONE MANIFEST, TWO PLATFORMS. --manifest replaces -t: the name is a manifest list holding
    # an amd64 and an arm64 image, and `latest` is the same list pushed under a second name
    # below, so the two tags can never point at different builds. A manifest left by an earlier
    # attempt would collect a third image, so it is removed first.
    # TWO TARGETS, ONE COMPILE: both builds share every layer up to their own stage, so the second
    # reuses the first's web and .NET build, and both carry the one version.
    foreach ($b in Get-ImageBuilds -Image $image -Version $version) {
        & { $ErrorActionPreference = 'Continue'; podman manifest rm $b.Ref 2>$null } | Out-Null
        $build = @(
            'build', '--format', 'docker',
            '--target', $b.Target,
            '--platform', $platforms,
            $(if ($NoCache) { @('--no-cache', '--pull=always') } else { @() }),
            '--manifest', $b.Ref,
            '--build-arg', "HARNESS_VERSION=$version",
            '--build-arg', "HARNESS_COMMIT=$head",
            '--label', "org.opencontainers.image.source=https://github.com/$($remote.Owner)/$($remote.Repo)",
            '-f', (Join-Path $tree 'Containerfile'), $tree
        )
        Invoke-Checked "podman build --target $($b.Target)" { podman @build }
    }
} catch {
    git tag -d $tag | Out-Null
    throw
} finally {
    & { $ErrorActionPreference = 'Continue'; git worktree remove --force $tree 2>$null } | Out-Null
}

Invoke-Checked 'git push (tag)' { git push origin "refs/tags/$tag" }

$user = (gh api user --jq .login).Trim()
Invoke-Checked 'podman login ghcr.io' { gh auth token | podman login ghcr.io -u $user --password-stdin }
# --all pushes both platform images and the list that names them. Every version tag first: a failed
# version push must never move `latest` or `latest-worker`.
$builds = Get-ImageBuilds -Image $image -Version $version
foreach ($b in $builds) {
    Invoke-Checked "podman manifest push $($b.Ref)" { podman manifest push --all $b.Ref "docker://$($b.Ref)" }
}
foreach ($b in $builds) {
    Invoke-Checked "podman manifest push $($b.Latest)" { podman manifest push --all $b.Ref "docker://$($b.Latest)" }
}

# 6. The CLI, at the same tag, pinned to the image just pushed, and the GitHub Release with its
# archives. Built from a checkout OF THE TAG, not from this one: release.sh insists HEAD is the
# tag, and a commit made here while the release ran (it runs for most of an hour) stopped this
# step on "HEAD is not <tag>" (v2026.09.24.3). The image is pushed by now, so a failure here is
# reported with the commands that finish it rather than undone.
$cliDone = $false
$cliTree = Join-Path ([IO.Path]::GetTempPath()) "release-$stamp-cli"
try {
    Invoke-Checked 'git worktree add (CLI)' { git worktree add --detach $cliTree $tag }
    Push-Location (Join-Path $cliTree 'cli')
    try {
        $releaseArgs = @('scripts/release.sh', $tag) + $(if ($Prerelease) { @('--prerelease') } else { @() })
        Invoke-Checked 'The CLI release' { sh @releaseArgs }
        $cliDone = $true
    } finally {
        Pop-Location
    }
} catch {
    Write-Warning "The image is pushed but the GitHub Release and the CLI are not: $($_.Exception.Message)"
    Write-Warning "Finish it from a checkout of the tag: git worktree add --detach <dir> $tag; then in <dir>/cli: sh scripts/release.sh $tag"
} finally {
    & { $ErrorActionPreference = 'Continue'; git worktree remove --force $cliTree 2>$null } | Out-Null
}

Write-Host ''
Write-Host "Released $version."
Write-Host "  Tag:     $tag on $head"
Write-Host "  Images:  ${image}:$version and ${image}:$version-worker (and :latest, :latest-worker)"
if (-not $cliDone) { exit 1 }
Write-Host "  Release: https://github.com/$($remote.Owner)/$($remote.Repo)/releases/tag/$tag (notes and CLI archives)"
