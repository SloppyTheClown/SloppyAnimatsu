using SloppyAnimatsu.Core.Akt;

namespace SloppyAnimatsu.Core.Model;

/// <summary>One template scene of the Welcome window: the file the row opens and the name it shows.</summary>
public sealed record WelcomeTemplate(string FileName, string Name);

/// <summary>
/// The Welcome window: one row per template scene and a "Skip welcome window at startup" box. The
/// window is a startup dialog, not a File menu row: the app shows it after File &gt; New when no scene
/// was named on the command line, unless <c>CGeneralPreferences.SkipWelcomeWindow</c> is set. The
/// templates are the scenes (<c>.slopa</c> or <c>.akt</c>) found in the templates folder
/// (<see cref="UserFolders.Templates"/>), sorted by name; each row opens its scene through the
/// application's ordinary open path.
/// </summary>
public static class Welcome
{
    /// <summary>The preference member the "skip" box is bound to (<c>CGeneralPreferences</c>).</summary>
    public const string SkipMember = "SkipWelcomeWindow";

    /// <summary>The window's heading.</summary>
    public const string Heading = "WELCOME";

    /// <summary>The shared templates folder.</summary>
    public static string DefaultFolder => UserFolders.Templates;

    /// <summary>
    /// The template scenes in <paramref name="folder"/> (or the default folder), sorted by name; a
    /// missing or unreadable folder has none.
    /// </summary>
    public static IReadOnlyList<WelcomeTemplate> Find(string? folder = null)
    {
        folder ??= DefaultFolder;
        try
        {
            if (!Directory.Exists(folder)) return [];
            return Directory.EnumerateFiles(folder)
                .Where(SceneFiles.HasSceneExtension)
                .Select(path => new WelcomeTemplate(Path.GetFileName(path), Path.GetFileNameWithoutExtension(path)))
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    /// <summary>Where <paramref name="template"/> is, in <paramref name="folder"/> or the default folder.</summary>
    public static string TemplatePath(WelcomeTemplate template, string? folder = null) =>
        Path.Combine(folder ?? DefaultFolder, template.FileName);

    /// <summary>
    /// The app shows the window at startup when no scene was named on the command line, and stays
    /// quiet once the box has been ticked. A scripted run would block on a dialog nobody is there to
    /// answer, so the gate skips it.
    /// </summary>
    public static bool ShowAtStartup(bool sceneOnCommandLine, bool scriptedRun, bool skipWelcome) =>
        !sceneOnCommandLine && !scriptedRun && !skipWelcome;

    /// <summary>The preference's value; a missing preference file (or member) means "not skipped".</summary>
    public static bool IsSkipped(Preferences? preferences) => preferences?.SkipWelcomeWindow == true;
}
