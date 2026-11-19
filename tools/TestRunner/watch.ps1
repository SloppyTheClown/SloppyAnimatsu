# Live test progress, refreshed until the run ends.
#
#   powershell -ExecutionPolicy Bypass -File tools/TestRunner/watch.ps1
#   powershell -ExecutionPolicy Bypass -File tools/TestRunner/watch.ps1 -Interval 2
#
# Prints status.ps1 once every -Interval seconds (default 5) and stops when the run it was
# watching ends, leaving the final report on screen. Ctrl+C also stops it. The window title
# carries the same progress, so a window behind other windows still answers "how far along is
# it". The exit code is the last status.ps1 code: 0 nothing was running, 1 a run was live,
# 2 a run died without a summary.
#
# The console version of this is watch.bat, next to SloppyAnimatsu.sln; double-click that.

[CmdletBinding()]
param(
    [int]$Interval = 5,
    [string]$StatusFile = ''
)

$ErrorActionPreference = 'Stop'

if ($Interval -lt 1) { $Interval = 1 }

$statusScript = Join-Path $PSScriptRoot 'status.ps1'
if (-not (Test-Path -LiteralPath $statusScript)) {
    throw ('Cannot find {0}.' -f $statusScript)
}

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Resolve-StatusPath {
    $candidates = @()
    if ($StatusFile) { $candidates += $StatusFile }
    $candidates += (Join-Path (Get-Location).Path 'testrunner-status.json')
    $candidates += (Join-Path $repo 'testrunner-status.json')
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    return $null
}

function Set-WindowTitle([string]$Title) {
    try { $Host.UI.RawUI.WindowTitle = $Title } catch { }
}

function Get-ProgressTitle {
    $path = Resolve-StatusPath
    if (-not $path) { return 'SloppyAnimatsu tests: no run' }

    try {
        # Share delete too: the runner replaces the file with File.Move(overwrite), which needs every
        # open handle to allow it (see Read-SharedText in status.ps1).
        $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read,
            ([System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete))
        try {
            $reader = New-Object System.IO.StreamReader($stream)
            try { $status = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        }
        finally { $stream.Dispose() }
    }
    catch {
        return 'SloppyAnimatsu tests: reading status'
    }

    $total = [int]$status.total
    if ($total -le 0) { return ('SloppyAnimatsu tests: {0}' -f [string]$status.state) }

    $completed = [int]$status.completed
    $percent = 100.0 * $completed / $total
    return ('SloppyAnimatsu tests {0}/{1} ({2:0.0}%) - {3} failed - {4}' -f $completed, $total, $percent, [int]$status.failed, [string]$status.state)
}

$code = 0
$previous = $null
$first = $true

Set-WindowTitle 'SloppyAnimatsu tests'

while ($true) {
    try { Clear-Host } catch { }

    Write-Host ('SloppyAnimatsu test progress - updated {0:HH:mm:ss} - refresh {1}s - Ctrl+C to stop' -f (Get-Date), $Interval)
    Write-Host ''
    # Pass the arguments by name. Array splatting would bind them positionally, turning
    # "-StatusFile" into the value of the next parameter instead of a parameter name.
    if ($StatusFile) {
        & $statusScript -NoHeader -StatusFile $StatusFile
    }
    else {
        & $statusScript -NoHeader
    }
    $code = $LASTEXITCODE
    Set-WindowTitle (Get-ProgressTitle)

    if ($first) {
        $first = $false
        if ($code -eq 2) {
            Write-Host ''
            Write-Host 'Nothing is being tracked: the last run ended without a summary. There is nothing to watch.'
            break
        }
    }
    elseif ($previous -eq 1 -and $code -ne 1) {
        Write-Host ''
        if ($code -eq 2) {
            Write-Host ('The run has ended, but its status file still says "running": treat the result as unknown ({0:HH:mm:ss}).' -f (Get-Date))
        }
        else {
            Write-Host ('The run has finished - final state is above ({0:HH:mm:ss}).' -f (Get-Date))
        }
        try { [Console]::Beep() } catch { }
        break
    }

    $previous = $code
    Start-Sleep -Seconds $Interval
}

exit $code