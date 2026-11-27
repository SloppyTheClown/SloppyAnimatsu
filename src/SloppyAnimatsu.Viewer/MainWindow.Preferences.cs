// Preferences and startup persistence: applying the preference values to the controls, the joint
// alignment bits, the preferences and shortcut dialogs, the recent-scene list and the autosave
// timer.
//
// Preferences are read and written by SloppyAnimatsu.Core Preferences/Shortcuts; this file only maps them
// onto the widgets and the running session.
// Part of the partial MainWindow type; the provenance of the code is in MainWindow.cs.
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SloppyAnimatsu.Core;
using SloppyAnimatsu.Core.Aknm;
using SloppyAnimatsu.Core.Akt;
using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    private void SaveDocumentState()
    {
        if (_document != null)
        {
            _document.History = _history;
            _document.Sessions = _sessions;
            _document.Selected = _selectedAll;
            _document.Character = _character?.Name;
            _document.Animation = _animation?.Name;
            _document.Time = _time;
            _document.Context = _lastContext;
            _document.Dirty = _dirty;
            _document.Document.Dirty = _dirty;
            OrbitCamera camera = _viewport.Renderer.Camera;
            _document.CameraTarget = camera.Target;
            _document.CameraDistance = camera.Distance;
            _document.CameraYaw = camera.Yaw;
            _document.CameraPitch = camera.Pitch;
            _document.CameraFieldOfView = camera.FieldOfView;
        }
    }

    private void ApplyPreferenceValues()
    {
        float num = _preferences?.MoveStep ?? 0.1f;
        float num2 = _preferences?.RotateStep ?? 45f;
        float num3 = _preferences?.ScaleStep ?? 0.5f;
        _moveStep.Show(num);
        _rotateStep.Show(num2);
        _scaleStep.Show(num3);
        _timeline.MaxVisibleRows = _preferences?.VisibleTimelineRows ?? 4;
        _snapMove.IsChecked = _preferences?.MoveDiscrete ?? false;
        _snapRotate.IsChecked = _preferences?.RotateDiscrete ?? false;
        _snapScale.IsChecked = _preferences?.ScaleDiscrete ?? false;
        if (_preferences != null)
        {
            _jointAlignment = _preferences.JointAlignment;
        }
        ApplyTranslationUnit();
        RefreshMenu();
        RefreshMaterial();
        if (_autosaveTimer != null)
        {
            _autosaveTimer.Stop();
            _autosaveTimer.Interval = TimeSpan.FromMinutes(_preferences?.AutoSaveMinutes ?? 10);
            _autosaveTimer.Start();
        }
    }

    /// <summary>
    /// Shows the TRANSFORM window's translation fields in the preferred unit (scene units or
    /// metres; the model values are untouched) and names the unit on the Translate group's hover
    /// text and on each field.
    /// </summary>
    private void ApplyTranslationUnit()
    {
        TranslationUnit unit = _preferences?.TranslationUnit ?? TranslationUnit.SceneUnits;
        string tip = "Translate (" + unit.Suffix() + ")";
        foreach (SpinnerField field in _translateFields)
        {
            field.DisplayScale = unit.DisplayScale();
            // Metres are marked on the number itself ("m1.25") so a value is never mistaken for
            // scene units; scene units stay bare.
            field.Prefix = unit == TranslationUnit.Meters ? "m" : "";
            ToolTip.SetTip(field, tip);
        }
        if (_groupButtons != null) ToolTip.SetTip(_groupButtons[0], tip);
    }

    private void SetJointAxis(bool aim, JointAxis axis, bool negative)
    {
        _jointAlignment = (aim ? _jointAlignment with
        {
            Aim = axis,
            AimNegative = negative
        } : _jointAlignment with
        {
            Up = axis,
            UpNegative = negative
        });
        SaveJointAlignment();
    }

    private void SetJointAlignOnCreate(bool value)
    {
        _jointAlignment = _jointAlignment with
        {
            AlignOnCreate = value
        };
        SaveJointAlignment();
    }

    private void SaveJointAlignment()
    {
        RefreshMenu();
        _status.Text = "Joint axes: aim " + JointAlignment.NameOf(_jointAlignment.Aim) + (_jointAlignment.AimNegative ? "-" : "+") + ", up " + JointAlignment.NameOf(_jointAlignment.Up) + (_jointAlignment.UpNegative ? "-" : "+") + (_jointAlignment.AlignOnCreate ? ", Create Joint aligns." : ".");
        if (_preferences == null)
        {
            return;
        }
        try
        {
            if (_preferences.SetJointAlignment(_jointAlignment, save: true, out string error))
            {
                TextBlock status = _status;
                status.Text = status.Text + " Saved " + _preferences.Path + ".";
            }
            else if (error != null)
            {
                TextBlock status2 = _status;
                status2.Text = status2.Text + " " + error;
            }
        }
        catch (Exception ex)
        {
            TextBlock status3 = _status;
            status3.Text = status3.Text + " Could not save preferences: " + ex.Message;
        }
    }

    // The Preferences window's undo history, kept here so it survives closing the window.
    private readonly PreferenceHistory _preferenceHistory = new();

    private async Task OpenPreferencesDialog(string? pageClass = null)
    {
        if (_preferences == null)
        {
            _status.Text = "No preferences file is open; start the viewer with --preferences or create " + Preferences.DefaultPath + ".";
            return;
        }
        await PreferencesDialog.ShowFor(this, _preferences, _preferenceHistory, delegate(string label, bool sceneEdited)
        {
            ApplyPreferenceValues();
            RefreshMenu();
            SyncBankSortBox();
            RefreshBankView();
            if (sceneEdited)
            {
                _dirty = true;
                UpdateTitle();
            }
            // The Decorators page (scene-backed joint/pivot sizes, alphas, joint display mode,
            // per-view display) rebuilds the pose and redraws at once.
            Apply();
            _status.Text = "Preferences: " + label + ".";
        }, pageClass, (_pose == null) ? ((int?)null) : new int?((int)FrameRate), _pose?.Model.Scene);
    }

    private async Task OpenShortcutDialog()
    {
        if (_shortcuts != null)
        {
            await ShortcutDialog.ShowFor(this, _shortcuts, delegate
            {
                RefreshMenu();
                _status.Text = "Saved " + _shortcuts.Path + ".";
            });
        }
    }

    /// <summary>The Panel keys dialog: SloppyAnimatsu's own panel actions, saved to panelShortcuts.slopa.</summary>
    private async Task OpenPanelKeysDialog()
    {
        if (_panelShortcuts == null) return;
        await ShortcutDialog.ShowFor(this, _panelShortcuts, delegate
        {
            _status.Text = "Saved " + _panelShortcuts.Path + ".";
        }, panel: true);
    }

    private void LoadPreferences()
    {
        // A profile without a preferences file gets one generated (the defaults and its
        // default panel layout), so the layout, recent scenes and settings have somewhere to live.
        // The user's profile folder is tried first; when it cannot be read or written the profile
        // beside the program takes over, so recent scenes and the layout still persist. A path
        // given with --preferences is used as it is.
        string[] candidates = _options.PreferencesPath != null
            ? new[] { _options.PreferencesPath }
            : new[] { Preferences.DefaultPath, Preferences.PortablePath };
        bool existed = File.Exists(candidates[0]);
        try
        {
            _preferences = Preferences.OpenFirstUsable(candidates, _options.PreferencesPath == null, out IReadOnlyList<string> problems);
            if (_preferences == null)
            {
                _status.Text = "No preferences file could be opened, so settings and recent scenes are not kept: " + string.Join("; ", problems);
            }
            else if (problems.Count > 0)
            {
                _status.Text = "Using the preferences at " + _preferences.Path + " (" + problems[0] + ").";
            }
            else if (!existed)
            {
                _status.Text = "Created default preferences at " + _preferences.Path + ".";
            }
            // SloppyAnimatsu's own Material page (an older profile lacks it), saved so the Preferences
            // dialog, which reads the file, lists it.
            if (_preferences != null && (_preferences.EnsureMaterialPage() | _preferences.EnsureAnimBankPage()
                                         | _preferences.EnsureTransformPage()))
            {
                try { _preferences.Save(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            SyncBankSortBox();
        }
        catch (Exception ex)
        {
            _preferences = null;
            _status.Text = "Could not read the preferences: " + ex.Message;
        }
    }

    private void OpenRecent(string path)
    {
        if (File.Exists(path))
        {
            Load(path);
        }
        else
        {
            _status.Text = "The scene '" + path + "' no longer exists.";
        }
    }

    private void RememberRecent(string path)
    {
        if (_preferences != null && _preferences.AddRecentFile(path))
        {
            try
            {
                _preferences.Save();
            }
            catch (Exception ex)
            {
                _status.Text = "Could not save the preferences: " + ex.Message;
            }
            RefreshMenu();
        }
    }

    private void StartAutosave()
    {
        uint num = _preferences?.AutoSaveMinutes ?? 10;
        _autosaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(num)
        };
        _autosaveTimer.Tick += delegate
        {
            AutoSave();
        };
        _autosaveTimer.Start();
    }

    private void AutoSave()
    {
        if (_documents.Count == 0 || _drag != null || ProgressDialog.Busy)
        {
            return;
        }
        int num = 0;
        PluginsSceneSaving();
        try
        {
            foreach (SceneDocument document in _documents.Documents)
            {
                Autosave.Save(document.Model, Autosave.DefaultFolder, document.AutosaveBase);
                num++;
            }
            _status.Text = ((num == 1) ? ("Autosaved " + _documents.Active?.AutosaveBase + ".") : $"Autosaved {num} open documents.");
        }
        catch (Exception ex)
        {
            _status.Text = "Autosave failed: " + ex.Message;
        }
    }
}
