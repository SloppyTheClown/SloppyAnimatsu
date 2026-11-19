<#
.SYNOPSIS
Builds SloppyAnimatsu's Windows installer (a single setup .exe) from a lib-layout release folder.

.DESCRIPTION
Zips the folder (without a portable profile or logs) and compiles packaging\windows\installer\Setup.cs
twice with the .NET Framework C# compiler that ships with Windows: once as the uninstaller, then as
the setup with the zip and the uninstaller embedded. No installer toolkit or NuGet package is needed.
See Setup.cs for what the installer does and its command line.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File packaging\windows\build-installer.ps1 -Source release -Output dist\SloppyAnimatsu-setup.exe
#>
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Output,
    [string]$Version = '',
    # A test build: no administrator rights, registry in HKCU (shortcuts still go to the all-users
    # folders, so test it with --no-shortcuts). Never ship it.
    [switch]$PerUserTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Source = (Resolve-Path -LiteralPath $Source).Path
$Output = [IO.Path]::GetFullPath($Output)
if (-not (Test-Path -LiteralPath (Join-Path $Source 'lib'))) { throw "$Source has no lib folder; run packaging\windows\lib-layout.ps1 on it first." }
if (-not $Version) {
    $props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
    $match = [regex]::Match($props, '<Version>([^<]+)</Version>')
    $Version = if ($match.Success) { $match.Groups[1].Value.Trim() } else { '0.0.0' }
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw "The .NET Framework 4 compiler was not found at $csc." }

$work = Join-Path ([IO.Path]::GetDirectoryName($Output)) ('.installer-build-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $info = Join-Path $work 'BuildInfo.cs'
    [IO.File]::WriteAllText($info, "namespace SloppyAnimatsuSetup { internal static class BuildInfo { internal const string Version = `"$Version`"; } }")

    # The payload: everything in the release folder except per-machine state.
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $payload = Join-Path $work 'payload.zip'
    $skip = @('profile', 'logs', 'Uninstall.exe', 'uninstall.dat')
    $zip = [IO.Compression.ZipFile]::Open($payload, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $prefix = $Source.TrimEnd('\').Length + 1
        foreach ($top in Get-ChildItem -LiteralPath $Source) {
            if ($skip -contains $top.Name) { continue }
            $files = if ($top.PSIsContainer) { Get-ChildItem -LiteralPath $top.FullName -Recurse -File } else { @($top) }
            foreach ($file in $files) {
                $name = $file.FullName.Substring($prefix).Replace('\', '/')
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        }
    } finally { $zip.Dispose() }

    $installerDir = Join-Path $PSScriptRoot 'installer'
    $manifest = Join-Path $installerDir 'app.manifest'
    $defines = @()
    if ($PerUserTest) {
        $testManifest = Join-Path $work 'app.manifest'
        (Get-Content -LiteralPath $manifest -Raw).Replace('"requireAdministrator"', '"asInvoker"') | Set-Content -LiteralPath $testManifest -Encoding UTF8
        $manifest = $testManifest
        $defines = @('/define:PER_USER_TEST')
    }
    $common = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', "/win32manifest:$manifest",
        "/win32icon:$(Join-Path $root 'src\SloppyAnimatsu.Viewer\Assets\app.ico')",
        '/r:System.dll', '/r:System.Core.dll', '/r:Microsoft.CSharp.dll', '/r:System.Drawing.dll',
        '/r:System.Windows.Forms.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll') + $defines
    $sources = @((Join-Path $installerDir 'Setup.cs'), $info)

    $uninstaller = Join-Path $work 'uninstall.exe'
    & $csc @common "/out:$uninstaller" @sources
    if ($LASTEXITCODE -ne 0) { throw "Compiling the uninstaller failed." }
    & $csc @common "/out:$Output" "/resource:$payload,payload.zip" "/resource:$uninstaller,uninstall.exe" @sources
    if ($LASTEXITCODE -ne 0) { throw "Compiling the installer failed." }
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host ("Installer: {0} ({1:N1} MB)" -f $Output, ((Get-Item -LiteralPath $Output).Length / 1MB))
