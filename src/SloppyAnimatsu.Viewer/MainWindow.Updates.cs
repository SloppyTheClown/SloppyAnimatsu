// Updates: the startup check (CGeneralPreferences.AutoCheckUpdates), the status bar's "Update
// Available" notice, Help > Check for updates…, and installing a release from GitHub.
//
// SloppyAnimatsu.Core.Updates.UpdateCheck finds the release and picks the install method; this file
// downloads it, stages it, and hands the swap to a small script that waits for this process to exit
// (the program's own files cannot be replaced while it runs), then restarts the viewer.
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SloppyAnimatsu.Core;
using SloppyAnimatsu.Core.Diagnostics;
using SloppyAnimatsu.Core.Updates;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    /// <summary>The installer's file list; its presence marks a folder the Windows setup installed.</summary>
    private const string SetupListFile = "uninstall.dat";

    /// <summary>How long one GitHub request may take before the check gives up.</summary>
    private static readonly TimeSpan UpdateRequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The status bar's notice, shown once a newer release is known.</summary>
    private readonly Button _updateNotice = new Button
    {
        IsVisible = false,
        FontSize = 12.0,
        Padding = new Thickness(6.0, 1.0),
        Margin = new Thickness(12.0, 0.0, 0.0, 0.0),
        Background = Brushes.Transparent,
        Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0xC4, 0xFF)),
        FontWeight = FontWeight.DemiBold,
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
    };

    private ReleaseInfo? _availableRelease;
    private bool _updateCheckRunning;

    /// <summary>Puts the update notice on the status bar (right side, before the context label).</summary>
    private void AddUpdateNotice(DockPanel statusBar)
    {
        ToolTip.SetTip(_updateNotice, "A newer Sloppy Animatsu is published - click to see it and install it");
        _updateNotice.Click += async delegate
        {
            if (_availableRelease != null) await ShowUpdateDialog(_availableRelease);
        };
        DockPanel.SetDock(_updateNotice, Dock.Right);
        statusBar.Children.Add(_updateNotice);
    }

    /// <summary>
    /// The quiet startup check: no window, no error shown; a newer release only lights the status
    /// bar notice. Skipped for scripted runs and when the preference is off.
    /// </summary>
    private void StartupUpdateCheck()
    {
        if (_options.Scripted || _options.Screenshot != null) return;
        if (_preferences != null && !_preferences.AutoCheckUpdates) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using HttpClient client = UpdateCheck.CreateClient(UpdateRequestTimeout);
                ReleaseInfo release = await UpdateCheck.FetchLatestAsync(client);
                AppLog.Info($"update check: latest release {release.Version}, this build {BuildInfo.Version}");
                if (UpdateCheck.IsNewer(release.Version, BuildInfo.Version))
                    Dispatcher.UIThread.Post(() => ShowUpdateNotice(release));
            }
            catch (Exception ex)
            {
                AppLog.Info("update check failed: " + ex.Message);
            }
        });
    }

    private void ShowUpdateNotice(ReleaseInfo release)
    {
        _availableRelease = release;
        _updateNotice.Content = "Update Available v" + release.Version;
        _updateNotice.IsVisible = true;
    }

    /// <summary>Help > Check for updates…: asks GitHub now and always answers in a window.</summary>
    private async Task CheckForUpdates()
    {
        if (_updateCheckRunning) return;
        _updateCheckRunning = true;
        _status.Text = "Checking for updates…";
        ReleaseInfo release;
        try
        {
            using HttpClient client = UpdateCheck.CreateClient(UpdateRequestTimeout);
            release = await UpdateCheck.FetchLatestAsync(client);
        }
        catch (Exception ex)
        {
            AppLog.Warn("update check failed: " + ex.Message);
            _status.Text = "Could not check for updates: " + ex.Message;
            await ShowUpdateMessage("Check for updates",
                "Could not reach GitHub to check for updates.\n\n" + ex.Message + "\n\nReleases are listed at " + UpdateCheck.ReleasesPage);
            return;
        }
        finally
        {
            _updateCheckRunning = false;
        }
        if (UpdateCheck.IsNewer(release.Version, BuildInfo.Version))
        {
            ShowUpdateNotice(release);
            _status.Text = $"Version {release.Version} is available.";
            await ShowUpdateDialog(release);
        }
        else
        {
            _status.Text = "Sloppy Animatsu is up to date.";
            await ShowUpdateMessage("Check for updates",
                $"You have the latest version ({BuildInfo.Version}).");
        }
    }

    /// <summary>The "new version" window: its notes, and Install / Release page / Later.</summary>
    private async Task ShowUpdateDialog(ReleaseInfo release)
    {
        string notes = string.IsNullOrWhiteSpace(release.Notes) ? "(This release has no notes.)" : release.Notes.Trim();
        var install = new Button { Content = "Download and install", IsDefault = true };
        var page = new Button { Content = "Open release page" };
        var later = new Button { Content = "Later", IsCancel = true };
        var window = new Window
        {
            Title = "Update available",
            Width = 600.0,
            Height = 460.0,
            MinWidth = 420.0,
            MinHeight = 260.0,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var body = new DockPanel { Margin = new Thickness(18.0) };
        var header = new TextBlock
        {
            Text = $"Sloppy Animatsu {release.Version} is available. You have {BuildInfo.Version}.",
            FontWeight = FontWeight.DemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8.0,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0.0, 12.0, 0.0, 0.0),
            Children = { install, page, later },
        };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(header);
        body.Children.Add(buttons);
        body.Children.Add(new Border
        {
            BorderThickness = new Thickness(1.0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            Padding = new Thickness(8.0),
            Child = new ScrollViewer
            {
                Content = new SelectableTextBlock { Text = notes, TextWrapping = TextWrapping.Wrap, FontSize = 12.0 },
            },
        });
        window.Content = body;
        int choice = 0;
        install.Click += delegate { choice = 1; window.Close(); };
        page.Click += delegate { choice = 2; window.Close(); };
        later.Click += delegate { window.Close(); };
        await window.ShowDialog(this);
        if (choice == 1) await InstallUpdate(release);
        else if (choice == 2) OpenInBrowser(release.PageUrl);
    }

    /// <summary>
    /// Downloads the release, stages it, starts the script that swaps it in once this process has
    /// exited, then closes the window (through the unsaved-changes prompts).
    /// </summary>
    private async Task InstallUpdate(ReleaseInfo release)
    {
        string appFolder = AppFolder.Install.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? processPath = Environment.ProcessPath;
        string exeName = processPath != null ? Path.GetFileName(processPath) : (OperatingSystem.IsWindows() ? "SloppyAnimatsu.Viewer.exe" : "SloppyAnimatsu.Viewer");
        bool installedBySetup = File.Exists(Path.Combine(appFolder, SetupListFile));
        bool buildTree = UpdateCheck.IsDevelopmentFolder(appFolder);
        (UpdateMethod method, ReleaseAsset? asset) = UpdateCheck.ChooseMethod(release, UpdateCheck.CurrentRuntime,
            installedBySetup, IsWritable(appFolder), buildTree);
        if (method == UpdateMethod.ReleasePage || asset == null)
        {
            string why = buildTree
                ? "This is a development build, so it is not replaced automatically."
                : $"No package of this release can be installed here automatically ({UpdateCheck.CurrentRuntime}).";
            await ShowUpdateMessage("Install update", why + "\n\nThe release page will open so you can download it.");
            OpenInBrowser(release.PageUrl);
            return;
        }

        string stage = Path.Combine(Path.GetTempPath(), "SloppyAnimatsu-update", release.Version);
        string download = Path.Combine(stage, asset.Name);
        string unpacked = Path.Combine(stage, "unpacked");
        try
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            Directory.CreateDirectory(stage);
            await ProgressDialog.Run(this, "Downloading update", $"Downloading {asset.Name}…", progress =>
            {
                using HttpClient client = UpdateCheck.CreateClient(TimeSpan.FromMinutes(30));
                UpdateCheck.DownloadAsync(client, release, asset, download, progress).GetAwaiter().GetResult();
                if (method == UpdateMethod.Archive) Unpack(download, unpacked);
            });
            ProcessStartInfo helper = method == UpdateMethod.Setup
                ? SetupHelper(stage, download, appFolder)
                : ArchiveHelper(stage, PackageRoot(unpacked, exeName), appFolder, exeName);
            Process.Start(helper);
        }
        catch (Exception ex)
        {
            AppLog.Warn("update install failed: " + ex);
            _status.Text = "The update failed: " + ex.Message;
            await ShowUpdateMessage("Install update", "The update could not be prepared.\n\n" + ex.Message);
            return;
        }
        AppLog.Info($"update {release.Version} staged in {stage} ({method}); closing to install");
        _status.Text = $"Version {release.Version} installs when Sloppy Animatsu closes.";
        Close();
    }

    private static void Unpack(string archive, string target)
    {
        Directory.CreateDirectory(target);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, target, overwriteFiles: true);
            return;
        }
        using FileStream file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, target, overwriteFiles: true);
    }

    /// <summary>The unpacked folder that holds the viewer (the archive's single top folder).</summary>
    private static string PackageRoot(string unpacked, string exeName)
    {
        if (File.Exists(Path.Combine(unpacked, exeName))) return unpacked;
        foreach (string dir in Directory.GetDirectories(unpacked))
            if (File.Exists(Path.Combine(dir, exeName))) return dir;
        throw new InvalidDataException($"The downloaded package has no {exeName}.");
    }

    /// <summary>Runs the new Windows setup over this install once the viewer has exited (setup asks for administrator rights).</summary>
    private static ProcessStartInfo SetupHelper(string stage, string setup, string appFolder)
    {
        string script = Path.Combine(stage, "run-setup.ps1");
        File.WriteAllText(script, """
            param([int]$ProcessId, [string]$Setup, [string]$Target)
            Wait-Process -Id $ProcessId -ErrorAction SilentlyContinue
            try {
                Start-Process -FilePath $Setup -ArgumentList ('--dir="' + $Target + '"')
            } catch {
                Add-Type -AssemblyName System.Windows.Forms
                [System.Windows.Forms.MessageBox]::Show("The Sloppy Animatsu setup could not be started:`n`n$($_.Exception.Message)`n`nIt was downloaded to $Setup", 'Sloppy Animatsu update') | Out-Null
            }
            """, new UTF8Encoding(true));
        return PowerShell(script, "-ProcessId", Environment.ProcessId.ToString(), "-Setup", setup, "-Target", appFolder);
    }

    /// <summary>Copies the unpacked package over the program folder once the viewer has exited, then restarts it.</summary>
    private static ProcessStartInfo ArchiveHelper(string stage, string package, string appFolder, string exeName)
    {
        if (OperatingSystem.IsWindows())
        {
            string script = Path.Combine(stage, "apply-update.ps1");
            File.WriteAllText(script, """
                param([int]$ProcessId, [string]$Source, [string]$Target, [string]$Exe)
                Wait-Process -Id $ProcessId -ErrorAction SilentlyContinue
                $log = Join-Path (Split-Path $Source -Parent) 'apply-update.log'
                # Copies over the folder, keeping files the package does not have (a portable profile,
                # logs). A file can stay locked for a moment after the process ends (antivirus, the
                # shell), hence the retries. robocopy exit codes below 8 mean success.
                & robocopy.exe $Source $Target /E /R:10 /W:1 /NP /NJH /NJS "/LOG:$log" | Out-Null
                if ($LASTEXITCODE -ge 8) {
                    Add-Type -AssemblyName System.Windows.Forms
                    [System.Windows.Forms.MessageBox]::Show("The update could not be copied into $Target.`n`nDetails: $log`nThe new version is unpacked in $Source.", 'Sloppy Animatsu update') | Out-Null
                }
                Start-Process -FilePath (Join-Path $Target $Exe) -WorkingDirectory $Target
                """, new UTF8Encoding(true));
            return PowerShell(script, "-ProcessId", Environment.ProcessId.ToString(), "-Source", package, "-Target", appFolder, "-Exe", exeName);
        }
        string sh = Path.Combine(stage, "apply-update.sh");
        File.WriteAllText(sh, """
            #!/bin/sh
            # $1 pid, $2 unpacked package, $3 program folder, $4 viewer file name
            while kill -0 "$1" 2>/dev/null; do sleep 0.5; done
            if cp -a "$2/." "$3/" 2>"$2/../apply-update.log"; then
                nohup "$3/$4" >/dev/null 2>&1 &
            fi
            """.Replace("\r\n", "\n"), new UTF8Encoding(false));
        var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (string arg in new[] { sh, Environment.ProcessId.ToString(), package, appFolder, exeName })
            info.ArgumentList.Add(arg);
        return info;
    }

    private static ProcessStartInfo PowerShell(string script, params string[] args)
    {
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script })
            info.ArgumentList.Add(arg);
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    private static bool IsWritable(string folder)
    {
        try
        {
            string probe = Path.Combine(folder, ".update-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not open {url}: {ex.Message}";
        }
    }

    private async Task ShowUpdateMessage(string title, string message)
    {
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        var window = new Window
        {
            Title = title,
            Width = 460.0,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 12.0,
                Margin = new Thickness(18.0),
                Children = { new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, ok },
            },
        };
        ok.Click += delegate { window.Close(); };
        await window.ShowDialog(this);
    }
}
