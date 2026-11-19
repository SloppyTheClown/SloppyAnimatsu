using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The Welcome window: the template scenes it lists from its folder, the startup gate that opens it
/// and the <c>CGeneralPreferences.SkipWelcomeWindow</c> box that suppresses it.
/// </summary>
public class WelcomeTests
{
    [Fact]
    public void ListsTheScenesInTheFolderByName()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-templates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            foreach (string name in new[] { "b.slopa", "a.akt", "notes.txt" })
                File.WriteAllText(Path.Combine(folder, name), "");
            var templates = Welcome.Find(folder);
            Assert.Equal(["a.akt", "b.slopa"], templates.Select(t => t.FileName));
            Assert.Equal(["a", "b"], templates.Select(t => t.Name));
            Assert.All(templates, t => Assert.Equal(Path.Combine(folder, t.FileName), Welcome.TemplatePath(t, folder)));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void AMissingFolderListsNothing() =>
        Assert.Empty(Welcome.Find(Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-none-{Guid.NewGuid():N}")));

    [Fact]
    public void TemplateFolderIsTheAppsSharedTemplatesFolder() =>
        Assert.EndsWith(Path.Combine("SloppyAnimatsu", "Templates"), Welcome.DefaultFolder);

    [Theory]
    [InlineData(false, false, false, true)]  // a plain startup offers the window
    [InlineData(true, false, false, false)]  // a scene named on the command line does not
    [InlineData(false, true, false, false)]  // nor a scripted run: nobody would answer it
    [InlineData(false, false, true, false)]  // nor a startup the box has been ticked for
    public void StartupGate_NeedsNoSceneAScriptedGateOffAndTheBoxUnticked(
        bool sceneOnCommandLine, bool scriptedRun, bool skipWelcome, bool expected) =>
        Assert.Equal(expected, Welcome.ShowAtStartup(sceneOnCommandLine, scriptedRun, skipWelcome));

    [Fact]
    public void AMissingPreferencesFileIsNotSkipped()
    {
        Assert.False(Welcome.IsSkipped(null));
    }

    /// <summary>
    /// The box is <c>CGeneralPreferences.SkipWelcomeWindow</c> of the real preferences file: it reads
    /// the saved value and a write survives a reload, with every other member byte-identical.
    /// </summary>
    [Fact]
    public void SkipWelcomeWindowRoundTripsThroughThePreferences()
    {
        if (TestFiles.Preferences == null) return;
        string copy = TempFile("preferences-welcome");
        try
        {
            Preferences.Load(TestFiles.Preferences).SaveAs(copy);
            var preferences = Preferences.Load(copy);
            Assert.True(preferences.Document.Roots.Any(o => o.ClassKey == "CGeneralPreferences" && o.Has(Welcome.SkipMember)),
                "the sample preferences must carry the member the Welcome window writes");
            bool before = preferences.SkipWelcomeWindow;
            Assert.Equal(before, Welcome.IsSkipped(preferences));

            preferences.SkipWelcomeWindow = !before;
            preferences.Save();

            var reloaded = Preferences.Load(copy);
            Assert.Equal(!before, reloaded.SkipWelcomeWindow);
            Assert.Equal(!before, Welcome.IsSkipped(reloaded));

            reloaded.SkipWelcomeWindow = before;
            reloaded.Save();
            Assert.Equal(before, Preferences.Load(copy).SkipWelcomeWindow);
        }
        finally
        {
            File.Delete(copy);
        }
    }

    private static string TempFile(string purpose) =>
        Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-{purpose}-{Guid.NewGuid():N}.slopa");
}
