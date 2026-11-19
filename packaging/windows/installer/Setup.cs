// SloppyAnimatsu's Windows installer and uninstaller (one source, two builds).
//
// Built by packaging\windows\build-installer.ps1 with the C# compiler of the .NET Framework that
// ships with Windows (C# 5, no NuGet, no installer toolkit), so the result runs on any Windows 10/11
// without installing anything. The setup build embeds the program as "payload.zip" and the
// uninstaller build as "uninstall.exe"; a build without the payload is the uninstaller.
//
// Machine-wide install (the manifest asks for administrator rights): the default folder is
// %ProgramFiles%\SloppyAnimatsu, any other folder can be chosen, and the chosen folder is remembered
// in HKLM\Software\SloppyAnimatsu (InstallPath) so a reinstall or an update offers it again - also
// after an uninstall. Shortcuts go to the all-users Start menu and desktop.
//
// Command line (both builds): /S or --quiet runs without a window; setup also takes
// --dir=<folder> (or /D=<folder>) and --no-shortcuts.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SloppyAnimatsuSetup
{
    internal static class Setup
    {
        internal const string ProductName = "Sloppy Animatsu";
        internal const string SettingsKey = @"Software\SloppyAnimatsu";
        internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SloppyAnimatsu";
        internal const string ViewerExe = "SloppyAnimatsu.Viewer.exe";
        internal const string UninstallerName = "Uninstall.exe";
        internal const string ListName = "uninstall.dat";

        internal static bool Quiet;

        [STAThread]
        private static int Main(string[] args)
        {
            string dir = null;
            bool shortcuts = true;
            foreach (string arg in args)
            {
                string a = arg.Replace("\"", "").Trim();
                if (a.Equals("/S", StringComparison.OrdinalIgnoreCase) || a.Equals("--quiet", StringComparison.OrdinalIgnoreCase)) Quiet = true;
                else if (a.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase)) dir = a.Substring(6).Trim('"');
                else if (a.StartsWith("/D=", StringComparison.OrdinalIgnoreCase)) dir = a.Substring(3).Trim('"');
                else if (a.Equals("--no-shortcuts", StringComparison.OrdinalIgnoreCase)) shortcuts = false;
            }

            bool isSetup = Assembly.GetExecutingAssembly().GetManifestResourceInfo("payload.zip") != null;
            try
            {
                if (isSetup)
                {
                    string remembered = RememberedPath();
                    string target = dir ?? remembered ?? DefaultPath();
                    if (Quiet)
                    {
                        Installer.Install(Path.GetFullPath(target), shortcuts, shortcuts && DesktopWanted(), null);
                        return 0;
                    }
                    Application.EnableVisualStyles();
                    Application.Run(new SetupForm(target, remembered != null));
                    return 0;
                }

                string installDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                if (!Quiet && MessageBox.Show("Remove " + ProductName + " from\n" + installDir + "?\n\nYour preferences, key map and autosaves are kept.",
                        ProductName + " Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return 1;
                }
                Installer.Uninstall(installDir);
                if (!Quiet) MessageBox.Show(ProductName + " was removed.", ProductName + " Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception ex)
            {
                if (!Quiet) MessageBox.Show(ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                else Console.Error.WriteLine(ex);
                return 2;
            }
        }

        internal static string DefaultPath()
        {
            // ProgramW6432 is the 64-bit Program Files folder even if this process runs as 32-bit.
            string programFiles = Environment.GetEnvironmentVariable("ProgramW6432");
            if (string.IsNullOrEmpty(programFiles)) programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return Path.Combine(programFiles, "SloppyAnimatsu");
        }

        // The machine hive, always its 64-bit view. A test build (/define:PER_USER_TEST) uses the
        // current user's hive, so the install logic can be exercised without administrator rights.
        internal static RegistryKey Hive()
        {
#if PER_USER_TEST
            return RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
#else
            return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
#endif
        }

        internal static string RememberedPath()
        {
            using (RegistryKey hive = Hive())
            using (RegistryKey key = hive.OpenSubKey(SettingsKey))
            {
                string value = key == null ? null : key.GetValue("InstallPath") as string;
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }

        internal static bool DesktopWanted()
        {
            using (RegistryKey hive = Hive())
            using (RegistryKey key = hive.OpenSubKey(SettingsKey))
            {
                object value = key == null ? null : key.GetValue("DesktopShortcut");
                return value == null || Convert.ToInt32(value) != 0;
            }
        }
    }

    internal static class Installer
    {
        private static readonly string[] Programs = { "SloppyAnimatsu.Viewer", "SloppyAnimatsu.Cli" };

        internal static void Install(string target, bool startMenu, bool desktop, Action<int, string> progress)
        {
            if (progress == null) progress = delegate { };
            EnsureNotRunning(target);

            // A previous install somewhere else is moved, not duplicated: its program files go.
            string previous = Setup.RememberedPath();
            if (previous != null && !SamePath(previous, target) && File.Exists(Path.Combine(previous, Setup.ListName)))
            {
                EnsureNotRunning(previous);
                progress(0, "Removing the previous install in " + previous);
                RemoveProgramFiles(previous);
                TryDeleteEmpty(previous);
            }

            Directory.CreateDirectory(target);
            progress(0, "Removing the old version");
            RemoveProgramFiles(target);

            var installed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            long size = 0;
            Assembly self = Assembly.GetExecutingAssembly();
            using (Stream payload = self.GetManifestResourceStream("payload.zip"))
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                int total = zip.Entries.Count, done = 0;
                string root = Path.GetFullPath(target).TrimEnd('\\') + "\\";
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('/', '\\');
                    string path = Path.GetFullPath(Path.Combine(root, name));
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Bad entry in the payload: " + name);
                    int slash = name.IndexOf('\\');
                    installed.Add(slash < 0 ? name : name.Substring(0, slash));
                    if (name.EndsWith("\\"))
                    {
                        Directory.CreateDirectory(path);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        entry.ExtractToFile(path, true);
                        size += entry.Length;
                    }
                    done++;
                    if (done % 8 == 0 || done == total) progress(done * 100 / total, "Copying " + name);
                }
            }

            using (Stream uninstaller = self.GetManifestResourceStream("uninstall.exe"))
            using (FileStream file = File.Create(Path.Combine(target, Setup.UninstallerName)))
            {
                uninstaller.CopyTo(file);
            }
            installed.Add(Setup.UninstallerName);
            installed.Add(Setup.ListName);
            File.WriteAllLines(Path.Combine(target, Setup.ListName), installed);

            progress(100, "Registering");
            string viewer = Path.Combine(target, Setup.ViewerExe);
            using (RegistryKey hive = Setup.Hive())
            using (RegistryKey key = hive.CreateSubKey(Setup.SettingsKey))
            {
                key.SetValue("InstallPath", target);
                key.SetValue("Version", BuildInfo.Version);
                key.SetValue("DesktopShortcut", desktop ? 1 : 0, RegistryValueKind.DWord);
            }
            using (RegistryKey hive = Setup.Hive())
            using (RegistryKey key = hive.CreateSubKey(Setup.UninstallKey))
            {
                key.SetValue("DisplayName", Setup.ProductName);
                key.SetValue("DisplayVersion", BuildInfo.Version);
                key.SetValue("Publisher", "SloppyAnimatsu");
                key.SetValue("InstallLocation", target);
                key.SetValue("DisplayIcon", viewer + ",0");
                key.SetValue("UninstallString", "\"" + Path.Combine(target, Setup.UninstallerName) + "\"");
                key.SetValue("QuietUninstallString", "\"" + Path.Combine(target, Setup.UninstallerName) + "\" /S");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
            }

            // An unticked shortcut is removed only when it is ours (points into this install).
            if (startMenu) WriteShortcut(StartMenuShortcut(), viewer, target);
            else if (ShortcutPointsInto(StartMenuShortcut(), target)) TryDelete(StartMenuShortcut());
            if (desktop) WriteShortcut(DesktopShortcut(), viewer, target);
            else if (ShortcutPointsInto(DesktopShortcut(), target)) TryDelete(DesktopShortcut());
            progress(100, "Done");
        }

        internal static void Uninstall(string installDir)
        {
            EnsureNotRunning(installDir);
            RemoveProgramFiles(installDir);
            foreach (string lnk in new[] { StartMenuShortcut(), DesktopShortcut() })
            {
                if (ShortcutPointsInto(lnk, installDir)) TryDelete(lnk);
            }
            using (RegistryKey hive = Setup.Hive()) hive.DeleteSubKeyTree(Setup.UninstallKey, false);
            // InstallPath stays, so installing again offers the same folder.

            // The running uninstaller cannot delete itself; a hidden shell does it once we exit.
            string script = "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + Path.Combine(installDir, Setup.UninstallerName)
                + "\" & rmdir \"" + installDir + "\"";
            var start = new ProcessStartInfo("cmd.exe", script) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() };
            Process.Start(start);
        }

        // Removes what the last install listed in uninstall.dat (never the profile or logs folders).
        private static void RemoveProgramFiles(string dir)
        {
            string list = Path.Combine(dir, Setup.ListName);
            if (!File.Exists(list)) return;
            foreach (string line in File.ReadAllLines(list))
            {
                string name = line.Trim();
                if (name.Length == 0 || name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || name == "." || name == "..") continue;
                if (name.Equals("profile", StringComparison.OrdinalIgnoreCase) || name.Equals("logs", StringComparison.OrdinalIgnoreCase)) continue;
                string path = Path.Combine(dir, name);
                if (name.Equals(Setup.UninstallerName, StringComparison.OrdinalIgnoreCase) && IsSelf(path)) continue;
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
            }
        }

        private static bool IsSelf(string path)
        {
            return SamePath(path, Assembly.GetExecutingAssembly().Location);
        }

        private static void EnsureNotRunning(string dir)
        {
            string root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            foreach (string name in Programs)
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    string file = null;
                    try { file = process.MainModule.FileName; }
                    catch (Exception) { /* exited, another user's, or inaccessible */ }
                    if (file != null && file.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(name + " is running from " + dir + ". Close it and try again.");
                }
            }
        }

        private static string StartMenuShortcut()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Setup.ProductName + ".lnk");
        }

        private static string DesktopShortcut()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Setup.ProductName + ".lnk");
        }

        private static void WriteShortcut(string lnk, string target, string workingDir)
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic shortcut = shell.CreateShortcut(lnk);
            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = workingDir;
            shortcut.IconLocation = target + ",0";
            shortcut.Description = Setup.ProductName + " " + BuildInfo.Version;
            shortcut.Save();
        }

        private static bool ShortcutPointsInto(string lnk, string dir)
        {
            if (!File.Exists(lnk)) return false;
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                string target = shell.CreateShortcut(lnk).TargetPath;
                return target.StartsWith(Path.GetFullPath(dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        internal static bool SamePath(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch (Exception) { }
        }

        private static void TryDeleteEmpty(string dir)
        {
            try { if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); } catch (Exception) { }
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly TextBox path = new TextBox();
        private readonly Button browse = new Button();
        private readonly CheckBox startMenu = new CheckBox();
        private readonly CheckBox desktop = new CheckBox();
        private readonly ProgressBar bar = new ProgressBar();
        private readonly Label status = new Label();
        private readonly Button install = new Button();
        private readonly Button cancel = new Button();
        private bool finished;

        internal SetupForm(string target, bool update)
        {
            Text = Setup.ProductName + " " + BuildInfo.Version + (update ? " - Update" : " - Setup");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(520, 210);
            Font = SystemFonts.MessageBoxFont;
            try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch (Exception) { }

            var heading = new Label { Text = (update ? "Update " : "Install ") + Setup.ProductName + " into this folder:", Location = new Point(12, 14), AutoSize = true };
            path.Text = target;
            path.SetBounds(12, 38, 410, 24);
            browse.Text = "Browse...";
            browse.SetBounds(430, 36, 78, 27);
            browse.Click += delegate { Browse(); };
            startMenu.Text = "Start menu shortcut";
            startMenu.Checked = true;
            startMenu.SetBounds(12, 72, 240, 22);
            desktop.Text = "Desktop shortcut";
            desktop.Checked = Setup.DesktopWanted();
            desktop.SetBounds(12, 96, 240, 22);
            bar.SetBounds(12, 130, 496, 18);
            status.SetBounds(12, 152, 496, 18);
            status.AutoEllipsis = true;
            install.Text = update ? "Update" : "Install";
            install.SetBounds(332, 176, 86, 27);
            install.Click += delegate { OnInstall(); };
            cancel.Text = "Cancel";
            cancel.SetBounds(422, 176, 86, 27);
            cancel.Click += delegate { Close(); };
            AcceptButton = install;
            CancelButton = cancel;
            Controls.AddRange(new Control[] { heading, path, browse, startMenu, desktop, bar, status, install, cancel });
        }

        private void Browse()
        {
            using (var dialog = new FolderBrowserDialog { Description = "Choose the folder to install " + Setup.ProductName + " into", ShowNewFolderButton = true })
            {
                string current = path.Text.Trim();
                if (Directory.Exists(current)) dialog.SelectedPath = current;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string chosen = dialog.SelectedPath;
                // Picking a parent folder such as "D:\Apps" installs into "D:\Apps\SloppyAnimatsu".
                if (Directory.Exists(chosen) && Directory.GetFileSystemEntries(chosen).Length > 0
                    && !File.Exists(Path.Combine(chosen, Setup.ViewerExe)))
                {
                    chosen = Path.Combine(chosen, "SloppyAnimatsu");
                }
                path.Text = chosen;
            }
        }

        private void OnInstall()
        {
            if (finished)
            {
                // Started through the shell so the viewer runs as the signed-in user, not elevated.
                Process.Start("explorer.exe", "\"" + Path.Combine(path.Text, Setup.ViewerExe) + "\"");
                Close();
                return;
            }
            string target;
            try { target = Path.GetFullPath(path.Text.Trim()); }
            catch (Exception) { MessageBox.Show(this, "That is not a valid folder.", Text); return; }
            if (Directory.Exists(target) && Directory.GetFileSystemEntries(target).Length > 0
                && !File.Exists(Path.Combine(target, Setup.ListName))
                && MessageBox.Show(this, target + "\nis not empty. Install into it anyway?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            foreach (Control c in new Control[] { path, browse, startMenu, desktop, install, cancel }) c.Enabled = false;
            bool withStartMenu = startMenu.Checked, withDesktop = desktop.Checked;
            var worker = new Thread(delegate ()
            {
                Exception error = null;
                try
                {
                    Installer.Install(target, withStartMenu, withDesktop, delegate (int percent, string message)
                    {
                        BeginInvoke((Action)delegate { bar.Value = Math.Min(100, percent); status.Text = message; });
                    });
                }
                catch (Exception ex) { error = ex; }
                BeginInvoke((Action)delegate { Done(target, error); });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void Done(string target, Exception error)
        {
            cancel.Enabled = true;
            if (error != null)
            {
                status.Text = "Failed: " + error.Message;
                foreach (Control c in new Control[] { path, browse, startMenu, desktop, install }) c.Enabled = true;
                MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            finished = true;
            path.Text = target;
            status.Text = Setup.ProductName + " " + BuildInfo.Version + " is installed in " + target;
            install.Text = "Launch";
            install.Enabled = true;
            cancel.Text = "Close";
        }
    }
}
