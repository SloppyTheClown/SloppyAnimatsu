using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The default panel arrangement and how panels behave together:
/// the built-in presets, a panel hanging under the one it is dropped against, the window resize
/// that keeps every panel inside the top and sides and brings it home again, writing the
/// arrangement back to the saved layout, and the generated preferences file.
/// </summary>
public class PanelChainTests
{
    private static FloatingLayout Preset(string context, LayoutPreset preset = LayoutPreset.Default,
        int width = 1600, int height = 1200) =>
        FloatingLayout.FromPreset(preset, context, PanelCatalogue.For(context), width, height);

    private static void AssertNoSidePanelsOverlap(FloatingLayout layout)
    {
        var side = layout.Panels.Where(p => p.Anchor != PanelAnchor.Bottom).ToList();
        for (int i = 0; i < side.Count; i++)
        for (int j = i + 1; j < side.Count; j++)
        {
            var a = side[i];
            var b = side[j];
            bool overlap = a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Bottom && b.Y < a.Bottom;
            Assert.False(overlap, $"{a} overlaps {b}");
        }
    }

    private static void AssertInsideTopAndSides(FloatingLayout layout)
    {
        Assert.All(layout.Panels, p =>
        {
            Assert.True(p.Y >= 0, $"{p} is above the window");
            Assert.True(p.X >= 0, $"{p} is left of the window");
            Assert.True(p.X + p.Width <= layout.WindowWidth, $"{p} is right of the window");
        });
    }

    [Theory]
    [InlineData("Rigging", LayoutPreset.Default)]
    [InlineData("Animation", LayoutPreset.Default)]
    [InlineData("Rigging", LayoutPreset.Folded)]
    [InlineData("Animation", LayoutPreset.Folded)]
    [InlineData("Rigging", LayoutPreset.RightColumn)]
    [InlineData("Animation", LayoutPreset.RightColumn)]
    public void Preset_PlacesEveryPanelWithoutOverlap(string context, LayoutPreset preset)
    {
        var layout = Preset(context, preset, 1982, 1076);

        Assert.Equal(PanelCatalogue.For(context).Count, layout.Panels.Count);
        AssertNoSidePanelsOverlap(layout);
        AssertInsideTopAndSides(layout);
        if (preset == LayoutPreset.Folded)
            Assert.All(layout.Panels.Where(p => p.Anchor != PanelAnchor.Bottom), p => Assert.True(p.Collapsed));
        if (preset == LayoutPreset.RightColumn)
            Assert.All(layout.Panels.Where(p => p.Anchor != PanelAnchor.Bottom),
                p => Assert.Equal(PanelAnchor.Right, p.Anchor));
    }

    [Fact]
    public void Preset_DefaultFollowsTheDefaultColumns()
    {
        var rigging = Preset("Rigging", width: 1982, height: 1076);
        var bank = rigging.Panel(PanelKeys.Bank)!;
        var scene = rigging.Panel(PanelKeys.Scene)!;
        var properties = rigging.Panel(PanelKeys.Properties)!;
        // Left column: bank, tree, properties, each hanging under the one above.
        Assert.Equal(PanelAnchor.Left, bank.Anchor);
        Assert.Null(bank.Parent);
        Assert.Same(bank, scene.Parent);
        Assert.Same(scene, properties.Parent);
        Assert.Equal(bank.Bottom, scene.Y);
        Assert.Equal(scene.Bottom, properties.Y);

        // Two right columns: the transform column against the edge, the skin column inside it.
        var transform = rigging.Panel(PanelKeys.Transform)!;
        var skin = rigging.Panel(PanelKeys.SkinWorkshop)!;
        var blendshapes = rigging.Panel(PanelKeys.Blendshapes)!;
        Assert.Equal(1982 - FloatingLayout.Margin, transform.X + transform.Width);
        Assert.True(skin.X + skin.Width <= transform.X, "the skin column is left of the transform column");
        Assert.Same(skin, blendshapes.Parent);
        Assert.Same(transform, rigging.Panel(PanelKeys.Picker)!.Parent);

        var animation = Preset("Animation", width: 1982, height: 1076);
        Assert.Equal(PanelAnchor.Bottom, animation.Panel(PanelKeys.Timeline)!.Anchor);
        Assert.Same(animation.Panel(PanelKeys.Mixer), animation.Panel(PanelKeys.Picker)!.Parent);
        Assert.Same(animation.Panel(PanelKeys.Picker), animation.Panel(PanelKeys.Cyclemaker)!.Parent);
        Assert.Same(animation.Panel(PanelKeys.Stacker), animation.Panel(PanelKeys.Transform)!.Parent);
        Assert.Equal(1982 - FloatingLayout.Margin, animation.Panel(PanelKeys.Stacker)!.X + animation.Panel(PanelKeys.Stacker)!.Width);
    }

    [Fact]
    public void Move_TakesThePanelsHangingBelowAndLeavesTheOneAbove()
    {
        var layout = Preset("Rigging");
        var bank = layout.Panel(PanelKeys.Bank)!;
        var scene = layout.Panel(PanelKeys.Scene)!;
        var properties = layout.Panel(PanelKeys.Properties)!;
        double bankX = bank.X, bankY = bank.Y;

        // Dragging the middle panel takes what hangs under it and detaches from the one above.
        Assert.True(layout.Move(PanelKeys.Scene, 600, 200));
        Assert.Equal(600, scene.X);
        Assert.Equal(200, scene.Y);
        Assert.Null(scene.Parent);
        Assert.Same(scene, properties.Parent);
        Assert.Equal(scene.X, properties.X);
        Assert.Equal(scene.Bottom, properties.Y);
        Assert.Equal(bankX, bank.X);
        Assert.Equal(bankY, bank.Y);
        Assert.Equal(new[] { properties }, layout.Below(scene));

        // Dragging the head takes the whole chain.
        Assert.True(layout.Move(PanelKeys.Scene, 640, 260));
        Assert.Equal(640, properties.X);
        Assert.Equal(scene.Bottom, properties.Y);
    }

    [Fact]
    public void Move_SnapsAPanelDroppedAgainstTheBottomOfAnother()
    {
        var layout = Preset("Rigging");
        var bank = layout.Panel(PanelKeys.Bank)!;
        var blendshapes = layout.Panel(PanelKeys.Blendshapes)!;
        var skin = layout.Panel(PanelKeys.SkinWorkshop)!;
        layout.Move(PanelKeys.Scene, 600, 200);   // frees the space under the bank

        // Dropped within the snap distance of the bank's bottom edge: it hangs under the bank.
        layout.Move(PanelKeys.Blendshapes, bank.X + 5, bank.Bottom + FloatingLayout.SnapDistance - 2);
        Assert.Same(bank, blendshapes.Parent);
        Assert.Equal(bank.X, blendshapes.X);
        Assert.Equal(bank.Bottom, blendshapes.Y);
        Assert.Empty(layout.Below(skin));

        // It now follows the bank.
        layout.Move(PanelKeys.Bank, 300, 150);
        Assert.Equal(300, blendshapes.X);
        Assert.Equal(bank.Bottom, blendshapes.Y);

        // Dropped well away from every panel it stays alone.
        layout.Move(PanelKeys.Blendshapes, 900, 500);
        Assert.Null(blendshapes.Parent);
        Assert.Empty(layout.Below(bank));
    }

    [Fact]
    public void Move_SnapsAPanelToTheWindowsSidesAndTheTimelineBackToTheBottom()
    {
        var layout = Preset("Animation");
        var mixer = layout.Panel(PanelKeys.Mixer)!;
        var timeline = layout.Panel(PanelKeys.Timeline)!;
        var home = (timeline.X, timeline.Y, timeline.Width);

        // Well inside the window a panel stays where it is dropped.
        layout.Move(PanelKeys.Mixer, 700, 300);
        Assert.Equal(PanelAnchor.Free, mixer.Anchor);
        Assert.Equal(700, mixer.X);

        // Dropped within the snap distance of the left edge it becomes a left column.
        layout.Move(PanelKeys.Mixer, FloatingLayout.Margin + FloatingLayout.SnapDistance - 1, 500);
        if (mixer.Parent == null)
        {
            Assert.Equal(PanelAnchor.Left, mixer.Anchor);
            Assert.Equal(FloatingLayout.Margin, mixer.X);
        }

        // And the same against the right edge, where it follows the edge when the window grows.
        layout.Move(PanelKeys.Mixer, 700, 300);
        layout.Move(PanelKeys.Mixer, 1600 - mixer.Width - FloatingLayout.Margin - 3, 300);
        if (mixer.Parent == null)
        {
            Assert.Equal(PanelAnchor.Right, mixer.Anchor);
            Assert.Equal(1600 - FloatingLayout.Margin - mixer.Width, mixer.X);
            layout.Arrange(1800, 1200);
            Assert.Equal(1800 - FloatingLayout.Margin - mixer.Width, mixer.X);
            layout.Arrange(1600, 1200);
        }

        // The timeline dragged away floats; dropped with its foot near the bottom it is the bottom panel again.
        layout.Move(PanelKeys.Timeline, 300, 200);
        Assert.Equal(PanelAnchor.Free, timeline.Anchor);
        layout.Move(PanelKeys.Timeline, 300, 1200 - timeline.OuterHeight - 10);
        Assert.Equal(PanelAnchor.Bottom, timeline.Anchor);
        Assert.Equal(home.X, timeline.X, 0.5);
        Assert.Equal(home.Y, timeline.Y, 0.5);
        Assert.Equal(home.Width, timeline.Width, 0.5);
    }

    [Theory]
    [InlineData("Rigging")]
    public void WriteTo_SavesTheBlendshapesPanelEvenWhenTheProfileHadNoEntryForIt(string context)
    {
        if (TestFiles.Preferences is not { } path) return;
        var preferences = Preferences.Load(path);
        var specs = PanelCatalogue.For(context);
        var layout = FloatingLayout.FromPreset(LayoutPreset.Default, context, specs, 1600, 1200,
            DockLayout.For(preferences, context));
        layout.Move(PanelKeys.Blendshapes, 640, 260);   // on its own, away from every panel and edge
        layout.WriteTo(DockLayout.For(preferences, context));

        // Through the file as well: the entry is a real CDialogPosition of that context.
        string folder = Path.Combine(Path.GetTempPath(), "sloppyanimatsu-face-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string saved = Path.Combine(folder, "preferences.akt");
            preferences.SaveAs(saved);
            var read = FloatingLayout.FromDock(DockLayout.For(Preferences.Load(saved), context), specs, 1600, 1200);
            var face = read.Panel(PanelKeys.Blendshapes)!;
            Assert.Equal("CFaceDialog", face.DialogClass);
            Assert.Null(face.Parent);
            Assert.True(Math.Abs(face.X - 640) <= 1.5 && Math.Abs(face.Y - 260) <= 1.5, $"{context}: read back {face}");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Arrange_KeepsPanelsInsideAndBringsThemHome()
    {
        var layout = Preset("Rigging");
        layout.Move(PanelKeys.SkinWorkshop, 700, 420);   // a dragged chain as well as the pinned ones
        var home = layout.Panels.ToDictionary(p => p.Key, p => (p.X, p.Y));

        // Narrower: nothing leaves the sides. Shorter: panels move up, never above the top.
        layout.Arrange(700, 1200);
        AssertInsideTopAndSides(layout);
        layout.Arrange(1600, 400);
        AssertInsideTopAndSides(layout);
        var skin = layout.Panel(PanelKeys.SkinWorkshop)!;
        Assert.True(skin.Y < 420, "a shorter window moves the panel up");
        layout.Arrange(500, 300);
        AssertInsideTopAndSides(layout);
        // A chain stays a chain while it is squeezed.
        Assert.Equal(skin.Bottom, layout.Panel(PanelKeys.Blendshapes)!.Y);

        // Back at the first size every panel is where it started.
        layout.Arrange(1600, 1200);
        Assert.All(layout.Panels, p =>
        {
            Assert.Equal(home[p.Key].X, p.X, 0.5);
            Assert.Equal(home[p.Key].Y, p.Y, 0.5);
        });
    }

    [Theory]
    [InlineData("Rigging")]
    [InlineData("Animation")]
    public void WriteTo_RoundTripsTheArrangementThroughTheSavedLayout(string context)
    {
        if (TestFiles.Preferences is not { } path) return;
        var preferences = Preferences.Load(path);
        var dock = DockLayout.For(preferences, context);
        var specs = PanelCatalogue.For(context);
        var layout = FloatingLayout.FromPreset(LayoutPreset.Default, context, specs, 1600, 1200, dock);
        layout.Move(PanelKeys.Scene, 600, 200);   // a floating chain: the tree with the properties under it
        layout.WriteTo(dock);

        var read = FloatingLayout.FromDock(DockLayout.For(preferences, context), specs, 1600, 1200);
        foreach (var expected in layout.Panels.Where(p => p.DialogClass != null))
        {
            var actual = read.Panel(expected.Key)!;
            Assert.True(Math.Abs(expected.X - actual.X) <= 1.5 && Math.Abs(expected.Y - actual.Y) <= 1.5,
                $"{context}: expected {expected}, read back {actual}");
            Assert.Equal(expected.Parent?.Key, actual.Parent?.Key);
        }
        AssertNoSidePanelsOverlap(read);
    }

    [Fact]
    public void CreateDefault_WritesAPreferencesFileWithTheDefaultLayout()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sloppyanimatsu-prefs-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "preferences.slopa");
        try
        {
            var created = Preferences.CreateDefault(path);
            Assert.True(File.Exists(path));
            Assert.Empty(created.RecentFiles);

            // The file reads back, carries nothing personal and places every panel.
            var loaded = Preferences.Load(path);
            Assert.Empty(loaded.RecentFiles);
            // The members kept only for akeytsu's layout are left out of a generated file.
            var general = loaded.Document.Roots.Single(o => o.ClassKey == "CGeneralPreferences");
            Assert.All(Preferences.HiddenMembers, member => Assert.False(general.Has(member)));
            foreach (string context in new[] { "Rigging", "Animation" })
            {
                var dock = DockLayout.For(loaded, context);
                Assert.NotEmpty(dock.Columns);
                var layout = FloatingLayout.FromDock(dock, PanelCatalogue.For(context),
                    Preferences.DefaultLayoutWidth, Preferences.DefaultLayoutHeight);
                AssertNoSidePanelsOverlap(layout);
                AssertInsideTopAndSides(layout);
            }
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
