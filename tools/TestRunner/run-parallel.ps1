<#
.SYNOPSIS
  Runs the SloppyAnimatsu test suite in parallel TestRunner processes and prints one live line.

.DESCRIPTION
  Runs the suite across N TestRunner processes. Every test parses its own scene, so the driver
  deals the cases round-robin: each shard gets an equal share of every scene group and the
  heaviest sample cannot own one shard. The driver sums the shards' status files into one progress
  line and owns the run baseline that `TestRunner --changed` reads: it is written only when every
  shard finished, so an interrupted run does not hide work from the next `--changed` run.

  Run it on the third monitor like every other window:
    powershell -NoProfile -ExecutionPolicy Bypass -File SloppyAnimatsu\tools\launch-on-monitor3.ps1 `
        -FilePath powershell.exe -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',
        '<repo>\tools\TestRunner\run-parallel.ps1'

.EXAMPLE
  .\run-parallel.ps1 -Shards 4

.EXAMPLE
  $env:SLOPPYANIMATSU_SAMPLES = 'Z:\scenes;Z:\more'; .\run-parallel.ps1 -Shards 4 -Changed
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 16)][int]$Shards = 3,
    [string]$Samples = $env:SLOPPYANIMATSU_SAMPLES,
    [switch]$Changed,
    [string[]]$Filter = @(),
    [switch]$NoBuild,
    [switch]$NoBaseline
)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$runner = Join-Path $root 'tools\TestRunner\bin\Debug\net9.0\TestRunner.dll'
$statusScript = Join-Path $root 'tools\TestRunner\status.ps1'
$baseline = Join-Path $root '.tmp-run\test-baseline.json'
$runDir = Join-Path $root '.tmp-run'
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

if (-not (Test-Path $runner)) {
    throw "TestRunner is not built: $runner"
}

# A live run already owns tools/TestRunner/bin; never start a second one.
& $statusScript *> $null
if ($LASTEXITCODE -eq 1) {
    Write-Host 'A test run is already live - stopping it first (status.ps1), then start again.' -ForegroundColor Yellow
    exit 1
}

if (-not $NoBuild) {
    Write-Host 'Building...' -ForegroundColor Cyan
    Push-Location $root
    try {
        $build = & dotnet build SloppyAnimatsu.sln -m:1 /nodeReuse:false -v q 2>&1
        if ($LASTEXITCODE -ne 0) {
            $build | Select-Object -Last 25
            throw 'Build failed - no tests were started.'
        }
    }
    finally {
        Pop-Location
    }
    Write-Host 'Built.' -ForegroundColor Cyan
}

if ($PSBoundParameters.ContainsKey('Samples') -or $Samples) {
    $env:SLOPPYANIMATSU_SAMPLES = $Samples
}
else {
    # The workspace's own copy of the sample scenes.
    $localSamples = Join-Path (Split-Path $root -Parent) 'samples\dupmodel-blackcat'
    if (Test-Path $localSamples) { $env:SLOPPYANIMATSU_SAMPLES = $localSamples }
}

if (-not $env:SLOPPYANIMATSU_SAMPLES) {
    Write-Host 'WARNING: SLOPPYANIMATSU_SAMPLES is not set. The scene theories produce no cases, so this is only the no-sample suite (a few hundred tests), not the full run. A green result here does not say the sample tests passed.' -ForegroundColor Yellow
}

$started = Get-Date
# Not $shards: PowerShell variable names are case-insensitive, so that would be the [int]$Shards
# parameter and assigning the list would fail converting Object[] to Int32.
$shardList = @()
$oldTemp, $oldTmp = $env:TEMP, $env:TMP
try {
    for ($i = 1; $i -le $Shards; $i++) {
        $status = Join-Path $root ("testrunner-status.s{0}of{1}.json" -f $i, $Shards)
        $log = Join-Path $runDir ("shard-{0}-of-{1}.log" -f $i, $Shards)
        $err = Join-Path $runDir ("shard-{0}-of-{1}.err.log" -f $i, $Shards)
        Remove-Item $status, $log, $err -ErrorAction SilentlyContinue

        $env:TEMP = Join-Path $runDir ("temp\s{0}" -f $i)
        $env:TMP = $env:TEMP
        New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null

        $arguments = @($runner, "--shard=$i/$Shards", "--no-baseline", "--status=$status")
        if ($Changed) { $arguments += '--changed' }
        foreach ($f in $Filter) { $arguments += "--filter=$f" }
        # Start-Process joins the list with spaces without quoting: keep sample/filter paths with
        # spaces (and --status=...) in one argument.
        $arguments = @($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })

        $process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $root `
            -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError $err
        $shardList += [pscustomobject]@{ Index = $i; Process = $process; Status = $status; Log = $log; ErrorLog = $err }
        Write-Host ("shard {0}/{1}: pid {2}" -f $i, $Shards, $process.Id)
    }
    $env:TEMP = $oldTemp; $env:TMP = $oldTmp

    $width = $Shards.ToString().Length

    function Read-ShardState([string]$path) {
        # The runner replaces its status file while it runs, so a read can land in the gap where
        # the file is briefly absent or half written. That is not a failure: retry, then treat it
        # as "no state yet".
        for ($attempt = 0; $attempt -lt 4; $attempt++) {
            try {
                if (-not (Test-Path -LiteralPath $path)) { return $null }
                return Get-Content -Raw -LiteralPath $path -ErrorAction Stop | ConvertFrom-Json
            }
            catch { Start-Sleep -Milliseconds 75 }
        }
        return $null
    }

    while ($true) {
        $live = @($shardList | Where-Object { -not $_.Process.HasExited })
        $total = 0; $done = 0; $failed = 0; $skipped = 0
        $finishedShards = 0
        foreach ($shard in $shardList) {
            $state = Read-ShardState $shard.Status
            if ($null -eq $state) { continue }
            $total += [int]$state.total
            $done += [int]$state.completed
            $failed += [int]$state.failed
            $skipped += [int]$state.skipped
            if ($state.state -in 'passed', 'failed') { $finishedShards++ }
        }
        $elapsed = (Get-Date) - $started
        $percent = if ($total -gt 0) { 100.0 * $done / $total } else { 0 }
        $line = "{0,5}/{1,-5} ({2,5:N1}%)  {3,4} failed  {4,4} skipped  {5,2}/{6} shards done  {7:hh\:mm\:ss}" -f `
            $done, $total, $percent, $failed, $skipped, $finishedShards, $Shards, $elapsed
        Write-Host ("`r" + $line) -NoNewline
        if ($live.Count -eq 0) { break }
        Start-Sleep -Seconds 5
    }
    Write-Host ''

    $exit = 0
    $allFinished = $true
    $failedClasses = @()
    foreach ($shard in $shardList) {
        $state = Read-ShardState $shard.Status
        # The shard's own status is authoritative: Start-Process's ExitCode is not available here
        # (it stays empty), and a shard that crashed or found no test must not read as green.
        if ($null -eq $state -or $state.state -notin 'passed', 'failed') {
            $allFinished = $false
            $exit = 1
        }
        elseif ([int]$state.failed -gt 0 -or $state.state -eq 'failed') {
            $exit = 1
        }
        $summary = if ($null -eq $state) {
            'no status file'
        } else {
            "{0} passed, {1} failed, {2} skipped in {3:N0}s ({4})" -f `
                $state.passed, $state.failed, $state.skipped, $state.elapsedSeconds, $state.state
        }
        Write-Host ("shard {0}/{1}: {2}" -f $shard.Index, $Shards, $summary)

        if (Test-Path $shard.Log) {
            $fails = @(Select-String -Path $shard.Log -Pattern '^FAIL ' | ForEach-Object { $_.Line.Substring(5) })
            foreach ($name in $fails) { Write-Host "  FAIL $name"; $failedClasses += ($name -split '\.')[0] }
        }
        if ((Test-Path $shard.ErrorLog) -and (Get-Item $shard.ErrorLog).Length -gt 0) {
            Write-Host "  stderr: $($shard.ErrorLog)" -ForegroundColor Yellow
        }
        Write-Host ("  log: {0}" -f $shard.Log)
    }

    if (-not $NoBaseline -and $allFinished -and $Filter.Count -eq 0) {
        $payload = [ordered]@{
            finishedAt = (Get-Date).ToString('o')
            samples = $env:SLOPPYANIMATSU_SAMPLES
            failed = @($failedClasses | Where-Object { $_ } | Sort-Object -Unique)
        }
        $payload | ConvertTo-Json | Set-Content -LiteralPath $baseline -Encoding UTF8
        Write-Host ("baseline: {0} ({1} failed classes)" -f $baseline, $payload.failed.Count)
    }

    Write-Host ("{0}  total {1:hh\:mm\:ss}" -f ($(if ($exit -eq 0) { 'GREEN' } else { 'FAILED' })), ((Get-Date) - $started))
    exit $exit
}
finally {
    $env:TEMP = $oldTemp; $env:TMP = $oldTmp
    foreach ($shard in $shardList) {
        if ($shard.Process -and -not $shard.Process.HasExited) {
            Write-Host ("stopping shard {0} (pid {1})" -f $shard.Index, $shard.Process.Id)
            Stop-Process -Id $shard.Process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

