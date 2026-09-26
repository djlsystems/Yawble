# Installs the yawble binary and nothing else. Piped from the internet, this places one file, puts
# its folder on the user's Path, and stops; the binary does everything else where it can be
# inspected and rerun.
#
#   irm https://raw.githubusercontent.com/djlsystems/Yawble/main/cli/scripts/install.ps1 | iex
#
# $env:YAWBLE_VERSION = 'v2026.09.24.1' pins a release; $env:YAWBLE_INSTALL_DIR overrides the folder.
#
# While the repository is private, set $env:GH_TOKEN (or $env:GITHUB_TOKEN) to a token with the
# repo scope, and fetch this script with it too:
#
#   $env:GH_TOKEN = '...'
#   irm -Headers @{ Authorization = "Bearer $env:GH_TOKEN" } https://raw.githubusercontent.com/djlsystems/Yawble/main/cli/scripts/install.ps1 | iex
#
# With a token the release is read through the GitHub API, the only place a private repository
# serves its assets. The token is sent to api.github.com only: the asset's redirect to storage is
# followed here, by a request that does not carry it. It is never printed.
$ErrorActionPreference = 'Stop'

$repo = 'djlsystems/Yawble'
$api = if ($env:YAWBLE_GITHUB_API) { $env:YAWBLE_GITHUB_API } else { 'https://api.github.com' }
$dir = if ($env:YAWBLE_INSTALL_DIR) { $env:YAWBLE_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\yawble' }
$token = if ($env:GH_TOKEN) { $env:GH_TOKEN } elseif ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } else { '' }

$arch = switch ($env:PROCESSOR_ARCHITECTURE) {
    'AMD64' { 'amd64' }
    'ARM64' { 'arm64' }
    default { throw "yawble: unsupported architecture $($env:PROCESSOR_ARCHITECTURE)" }
}

$release = $null
if ($token) {
    $releaseUrl = if ($env:YAWBLE_VERSION) { "$api/repos/$repo/releases/tags/v" + $env:YAWBLE_VERSION.TrimStart('v') } else { "$api/repos/$repo/releases/latest" }
    try {
        $release = Invoke-RestMethod -Uri $releaseUrl -UseBasicParsing -Headers @{ 'User-Agent' = 'yawble-install'; 'Authorization' = "Bearer $token" }
    } catch {
        throw "yawble: could not read the release of $repo with GH_TOKEN set (does the token have the repo scope?)"
    }
    $version = $release.tag_name
    if (-not $version) { throw "yawble: could not read the release of $repo" }
} elseif ($env:YAWBLE_VERSION) {
    $version = 'v' + $env:YAWBLE_VERSION.TrimStart('v')
} else {
    try {
        $latest = Invoke-RestMethod -Uri "$api/repos/$repo/releases/latest" -UseBasicParsing -Headers @{ 'User-Agent' = 'yawble-install' }
    } catch {
        throw "yawble: could not read the latest release of $repo (a private repository needs GH_TOKEN)"
    }
    $version = $latest.tag_name
    if (-not $version) { throw "yawble: could not read the latest release of $repo" }
}
$bare = $version.TrimStart('v')
$asset = "yawble_${bare}_windows_${arch}.zip"
$base = "https://github.com/$repo/releases/download/$version"

# Without a token, the public URL. With one, the asset's API URL, asked with redirects OFF: GitHub
# answers 302 to a signed storage URL, which is then fetched by a request that carries no token.
# Neither Invoke-WebRequest can be trusted to drop the header on its own across 5.1 and 7.
function Save-Asset([string]$Name, [string]$OutFile) {
    if (-not $token) {
        Invoke-WebRequest -Uri "$base/$Name" -OutFile $OutFile -UseBasicParsing
        return
    }
    $entry = @($release.assets | Where-Object { $_.name -eq $Name }) | Select-Object -First 1
    if (-not $entry) { throw "yawble: release $version has no asset $Name" }
    $request = [System.Net.HttpWebRequest]::Create([string]$entry.url)
    $request.AllowAutoRedirect = $false
    $request.UserAgent = 'yawble-install'
    $request.Accept = 'application/octet-stream'
    $request.Headers.Add('Authorization', "Bearer $token")
    $location = $null
    $response = $request.GetResponse()
    try {
        $code = [int]$response.StatusCode
        if ($code -ge 300 -and $code -lt 400) {
            $location = $response.Headers['Location']
            if (-not $location) { throw "yawble: GitHub redirected $Name to nowhere" }
        } else {
            $stream = $response.GetResponseStream()
            $file = [System.IO.File]::Create($OutFile)
            try { $stream.CopyTo($file) } finally { $file.Dispose(); $stream.Dispose() }
        }
    } finally {
        $response.Close()
    }
    if ($location) {
        Invoke-WebRequest -Uri $location -OutFile $OutFile -UseBasicParsing
    }
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("yawble-install-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    Write-Host "Downloading yawble $version for windows/$arch"
    Save-Asset $asset (Join-Path $tmp $asset)
    Save-Asset 'checksums.txt' (Join-Path $tmp 'checksums.txt')

    # The checksum file is the release's own statement of what it shipped. Accepts "hash  name"
    # and coreutils' binary-mode "hash *name".
    $expected = $null
    foreach ($line in Get-Content (Join-Path $tmp 'checksums.txt')) {
        $parts = $line.Trim() -split '\s+', 2
        if ($parts.Count -eq 2 -and ($parts[1] -eq $asset -or $parts[1] -eq "*$asset")) { $expected = $parts[0].ToLowerInvariant(); break }
    }
    if (-not $expected) { throw "yawble: $asset is not in checksums.txt" }
    $actual = (Get-FileHash -Algorithm SHA256 -Path (Join-Path $tmp $asset)).Hash.ToLowerInvariant()
    if ($expected -ne $actual) { throw "yawble: checksum mismatch for $asset; nothing installed" }

    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Expand-Archive -Path (Join-Path $tmp $asset) -DestinationPath $tmp -Force
    Copy-Item -Path (Join-Path $tmp 'yawble.exe') -Destination (Join-Path $dir 'yawble.exe') -Force
    Write-Host "Installed $(Join-Path $dir 'yawble.exe')"
} finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

# The USER Path, not the machine's: no elevation, and it survives reinstalls. Read and written as
# the RAW registry value (REG_EXPAND_SZ, %USERPROFILE% left unexpanded): the .NET accessor would
# expand every entry and store the expansion back, flattening the default user Path. An account
# with no user Path yet gets one holding only this folder. The current session gets it too, so
# `yawble up` works without a new terminal.
$key = Get-Item 'HKCU:\Environment'
$userPath = [string]$key.GetValue('Path', '', 'DoNotExpandEnvironmentNames')
$entries = @($userPath -split ';' | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\') })
if (-not ($entries -contains $dir.TrimEnd('\'))) {
    $newPath = if ($userPath.Trim()) { $userPath.TrimEnd(';') + ';' + $dir } else { $dir }
    Set-ItemProperty -Path 'HKCU:\Environment' -Name 'Path' -Value $newPath -Type ExpandString
    Write-Host "Added $dir to your user Path (new terminals will see it)."
}
if (-not (($env:Path -split ';') -contains $dir)) { $env:Path = "$env:Path;$dir" }

& (Join-Path $dir 'yawble.exe') version | Select-Object -First 1

# A CONTAINER ENGINE FIRST. yawble installs none: it uses Podman or Docker. With
# neither installed, say where to get one - Podman recommended - and to run `yawble up` after.
# Either installer sets up WSL on Windows, which needs a restart.
$hasPodman = [bool](Get-Command podman -ErrorAction SilentlyContinue)
$hasDocker = [bool](Get-Command docker -ErrorAction SilentlyContinue)
if ($hasPodman -or $hasDocker) {
    Write-Host 'Next: yawble up'
} else {
    Write-Host ''
    Write-Host 'No container engine is installed. Yawble runs in one; install either, then run: yawble up'
    Write-Host '  Podman Desktop (recommended, free for everyone):  https://podman-desktop.io'
    Write-Host '  Docker Desktop (free for personal use and small businesses):  https://www.docker.com/products/docker-desktop'
    Write-Host 'Either one sets up WSL, which needs a restart of Windows.'
}
