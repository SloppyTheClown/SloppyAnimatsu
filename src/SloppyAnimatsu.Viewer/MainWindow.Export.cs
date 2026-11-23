// File > Export…: the Export Settings window, the live highlight of what it would write, and the
// export itself (one FBX, one per character and/or per animation, or .aknm animation files).
// Part of the partial MainWindow type.
using System.Text;
using Avalonia.Platform.Storage;
using SloppyAnimatsu.Core.Aknm;
using SloppyAnimatsu.Core.Fbx;
using SloppyAnimatsu.Core.Model;

namespace SloppyAnimatsu.Viewer;

public sealed partial class MainWindow
{
    /// <summary>One file an export writes: its path and what it holds.</summary>
    private sealed record ExportFile(string Path, FbxExportOptions Options);

    /// <summary>The objects the scene tree and the viewport have selected.</summary>
    private List<Object3D> ExportSelection()
    {
        var list = new List<Object3D>(_outliner.SelectedObjects);
        if (_selected != null && !list.Contains(_selected)) list.Add(_selected);
        return list;
    }

    /// <summary>The animations the Anim Bank has picked: the selected rows (a folder brings every
    /// animation under it), else the highlighted folder, else the current animation.</summary>
    private List<Animation> PickedBankAnimations(SceneModel model)
    {
        List<SceneObject> picks = SelectedBankItems();
        if (picks.Count == 0)
        {
            if (_bankFolder != null) picks.Add(_bankFolder);
            else if (_animation != null) picks.Add(_animation);
        }
        var folders = picks.OfType<Folder>().ToList();
        return model.Scene.Characters.SelectMany(c => c.Animations)
            .Where(a => picks.Contains(a) || folders.Any(f => IsUnder(a, f))).ToList();
    }

    /// <summary>The exporter options for the whole export, before it is split into files.</summary>
    private FbxExportOptions ExportOptions(SceneModel model, FbxExportSettings s, string? textureDirectory)
    {
        var selection = ExportSelection();
        return s.ToOptions(
            s.Scope == FbxExportScope.Scene ? null : s.Characters(model, selection),
            s.LooseMeshes(selection),
            s.OnlySelectedAnimations ? PickedBankAnimations(model) : null,
            _path, textureDirectory);
    }

    /// <summary>A one-line account of what the settings export, shown under the toggles.</summary>
    private string DescribeExport(SceneModel model, FbxExportSettings s)
    {
        var options = ExportOptions(model, s, null);
        int characters = FbxExporter.ExportedCharacters(model, options).Count;
        int animations = FbxExporter.ExportedAnimations(model, options).Count;
        int objects = FbxExporter.ExportedObjects(model, options).Count;
        int files = SplitExport(model, s, options, "x").Count;
        return $"Exports {characters} character(s), {objects} object(s) and {animations} animation(s)"
            + (files == 1 ? " to one file." : $" to {files} files.");
    }

    /// <summary>The open Export Settings window (it is not modal), and the scene it was opened on.</summary>
    private ExportSettingsDialog? _exportDialog;
    private SceneModel? _exportModel;

    /// <summary>Re-lights what the open Export Settings window would write after the selection or
    /// the Anim Bank changed; closes the window when its scene is no longer the open one.</summary>
    private void RefreshExportPreview()
    {
        if (_exportDialog == null) return;
        if (_pose?.Model != _exportModel)
        {
            _exportDialog.Close();
            return;
        }
        _exportDialog.Refresh();
    }

    /// <summary>File > Export…: the Export Settings window, then where to write.</summary>
    private async Task ShowExportSettings()
    {
        if (_exportDialog != null)
        {
            _exportDialog.Activate();
            return;
        }
        if (_pose == null)
        {
            _status.Text = "No scene to export.";
            return;
        }
        SceneModel model = _pose.Model;
        FbxExportSettings initial = _preferences?.ExportSettings ?? FbxExportSettings.FromFlags(FbxExportSettings.DefaultFlags);
        FbxExportSettings? chosen;
        EventHandler<Avalonia.Controls.SelectionChangedEventArgs> bankChanged = (_, _) => RefreshExportPreview();
        _animations.SelectionChanged += bankChanged;
        _exportModel = model;
        try
        {
            chosen = await ExportSettingsDialog.Ask(this, initial, s => HighlightExport(model, s), s => DescribeExport(model, s),
                dialog => _exportDialog = dialog);
        }
        finally
        {
            _animations.SelectionChanged -= bankChanged;
            _exportDialog = null;
            _exportModel = null;
            _outliner.ExportFilter = null;
            SetBankExportFilter(null);
        }
        if (chosen == null || _pose?.Model != model) return;

        if (_preferences != null)
        {
            _preferences.ExportSettings = chosen;
            try
            {
                _preferences.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _status.Text = "Could not save the preferences: " + ex.Message;
            }
        }
        await RunExport(model, chosen);
    }

    /// <summary>Lights, in the scene tree and the Anim Bank, everything <paramref name="s"/> writes.</summary>
    private void HighlightExport(SceneModel model, FbxExportSettings s)
    {
        var options = ExportOptions(model, s, null);
        var objects = FbxExporter.ExportedObjects(model, options);
        var animations = new HashSet<Animation>(FbxExporter.ExportedAnimations(model, options), ReferenceEqualityComparer.Instance);
        if (s.WithoutAnimations) animations.Clear();
        _outliner.ExportFilter = o => objects.Contains(o);
        SetBankExportFilter(item => item switch
        {
            Animation a => animations.Contains(a),
            Folder f => animations.Any(a => IsUnder(a, f)),
            _ => false,
        });
    }

    /// <summary>
    /// The files one export writes from the chosen <paramref name="basePath"/>: a single file, or
    /// <c>&lt;base&gt;_&lt;character&gt;</c> per character and/or <c>&lt;base&gt;_&lt;animation&gt;</c> per
    /// animation (both: <c>&lt;base&gt;_&lt;character&gt;_&lt;animation&gt;</c>), where the animation part is
    /// <see cref="FbxExportSettings.AnimationFileName"/>. Unbound meshes go to <c>&lt;base&gt;_meshes</c>
    /// when the export is split by character. A name already taken gets <c>_2</c>, <c>_3</c>… so no
    /// file overwrites another of the same export.
    /// </summary>
    private static List<ExportFile> SplitExport(SceneModel model, FbxExportSettings s, FbxExportOptions whole, string basePath)
    {
        string folder = Path.GetDirectoryName(basePath) ?? "";
        string stem = Path.GetFileNameWithoutExtension(basePath);
        string ext = Path.GetExtension(basePath) is { Length: > 0 } e ? e : ".fbx";
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string PathFor(params string[] parts)
        {
            string name = string.Join("_", new[] { stem }.Concat(parts.Select(SafeFileName)));
            string unique = name;
            for (int n = 2; !taken.Add(unique); n++) unique = name + "_" + n;
            return Path.Combine(folder, unique + ext);
        }
        string AnimationPart(Animation a) => s.AnimationFileName(a);

        bool perAnimation = s.OneFilePerAnimation && !s.WithoutAnimations;
        var characters = FbxExporter.ExportedCharacters(model, whole);
        var animations = FbxExporter.ExportedAnimations(model, whole);
        var files = new List<ExportFile>();

        if (!s.OneFilePerCharacter)
        {
            if (!perAnimation)
                files.Add(new ExportFile(basePath, whole));
            else
                foreach (var a in animations)
                    files.Add(new ExportFile(PathFor(AnimationPart(a)), whole with { Animations = [a] }));
            return files;
        }

        foreach (var c in characters)
        {
            var only = whole with { Characters = [c], LooseMeshes = [] };
            if (!perAnimation)
                files.Add(new ExportFile(PathFor(c.Name ?? "character"), only));
            else
                foreach (var a in animations.Where(a => a.Character == c))
                    files.Add(new ExportFile(PathFor(c.Name ?? "character", AnimationPart(a)), only with { Animations = [a] }));
        }
        var loose = whole.LooseMeshes ?? (whole.Characters == null
            ? model.Scene.Meshes.Where(m => m.BindPose?.Character == null).ToList()
            : []);
        if (loose.Count > 0)
            files.Add(new ExportFile(PathFor("meshes"), whole with { Characters = [], LooseMeshes = loose.ToList(), Animations = [] }));
        return files;
    }

    /// <summary><paramref name="name"/> with path separators and characters a file name cannot hold
    /// replaced by '-'.</summary>
    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char ch in name)
            sb.Append(ch == '/' || ch == '\\' || Array.IndexOf(invalid, ch) >= 0 ? '-' : ch);
        return sb.Length == 0 ? "unnamed" : sb.ToString();
    }

    /// <summary>Asks where to write, then writes the FBX file(s), or .aknm animations when that type is chosen.</summary>
    private async Task RunExport(SceneModel model, FbxExportSettings s)
    {
        var fbxType = new FilePickerFileType(s.Ascii ? "FBX (ASCII)" : "FBX") { Patterns = ["*.fbx"] };
        var aknmType = new FilePickerFileType("Animation (.aknm), one file per character") { Patterns = ["*.aknm"] };
        string? path = (await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_path ?? _documentName) + ".fbx",
            DefaultExtension = "fbx",
            FileTypeChoices = [fbxType, aknmType],
        }))?.TryGetLocalPath();
        if (path == null) return;
        _play.IsChecked = false;

        if (path.EndsWith(".aknm", StringComparison.OrdinalIgnoreCase))
        {
            await ExportAknm(model, s, path);
            return;
        }

        try
        {
            var whole = ExportOptions(model, s, Path.GetDirectoryName(path));
            var files = SplitExport(model, s, whole, path);
            if (files.Count == 0)
            {
                _status.Text = "Nothing to export with these settings.";
                return;
            }
            await ProgressDialog.Run(this, "Exporting FBX", "Exporting " + Path.GetFileName(path) + "...", progress =>
            {
                for (int i = 0; i < files.Count; i++)
                {

                    var part = new PartProgress(progress, i, files.Count);
                    FbxExporter.Export(model, files[i].Path, files[i].Options, part);
                }
            });
            _status.Text = files.Count == 1
                ? $"Exported {s.Target} FBX to {files[0].Path} ({Bytes(files[0].Path)})."
                : $"Exported {files.Count} {s.Target} FBX files to {Path.GetDirectoryName(path)}.";
        }
        catch (Exception ex)
        {
            _status.Text = "FBX export failed: " + ex.Message;
        }
    }

    /// <summary>Writes the exported animations as .aknm, one file per exported character
    /// (<c>&lt;base&gt;_&lt;character&gt;.aknm</c> when there are several).</summary>
    private async Task ExportAknm(SceneModel model, FbxExportSettings s, string path)
    {
        try
        {
            var whole = ExportOptions(model, s, null);
            var animations = FbxExporter.ExportedAnimations(model, whole);
            var jobs = FbxExporter.ExportedCharacters(model, whole)
                .Select(c => (Character: c, Animations: animations.Where(a => a.Character == c).ToList()))
                .Where(j => j.Animations.Count > 0).ToList();
            if (jobs.Count == 0)
            {
                _status.Text = "No animation to export with these settings.";
                return;
            }
            string folder = Path.GetDirectoryName(path) ?? "";
            string stem = Path.GetFileNameWithoutExtension(path);
            string PathFor(Character c) => jobs.Count == 1 ? path
                : Path.Combine(folder, stem + "_" + SafeFileName(c.Name ?? "character") + ".aknm");
            await ProgressDialog.Run(this, "Exporting animation", "Exporting " + Path.GetFileName(path) + "...", progress =>
            {
                for (int i = 0; i < jobs.Count; i++)
                {

                    var part = new PartProgress(progress, i, jobs.Count);
                    AknmExport.Write(model, jobs[i].Character, jobs[i].Animations, PathFor(jobs[i].Character), part);
                }
            });
            int count = jobs.Sum(j => j.Animations.Count);
            _status.Text = jobs.Count == 1
                ? $"Exported {count} animation(s) of '{jobs[0].Character.Name}' to {path} ({Bytes(path)})."
                : $"Exported {count} animation(s) of {jobs.Count} characters to {folder}.";
        }
        catch (Exception ex)
        {
            _status.Text = "AKNM export failed: " + ex.Message;
        }
    }

    /// <summary>Reports one part's progress as its share of the whole: part <c>index</c> of <c>count</c>.</summary>
    private sealed class PartProgress(IProgress<double> whole, int index, int count) : IProgress<double>
    {
        public void Report(double value) => whole.Report((index + Math.Clamp(value, 0, 1)) / count);
    }
}
