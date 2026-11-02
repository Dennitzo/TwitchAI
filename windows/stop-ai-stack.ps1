#requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ai-stack-common.ps1')
Assert-TwitchAiLocalDocker
try {
    & docker compose -f (Join-Path $PSScriptRoot '..\deploy\twitchai\compose.yaml') stop --timeout 15
    if ($LASTEXITCODE -ne 0) { throw 'TwitchAI gateway stop failed.' }
} finally {
    & (Join-Path $PSScriptRoot 'manage-coding-llama.ps1') -Action Stop -ShutdownTimeoutSeconds 30
}
