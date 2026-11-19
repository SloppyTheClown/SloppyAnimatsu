using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// Several documents open at once:
/// the tab set, the active document, Close file, the untitled-name counter and the folder a
/// document writes into <c>CScene.SavePath</c>. The behaviour and
/// as follows: New adds a tab, Open reuses an untouched untitled document
/// instead of adding one (the Welcome row into the start-up <c>New_1</c> left one tab), Close
/// activates the tab to the left of the closed one (or the first tab when the closed one was first),
/// closing the last saved document builds a fresh untitled <c>New_N</c>, and a lone untouched
/// untitled document is not closed at all. The untitled counter counts the run's documents.
/// </summary>
public class DocumentSetTests
{
    [Fact]
    public void New_AddsUntitledDocumentsNumberedPerRun()
    {
        var documents = new DocumentSet();
        SceneDocument first = documents.New(SceneFactory.NewSceneModel(60));
        SceneDocument second = documents.New(SceneFactory.NewSceneModel(60));

        Assert.Equal(2, documents.Count);
        Assert.Equal("New_1", first.Name);
        Assert.Equal("New_2", second.Name);
        Assert.Equal(2, documents.UntitledCount);
        Assert.Same(second, documents.Active);
        Assert.Equal(1, documents.ActiveIndex);
        Assert.All(documents.Documents, d => Assert.True(d.IsUntitled));
        Assert.Null(first.Path);
    }

    [Fact]
    public void Open_ReusesAnUntouchedUntitledDocument()
    {
        var documents = new DocumentSet();
        SceneDocument placeholder = documents.New(SceneFactory.NewSceneModel(60));
        string path = Path.Combine(Path.GetTempPath(), "sloppyanimatsu-documents", "walk.akt");

        SceneDocument opened = documents.Open(SceneFactory.NewSceneModel(60), path);

        // The open reuses the untouched untitled document: a Welcome row opened into the
        // start-up New_1 left a single tab, and a.akt + an untouched New_2 + Open left two tabs
        // with the second tab renamed.
        Assert.Equal(1, documents.Count);
        Assert.Same(opened, documents.Active);
        Assert.DoesNotContain(placeholder, documents.Documents);
        Assert.Equal(0, documents.ActiveIndex);
        Assert.Equal("walk.akt", opened.Name);
        Assert.Equal(path, opened.Path);
        Assert.False(opened.IsUntitled);
        // A New scene consumed the counter; opening a file does not.
        Assert.Equal(1, documents.UntitledCount);
        // The opened scene carries the folder it came from, as the open writes it.
        Assert.Equal(ScenePaths.Folder(path), ScenePaths.Get(opened.Model, ScenePaths.SavePath));
        Assert.EndsWith("/", ScenePaths.Get(opened.Model, ScenePaths.SavePath));
    }

    [Fact]
    public void Open_AddsATabWhenTheActiveDocumentIsAFile()
    {
        var documents = new DocumentSet();
        documents.New(SceneFactory.NewSceneModel(60));
        string folder = Path.Combine(Path.GetTempPath(), "sloppyanimatsu-documents");
        SceneDocument first = documents.Open(SceneFactory.NewSceneModel(60), Path.Combine(folder, "a.akt"));

        SceneDocument second = documents.Open(SceneFactory.NewSceneModel(60), Path.Combine(folder, "b.akt"));

        // Row 18: a scene named on the command line plus one opened through the dialog showed two
        // tabs, the opened one active.
        Assert.Equal(2, documents.Count);
        Assert.Same(first, documents.Documents[0]);
        Assert.Same(second, documents.Active);
        Assert.Equal(1, documents.UntitledCount);
    }

    [Fact]
    public void Close_ActivatesTheTabToTheLeft()
    {
        var documents = new DocumentSet();
        SceneDocument first = documents.New(SceneFactory.NewSceneModel(60));
        SceneDocument second = documents.New(SceneFactory.NewSceneModel(60));
        SceneDocument third = documents.New(SceneFactory.NewSceneModel(60));
        Assert.Same(third, documents.Active);

        // The app activates the tab to the left of a closed one, or the first tab when the closed
        // one was first.
        Assert.Same(second, documents.Close(third));
        Assert.Same(first, documents.Close(second));
        Assert.Same(first, documents.Active);
        Assert.Null(documents.Close(first));
        Assert.Equal(0, documents.Count);
        Assert.Null(documents.Active);
        Assert.Equal(-1, documents.ActiveIndex);
    }

    [Fact]
    public void Close_OfTheMiddleTabActivatesTheOneBeforeIt()
    {
        var documents = new DocumentSet();
        SceneDocument first = documents.New(SceneFactory.NewSceneModel(60));
        SceneDocument second = documents.New(SceneFactory.NewSceneModel(60));
        SceneDocument third = documents.New(SceneFactory.NewSceneModel(60));
        Assert.True(documents.Activate(second));

        Assert.Same(first, documents.Close(second));
        Assert.Same(first, documents.Active);
        Assert.Equal(new[] { first, third }, documents.Documents);
    }

    [Fact]
    public void Close_OfTheFirstTabActivatesTheNext()
    {
        var documents = new DocumentSet();
        SceneDocument first = documents.New(SceneFactory.NewSceneModel(60));
        SceneDocument second = documents.New(SceneFactory.NewSceneModel(60));
        SceneDocument third = documents.New(SceneFactory.NewSceneModel(60));
        Assert.True(documents.Activate(first));

        Assert.Same(second, documents.Close(first));
        Assert.Equal(0, documents.ActiveIndex);
    }

    [Fact]
    public void CanClose_RefusesTheOnlyUntouchedUntitledScene()
    {
        var documents = new DocumentSet();
        SceneDocument lone = documents.New(SceneFactory.NewSceneModel(60));
        Assert.False(documents.CanClose(lone));

        // An edit makes it closable, and so does a second document (the app closed an untitled
        // New_2 when other tabs were open).
        lone.Dirty = true;
        Assert.True(documents.CanClose(lone));
        lone.Dirty = false;
        documents.New(SceneFactory.NewSceneModel(60));
        Assert.True(documents.CanClose(lone));

        var saved = new DocumentSet();
        SceneDocument file = saved.New(SceneFactory.NewSceneModel(60));
        file.Saved(Path.Combine(Path.GetTempPath(), "sloppyanimatsu-documents", "a.akt"));
        Assert.True(saved.CanClose(file));
    }

    [Fact]
    public void Close_OfABackgroundDocumentKeepsTheActiveOne()
    {
        var documents = new DocumentSet();
        SceneDocument first = documents.New(SceneFactory.NewSceneModel(60));
        SceneDocument second = documents.New(SceneFactory.NewSceneModel(60));
        SceneDocument third = documents.New(SceneFactory.NewSceneModel(60));

        Assert.Same(third, documents.Close(first));
        Assert.Same(third, documents.Active);
        Assert.Equal(new[] { second, third }, documents.Documents);
        Assert.Throws<ArgumentException>(() => documents.Close(first));
    }

    [Fact]
    public void New_AfterClosingAFileContinuesTheRunsCounter()
    {
        var documents = new DocumentSet();
        SceneDocument first = documents.New(SceneFactory.NewSceneModel(60));
        documents.Close(first);
        Assert.Equal("New_2", documents.New(SceneFactory.NewSceneModel(60)).Name);
    }

    [Fact]
    public void Saved_TakesTheFileNameAndFolderAndClearsTheMarker()
    {
        var documents = new DocumentSet();
        SceneDocument document = documents.New(SceneFactory.NewSceneModel(60));
        document.Dirty = true;
        string folder = Path.Combine(Path.GetTempPath(), "sloppyanimatsu-documents");
        string path = Path.Combine(folder, "walk.akt");

        document.Saved(path);

        Assert.Equal(path, document.Path);
        Assert.Equal("walk.akt", document.Name);
        Assert.False(document.Dirty);
        Assert.Equal("walk.akt", document.Title);
        Assert.Equal("walk", document.AutosaveBase);
        Assert.Equal(ScenePaths.Folder(path), ScenePaths.Get(document.Model, ScenePaths.SavePath));
        // Saving under a new name keeps the document's place in the set.
        Assert.Same(document, documents.Active);
        Assert.Equal(1, documents.Count);
    }

    [Fact]
    public void Title_ShowsTheModifiedMarker()
    {
        var documents = new DocumentSet();
        SceneDocument document = documents.New(SceneFactory.NewSceneModel(60));

        Assert.Equal("New_1", document.Title);
        document.Dirty = true;
        Assert.Equal("New_1 *", document.Title);
    }

    [Fact]
    public void AutosaveBase_IsTheFileNameOrTheUntitledName()
    {
        var documents = new DocumentSet();
        Assert.Equal("New_1", documents.New(SceneFactory.NewSceneModel(60)).AutosaveBase);

        SceneDocument opened = documents.Open(SceneFactory.NewSceneModel(60),
            Path.Combine(Path.GetTempPath(), "19_run.akt"));
        Assert.Equal("19_run", opened.AutosaveBase);
        // The two slots the app writes for it are built from that base name.
        string folder = Path.Combine(Path.GetTempPath(), "autosave");
        Assert.Equal(Path.Combine(folder, "19_run_autosave_01.slopa"),
            Autosave.SlotPath(folder, opened.AutosaveBase, 1));
    }

    [Fact]
    public void NextAndPrevious_WrapAroundTheOpenDocuments()
    {
        var documents = new DocumentSet();
        Assert.Null(documents.Next());
        Assert.Null(documents.Previous());

        SceneDocument first = documents.New(SceneFactory.NewSceneModel(60));
        SceneDocument second = documents.New(SceneFactory.NewSceneModel(60));
        Assert.Same(second, documents.Active);
        Assert.Same(first, documents.Next());
        Assert.Same(first, documents.Previous());
        Assert.True(documents.Activate(first));
        Assert.Same(second, documents.Next());
        Assert.True(documents.Activate(second));
        Assert.Same(first, documents.Previous());
        Assert.Null(documents.ActivateAt(2));
        Assert.Same(second, documents.ActivateAt(1));
    }

    [Fact]
    public void Folder_IsAbsoluteForwardSlashedWithOneTrailingSlash()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sloppyanimatsu-documents");
        string expected = folder.Replace('\\', '/') + "/";

        Assert.Equal(expected, ScenePaths.Folder(Path.Combine(folder, "walk.akt")));
        Assert.Equal(expected, ScenePaths.Folder(Path.Combine(folder, "nested", "walk.akt"))
            .Replace("nested/", ""));
        Assert.DoesNotContain('\\', ScenePaths.Folder(Path.Combine(folder, "walk.akt")));
        Assert.False(ScenePaths.Folder(Path.Combine(folder, "walk.akt")).EndsWith("//"));

        // A relative name resolves against the working directory, as the app stores an absolute folder.
        Assert.True(Path.IsPathRooted(ScenePaths.Folder("walk.akt")));
        Assert.Equal(ScenePaths.Folder(Path.Combine(folder, "walk.akt")),
            ScenePaths.Folder(Path.Combine(folder, "walk.akt")));
    }
}
