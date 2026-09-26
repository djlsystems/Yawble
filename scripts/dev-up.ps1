# Local development: build this checkout's image and the yawble CLI, then run the instance with
# `yawble up`. The instance is yawble's own (pod and container
# "yawble", volume "yawble-data"), so what a developer runs is what an operator runs.
#
# Settings are yawble's, set once: yawble config set memory 12g / cpus 8 / maxRunning 8.
# Secrets are yawble's too: yawble github, or <command> | yawble secret set NAME.
#
# THE IMAGE IS TAGGED BY COMMIT, not reused under one tag. `yawble up` replaces the container only
# when a setting changes, and the image name is one of them: a rebuild under the same tag would
# leave the old container running. A checkout with uncommitted changes gets a time suffix too.
param(
    # Answer yawble's questions yes (Podman machine changes). A restart is not asked about: yawble up
    # replaces the container and names the agent runs it stops.
    [switch]$Yes
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

# The version rule is scripts/version.ps1, as for a release. try/catch because Windows PowerShell
# turns a native command's stderr into a terminating error under 'Stop'.
$version = $null
$commit = $null
try { $version = & (Join-Path $PSScriptRoot 'version.ps1') 2>$null } catch { }
if ($LASTEXITCODE -ne 0 -or -not $version) { $version = '0.0.0+unknown' }
try { $commit = git rev-parse HEAD 2>$null } catch { }
if ($LASTEXITCODE -ne 0 -or -not $commit) { $commit = 'unknown' }

# 1. The CLI, where install.ps1 puts it, so `yawble` on PATH is this build.
$go = Get-Command go -ErrorAction SilentlyContinue
$goExe = if ($go) { $go.Source } else { Join-Path $env:LOCALAPPDATA 'Programs\Go\bin\go.exe' }
if (-not (Test-Path $goExe)) { throw "Go is not installed; the CLI is built from cli/ with it." }
$binDir = Join-Path $env:LOCALAPPDATA 'Programs\yawble'
New-Item -ItemType Directory -Force $binDir | Out-Null
$yawble = Join-Path $binDir 'yawble.exe'
$mod = 'github.com/djlsystems/yawble/cli/internal/buildinfo'
Write-Host "Building yawble $version"
Push-Location (Join-Path $root 'cli')
try {
    $env:CGO_ENABLED = '0'
    & $goExe build -trimpath -ldflags "-X $mod.Version=$version -X $mod.Commit=$commit" -o $yawble ./cmd/yawble
    if ($LASTEXITCODE -ne 0) { throw "go build failed" }
} finally {
    Pop-Location
}
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (-not (($userPath -split ';') -contains $binDir)) {
    Write-Host "note: $binDir is not on your PATH; add it to run yawble by name."
}

# 2. The image. --format docker keeps the Containerfile's HEALTHCHECK (OCI drops it).
$short = if ($commit -ne 'unknown') { $commit.Substring(0, 7) } else { 'unknown' }
$dirty = $false
try { $dirty = [bool](git status --porcelain 2>$null) } catch { }
$tag = "localhost/yawble:dev-$short"
if ($dirty) { $tag += '-' + (Get-Date -Format 'yyyyMMddHHmmss') }
Write-Host "Building $tag"
podman build --format docker `
    --build-arg "HARNESS_VERSION=$version" `
    --build-arg "HARNESS_COMMIT=$commit" `
    -t $tag -f Containerfile .
if ($LASTEXITCODE -ne 0) { throw "podman build failed" }

# 3. Point yawble at it and bring the instance up. A new tag is a changed setting, so up
# replaces the container on the same volume (asking first when agents are running).
& $yawble config set image $tag
if ($LASTEXITCODE -ne 0) { throw "yawble config set image failed" }
$upArgs = @('up', '--no-browser')
if ($Yes) { $upArgs += '--yes' }
& $yawble @upArgs
if ($LASTEXITCODE -ne 0) { throw "yawble up failed" }

# 4. Older dev images. One still in use is refused by podman and kept; that is fine.
$old = podman images --format '{{.Repository}}:{{.Tag}}' |
    Where-Object { $_ -like 'localhost/yawble:dev-*' -and $_ -ne $tag }
foreach ($image in $old) {
    try { podman rmi $image 2>$null | Out-Null } catch { }
}
