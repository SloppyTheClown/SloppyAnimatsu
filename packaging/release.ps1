<#
.SYNOPSIS
Refreshes the local release folder (SloppyAnimatsu\release) and the desktop shortcut to it.

.DESCRIPTION
Publishes SloppyAnimatsu.Viewer and SloppyAnimatsu.Cli for win-x64 (self-contained, Release) into a staging
folder, then swaps it in as SloppyAnimatsu\release, so a failed build never leaves a half-written
release. Afterwards the desktop shortcut "Sloppy Animatsu.lnk" is (re)written to point at the viewer
exe in that folder; run this again whenever the release path or the exe name changes and the
shortcut follows. The version comes from Directory.Build.props and is written to
release\VERSION.txt with the build time.

The swap fails if the released viewer is running (its files are locked); close it and rerun.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File packaging\release.ps1
#>
param(
    [string]$ReleaseDir = '',
    [string]$ShortcutName = 'Sloppy Animatsu'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $ReleaseDir) { $ReleaseDir = Join-Path $root 'release' }
$release = [IO.Path]::GetFullPath($ReleaseDir)
$staging = "$release.new"
$exeName = 'SloppyAnimatsu.Viewer.exe'

$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$match = [regex]::Match($props, '<Version>([^<]+)</Version>')
$version = if ($match.Success) { $match.Groups[1].Value.Trim() } else { '0.0.0' }

function Invoke-Dotnet([string[]]$Arguments, [string]$What) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$What failed (dotnet exited $LASTEXITCODE)." }
}

if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null

Write-Host "SloppyAnimatsu ${version}: publishing the release into $staging"
# The .pdb files stay beside the exes: a crash log's stack trace carries file names and line
# numbers only when the matching symbols are next to the assembly (SloppyAnimatsu.Core.Diagnostics.AppLog).
foreach ($project in 'src\SloppyAnimatsu.Viewer', 'src\SloppyAnimatsu.Cli') {
    Invoke-Dotnet @(
        'publish', (Join-Path $root $project), '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', "-p:Version=$version", '-p:DebugType=portable',
        '-p:DebugSymbols=true', '-p:GenerateDocumentationFile=false',
        '--nologo', '-v:minimal', '-o', $staging
    ) "publishing $project"
}
# Exes on top, the runtime and every assembly in lib\ (the apphosts are pointed at lib\).
& (Join-Path $PSScriptRoot 'windows\lib-layout.ps1') -Folder $staging
Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $staging -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'windows\README.txt') -Destination (Join-Path $staging 'README.txt')
Set-Content -LiteralPath (Join-Path $staging 'VERSION.txt') -Encoding ascii -Value @(
    "SloppyAnimatsu $version (win-x64)",
    "Built $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
)

$exe = Join-Path $staging $exeName
if (-not (Test-Path -LiteralPath $exe)) { throw "The publish did not produce $exeName." }

# Swap: old release aside, staging in, old release removed.
$old = "$release.old"
if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force }
if (Test-Path -LiteralPath $release) {
    try { Rename-Item -LiteralPath $release -NewName (Split-Path -Leaf $old) }
    catch { throw "Could not replace $release (is the released viewer running? close it and rerun): $_" }
}
# The viewer's portable profile (recent scenes, layout, settings) lives in release\profile when the
# user's AppData profile cannot be written; carry it over or every release forgets the recents.
$oldProfile = Join-Path $old 'profile'
if (Test-Path -LiteralPath $oldProfile) {
    Copy-Item -LiteralPath $oldProfile -Destination (Join-Path $staging 'profile') -Recurse -Force
}
Rename-Item -LiteralPath $staging -NewName (Split-Path -Leaf $release)
if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force }

# The workspace folder carries a Low mandatory integrity label (set by an agent sandbox), and
# Windows starts an exe whose file is labelled Low at Low integrity: the released viewer could then
# write only inside the workspace ("Access to the path ... is denied" for any other Z: folder,
# Documents or AppData). An explicit, inheritable Medium label on the release folder makes the exes
# start at the user's normal (Medium) integrity. Without it the release CLI's `new` to F:
# fails with access denied.
& icacls $release /setintegritylevel '(OI)(CI)M' | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warning "Could not set a Medium integrity label on $release; the viewer may be unable to save outside the workspace." }

# Desktop shortcut, always rewritten so it follows the current path and exe name.
$target = Join-Path $release $exeName
$desktop = [Environment]::GetFolderPath('Desktop')
$lnk = Join-Path $desktop "$ShortcutName.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($lnk)
$shortcut.TargetPath = $target
$shortcut.WorkingDirectory = $release
$shortcut.IconLocation = "$target,0"
$shortcut.Description = "Sloppy Animatsu $version"
$shortcut.Save()

# Explorer caches icons by path; ask it to refresh so the shortcut and taskbar show the current app.ico.
$ie4 = Join-Path $env:SystemRoot 'System32\ie4uinit.exe'
if (Test-Path -LiteralPath $ie4) { & $ie4 -show 2>$null }

Write-Host ''
Write-Host "Release:  $release"
Write-Host "Shortcut: $lnk -> $target"
