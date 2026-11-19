<#
.SYNOPSIS
Starts a program or batch file on the third monitor (Windows display 3, the leftmost).

.DESCRIPTION
Anything launched for testing, output or reporting (the viewer, a console watcher, a
screenshot run) belongs on the third monitor, never on the
main screen. The monitor is resolved at launch time as the **leftmost** display, then by
the name \\.\DISPLAY3, then the primary, because the desktop an agent session sees can be
narrower than the real arrangement (e.g. a remote session presenting one virtual display)
and because the \\.\DISPLAYn names are not the numbers Display Settings shows.

The new top-level window is found by diffing the window list before and after the launch,
then moved into the monitor's work area. The largest new window wins, so a splash screen
or a tooltip does not capture the placement.

.PARAMETER FilePath
Program, .bat or .cmd to start. Batch files are shell-executed, so a console window opens.

.PARAMETER ArgumentList
Arguments passed to the target (strings).

.PARAMETER WorkingDirectory
Starting directory for the process.

.PARAMETER Title
Only consider new windows whose title contains this text.

.PARAMETER TimeoutSeconds
How long to wait for the window to appear (default 20).

.PARAMETER Wait
Wait for the process to exit after placing the window (default: return at once).

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File SloppyAnimatsu\tools\launch-on-monitor3.ps1 `
    -FilePath SloppyAnimatsu\watch.bat -WorkingDirectory SloppyAnimatsu

.EXAMPLE
...launch-on-monitor3.ps1 -FilePath src\SloppyAnimatsu.Viewer\bin\Debug\net9.0\SloppyAnimatsu.Viewer.exe `
    -ArgumentList @('C:\scenes\19_run.akt')
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)][string]$FilePath,
    [Parameter(Position = 1)][string[]]$ArgumentList = @(),
    [string]$WorkingDirectory,
    [string]$Title,
    [int]$TimeoutSeconds = 20,
    [switch]$Wait
)

$ErrorActionPreference = 'Stop'

if (-not ('MonitorPlacement' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class MonitorPlacement
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr data);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX mi);
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr data);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // One line per monitor: device;monitor rect;work rect;primary
    public static string[] Monitors()
    {
        var list = new List<string>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT clip, IntPtr data) =>
        {
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            if (GetMonitorInfo(h, ref mi))
            {
                list.Add(string.Format("{0};{1},{2},{3},{4};{5},{6},{7},{8};{9}",
                    mi.szDevice,
                    mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                    mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top,
                    (mi.dwFlags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }

    // Top-level windows a user could see: visible, titled, at least 100x50.
    public static long[] Windows()
    {
        var list = new List<long>();
        EnumWindows((IntPtr h, IntPtr data) =>
        {
            if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0) return true;
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            if (r.Right - r.Left < 100 || r.Bottom - r.Top < 50) return true;
            list.Add(h.ToInt64());
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }

    public static string WindowTitle(IntPtr h)
    {
        var text = new StringBuilder(512);
        GetWindowText(h, text, text.Capacity);
        return text.ToString();
    }

    public static long Area(IntPtr h)
    {
        RECT r;
        if (!GetWindowRect(h, out r)) return 0;
        return (long)(r.Right - r.Left) * (r.Bottom - r.Top);
    }

    public static bool Place(IntPtr h, int x, int y, int cx, int cy)
    {
        ShowWindow(h, 9); // SW_RESTORE
        // SWP_NOZORDER (0x4) keeps the stacking alone; SWP_SHOWWINDOW (0x40) makes it visible.
        bool ok = SetWindowPos(h, IntPtr.Zero, x, y, cx, cy, 0x4 | 0x40);
        SetForegroundWindow(h);
        return ok;
    }
}
"@
}

function Resolve-TargetMonitor {
    $monitors = [MonitorPlacement]::Monitors() | ForEach-Object {
        $device, $bounds, $work, $primary = $_ -split ';'
        $b = $bounds -split ','
        $w = $work -split ','
        [pscustomobject]@{
            Device = $device
            X = [int]$b[0]; Y = [int]$b[1]; Width = [int]$b[2]; Height = [int]$b[3]
            WorkX = [int]$w[0]; WorkY = [int]$w[1]; WorkWidth = [int]$w[2]; WorkHeight = [int]$w[3]
            Primary = ($primary -eq 'True')
        }
    }
    # The user's "third monitor" is the leftmost display. Resolve it geometrically first: the
    # \\.\DISPLAYn device names are NOT the numbers Display Settings shows (on this machine
    # \\.\DISPLAY3 is the middle 2560x1440 primary while the leftmost is \\.\DISPLAY2), so match
    # "leftmost" and use the name only as a tie-break between displays sharing the same left edge.
    $leftmost = $monitors | Sort-Object X, Y | Select-Object -First 1
    if ($leftmost) { return $leftmost }
    $byName = $monitors | Where-Object { $_.Device -eq '\\.\DISPLAY3' } | Select-Object -First 1
    if ($byName) { return $byName }
    return $monitors | Where-Object Primary | Select-Object -First 1
}

$monitor = Resolve-TargetMonitor
if (-not $monitor) { throw 'No display monitor found.' }

$before = New-Object 'System.Collections.Generic.HashSet[long]'
[MonitorPlacement]::Windows() | ForEach-Object { [void]$before.Add($_) }

function Quote-Argument([string]$value) {
    if ($value -match '[\s"]') { return '"' + $value.Replace('"', '\"') + '"' }
    return $value
}

$extension = [System.IO.Path]::GetExtension($FilePath)
if ($extension -match '^\.(bat|cmd)$') {
    # A .bat on a network drive opened through ShellExecute (Start-Process on the file itself)
    # raises "Open File - Security Warning" and blocks the launch forever. cmd runs the file
    # without that check, and `start` gives it a console window of its own.
    $batch = if ([System.IO.Path]::IsPathRooted($FilePath)) { $FilePath }
             else { Join-Path (Get-Location).Path $FilePath }
    $inner = ((@($batch) + $ArgumentList) | ForEach-Object { Quote-Argument $_ }) -join ' '
    $startArgs = @{
        FilePath = 'cmd.exe'
        ArgumentList = @('/c', 'start', '""', '/normal', 'cmd', '/c', $inner)
        PassThru = $true
    }
}
else {
    $startArgs = @{ FilePath = $FilePath; PassThru = $true }
    if ($ArgumentList.Count -gt 0) { $startArgs.ArgumentList = $ArgumentList }
}
if ($WorkingDirectory) { $startArgs.WorkingDirectory = $WorkingDirectory }
$process = Start-Process @startArgs

$window = [IntPtr]::Zero
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
    $candidates = [MonitorPlacement]::Windows() | Where-Object { -not $before.Contains($_) }
    if ($Title) {
        $candidates = $candidates | Where-Object { [MonitorPlacement]::WindowTitle([IntPtr]$_) -like "*$Title*" }
    }
    if ($candidates) {
        $window = [IntPtr]($candidates | Sort-Object { [MonitorPlacement]::Area([IntPtr]$_) } -Descending | Select-Object -First 1)
        break
    }
}

if ($window -ne [IntPtr]::Zero) {
    [void][MonitorPlacement]::Place($window, $monitor.WorkX, $monitor.WorkY, $monitor.WorkWidth, $monitor.WorkHeight)
    $asText = [MonitorPlacement]::WindowTitle($window)
    "moved '{0}' (pid {1}) to {2} at {3},{4} {5}x{6}" -f $asText, $process.Id, $monitor.Device,
        $monitor.WorkX, $monitor.WorkY, $monitor.WorkWidth, $monitor.WorkHeight
} else {
    "started pid {0} but no new window appeared within {1}s; place it by hand" -f $process.Id, $TimeoutSeconds
}

if ($Wait) { $process.WaitForExit() }
