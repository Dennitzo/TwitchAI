#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Start', 'Stop', 'Status')][string] $Action = 'Start',
    [Alias('NativeModelRoot')][string] $ModelRoot = 'C:\Users\AMD\.cache\huggingface',
    [string] $BinaryPath,
    [string] $PythonPath,
    # AppData is virtualized when launched from an MSIX host such as Codex.
    # Keep one identity/log directory for repository and portable app launches.
    [string] $StateDirectory = (Join-Path $env:USERPROFILE '.twitchai\native-runtime'),
    [ValidateRange(1024, 65535)][int] $Port = 8181,
    # Each GPU keeps this reserve; llama fits from the model's native context maximum.
    [ValidateRange(256, 32768)][int] $FitTargetMiB = 2048,
    [string] $GpuLayers = 'auto',
    # Explicit application shutdown gives snapshots a bounded grace period.
    # Shared/manual stops retain their previous allowance for large KV caches.
    [ValidateRange(1, 600)][int] $ShutdownTimeoutSeconds = 600,
    # GUI callers do not redirect process pipes: the long-lived Python child
    # could inherit them and prevent ReadToEndAsync from ever completing.
    [string] $ErrorFile
)

if ([IO.Path]::GetFullPath($ModelRoot).TrimEnd('\') -ne 'C:\Users\AMD\.cache\huggingface') { throw 'Only C:\Users\AMD\.cache\huggingface is allowed.' }
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($BinaryPath) -and $Action -eq 'Start') {
    $binaryManifest = Join-Path $env:USERPROFILE '.twitchai\llama-server\current-release.json'
    if (-not (Test-Path -LiteralPath $binaryManifest)) { throw 'Run windows/build-ai-stack.ps1 first.' }
    $BinaryPath = (Get-Content -LiteralPath $binaryManifest -Raw | ConvertFrom-Json).serverPath
}
trap {
    if ([string]::IsNullOrWhiteSpace($ErrorFile)) { break }
    $startupError = $_
    try {
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($ErrorFile), $startupError.Exception.Message, [Text.UTF8Encoding]::new($false))
    } catch {
        [Console]::Error.WriteLine('Could not write the native runtime startup error file: ' + $_.Exception.Message)
    }
    Write-Error -ErrorRecord $startupError -ErrorAction Continue
    exit 1
}
$catalogScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\workers\coding\catalog.py'))
$StateDirectory = [IO.Path]::GetFullPath($StateDirectory)
$ownerFile = Join-Path $StateDirectory 'supervisor.json'
$stopFile = Join-Path $StateDirectory 'stop.requested'

function Test-NativeSupervisorIdentity {
    param($Owner, $Process, [string] $CommandLine, [string] $ExpectedStateDirectory)
    if ($null -eq $Process -or $Process.StartTime.ToUniversalTime().Ticks -ne [long]$Owner.startedUtcTicks) { return $false }
    # An existing supervisor may have been started by another copy of TwitchAI. Check
    # its recorded script, not this invocation's repository/portable script path.
    $savedScript = [string]$Owner.catalogScript
    if ([string]::IsNullOrWhiteSpace($savedScript) -or -not [IO.Path]::IsPathRooted($savedScript) -or
        [IO.Path]::GetFileName($savedScript) -ne 'catalog.py' -or [string]::IsNullOrWhiteSpace($CommandLine)) { return $false }
    # Both script and state arguments are quoted by this launcher. Exact tokens
    # prevent a same-prefix path from being mistaken for the saved identity.
    $scriptArgument = '"' + $savedScript + '"'
    $stateArgument = '"' + ($ExpectedStateDirectory -replace '(\\+)$', '$1$1') + '"'
    return $CommandLine.IndexOf($scriptArgument, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
        $CommandLine.IndexOf('--state-directory', [StringComparison]::Ordinal) -ge 0 -and
        $CommandLine.IndexOf($stateArgument, [StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Write-NativeSupervisorOwner {
    param($Process)
    $temporaryOwner = $ownerFile + '.new'
    [IO.File]::WriteAllText($temporaryOwner, (@{ processId = $Process.Id; startedUtcTicks = $Process.StartTime.ToUniversalTime().Ticks
        catalogScript = $catalogScript; binary = [IO.Path]::GetFullPath($BinaryPath); port = $Port } | ConvertTo-Json))
    if ([IO.File]::Exists($ownerFile)) { [IO.File]::Replace($temporaryOwner, $ownerFile, [NullString]::Value) }
    else { [IO.File]::Move($temporaryOwner, $ownerFile) }
}

function ConvertTo-NativeQuotedArgument {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)
    if ($Value -match '"') { throw 'An argument contains an unsupported double quote.' }
    # Pass literal UNC/local paths, including spaces and apostrophes, to the
    # Windows argv parser. Only trailing slashes before the closing quote need
    # doubling; this never evaluates a path as PowerShell code.
    return '"' + ($Value -replace '(\\+)$', '$1$1') + '"'
}

function Get-NativeStartupTimeoutSeconds {
    param([Parameter(Mandatory = $true)][string] $ModelDirectory)
    # A first metadata pass on SMB may be slower than a local drive. This is
    # only a readiness ceiling; a ready router returns immediately.
    if ([IO.Path]::GetFullPath($ModelDirectory).StartsWith('\\', [StringComparison]::Ordinal)) { return 120 }
    return 30
}

function Get-OwnedSupervisor {
    if (-not (Test-Path -LiteralPath $ownerFile -PathType Leaf)) { return $null }
    $owner = Get-Content -LiteralPath $ownerFile -Raw | ConvertFrom-Json
    $process = Get-Process -Id $owner.processId -ErrorAction SilentlyContinue
    if ($null -eq $process -or $process.StartTime.ToUniversalTime().Ticks -ne [long]$owner.startedUtcTicks) { return $null }
    $native = Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)"
    if ($null -eq $native -or -not (Test-NativeSupervisorIdentity -Owner $owner -Process $process -CommandLine $native.CommandLine -ExpectedStateDirectory $StateDirectory)) {
        throw 'The saved process identity does not match this native model supervisor; no process will be stopped.'
    }
    return $process
}

function Get-OwnedNativeChildren {
    param([Diagnostics.Process] $Supervisor)
    $owner = Get-Content -LiteralPath $ownerFile -Raw | ConvertFrom-Json
    $expectedBinary = [IO.Path]::GetFullPath([string]$owner.binary)
    $snapshot = @(Get-CimInstance Win32_Process)
    $parents = [Collections.Generic.Queue[object]]::new()
    $parents.Enqueue(@{ Id = $Supervisor.Id; StartedUtcTicks = $Supervisor.StartTime.ToUniversalTime().Ticks })
    while ($parents.Count -gt 0) {
        $parent = $parents.Dequeue()
        foreach ($native in @($snapshot | Where-Object { $_.ParentProcessId -eq $parent.Id })) {
            $child = Get-Process -Id $native.ProcessId -ErrorAction SilentlyContinue
            if ($null -eq $child) { continue }
            try {
                $started = $child.StartTime.ToUniversalTime().Ticks
                # A recycled parent PID cannot own a process created before it.
                if ($started -lt $parent.StartedUtcTicks -or
                    $child.StartTime.ToUniversalTime().ToString('yyyyMMddHHmmssfff') -ne
                    $native.CreationDate.ToUniversalTime().ToString('yyyyMMddHHmmssfff')) { continue }
                $parents.Enqueue(@{ Id = $child.Id; StartedUtcTicks = $started })
                if (-not [string]::IsNullOrWhiteSpace($child.Path) -and
                    [IO.Path]::GetFullPath($child.Path).Equals($expectedBinary, [StringComparison]::OrdinalIgnoreCase)) {
                    [pscustomobject]@{ ProcessId = $child.Id; StartedUtcTicks = $started }
                }
            } catch [System.InvalidOperationException] {
                # A child that completed while the process snapshot was read is
                # already gone and needs no wait or cleanup.
                if (-not $child.HasExited) { throw }
            } finally { $child.Dispose() }
        }
    }
}

function Wait-OwnedNativeChildren {
    param([object[]] $Children, [DateTime] $Deadline)
    $remainingChildren = @()
    foreach ($identity in $Children) {
        $child = Get-Process -Id $identity.ProcessId -ErrorAction SilentlyContinue
        if ($null -eq $child) { continue }
        try {
            # Wait only for the same captured process. A newly reused PID is not
            # part of this supervisor's shutdown and must never be touched.
            if ($child.StartTime.ToUniversalTime().Ticks -ne $identity.StartedUtcTicks) { continue }
            $remainingMilliseconds = [Math]::Max(0, [Math]::Ceiling(($Deadline - [DateTime]::UtcNow).TotalMilliseconds))
            if (-not $child.WaitForExit([int]$remainingMilliseconds)) { $remainingChildren += $identity.ProcessId }
        } catch [System.InvalidOperationException] {
            if (-not $child.HasExited) { throw }
        } finally { $child.Dispose() }
    }
    if ($remainingChildren.Count -gt 0) {
        throw ('The owned native model process tree is still closing after 10 seconds (process IDs: ' +
            ($remainingChildren -join ', ') + '). Inspect ' + (Join-Path $StateDirectory 'llama.stderr.log'))
    }
}

function Show-NativeStatus {
    $process = Get-OwnedSupervisor
    $healthy = $false
    $models = @()
    if ($process) {
        try {
            $null = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 3
            $models = @( (Invoke-RestMethod -Uri "http://127.0.0.1:$Port/v1/models?reload=1" -TimeoutSec 5).data )
            $healthy = $true
        } catch { $healthy = $false }
    }
    [pscustomobject]@{ Running = $null -ne $process; Healthy = $healthy
        SupervisorId = if ($process) { $process.Id } else { $null }
        Endpoint = "http://127.0.0.1:$Port"; ModelCount = $models.Count; StateDirectory = $StateDirectory }
}

if ($Action -in @('Status', 'Stop') -and (Test-Path -LiteralPath $ownerFile -PathType Leaf)) {
    $Port = [int](Get-Content -LiteralPath $ownerFile -Raw | ConvertFrom-Json).port
}
if ($Action -eq 'Status') { Show-NativeStatus; return }
if ($Action -eq 'Stop') {
    $process = Get-OwnedSupervisor
    if ($process) {
        $ownedChildren = @(Get-OwnedNativeChildren -Supervisor $process)
        [IO.File]::WriteAllText($stopFile, 'stop')
        # A cache prepare may first save and restore under the manager lock;
        # shutdown then checkpoints up to two model slots before terminating.
        $childrenDeadline = [DateTime]::MinValue
        if (-not $process.WaitForExit($ShutdownTimeoutSeconds * 1000)) {
            $childrenDeadline = [DateTime]::UtcNow.AddSeconds(10)
            $verified = Get-OwnedSupervisor
            if ($verified) {
                # The supervisor owns a Windows Job Object containing only its
                # llama router and GPU0/GPU1 children. Closing its process also
                # closes that Job Object and releases their model allocations.
                Stop-Process -Id $verified.Id -Force
                $remainingMilliseconds = [Math]::Max(0, [Math]::Ceiling(($childrenDeadline - [DateTime]::UtcNow).TotalMilliseconds))
                if (-not $verified.WaitForExit([int]$remainingMilliseconds)) {
                    throw 'The owned native model supervisor could not be stopped.'
                }
            }
        }
        # Closing the supervisor's Job Object initiates termination of its
        # model workers, but their CUDA teardown can outlast the supervisor.
        # Report success only once those captured, verified workers exited.
        if ($childrenDeadline -eq [DateTime]::MinValue) { $childrenDeadline = [DateTime]::UtcNow.AddSeconds(10) }
        Wait-OwnedNativeChildren -Children $ownedChildren -Deadline $childrenDeadline
    }
    Show-NativeStatus
    return
}

if (Get-OwnedSupervisor) {
    $runningState = Get-Content -LiteralPath (Join-Path $StateDirectory 'runtime.json') -Raw | ConvertFrom-Json
    if ([IO.Path]::GetFullPath($runningState.modelRoot) -ne [IO.Path]::GetFullPath($ModelRoot) -or
        [IO.Path]::GetFullPath($runningState.binary) -ne [IO.Path]::GetFullPath($BinaryPath) -or [int]$runningState.port -ne $Port) {
        throw 'The managed native runtime is already running with different paths or port. Stop it before changing its configuration.'
    }
    $status = Show-NativeStatus
    if (-not $status.Healthy) {
        throw "The managed native supervisor is running but its model endpoint is not ready. Inspect $StateDirectory\llama.stderr.log and $StateDirectory\stderr.log"
    }
    $status
    return
}
if (-not (Test-Path -LiteralPath $catalogScript -PathType Leaf)) { throw "Native model catalog script not found: $catalogScript. Restore the complete Assets\NativeRuntime folder from the portable package." }
if (-not (Test-Path -LiteralPath $ModelRoot -PathType Container)) { throw "AI model directory unavailable: $ModelRoot. Check the folder, NAS connection and access permissions." }
if (-not (Test-Path -LiteralPath $BinaryPath -PathType Leaf)) { throw "Native llama-server.exe not found: $BinaryPath. Specify an existing binary with -BinaryPath." }
if ([string]::IsNullOrWhiteSpace($PythonPath)) {
    $PythonPath = (Get-Command python.exe -ErrorAction Stop).Source
}
if (-not (Test-Path -LiteralPath $PythonPath -PathType Leaf)) { throw "Python interpreter not found: $PythonPath" }
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { throw "Port $Port is already occupied; its existing process will not be changed." }
# llama-server writes its banner and help text to stderr. Windows PowerShell
# turns native stderr into ErrorRecords when ErrorActionPreference is Stop,
# which used to abort startup before the exit code and capability checks ran.
$savedErrorActionPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = 'Continue'
    $help = (& $BinaryPath --help 2>&1 | Out-String)
    $helpExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $savedErrorActionPreference
}
if ($helpExitCode -ne 0 -or $help -notmatch '--models-preset' -or $help -notmatch '--fit-target' -or $help -notmatch '--fit-ctx' -or
    $help -notmatch '--embedding' -or $help -notmatch '--mmproj' -or $help -notmatch '--tags' -or
    $help -notmatch '--reasoning-effort' -or $help -notmatch '--reasoning-budget' -or $help -notmatch '--sleep-idle-seconds') {
    throw 'The existing native llama binary does not support the required model router, memory fitting and model reasoning options.'
}
New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null
# A graceful stop is communicated through this durable marker. A later start
# must clear it before launching the new supervisor; otherwise catalog.py sees
# the marker immediately and exits after the start command reported success.
if (Test-Path -LiteralPath $stopFile -PathType Leaf) { Remove-Item -LiteralPath $stopFile -Force }
$arguments = @($catalogScript, '--model-root', [IO.Path]::GetFullPath($ModelRoot), '--binary', [IO.Path]::GetFullPath($BinaryPath),
    '--state-directory', $StateDirectory, '--port', [string]$Port, '--fit-target', [string]$FitTargetMiB, '--gpu-layers', $GpuLayers)
$quotedArguments = ($arguments | ForEach-Object {
    ConvertTo-NativeQuotedArgument $_
}) -join ' '
$process = Start-Process -FilePath $PythonPath -ArgumentList $quotedArguments -WorkingDirectory $StateDirectory -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $StateDirectory 'stdout.log') -RedirectStandardError (Join-Path $StateDirectory 'stderr.log') -PassThru
Write-NativeSupervisorOwner -Process $process
$startupTimeoutSeconds = Get-NativeStartupTimeoutSeconds $ModelRoot
$deadline = [DateTime]::UtcNow.AddSeconds($startupTimeoutSeconds)
do {
    if ($process.HasExited) { throw "Native model supervisor exited. Inspect $StateDirectory\stderr.log" }
    try {
        $null = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 2
        # A venv python.exe can be a launcher for another interpreter process.
        # Persist the actual supervisor so forced shutdown closes its Job Object.
        $runtimeState = Get-Content -LiteralPath (Join-Path $StateDirectory 'runtime.json') -Raw | ConvertFrom-Json
        $actualSupervisor = Get-Process -Id $runtimeState.supervisorPid -ErrorAction Stop
        Write-NativeSupervisorOwner -Process $actualSupervisor
        Show-NativeStatus
        return
    } catch { Start-Sleep -Milliseconds 500 }
} while ([DateTime]::UtcNow -lt $deadline)
throw "Native model supervisor did not become ready within $startupTimeoutSeconds seconds. Inspect $StateDirectory\stderr.log"
