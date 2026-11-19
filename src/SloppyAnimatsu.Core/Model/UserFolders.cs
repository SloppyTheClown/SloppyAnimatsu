namespace SloppyAnimatsu.Core.Model;

/// <summary>
/// SloppyAnimatsu's folders in the user's documents: <c>Documents/SloppyAnimatsu</c> holds the
/// autosaves (<c>Autosave</c>), the retargeting name-pair cache (<c>Retargeting</c>) and is where the
/// scene pickers start; the shared <c>Templates</c> folder is below the common documents.
/// A machine without a documents folder (some Linux setups) falls back to the home folder.
/// </summary>
public static class UserFolders
{
    /// <summary>The folder name used everywhere below the user's and the shared documents.</summary>
    public const string AppName = "SloppyAnimatsu";

    /// <summary><c>Documents/SloppyAnimatsu</c>.</summary>
    public static string Documents => Path.Combine(DocumentsRoot(), AppName);

    /// <summary><c>Documents/SloppyAnimatsu/Autosave</c>.</summary>
    public static string Autosave => Path.Combine(Documents, "Autosave");

    /// <summary><c>Documents/SloppyAnimatsu/Retargeting</c>.</summary>
    public static string Retargeting => Path.Combine(Documents, "Retargeting");

    /// <summary>
    /// <c>&lt;Common Documents&gt;/SloppyAnimatsu/Templates</c>: on Windows the documents folder every
    /// user shares, so a template installed once serves every account; elsewhere the user's own documents.
    /// </summary>
    public static string Templates
    {
        get
        {
            string shared = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
            if (string.IsNullOrEmpty(shared)) shared = DocumentsRoot();
            return Path.Combine(shared, AppName, "Templates");
        }
    }

    private static string DocumentsRoot()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrEmpty(documents)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : documents;
    }
}
