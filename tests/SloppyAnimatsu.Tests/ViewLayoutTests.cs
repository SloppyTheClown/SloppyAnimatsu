using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The viewport layouts: the four layouts, their camera runs,
/// the Layout/Camera commands and the L toggle.
/// </summary>
public class ViewLayoutTests
{
    [Fact]
    public void Layouts_HaveTheViewCountsAndSlotRuns()
    {
        Assert.Equal(4, ViewLayouts.Count);
        Assert.Equal(9, ViewLayouts.SlotCount);
        Assert.Equal(new[] { 1, 2, 2, 4 }, Enum.GetValues<SceneLayout>().Select(ViewLayouts.ViewCount));
        Assert.Equal(new[] { 0, 1, 3, 5 }, Enum.GetValues<SceneLayout>().Select(ViewLayouts.SlotOffset));
        Assert.Equal(new[] { 0, 0, 0, 1, 0, 1, 0, 4, 5 }, ViewLayouts.DefaultViewCamIndices);

        Assert.Equal("Single view", ViewLayouts.Name(SceneLayout.SingleView));
        Assert.Equal("Two views", ViewLayouts.Name(SceneLayout.TwoViews));
        Assert.Equal("Two views and curveboard", ViewLayouts.Name(SceneLayout.TwoViewsCurveboard));
        Assert.Equal("Four views", ViewLayouts.Name(SceneLayout.FourViews));

        Assert.True(ViewLayouts.TryParse("single", out var single));
        Assert.Equal(SceneLayout.SingleView, single);
        Assert.True(ViewLayouts.TryParse("2views", out var two));
        Assert.Equal(SceneLayout.TwoViews, two);
        Assert.True(ViewLayouts.TryParse("Two views and curveboard", out var curveboard));
        Assert.Equal(SceneLayout.TwoViewsCurveboard, curveboard);
        Assert.True(ViewLayouts.TryParse("3", out var four));
        Assert.Equal(SceneLayout.FourViews, four);
        Assert.False(ViewLayouts.TryParse("five", out _));
        Assert.False(ViewLayouts.TryParse("4", out _));
    }

    [Fact]
    public void NewScene_HasTheDefaultViewState()
    {
        var views = new SceneViews(SceneFactory.NewSceneModel(60).Scene);

        Assert.Equal(0, views.RawLayoutIndex);
        Assert.Equal(SceneLayout.SingleView, views.Layout);
        Assert.Equal(3, views.ToggledLayoutIndex);
        Assert.Equal(-1, views.ToggledLocalViewIndex);
        Assert.Equal(new[] { 0, 0, 0, 1, 0, 1, 0, 4, 5 }, views.CameraIndices);
        Assert.Equal(new[] { 0 }, views.Cameras(SceneLayout.SingleView));
        Assert.Equal(new[] { 0, 0 }, views.Cameras(SceneLayout.TwoViews));
        Assert.Equal(new[] { 1, 0 }, views.Cameras(SceneLayout.TwoViewsCurveboard));
        Assert.Equal(new[] { 1, 0, 4, 5 }, views.Cameras(SceneLayout.FourViews));
        Assert.Equal("Front", views.CameraName(1));
        Assert.Equal("Photo", views.CameraName(7));
        Assert.Null(views.CameraName(8));
    }

    [Fact]
    public void SetCamera_WritesOnlyThatLayoutsSlotAndReloads()
    {
        string first = TempFile("views-set");
        string second = TempFile("views-set");
        try
        {
            var model = SceneFactory.NewSceneModel();
            var views = new SceneViews(model.Scene);
            views.SetCamera(SceneLayout.SingleView, 0, 7);
            views.SetCamera(SceneLayout.TwoViews, 1, 6);
            views.SetCamera(SceneLayout.TwoViewsCurveboard, 0, 3);
            views.SetCamera(SceneLayout.FourViews, 2, 2);

            Assert.Equal(new[] { 7, 0, 6, 3, 0, 1, 0, 2, 5 }, views.CameraIndices.Select(v => v).ToArray());
            Assert.Equal(new[] { 7 }, views.Cameras(SceneLayout.SingleView));
            Assert.Equal(new[] { 0, 6 }, views.Cameras(SceneLayout.TwoViews));
            Assert.Equal(new[] { 3, 0 }, views.Cameras(SceneLayout.TwoViewsCurveboard));
            Assert.Equal(new[] { 1, 0, 2, 5 }, views.Cameras(SceneLayout.FourViews));

            model.Save(first);
            var reloaded = new SceneViews(TestFiles.LoadScene(first).Scene);
            Assert.Equal(views.CameraIndices, reloaded.CameraIndices);
            TestFiles.LoadScene(first).Save(second);
            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public void AnimatedCamera_IsAppendedToCameraListSoAViewCanStoreIt()
    {
        var model = SceneFactory.NewSceneModel();
        var character = model.Scene.Characters.First();
        var camera = CameraEditor.AddAnimatedCamera(character, "AnimCam");
        int before = model.Scene.Cameras.Count;

        Assert.Equal(-1, model.Scene.IndexOfCamera(camera));
        int index = model.Scene.EnsureCameraInList(camera);
        Assert.Equal(before, index);
        Assert.Equal(before + 1, model.Scene.Cameras.Count);
        Assert.Equal(camera.Source, model.Scene.Cameras[index].Source);
        Assert.IsType<CameraAnimated>(model.Scene.Cameras[index]);
        Assert.Equal(index, model.Scene.EnsureCameraInList(camera));

        var views = new SceneViews(model.Scene);
        views.SetCamera(SceneLayout.TwoViews, 1, index);
        Assert.Equal(before + 1, model.Scene.Cameras.Count);
        Assert.Equal("AnimCam", views.CameraName(index));
        Assert.IsType<CameraAnimated>(views.CameraAt(index));

        string path = TempFile("views-animated");
        try
        {
            model.Save(path);
            var reloaded = new SceneViews(TestFiles.LoadScene(path).Scene);
            Assert.Equal(index, reloaded.CameraIndex(SceneLayout.TwoViews, 1));
            Assert.Equal("AnimCam", reloaded.CameraName(index));
            Assert.IsType<CameraAnimated>(reloaded.CameraAt(index));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Change_ToTheSingleViewCarriesTheFocusedCamera()
    {
        var model = SceneFactory.NewSceneModel();
        var views = new SceneViews(model.Scene);
        views.Layout = SceneLayout.FourViews;
        views.SetCamera(SceneLayout.FourViews, 1, 6);

        views.Change(SceneLayout.SingleView, focusedView: 1);

        Assert.Equal(SceneLayout.SingleView, views.Layout);
        Assert.Equal(6, views.CameraIndex(SceneLayout.SingleView, 0));
        Assert.Equal(6, views.CameraIndex(SceneLayout.FourViews, 1)); // the layout keeps its own slot

        // Changing to a multi-view layout does not touch its slots.
        views.SetCamera(SceneLayout.TwoViews, 0, 2);
        views.Change(SceneLayout.TwoViews, focusedView: 0);
        Assert.Equal(SceneLayout.TwoViews, views.Layout);
        Assert.Equal(new[] { 2, 0 }, views.Cameras(SceneLayout.TwoViews));
    }

    [Fact]
    public void Toggle_FromTheSingleViewUsesTheToggledLayoutAndView()
    {
        var model = SceneFactory.NewSceneModel();
        var views = new SceneViews(model.Scene);
        views.ToggledLayoutIndex = (int)SceneLayout.TwoViews;
        views.ToggledLocalViewIndex = 1;
        views.SetCamera(SceneLayout.SingleView, 0, 1); // Front

        int focused = views.Toggle(focusedView: 0);

        Assert.Equal(SceneLayout.TwoViews, views.Layout);
        Assert.Equal(1, focused);
        Assert.Equal(new[] { 0, 1 }, views.Cameras(SceneLayout.TwoViews));
        Assert.Equal(1, views.CameraIndex(SceneLayout.SingleView, 0)); // the single view keeps its camera
        Assert.Equal((int)SceneLayout.TwoViews, views.ToggledLayoutIndex);
        Assert.Equal(1, views.ToggledLocalViewIndex);
    }

    [Fact]
    public void Toggle_FromFourViewsRemembersTheFocusAndReturnsToIt()
    {
        var model = SceneFactory.NewSceneModel();
        var views = new SceneViews(model.Scene);
        views.Layout = SceneLayout.FourViews;
        int left = views.CameraIndex(SceneLayout.FourViews, 2); // 4 = Left in the default layout

        int focused = views.Toggle(focusedView: 2);

        Assert.Equal(SceneLayout.SingleView, views.Layout);
        Assert.Equal(0, focused);
        Assert.Equal((int)SceneLayout.FourViews, views.ToggledLayoutIndex);
        Assert.Equal(2, views.ToggledLocalViewIndex);
        Assert.Equal(left, views.CameraIndex(SceneLayout.SingleView, 0));

        Assert.Equal(2, views.Toggle(focusedView: 0));
        Assert.Equal(SceneLayout.FourViews, views.Layout);
        Assert.Equal(left, views.CameraIndex(SceneLayout.FourViews, 2));
    }

    [Fact]
    public void Samples_HaveReadableViewStates()
    {
        foreach (string path in TestFiles.SceneSamples)
        {
            var scene = TestFiles.LoadScene(path).Scene;
            var views = new SceneViews(scene);
            Assert.InRange(views.RawLayoutIndex, 0, ViewLayouts.Count - 1);
            Assert.Equal(ViewLayouts.SlotCount, views.CameraIndices.Count);
            foreach (var layout in Enum.GetValues<SceneLayout>())
            {
                var cameras = views.Cameras(layout);
                Assert.Equal(ViewLayouts.ViewCount(layout), cameras.Count);
                Assert.All(cameras, c => Assert.InRange(c, 0, scene.Cameras.Count - 1));
            }
        }
    }

    [Fact]
    public void ViewGrid_SplitsTheViewportByLayout()
    {
        var single = ViewGrid.Rects(SceneLayout.SingleView);
        Assert.Equal(new[] { new ViewRect(0, 0, 1, 1) }, single);

        foreach (var layout in Enum.GetValues<SceneLayout>())
        {
            var rects = ViewGrid.Rects(layout);
            Assert.Equal(ViewLayouts.ViewCount(layout), rects.Count);
            Assert.Equal(1f, rects.Sum(r => r.Width * r.Height), 4);
            for (int i = 0; i < rects.Count; i++)
            {
                var (x, y, w, h) = rects[i].Scale(1000, 800);
                Assert.InRange(x + w, 0, 1000);
                Assert.InRange(y + h, 0, 800);
                for (int j = i + 1; j < rects.Count; j++)
                {
                    var (x2, y2, w2, h2) = rects[j].Scale(1000, 800);
                    Assert.True(x + w <= x2 || x2 + w2 <= x || y + h <= y2 || y2 + h2 <= y,
                        $"views {i} and {j} of {layout} overlap");
                }
            }
        }

        var four = ViewGrid.Rects(SceneLayout.FourViews);
        Assert.Equal(0, ViewGrid.ViewAt(four, 100, 100, 1000, 800));
        Assert.Equal(1, ViewGrid.ViewAt(four, 900, 100, 1000, 800));
        // The app fills the four-view grid clockwise, so view 2 is bottom right and view 3 is
        // bottom left.
        Assert.Equal(2, ViewGrid.ViewAt(four, 900, 700, 1000, 800));
        Assert.Equal(3, ViewGrid.ViewAt(four, 100, 700, 1000, 800));
        Assert.Equal(-1, ViewGrid.ViewAt([], 100, 100, 1000, 800));
    }

    [Fact]
    public void Renderer_FocusesAndAreasTheActiveView()
    {
        var renderer = new SceneRenderer();
        renderer.Views =
        [
            new RenderViewport(null, new ViewRect(0, 0, 0.5f, 1)),
            new RenderViewport(null, new ViewRect(0.5f, 0, 0.5f, 1)),
        ];
        renderer.ActiveViewIndex = 1;

        Assert.True(renderer.HasViews);
        Assert.Equal(1, renderer.ActiveViewIndex);
        Assert.Equal(new ViewRect(0.5f, 0, 0.5f, 1), renderer.ActiveRect);
        Assert.Same(renderer.Camera, renderer.ActiveView);
        Assert.True(renderer.ActiveIsOrbit);
        Assert.Equal(1, renderer.ViewAt(new System.Numerics.Vector2(900, 400), new System.Numerics.Vector2(1000, 800)));
        Assert.Equal(0, renderer.ViewAt(new System.Numerics.Vector2(100, 400), new System.Numerics.Vector2(1000, 800)));

        var area = renderer.RenderArea(new System.Numerics.Vector2(1000, 800));
        Assert.Equal(500, area.Origin.X, 3);
        Assert.Equal(500, area.Size.X, 3);

        renderer.ActiveViewIndex = 99;
        Assert.Equal(1, renderer.ActiveViewIndex);
    }

    private static string TempFile(string name) =>
        Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-{name}-{Guid.NewGuid():N}.akt");
}
