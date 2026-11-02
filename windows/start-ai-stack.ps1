#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$errorLog = Join-Path $env:USERPROFILE '.twitchai\logs\start-ai-stack.log'
trap {
    $failure = ($_ | Out-String) + [Environment]::NewLine + $_.ScriptStackTrace
    [IO.Directory]::CreateDirectory((Split-Path $errorLog -Parent)) | Out-Null
    [IO.File]::WriteAllText($errorLog, $failure, [Text.UTF8Encoding]::new($false))
    Write-Error -Message $failure -ErrorAction Continue
    exit 1
}
. (Join-Path $PSScriptRoot 'ai-stack-common.ps1')
Start-TwitchAiDocker
$manifest = Join-Path $env:USERPROFILE '.twitchai\llama-server\current-release.json'
if (-not (Test-Path -LiteralPath $manifest)) { throw 'Run windows/build-ai-stack.ps1 first.' }
$installed = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
$binary = $installed.serverPath
if (-not (Test-Path -LiteralPath $binary -PathType Leaf) -or
    (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ne $installed.serverSha256) {
    throw 'The TwitchAI llama binary does not match its installation manifest.'
}
& (Join-Path $PSScriptRoot 'manage-coding-llama.ps1') -Action Start -BinaryPath $binary
try {
    & docker compose -f (Join-Path $PSScriptRoot '..\deploy\twitchai\compose.yaml') up -d --no-build
    if ($LASTEXITCODE -ne 0) { throw 'TwitchAI gateway start failed.' }
} catch {
    & (Join-Path $PSScriptRoot 'manage-coding-llama.ps1') -Action Stop -BinaryPath $binary -ShutdownTimeoutSeconds 30
    throw
}
[IO.Directory]::CreateDirectory((Split-Path $errorLog -Parent)) | Out-Null
[IO.File]::WriteAllText($errorLog, ('Started successfully at ' + [DateTimeOffset]::UtcNow.ToString('O')), [Text.UTF8Encoding]::new($false))
