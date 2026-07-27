[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'src\TwitchAI.App\TwitchAI.App.csproj'
$artifactRoot = Join-Path $repositoryRoot 'artifacts'
$outputPath = Join-Path $artifactRoot 'TwitchAI-Portable-x64'
$executablePath = Join-Path $outputPath 'TwitchAI.App.exe'

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Das TwitchAI-App-Projekt wurde nicht gefunden: $projectPath"
}

# Der feste Zielordner wird vor jedem Publish geleert, damit keine Dateien
# eines älteren Builds im portablen Artefakt verbleiben.
$resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
$resolvedOutputPath = [System.IO.Path]::GetFullPath($outputPath)
$artifactPrefix = $resolvedArtifactRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar

if (-not $resolvedOutputPath.StartsWith(
        $artifactPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Der Ausgabepfad liegt unerwartet außerhalb des Artefaktordners."
}

if (Test-Path -LiteralPath $resolvedOutputPath) {
    Remove-Item -LiteralPath $resolvedOutputPath -Recurse -Force
}

New-Item -ItemType Directory -Path $resolvedOutputPath -Force | Out-Null

$publishArguments = @(
    'publish'
    $projectPath
    '--configuration'
    'Portable'
    '--runtime'
    'win-x64'
    '--output'
    $resolvedOutputPath
    '-p:Platform=x64'
)

if ($NoRestore) {
    $publishArguments += '--no-restore'
}

Write-Host 'TwitchAI Portable x64 wird erstellt ...' -ForegroundColor Cyan
Write-Host "Fester Zielordner: $resolvedOutputPath"

& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish wurde mit Exitcode $LASTEXITCODE beendet."
}

if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "Der Build war erfolgreich, aber die erwartete EXE fehlt: $executablePath"
}

$executable = Get-Item -LiteralPath $executablePath
Write-Host ''
Write-Host 'Portable Build erfolgreich erstellt.' -ForegroundColor Green
Write-Host "EXE: $($executable.FullName)"
Write-Host ("Größe: {0:N1} MB" -f ($executable.Length / 1MB))

if ($Run) {
    Write-Host 'TwitchAI wird gestartet ...' -ForegroundColor Cyan
    Start-Process -FilePath $executable.FullName -WorkingDirectory $resolvedOutputPath
}

