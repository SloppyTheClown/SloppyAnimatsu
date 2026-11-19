<#
.SYNOPSIS
Moves a published win-x64 SloppyAnimatsu folder into the tidy "exes on top, everything else in lib\"
layout.

.DESCRIPTION
A self-contained .NET publish puts ~230 files (the runtime, Avalonia, SkiaSharp, the app's own
assemblies) beside the two exes. This script moves every published file except the two apphosts
into <Folder>\lib and rewrites the app path embedded in each apphost from "<App>.dll" to
"lib/<App>.dll". The .NET apphost resolves that path relative to itself and treats lib\ as the
application folder, so the runtime, the deps.json/runtimeconfig.json and every assembly are found
there with no code change. The program locates its install folder (portable profile, logs) through
SloppyAnimatsu.Core.Diagnostics.AppFolder, which steps out of lib\.

Folders already present (docs, profile) and the text files the packaging scripts add afterwards
are left alone.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File packaging\windows\lib-layout.ps1 -Folder release.new
#>
param(
    [Parameter(Mandatory = $true)][string]$Folder,
    [string[]]$Apps = @('SloppyAnimatsu.Viewer', 'SloppyAnimatsu.Cli')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Folder = (Resolve-Path -LiteralPath $Folder).Path
$lib = Join-Path $Folder 'lib'
New-Item -ItemType Directory -Force $lib | Out-Null

$keep = @{}
foreach ($app in $Apps) { $keep["$app.exe"] = $true }

# Every published file except the apphosts goes to lib\ (the published subfolders, e.g. satellite
# resource folders, too); the packaging scripts' own folders stay where they are.
$ownFolders = @('lib', 'docs', 'profile', 'logs')
foreach ($item in Get-ChildItem -LiteralPath $Folder) {
    if ($item.PSIsContainer) {
        if ($ownFolders -contains $item.Name) { continue }
    } elseif ($keep.ContainsKey($item.Name)) {
        continue
    }
    Move-Item -LiteralPath $item.FullName -Destination (Join-Path $lib $item.Name) -Force
}

# The apphost template carries a 1024-byte UTF-8 slot holding the app's dll name, NUL-padded.
function Set-AppHostPath([string]$Exe, [string]$OldName, [string]$NewName) {
    $bytes = [IO.File]::ReadAllBytes($Exe)
    $old = [Text.Encoding]::UTF8.GetBytes($OldName)
    $new = [Text.Encoding]::UTF8.GetBytes($NewName)
    $hits = New-Object System.Collections.Generic.List[int]
    for ($i = 0; $i -le $bytes.Length - $old.Length - 1; $i++) {
        if ($bytes[$i] -ne $old[0]) { continue }
        $match = $true
        for ($j = 1; $j -lt $old.Length; $j++) {
            if ($bytes[$i + $j] -ne $old[$j]) { $match = $false; break }
        }
        # The slot holds the name alone, NUL-terminated.
        if ($match -and $bytes[$i + $old.Length] -eq 0) { $hits.Add($i) }
    }
    if ($hits.Count -ne 1) { throw "Expected one embedded app path '$OldName' in $Exe, found $($hits.Count)." }
    $at = $hits[0]
    for ($k = $old.Length; $k -lt $new.Length + 1; $k++) {
        if ($bytes[$at + $k] -ne 0) { throw "No room to lengthen the embedded app path in $Exe." }
    }
    [Array]::Copy($new, 0, $bytes, $at, $new.Length)
    [IO.File]::WriteAllBytes($Exe, $bytes)
}

foreach ($app in $Apps) {
    $exe = Join-Path $Folder "$app.exe"
    if (-not (Test-Path -LiteralPath $exe)) { throw "$exe is missing." }
    if (-not (Test-Path -LiteralPath (Join-Path $lib "$app.dll"))) { throw "lib\$app.dll is missing." }
    Set-AppHostPath $exe "$app.dll" "lib/$app.dll"
}
Write-Host "lib layout: $((Get-ChildItem -LiteralPath $lib -File).Count) files in $lib"
