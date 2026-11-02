#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Status', 'Update')]
    [string] $Action = 'Update',

    [string] $InstallRoot = (Join-Path $env:USERPROFILE '.twitchai\llama-server'),

    [string] $CudaVersion = '12.4',

    [ValidatePattern('^b[0-9]+$')]
    [string] $ReleaseTag,

    [string] $ResolvedPathFile,

    [string] $GitHubApiBase = 'https://api.github.com'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repository = 'ggml-org/llama.cpp'
$updateLock = $null

function Enter-UpdateLock {
    New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
    $lockPath = Join-Path $InstallRoot 'update.lock'
    $deadline = [DateTimeOffset]::Now.AddMinutes(2)
    while ([DateTimeOffset]::Now -lt $deadline) {
        try {
            return [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        } catch [IO.IOException] {
            Start-Sleep -Milliseconds 500
        }
    }
    throw "Another llama.cpp update still owns '$lockPath'."
}

function Invoke-GitHubRequest {
    param([Parameter(Mandatory = $true)] [string] $Uri)

    return Invoke-RestMethod -Uri $Uri -Headers @{
        Accept = 'application/vnd.github+json'
        'User-Agent' = 'TwitchAI-llama-server-installer'
        'X-GitHub-Api-Version' = '2022-11-28'
    } -TimeoutSec 60
}

function Get-Utf8ResponseText {
    param([Parameter(Mandatory = $true)] $Response)

    if ($Response.Content -is [byte[]]) {
        return [Text.Encoding]::UTF8.GetString([byte[]] $Response.Content)
    }
    return [string] $Response.Content
}

function Get-LatestBinaryRelease {
    $apiBase = $GitHubApiBase.TrimEnd('/')
    if (-not [string]::IsNullOrWhiteSpace($ReleaseTag)) {
        $selectedRelease = Invoke-GitHubRequest -Uri "$apiBase/repos/$repository/releases/tags/$ReleaseTag"
        if (-not [string]::Equals([string]$selectedRelease.tag_name, $ReleaseTag, [StringComparison]::Ordinal)) {
            throw "GitHub returned a different release than the explicitly selected '$ReleaseTag'."
        }
        return $selectedRelease
    }
    $release = Invoke-GitHubRequest -Uri "$apiBase/repos/$repository/releases/latest"
    $pointerAsset = @($release.assets | Where-Object { $_.name -eq 'nightly-tag.txt' } | Select-Object -First 1)
    if ($pointerAsset.Count -eq 0) {
        return $release
    }

    $pointerResponse = Invoke-WebRequest -Uri $pointerAsset[0].browser_download_url -UseBasicParsing -TimeoutSec 60
    $tag = (Get-Utf8ResponseText -Response $pointerResponse).Trim()
    if ([string]::IsNullOrWhiteSpace($tag) -or $tag -notmatch '^[A-Za-z0-9._-]+$') {
        throw "The llama.cpp nightly release pointer returned an invalid tag: '$tag'."
    }

    return Invoke-GitHubRequest -Uri "$apiBase/repos/$repository/releases/tags/$tag"
}

function Get-RequiredAsset {
    param(
        [Parameter(Mandatory = $true)] $Release,
        [Parameter(Mandatory = $true)] [string] $Name
    )

    $asset = @($Release.assets | Where-Object { $_.name -eq $Name } | Select-Object -First 1)
    if ($asset.Count -ne 1) {
        throw "The release '$($Release.tag_name)' does not contain '$Name'."
    }
    return $asset[0]
}

function Assert-AssetDigest {
    param(
        [Parameter(Mandatory = $true)] $Asset,
        [Parameter(Mandatory = $true)] [string] $Path
    )

    $digest = [string] $Asset.digest
    if ([string]::IsNullOrWhiteSpace($digest)) {
        throw "GitHub did not publish a SHA-256 digest for '$($Asset.name)'. Automatic installation was refused."
    }
    if ($digest -notmatch '^sha256:(?<hash>[0-9a-fA-F]{64})$') {
        throw "Unsupported GitHub asset digest: $digest"
    }

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if (-not [string]::Equals($actual, $Matches.hash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "SHA-256 mismatch for '$Path'. Expected $($Matches.hash), got $actual."
    }
}

function Receive-ReleaseAsset {
    param(
        [Parameter(Mandatory = $true)] $Asset,
        [Parameter(Mandatory = $true)] [string] $Destination
    )

    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        $existing = Get-Item -LiteralPath $Destination
        if ($existing.Length -eq [long] $Asset.size) {
            Assert-AssetDigest -Asset $Asset -Path $Destination
            Write-Host "Release asset already present: $Destination" -ForegroundColor DarkGray
            return
        }
        throw "An incomplete release asset exists at '$Destination'. Remove it before retrying."
    }

    $partial = "$Destination.part"
    if (-not (Test-Path -LiteralPath $partial -PathType Leaf)) {
        New-Item -ItemType File -Path $partial -Force | Out-Null
    }
    if ((Get-Item -LiteralPath $partial).Length -gt [long] $Asset.size) {
        $quarantine = "$partial.corrupt-" + [DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmss')
        Move-Item -LiteralPath $partial -Destination $quarantine
        New-Item -ItemType File -Path $partial -Force | Out-Null
        Write-Warning "An oversized partial download was quarantined as '$quarantine'."
    }

    Write-Host "Downloading $($Asset.name) ..." -ForegroundColor Cyan
    & curl.exe @(
        '--location',
        '--fail',
        '--silent',
        '--show-error',
        '--retry', '8',
        '--retry-all-errors',
        '--retry-delay', '5',
        '--connect-timeout', '30',
        '--speed-limit', '1024',
        '--speed-time', '120',
        '--continue-at', '-',
        '--output', $partial,
        [string] $Asset.browser_download_url
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Download failed with curl exit code ${LASTEXITCODE}: $($Asset.name)"
    }
    if ((Get-Item -LiteralPath $partial).Length -ne [long] $Asset.size) {
        throw "Downloaded asset length does not match GitHub metadata: $($Asset.name)"
    }

    Assert-AssetDigest -Asset $Asset -Path $partial
    Move-Item -LiteralPath $partial -Destination $Destination
}

function Install-LatestLlamaServer {
    New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
    $release = Get-LatestBinaryRelease
    $tag = [string] $release.tag_name
    if ($tag -notmatch '^[A-Za-z0-9._-]+$') {
        throw "The release contains an invalid tag: '$tag'."
    }

    $binaryName = "llama-$tag-bin-win-cuda-$CudaVersion-x64.zip"
    $cudaName = "cudart-llama-bin-win-cuda-$CudaVersion-x64.zip"
    $binaryAsset = Get-RequiredAsset -Release $release -Name $binaryName
    $cudaAsset = Get-RequiredAsset -Release $release -Name $cudaName
    $downloadDirectory = Join-Path $InstallRoot 'downloads'
    $versionDirectory = Join-Path (Join-Path $InstallRoot 'versions') $tag
    $binaryZip = Join-Path $downloadDirectory $binaryName
    $cudaZip = Join-Path $downloadDirectory $cudaName
    New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null

    $installedServer = Get-ChildItem -LiteralPath $versionDirectory -Filter 'llama-server.exe' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $installedServer) {
        if (Test-Path -LiteralPath $versionDirectory) {
            throw "An incomplete version directory exists: $versionDirectory"
        }

        Receive-ReleaseAsset -Asset $binaryAsset -Destination $binaryZip
        Receive-ReleaseAsset -Asset $cudaAsset -Destination $cudaZip

        $staging = Join-Path (Join-Path $InstallRoot 'staging') ("$tag-" + [Guid]::NewGuid().ToString('N'))
        $binaryStaging = Join-Path $staging 'binary'
        $cudaStaging = Join-Path $staging 'cuda'
        New-Item -ItemType Directory -Path $binaryStaging -Force | Out-Null
        New-Item -ItemType Directory -Path $cudaStaging -Force | Out-Null
        Expand-Archive -LiteralPath $binaryZip -DestinationPath $binaryStaging -Force
        Expand-Archive -LiteralPath $cudaZip -DestinationPath $cudaStaging -Force

        $stagedServer = Get-ChildItem -LiteralPath $binaryStaging -Filter 'llama-server.exe' -File -Recurse | Select-Object -First 1
        if ($null -eq $stagedServer) {
            throw "The official binary archive did not contain llama-server.exe."
        }
        $serverDirectory = $stagedServer.Directory.FullName
        foreach ($cudaFile in (Get-ChildItem -LiteralPath $cudaStaging -File -Recurse)) {
            Copy-Item -LiteralPath $cudaFile.FullName -Destination (Join-Path $serverDirectory $cudaFile.Name) -Force
        }

        New-Item -ItemType Directory -Path (Split-Path $versionDirectory -Parent) -Force | Out-Null
        Move-Item -LiteralPath $binaryStaging -Destination $versionDirectory
        $installedServer = Get-ChildItem -LiteralPath $versionDirectory -Filter 'llama-server.exe' -File -Recurse | Select-Object -First 1
        Remove-Item -LiteralPath $staging -Recurse -Force
    }

    $serverSha256 = (Get-FileHash -LiteralPath $installedServer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $previousManifestPath = Join-Path $InstallRoot 'current-release.json'
    $previousManifest = if (Test-Path -LiteralPath $previousManifestPath -PathType Leaf) {
        try { Get-Content -LiteralPath $previousManifestPath -Raw | ConvertFrom-Json } catch { $null }
    } else { $null }
    $previousServerHash = if ($null -ne $previousManifest -and
        $null -ne $previousManifest.PSObject.Properties['serverSha256']) { [string]$previousManifest.serverSha256 } else { '' }
    if ($null -ne $previousManifest -and [string]$previousManifest.releaseTag -eq $tag -and
        -not [string]::IsNullOrWhiteSpace($previousServerHash)) {
        if (-not [string]::Equals($serverSha256, [string]$previousManifest.serverSha256, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The installed llama-server.exe differs from the verified manifest for '$tag'."
        }
    } else {
        if (-not (Test-Path -LiteralPath $binaryZip -PathType Leaf) -or -not (Test-Path -LiteralPath $cudaZip -PathType Leaf)) {
            throw "The installed llama.cpp version '$tag' has no verifiable manifest or release archives."
        }
        Assert-AssetDigest -Asset $binaryAsset -Path $binaryZip
        Assert-AssetDigest -Asset $cudaAsset -Path $cudaZip
    }

    $metadata = [ordered]@{
        repository = $repository
        releaseTag = $tag
        releaseUrl = [string] $release.html_url
        publishedAt = [string] $release.published_at
        cudaVersion = $CudaVersion
        serverPath = $installedServer.FullName
        serverSha256 = $serverSha256
        binaryAssetDigest = [string] $binaryAsset.digest
        cudaAssetDigest = [string] $cudaAsset.digest
        installedAt = [DateTimeOffset]::Now.ToString('O')
    }
    $metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $InstallRoot 'current-release.json') -Encoding UTF8
    Write-Host "llama.cpp $tag is ready: $($installedServer.FullName)" -ForegroundColor Green
    return $installedServer.FullName
}

function Get-InstalledLlamaServer {
    param([string] $ExpectedReleaseTag)
    $manifestPath = Join-Path $InstallRoot 'current-release.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { return $null }
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if (-not [string]::IsNullOrWhiteSpace($ExpectedReleaseTag) -and
            -not [string]::Equals([string]$manifest.releaseTag, $ExpectedReleaseTag, [StringComparison]::Ordinal)) { return $null }
        $candidate = [string] $manifest.serverPath
        $expectedHash = [string] $manifest.serverSha256
        if ([string]::IsNullOrWhiteSpace($candidate) -or -not [IO.Path]::IsPathRooted($candidate) -or
            -not (Test-Path -LiteralPath $candidate -PathType Leaf) -or
            [IO.Path]::GetFileName($candidate) -ne 'llama-server.exe' -or
            $expectedHash -notmatch '^[0-9a-fA-F]{64}$') { return $null }
        $root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') + '\'
        $resolved = [IO.Path]::GetFullPath($candidate)
        if (-not $resolved.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { return $null }
        $actualHash = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::OrdinalIgnoreCase)) { return $null }
        return $resolved
    } catch { return $null }
}

function Write-ResolvedServerPath {
    param([Parameter(Mandatory = $true)] [string] $ServerPath)
    if ([string]::IsNullOrWhiteSpace($ResolvedPathFile)) { return }
    $target = [IO.Path]::GetFullPath($ResolvedPathFile)
    $directory = Split-Path $target -Parent
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $temporary = $target + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temporary, [IO.Path]::GetFullPath($ServerPath), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $target -Force
}

switch ($Action) {
    'Update' {
        $updateLock = Enter-UpdateLock
        try {
            try {
                $resolvedServer = Install-LatestLlamaServer
            } catch {
                $resolvedServer = Get-InstalledLlamaServer -ExpectedReleaseTag $ReleaseTag
                if ($null -eq $resolvedServer) { throw }
                Write-Warning "GitHub update check failed; continuing with the last verified llama.cpp installation. $($_.Exception.Message)"
            }
            Write-ResolvedServerPath -ServerPath $resolvedServer
        } finally {
            if ($null -ne $updateLock) { $updateLock.Dispose() }
        }
    }
    'Status' {
        Get-InstalledLlamaServer -ExpectedReleaseTag $ReleaseTag
    }
}
