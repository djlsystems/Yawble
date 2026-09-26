# Bring the local instance to the latest main in one command: pull, wait for running agents,
# rebuild and replace the container with dev-up.ps1 (yawble up), then report the version now running.
#
#   scripts/update.ps1          # waits while any agent CLI runs in the container
#   scripts/update.ps1 -Force   # does not wait; running agents are cut off by the replacement
param(
    [switch]$Force,
    [string]$Container = 'yawble',
    # How often to look for running agents.
    [int]$PollSeconds = 30,
    # How long to wait for /healthz after the container is replaced. The first boot installs the
    # agent CLIs before the host listens; the image's HEALTHCHECK allows ten minutes for that.
    [int]$HealthTimeoutMinutes = 15
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-functions.ps1')
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# 1. Clean, and on main.
Assert-CleanMain $root

# 2. Fast-forward only: a local commit that is not on origin stops the update here.
Invoke-Checked 'git pull --ff-only' { git pull --ff-only }

# 3. Replacing the container kills every agent in it. Wait for them to finish.
if ($Force) {
    Write-Host 'Not waiting for running agents (-Force).'
} else {
    while ($true) {
        $running = @()
        $listing = @(podman exec $Container sh -c (ConvertTo-ShArgument $AgentProcessListing))
        if ($LASTEXITCODE -eq 0) {
            $running = @(Get-AgentProcess -Lines $listing)
        } else {
            Write-Host "Container '$Container' is not answering; nothing to wait for."
        }
        if ($running.Count -eq 0) { break }
        $names = @($running | ForEach-Object { "$($_.Who) ($($_.Cli), pid $($_.Pid))" })
        Write-Host "$(Get-Date -Format 'HH:mm:ss') Waiting for $($running.Count) agent(s): $($names -join ', '). Checking again in $PollSeconds s; -Force skips the wait."
        Start-Sleep -Seconds $PollSeconds
    }
    Write-Host 'No agent is running.'
}

# 4. Build from this checkout and replace the container. yawble up does not wait for agents (the
# wait above, or -Force, decided that); -Yes answers its Podman machine questions.
& (Join-Path $PSScriptRoot 'dev-up.ps1') -Yes
Set-Location $root

# 5. Asked from inside the container, so the answer does not depend on how the port is published.
Write-Host 'Waiting for /healthz...'
$deadline = (Get-Date).AddMinutes($HealthTimeoutMinutes)
$healthy = $false
while ((Get-Date) -lt $deadline) {
    $code = & { $ErrorActionPreference = 'Continue'; podman exec $Container curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8080/healthz 2>$null }
    if ("$code".Trim() -eq '200') { $healthy = $true; break }
    Start-Sleep -Seconds 5
}
if (-not $healthy) {
    throw "The container did not report healthy within $HealthTimeoutMinutes minutes. Read: podman logs $Container"
}
Write-Host 'Healthy.'

$body = & { $ErrorActionPreference = 'Continue'; podman exec $Container curl -fsS http://127.0.0.1:8080/api/version 2>$null }
if ($LASTEXITCODE -eq 0 -and $body) {
    $v = ($body | Out-String) | ConvertFrom-Json
    # PowerShell 7 turns an ISO timestamp into a DateTime; print it as ISO again.
    $built = $v.builtAt
    if ($built -is [datetime]) { $built = $built.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') }
    Write-Host "Now running $($v.version) (commit $($v.commit), built $built)."
} else {
    Write-Host 'Now running a build that does not answer /api/version.'
}
