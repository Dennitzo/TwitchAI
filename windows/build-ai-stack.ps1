#requires -Version 5.1
[CmdletBinding()]
param([string] $BinaryDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ai-stack-common.ps1')
Start-TwitchAiDocker
& docker compose -f (Join-Path $PSScriptRoot '..\deploy\twitchai\compose.yaml') build
if ($LASTEXITCODE -ne 0) { throw 'TwitchAI gateway build failed.' }
if ([string]::IsNullOrWhiteSpace($BinaryDirectory)) {
    & (Join-Path $PSScriptRoot 'manage-llama-server.ps1') -Action Update
} else {
    $source = [IO.Path]::GetFullPath($BinaryDirectory)
    $sourceServer = Join-Path $source 'llama-server.exe'
    if (-not (Test-Path -LiteralPath $sourceServer -PathType Leaf)) { throw 'llama-server.exe is missing in BinaryDirectory.' }
    $sourceHash = (Get-FileHash -LiteralPath $sourceServer -Algorithm SHA256).Hash
    $installRoot = Join-Path $env:USERPROFILE '.twitchai\llama-server'
    $target = Join-Path $installRoot ('versions\local-' + $sourceHash.Substring(0, 16))
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Extension -in '.exe', '.dll' } | ForEach-Object {
        $destination = Join-Path $target $_.Name
        Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash) {
            throw "Binary copy hash mismatch: $($_.Name)"
        }
    }
    @{
        serverPath = Join-Path $target 'llama-server.exe'
        serverSha256 = $sourceHash
        sourceDirectory = $source
        installedAt = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $installRoot 'current-release.json') -Encoding UTF8
    Write-Host "Independent TwitchAI llama binaries installed: $target"
}
