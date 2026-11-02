function Assert-TwitchAiLocalDocker {
    $endpoint = $env:DOCKER_HOST
    if (-not [string]::IsNullOrWhiteSpace($env:DOCKER_CONTEXT) -or [string]::IsNullOrWhiteSpace($endpoint)) {
        $endpoint = & docker context inspect --format '{{.Endpoints.docker.Host}}'
        if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the local Docker endpoint.' }
    }
    if ($endpoint -notmatch '^npipe:////\./pipe/' -and $endpoint -notmatch '^unix:///' -and $endpoint -notmatch '^(tcp|https?)://(127\.0\.0\.1|localhost)(:|/)') {
        throw 'TwitchAI manages only the local Docker engine.'
    }
}

function Start-TwitchAiDocker {
    Assert-TwitchAiLocalDocker
    if (Test-TwitchAiDockerReady) { return }
    $desktop = Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'
    if (-not (Test-Path -LiteralPath $desktop)) { throw 'Docker Desktop is required.' }
    Start-Process -FilePath $desktop -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        Start-Sleep -Seconds 2
        if (Test-TwitchAiDockerReady) { return }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Docker Desktop did not become ready in 60 seconds.'
}

function Test-TwitchAiDockerReady {
    $savedPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & docker info --format '{{.ServerVersion}}' 2>$null | Out-Null
        return $LASTEXITCODE -eq 0
    } finally { $ErrorActionPreference = $savedPreference }
}
