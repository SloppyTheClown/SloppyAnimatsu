using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// SloppyAnimatsu's own settings files: the preferences,
/// the key map and the panel keys, all <c>.slopa</c> files in <c>%APPDATA%/SloppyAnimatsu</c> since
/// on. They hold SloppyAnimatsu's own nodes, so they are saved in its container; an
/// <c>.akt</c> settings file (a profile handed over with <c>--preferences</c> or
/// <c>--shortcuts</c>) keeps the .akt layout.
/// </summary>
public static class SettingsFiles
{
    /// <summary>The layout a settings file at <paramref name="path"/> is written in.</summary>
    public static AktFileFormat FormatFor(string path) => SceneFiles.FormatFor(path, AktFileFormat.SloppyAnimatsu);

}
