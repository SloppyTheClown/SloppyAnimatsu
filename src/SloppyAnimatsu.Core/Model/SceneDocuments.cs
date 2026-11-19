using System.IO;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// The folder members the app keeps on a scene
/// <c>CScene.SavePath</c> and <c>CScene.FBXPath</c> hold the folder
/// the scene or its FBX was last read from or written to, spelled with forward slashes and a
/// trailing slash (<c>C:/Projects/Scenes/</c> after a
/// Save As).
/// </summary>
/// <remarks>
/// Some files store the folder the file dialog was in, so scenes not saved since the
/// 20.3 override can carry an older spelling (its <c>PreferredPaths.LoadPath</c> value,
/// <c>C:/Users/&lt;user&gt;/Documents/scenes\</c>). SloppyAnimatsu writes the normalised form above; a
/// scene read from such a file keeps its own value until a document saves it again.
/// </remarks>
public static class ScenePaths
{
    public const string SavePath = "SavePath";
    public const string FbxPath = "FBXPath";
    public const string AknmPath = "AKNMPath";

    /// <summary>The folder of <paramref name="file"/>: absolute, forward slashes, one trailing slash.</summary>
    public static string Folder(string file)
    {
        string full = Path.GetFullPath(file);
        string? folder = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(folder)) return "";
        string slashed = folder.Replace('\\', '/');
        return slashed.EndsWith('/') ? slashed : slashed + "/";
    }

    /// <summary>The stored value of <paramref name="member"/>, or null when the scene has no such member.</summary>
    public static string? Get(SceneModel model, string member) => model.Scene.Source[member] as string;

    /// <summary>
    /// Records <paramref name="file"/>'s folder in <paramref name="member"/>, as the app does when a
    /// scene is opened or saved under a new name. A scene older than the member is left alone.
    /// </summary>
    public static bool Remember(SceneModel model, string member, string file)
    {
        if (!model.Scene.Source.Has(member)) return false;
        model.Scene.Source.Set(member, Folder(file));
        return true;
    }
}

/// <summary>
/// One open scene document. The app holds several at once ("Close file" is
/// not "Close scene"; autosave writes one autosave per open
/// document; a document's tab carries its file name), and this is the bookkeeping one needs: the
/// scene, the file it belongs to (none while untitled), its tab name and whether it has unsaved
/// edits.
/// </summary>
public sealed class SceneDocument
{
    internal SceneDocument(SceneModel model, string? path, string name)
    {
        Model = model;
        Path = path;
        Name = name;
    }

    public SceneModel Model { get; }

    /// <summary>The file the document was opened from or last saved to; null while it is untitled.</summary>
    public string? Path { get; private set; }

    /// <summary>The tab's name: the file name, or the untitled name (<c>New_1</c>, ...).</summary>
    public string Name { get; private set; }

    /// <summary>True when the scene has edits the app would ask about before closing it.</summary>
    public bool Dirty { get; set; }

    public bool IsUntitled => Path == null;

    /// <summary>The base name of the document's autosave files: <c>Walk</c> for <c>Walk.akt</c>.</summary>
    public string AutosaveBase => Path != null ? Autosave.BaseName(Path) : Name;

    /// <summary>The tab's label, with the modified marker.</summary>
    public string Title => Dirty ? $"{Name} *" : Name;

    /// <summary>
    /// A scene read from <paramref name="path"/>: the open writes the file's folder into
    /// <c>CScene.SavePath</c>, so the next Save knows where the document lives.
    /// </summary>
    internal void Opened(string path) => ScenePaths.Remember(Model, ScenePaths.SavePath, path);

    /// <summary>
    /// The document is written to <paramref name="path"/> (Save or Save As): it takes the file's
    /// name and folder, loses its modified marker, and the scene's <c>SavePath</c> follows the file.
    /// </summary>
    public void Saved(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        Dirty = false;
        ScenePaths.Remember(Model, ScenePaths.SavePath, path);
    }
}

/// <summary>
/// The set of open documents and which one is active, the model behind File &gt; Close file, the
/// document tabs and the autosave loop over every open scene
/// </summary>
/// <remarks>
/// The app opens a scene as a new document (opening adds the file to the open
/// document list) and keeps a per-run counter for untitled names, so <c>New_1</c>, <c>New_2</c>, ...
/// are never reused even after their document is closed. The window draws one tab per document and
/// runs the File menu's Close on the active one.
/// </remarks>
public sealed class DocumentSet
{
    private readonly List<SceneDocument> _documents = [];
    private int _active = -1;
    private int _untitled;

    public IReadOnlyList<SceneDocument> Documents => _documents;

    public int Count => _documents.Count;

    /// <summary>The active document's position, or -1 when no document is open.</summary>
    public int ActiveIndex => _active;

    public SceneDocument? Active => _active >= 0 ? _documents[_active] : null;

    /// <summary>The last untitled number handed out; the next new scene is <c>New_{UntitledCount + 1}</c>.</summary>
    public int UntitledCount => _untitled;

    /// <summary>File &gt; New Scene: adds the scene as a new untitled document and activates it.</summary>
    public SceneDocument New(SceneModel model) =>
        Append(new SceneDocument(model, null, SceneFactory.UntitledName(++_untitled)));

    /// <summary>
    /// A document for a scene built in memory that is not File &gt; New's numbered one - an FBX
    /// import builds its own scene and names it after the file. The untitled counter is left alone.
    /// </summary>
    public SceneDocument Add(SceneModel model, string name) =>
        Append(new SceneDocument(model, null, name));

    /// <summary>
    /// File &gt; Open: the scene becomes a document named after the file and the active one, and the
    /// scene's <c>SavePath</c> takes the file's folder, as the open does.
    /// </summary>
    /// <remarks>
    /// The open **reuses an untouched untitled document** instead of adding a tab: a file
    /// opened into the start-up <c>New_1</c> left one tab,
    /// and <c>a.akt</c> + an untouched <c>New_2</c> + Open <c>b.akt</c> left the tabs <c>a.akt</c>,
    /// <c>b.akt</c> - the untitled tab took the opened file's name. A file opened while the active
    /// document is a saved scene adds a tab.
    /// </remarks>
    public SceneDocument Open(SceneModel model, string path)
    {
        var document = new SceneDocument(model, path, System.IO.Path.GetFileName(path));
        document.Opened(path);
        if (Active is { IsUntitled: true, Dirty: false } placeholder)
        {
            int index = _documents.IndexOf(placeholder);
            _documents[index] = document;
            _active = index;
            return document;
        }
        return Append(document);
    }

    /// <summary>
    /// Whether File &gt; Close file may close <paramref name="document"/>. The app keeps one
    /// document in the window: closing the only one, when it is an untouched untitled scene,
    /// leaves it in place
    /// <c>New_2</c> left the <c>New_2</c> tab and its scene alone, where closing a lone saved
    /// <c>a.akt</c> built a fresh <c>New_2</c>).
    /// </summary>
    public bool CanClose(SceneDocument document) =>
        _documents.Count > 1 || !(document.IsUntitled && !document.Dirty);

    /// <summary>Closes <paramref name="document"/> and returns the document that becomes active.</summary>
    /// <remarks>
    /// The app activates the tab to the **left** of the closed one, or the first tab when the
    /// closed one was first: closing <c>b</c> of <c>a b c</c> left <c>a</c> active, closing
    /// <c>a</c> left <c>b</c>, closing the last left its predecessor.
    /// </remarks>
    public SceneDocument? Close(SceneDocument document)
    {
        int index = _documents.IndexOf(document);
        if (index < 0) throw new ArgumentException("The document is not open.", nameof(document));
        bool wasActive = index == _active;
        _documents.RemoveAt(index);
        if (_documents.Count == 0)
        {
            _active = -1;
            return null;
        }
        if (wasActive) _active = Math.Max(0, index - 1);
        else if (index < _active) _active--;
        return Active;
    }

    public bool Activate(SceneDocument document)
    {
        int index = _documents.IndexOf(document);
        if (index < 0) return false;
        _active = index;
        return true;
    }

    public SceneDocument? ActivateAt(int index)
    {
        if (index < 0 || index >= _documents.Count) return null;
        _active = index;
        return Active;
    }

    /// <summary>The next document, wrapping: the tab a "next document" gesture would show.</summary>
    public SceneDocument? Next() => Count == 0 ? null : _documents[(_active + 1) % Count];

    /// <summary>The previous document, wrapping.</summary>
    public SceneDocument? Previous() => Count == 0 ? null : _documents[(_active - 1 + Count) % Count];

    private SceneDocument Append(SceneDocument document)
    {
        _documents.Add(document);
        _active = _documents.Count - 1;
        return document;
    }
}
