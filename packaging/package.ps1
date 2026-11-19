<#
.SYNOPSIS
    Builds the self-contained SloppyAnimatsu packages for Windows and Linux.

.DESCRIPTION
    Publishes SloppyAnimatsu.Viewer and SloppyAnimatsu.Cli for each runtime and packs them as
    dist\SloppyAnimatsu-<version>-<runtime>.zip (Windows) or .tar.gz (Linux), together with the user
    documentation in docs\, a short README.txt and, on Linux, install.sh and its icon. Also writes
    dist\SHA256SUMS.

    The version comes from Directory.Build.props. The .NET 9 SDK is the only requirement, and the
    script works on Windows PowerShell 5.1 as well as PowerShell 7. packaging\package.sh does the
    same on Linux; either script can cross-publish the other platform's package.

.PARAMETER Runtime
    The runtime identifiers to publish. Default: win-x64 and linux-x64.

.PARAMETER Version
    Overrides the version in Directory.Build.props.

.PARAMETER OutDir
    Where the packages and SHA256SUMS are written. Default: dist beside the solution.

.PARAMETER KeepStage
    Keeps the unpacked dist\.stage folders, to inspect a package without extracting it.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File packaging\package.ps1

.EXAMPLE
    packaging\package.ps1 -Runtime linux-x64 -KeepStage
#>
[CmdletBinding()]
param(
    [string[]]$Runtime = @('win-x64', 'linux-x64'),
    [string]$Version = '',
    [string]$OutDir = '',
    [switch]$KeepStage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet is not on PATH; the .NET 9 SDK is needed to package.'
}
if (-not $Version) {
    $props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
    $match = [regex]::Match($props, '<Version>([^<]+)</Version>')
    if (-not $match.Success) { throw 'Directory.Build.props has no <Version> element.' }
    $Version = $match.Groups[1].Value.Trim()
}

$out = [IO.Path]::GetFullPath($OutDir)
$stageRoot = Join-Path $out '.stage'
New-Item -ItemType Directory -Force $out | Out-Null
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
if (Test-Path (Join-Path $out '.tool')) { Remove-Item (Join-Path $out '.tool') -Recurse -Force }
New-Item -ItemType Directory -Force $stageRoot | Out-Null

function Invoke-Dotnet([string[]]$Arguments, [string]$What) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$What failed (dotnet exited $LASTEXITCODE)." }
}

function Publish-App([string]$Project, [string]$RuntimeIdentifier, [string]$Target) {
    # The .pdb files stay beside the exes: a crash log's stack trace carries file names and line
    # numbers only when the matching symbols are next to the assembly.
    Invoke-Dotnet @(
        'publish', (Join-Path $root $Project), '-c', 'Release', '-r', $RuntimeIdentifier,
        '--self-contained', 'true', "-p:Version=$Version", '-p:DebugType=portable',
        '-p:DebugSymbols=true', '-p:GenerateDocumentationFile=false',
        '--nologo', '-v:minimal', '-o', $Target
    ) "publishing $Project for $RuntimeIdentifier"
}

Write-Host "SloppyAnimatsu ${Version}: packing for $($Runtime -join ', ')"
# The archiver is published once: it writes the tar.gz modes itself, because the system tar on
# Windows stores an extension-less Linux apphost as 0666.
$toolDir = Join-Path $out '.tool'
Invoke-Dotnet @('publish', (Join-Path $root 'tools\PackageTool'), '-c', 'Release',
    '--nologo', '-v:quiet', '-o', $toolDir) 'building tools\PackageTool'
$tool = Join-Path $toolDir 'PackageTool.exe'

$artifacts = @()
foreach ($rid in $Runtime) {
    $windowsTarget = $rid.StartsWith('win-')
    $name = "SloppyAnimatsu-$Version-$rid"
    # One folder per runtime, so a package's archive holds exactly one top-level folder.
    $container = Join-Path $stageRoot $rid
    $stage = Join-Path $container $name
    New-Item -ItemType Directory -Force $stage | Out-Null
    Write-Host "== $rid"
    Publish-App 'src\SloppyAnimatsu.Viewer' $rid $stage
    Publish-App 'src\SloppyAnimatsu.Cli' $rid $stage
    if ($windowsTarget) { & (Join-Path $PSScriptRoot 'windows\lib-layout.ps1') -Folder $stage }
    Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $stage -Recurse
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $stage 'LICENSE.txt')
    if ($windowsTarget) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'windows\README.txt') -Destination (Join-Path $stage 'README.txt')
        $archive = Join-Path $out "$name.zip"
        & $tool 'zip' $container $archive
        if ($LASTEXITCODE -ne 0) { throw "packing $archive failed." }
        $setup = Join-Path $out "$name-setup.exe"
        & (Join-Path $PSScriptRoot 'windows\build-installer.ps1') -Source $stage -Output $setup -Version $version
        $artifacts += $setup
    }
    else {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'linux\README.txt') -Destination (Join-Path $stage 'README.txt')
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'linux\install.sh') -Destination $stage
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'linux\sloppyanimatsu.svg') -Destination $stage
        $archive = Join-Path $out "$name.tar.gz"
        & $tool 'tar' $container $archive '--exec' "$name/SloppyAnimatsu.Viewer" '--exec' "$name/SloppyAnimatsu.Cli"
        if ($LASTEXITCODE -ne 0) { throw "packing $archive failed." }
    }
    $artifacts += $archive
}

$sums = foreach ($artifact in $artifacts) {
    $hash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $artifact)"
}
Set-Content -LiteralPath (Join-Path $out 'SHA256SUMS') -Value $sums -Encoding ascii

if (-not $KeepStage) { Remove-Item $stageRoot -Recurse -Force }
Remove-Item $toolDir -Recurse -Force

Write-Host ''
Write-Host "Packages in $out"
foreach ($artifact in $artifacts) {
    Write-Host ('  {0,-40} {1,8:N1} MB' -f (Split-Path -Leaf $artifact), ((Get-Item -LiteralPath $artifact).Length / 1MB))
}
Write-Host '  SHA256SUMS'






