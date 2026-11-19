namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// Autosave: every <c>AutoSaveTime</c>
/// minutes the open scenes are written to the user's autosave folder, in two alternating files,
/// so a recent earlier copy survives the latest one. A file named <c>Walk.akt</c> is autosaved as
/// <c>Walk_autosave_01.slopa</c> and <c>Walk_autosave_02.slopa</c> (SloppyAnimatsu container, so <see cref="Akt.SceneFiles.Extension"/>); an unsaved scene uses its untitled
/// name (<c>New_1</c>, see <see cref="SceneFactory.UntitledName"/>).
/// </summary>
public static class Autosave
{
    /// <summary>How many autosave files a scene keeps (01 and 02).</summary>
    public const int Slots = 2;

    /// <summary>The autosave folder, <c>Documents/SloppyAnimatsu/Autosave</c>.</summary>
    public static string DefaultFolder => UserFolders.Autosave;

    /// <summary>The autosave base name of a saved scene: its file name without the extension.</summary>
    public static string BaseName(string scenePath) => Path.GetFileNameWithoutExtension(scenePath);

    public static string SlotPath(string folder, string baseName, int slot)
    {
        if (slot is < 1 or > Slots) throw new ArgumentOutOfRangeException(nameof(slot));
        return Path.Combine(folder, $"{baseName}_autosave_{slot:00}{Akt.SceneFiles.Extension}");
    }

    /// <summary>
    /// The file the next autosave of <paramref name="baseName"/> writes: the first missing slot,
    /// or the older of the two when both exist.
    /// </summary>
    public static string ChoosePath(string folder, string baseName)
    {
        string first = SlotPath(folder, baseName, 1);
        if (!File.Exists(first)) return first;
        string second = SlotPath(folder, baseName, 2);
        if (!File.Exists(second)) return second;
        return File.GetLastWriteTimeUtc(first) <= File.GetLastWriteTimeUtc(second) ? first: second;
    }

    /// <summary>Autosaves <paramref name="model"/> into <paramref name="folder"/>; returns the file written.</summary>
    public static string Save(SceneModel model, string folder, string baseName) =>
        Save(model, ChoosePath(folder, baseName));

    /// <summary>Writes <paramref name="model"/> to <paramref name="path"/>, creating its folder.</summary>
    public static string Save(SceneModel model, string path)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        model.Save(path);
        return path;
    }
}
