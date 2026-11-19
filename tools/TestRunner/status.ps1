# Live progress of a SloppyAnimatsu TestRunner run.
#
#   powershell -ExecutionPolicy Bypass -File tools/TestRunner/status.ps1
#   powershell -ExecutionPolicy Bypass -File tools/TestRunner/status.ps1 -StatusFile path -Json
#
# Exit codes: 0 no run is live, 1 a run is live, 2 the status file says "running" but its
# process is gone (a stale file).
#
# Works with a current status file (testrunner-status.json) and, for runs started before that
# existed, by finding the TestRunner process and reading its .tmp-run/trace.log. For a view that
# refreshes itself, run watch.ps1 (or watch.bat, next to the solution) instead.

[CmdletBinding()]
param(
    [string]$StatusFile = '',
    [switch]$Json,
    [switch]$NoHeader,
    [int]$TraceTail = 3
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$workspace = Split-Path -Parent $repo

function Resolve-StatusFile {
    $candidates = @()
    if ($StatusFile) { $candidates += $StatusFile }
    $candidates += (Join-Path (Get-Location).Path 'testrunner-status.json')
    $candidates += (Join-Path $repo 'testrunner-status.json')
    $candidates += (Join-Path $workspace '.tmp-run\testrunner-status.json')

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    return $null
}

function Get-RunnerProcesses {
    $result = @()
    $processes = @(Get-Process -Name 'TestRunner', 'dotnet' -ErrorAction SilentlyContinue)
    foreach ($process in $processes) {
        $isRunner = $false
        try {
            $isRunner = [bool]($process.Modules | Where-Object { $_.ModuleName -eq 'TestRunner.dll' } | Select-Object -First 1)
        }
        catch {
            # Another user's process: its modules are not readable. Not a runner we started.
        }

        if ($isRunner) {
            $result += [pscustomobject]@{
                id             = $process.Id
                name           = $process.ProcessName
                started        = $process.StartTime
                elapsedSeconds = ((Get-Date) - $process.StartTime).TotalSeconds
                cpuSeconds     = $process.CPU
                memoryMB       = [math]::Round($process.WorkingSet64 / 1MB)
            }
        }
    }
    return $result
}

function Find-TraceFile {
    $candidates = @(
        (Join-Path (Get-Location).Path 'trace.log'),
        (Join-Path (Get-Location).Path '.tmp-run\trace.log'),
        (Join-Path $repo 'trace.log'),
        (Join-Path $workspace '.tmp-run\trace.log')
    )

    $best = $null
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            $item = Get-Item -LiteralPath $candidate
            if (($null -eq $best) -or ($item.LastWriteTime -gt $best.LastWriteTime)) {
                $best = $item
            }
        }
    }
    return $best
}

function Read-SharedText([string]$Path) {
    # A live run replaces its status file with File.Move(overwrite), which fails unless every open
    # handle allows delete, and it updates the file while this runs. Read with ReadWrite|Delete so
    # watching a run never makes the runner log "cannot write the status file".
    $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
    try {
        $reader = New-Object System.IO.StreamReader($stream)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally {
        $stream.Dispose()
    }
}

function Format-Duration([double]$Seconds) {
    $span = [TimeSpan]::FromSeconds($Seconds)
    # Use the TimeSpan components, not [int]$span.TotalHours: casting rounds (1.82 hours
    # becomes 2), which added an hour to every estimate past 1h30m.
    if ($span.TotalDays -ge 1) {
        return ('{0}d {1}h {2:00}m' -f $span.Days, $span.Hours, $span.Minutes)
    }
    if ($span.TotalHours -ge 1) {
        return ('{0}h {1:00}m {2:00}s' -f $span.Hours, $span.Minutes, $span.Seconds)
    }
    if ($span.TotalMinutes -ge 1) {
        return ('{0}m {1:00}s' -f $span.Minutes, $span.Seconds)
    }
    return ('{0}s' -f $span.Seconds)
}

function Write-Verdict([string]$Text, [string]$Color) {
    # Write-Host keeps the colour that makes the answer obvious at a glance, but it cannot be
    # captured, so fall back to plain output when the host is redirected (a log, a harness) or
    # when the session is running in ConstrainedLanguage.
    if ($ExecutionContext.SessionState.LanguageMode -eq 'FullLanguage' -and -not [Console]::IsOutputRedirected) {
        Write-Host $Text -ForegroundColor $Color
    }
    else {
        Write-Output $Text
    }
}

$statusPath = Resolve-StatusFile
$status = $null
if ($statusPath) {
    for ($attempt = 0; $attempt -lt 3 -and -not $status; $attempt++) {
        try {
            $status = Read-SharedText $statusPath | ConvertFrom-Json
        }
        catch {
            Start-Sleep -Milliseconds 100
        }
    }
}

$runners = @(Get-RunnerProcesses)
$statusPid = 0
if ($status -and $status.pid) { $statusPid = [int]$status.pid }
$statusProcess = $null
if ($statusPid -gt 0) {
    $statusProcess = Get-Process -Id $statusPid -ErrorAction SilentlyContinue
}
$statusAlive = [bool]$statusProcess
$state = ''
if ($status) { $state = [string]$status.state }

$live = ($runners.Count -gt 0)
$stale = ($state -eq 'running' -and -not $statusAlive)

if ($Json) {
    if ($statusPath) {
        Write-Output (Read-SharedText $statusPath)
    }
    else {
        Write-Output ($runners | ConvertTo-Json -Depth 4)
    }
    if ($live) { exit 1 }
    if ($stale) { exit 2 }
    exit 0
}

if (-not $NoHeader) {
    Write-Output 'SloppyAnimatsu TestRunner status'
}

$pidSuffix = ''
if ($statusPid -gt 0) { $pidSuffix = ' (pid {0})' -f $statusPid }
if ($stale -and $live) {
    $liveIds = @($runners | ForEach-Object { $_.id }) -join ', '
    Write-Verdict ('The last run stopped without a summary, and another runner IS live (pid {0}). Do not start one more.' -f $liveIds) 'Red'
}
elseif ($stale) {
    Write-Verdict 'No process is running, but the last run stopped without writing a summary.' 'Red'
}
elseif ($live) {
    Write-Verdict ('A test run IS live{0} - do not start another one.' -f $pidSuffix) 'Yellow'
}
else {
    Write-Verdict 'No test run is active - safe to start a session.' 'Green'
}
Write-Output ''

if ($status) {
    $stateLabel = $state.ToUpperInvariant()
    if ($stale) { $stateLabel = 'STALE (the process is gone)' }

    $pidText = [string]$statusPid
    if ($statusAlive) { $pidText = $pidText + ' (alive)' } else { $pidText = $pidText + ' (gone)' }
    $tracked = $runners | Where-Object { $_.id -eq $statusPid } | Select-Object -First 1
    if ($tracked) {
        $pidText = $pidText + (', {0} MB, {1:0}s CPU' -f $tracked.memoryMB, $tracked.cpuSeconds)
    }

    $heartbeatAge = -1
    if ($status.updatedAt) {
        try {
            $heartbeatAge = ([DateTimeOffset]::Now - [DateTimeOffset]::Parse([string]$status.updatedAt)).TotalSeconds
        }
        catch { }
    }

    $progressAge = -1
    if ($status.lastProgressAt) {
        try {
            $progressAge = ([DateTimeOffset]::Now - [DateTimeOffset]::Parse([string]$status.lastProgressAt)).TotalSeconds
        }
        catch { }
    }

    $total = [int]$status.total
    $completed = [int]$status.completed
    if ($total -gt 0) {
        $progress = '{0}/{1} ({2:0.0}%)' -f $completed, $total, (100.0 * $completed / $total)
    }
    else {
        $progress = '{0} completed' -f $completed
    }

    $results = '{0} passed, {1} failed, {2} skipped' -f [int]$status.passed, [int]$status.failed, [int]$status.skipped
    if ([int]$status.filteredOut -gt 0) {
        $results = $results + (', {0} filtered out' -f [int]$status.filteredOut)
    }

    Write-Output ('  state      : {0}' -f $stateLabel)
    Write-Output ('  pid        : {0}' -f $pidText)
    Write-Output ('  started    : {0} (elapsed {1})' -f $status.startedAt, (Format-Duration ([double]$status.elapsedSeconds)))
    if ($heartbeatAge -ge 0) {
        Write-Output ('  heartbeat  : {0:0}s ago' -f $heartbeatAge)
    }
    if ($progressAge -ge 0) {
        Write-Output ('  progress age: {0:0}s (last test started or finished)' -f $progressAge)
    }
    if ($status.workingSetMB) {
        Write-Output ('  memory     : {0:0} MB working, {1:0} MB managed, peak {2:0} MB' -f [double]$status.workingSetMB, [double]$status.managedMB, [double]$status.peakWorkingSetMB)
    }
    Write-Output ('  progress   : {0}' -f $progress)
    if ($state -eq 'running' -and $statusAlive -and $completed -gt 0 -and $total -gt $completed -and [double]$status.elapsedSeconds -gt 0) {
        $rate = $completed / [double]$status.elapsedSeconds
        $remaining = ($total - $completed) / $rate
        Write-Output ('  eta        : ~{0} at {1:0.0} tests/s' -f (Format-Duration $remaining), $rate)
    }
    Write-Output ('  results    : {0}' -f $results)
    if ($status.current) {
        Write-Output ('  current    : {0}' -f $status.current)
    }
    if ($status.last) {
        Write-Output ('  last       : {0} ({1})' -f $status.last, $status.lastResult)
    }
    if ($status.message) {
        Write-Output ('  message    : {0}' -f $status.message)
    }
    Write-Output ('  status file: {0}' -f $statusPath)

    if ($state -eq 'running') {
        if (-not $statusAlive) {
            Write-Output ''
            Write-Output 'WARNING: the status says "running" but its process is gone. Treat the result as unknown.'
        }
        elseif ($heartbeatAge -gt 30) {
            Write-Output ''
            Write-Output ('WARNING: no heartbeat for {0:0}s. The process is paused, frozen or gone.' -f $heartbeatAge)
        }
        elseif ($progressAge -gt 300) {
            Write-Output ''
            Write-Output ('WARNING: no test has started or finished for {0:0}s. The current test may be hung (or very slow).' -f $progressAge)
        }
        elseif ($completed -eq 0) {
            Write-Output ''
            Write-Output ('Waiting: no test has finished yet (last test change {0:0}s ago).' -f $progressAge)
        }
    }
}
else {
    Write-Output '  state      : no status file (a run started before progress reporting, or --no-status)'
}

$trackedId = if ($status) { $statusPid } else { -1 }
$extra = @($runners | Where-Object { $_.id -ne $trackedId })
if ($extra.Count -gt 0) {
    Write-Output ''
    Write-Output 'Untracked TestRunner process(es):'
    foreach ($runner in $extra) {
        Write-Output ('  pid {0} ({1}): started {2}, up {3}, {4} MB, {5:0}s CPU' -f $runner.id, $runner.name, $runner.started.ToString('HH:mm:ss'), (Format-Duration $runner.elapsedSeconds), $runner.memoryMB, $runner.cpuSeconds)
    }
}

$trace = Find-TraceFile
if (($extra.Count -gt 0 -or -not $status) -and $trace) {
    try {
        $text = Read-SharedText $trace.FullName
        $runs = @($text -split "`r?`n" | Where-Object { $_ -match '^RUN ' })
        $age = ((Get-Date) - $trace.LastWriteTime).TotalSeconds
        Write-Output ''
        Write-Output ('  trace      : {0}' -f $trace.FullName)
        Write-Output ('  started    : {0} tests, updated {1:0}s ago' -f $runs.Count, $age)
        if ($runs.Count -gt 0) {
            foreach ($line in @($runs | Select-Object -Last $TraceTail)) {
                Write-Output ('  {0}' -f $line)
            }
        }
        if ($age -gt 60) {
            Write-Output ('WARNING: the trace has not changed for {0:0}s. The test may be hung.' -f $age)
        }
    }
    catch {
        Write-Output ('  trace      : {0} (cannot read: {1})' -f $trace.FullName, $_.Exception.Message)
    }
}

if ($live) { exit 1 }
if ($stale) { exit 2 }
exit 0