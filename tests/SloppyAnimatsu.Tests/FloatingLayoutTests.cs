using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The floating panels
/// the first arrangement the saved dock layout gives, dragging, folding to the title
/// bar, resizing, the stack order and the window resize - the geometry a window applies, without a
/// window.
/// </summary>
public class FloatingLayoutTests
{
    private static FloatingLayout Empty(string context) =>
        FloatingLayout.FromDock(DockLayout.Empty(context), PanelCatalogue.For(context), 1600, 1000);

    [Fact]
    public void Layout_PlacesTheAnimationPanelsFromTheSavedDock()
    {
        if (TestFiles.Preferences is not { } path) return;
        var layout = FloatingLayout.FromDock(DockLayout.For(Preferences.Load(path), "Animation"),
            PanelCatalogue.For("Animation"), 1600, 1000);

        var bank = layout.Panel(PanelKeys.Bank)!;
        Assert.Equal(PanelAnchor.Left, bank.Anchor);
        Assert.Equal(FloatingLayout.Margin, bank.X);
        Assert.Equal("CAnimBankDialog", bank.DialogClass);

        // The side panels are pinned to their edges, whichever column the file put them in.
        var scene = layout.Panel(PanelKeys.Scene)!;
        Assert.Equal(PanelAnchor.Left, scene.Anchor);
        Assert.True(scene.Y > bank.Y, "the hierarchy follows the bank's saved DockDown chain");
        var transform = layout.Panel(PanelKeys.Transform)!;
        Assert.Equal(PanelAnchor.Right, transform.Anchor);
        Assert.Equal(1600 - FloatingLayout.Margin - transform.Width, transform.X);
        Assert.Equal("CTransformDialog", transform.DialogClass);

        // The timeline is the panel across the bottom, behind the side panels.
        var timeline = layout.Panel(PanelKeys.Timeline)!;
        Assert.Equal(PanelAnchor.Bottom, timeline.Anchor);
        Assert.Equal(FloatingLayout.BottomX(1600), timeline.X);
        Assert.Equal(FloatingLayout.BottomWidth(1600), timeline.Width);
        Assert.Equal(1000 - FloatingLayout.Margin - timeline.OuterHeight, timeline.Y);
        Assert.Equal(0, timeline.Order);

        // Every panel's title bar is reachable in the window.
        Assert.All(layout.Panels, state =>
        {
            Assert.InRange(state.Y, 0, 1000 - FloatingLayout.HeaderHeight);
            Assert.True(state.X + state.Width > 0 && state.X < 1600, $"{state} is outside the window");
        });
    }

    [Fact]
    public void Layout_PlacesTheRiggingPanelsAndTheCharacterBank()
    {
        if (TestFiles.Preferences is not { } path) return;
        var layout = FloatingLayout.FromDock(DockLayout.For(Preferences.Load(path), "Rigging"),
            PanelCatalogue.For("Rigging"), 1600, 1000);

        Assert.Equal("CCharacterBankDialog", layout.Panel(PanelKeys.Bank)!.DialogClass);
        Assert.Equal("CSkinWorkshopDialog", layout.Panel(PanelKeys.SkinWorkshop)!.DialogClass);
        Assert.Null(layout.Panel(PanelKeys.Mixer));
        Assert.Equal(PanelAnchor.Bottom, layout.Panel(PanelKeys.Timeline)!.Anchor);
        // The rigging context's file has no bottom panel, so the timeline is laid out across the
        // bottom from the catalogue's fallback.
        Assert.Equal(1000 - FloatingLayout.Margin - layout.Panel(PanelKeys.Timeline)!.OuterHeight,
            layout.Panel(PanelKeys.Timeline)!.Y);
    }

    [Fact]
    public void Layout_StacksTheCatalogueWhenTheFileNamesNoPanel()
    {
        var layout = Empty("Rigging");

        var bank = layout.Panel(PanelKeys.Bank)!;
        Assert.Equal(FloatingLayout.Margin, bank.X);
        Assert.Equal(FloatingLayout.Margin, bank.Y);
        Assert.Equal(bank.Spec.Width, bank.Width);
        var scene = layout.Panel(PanelKeys.Scene)!;
        // A column is a chain: each panel rests against the bottom of the one above it.
        Assert.Equal(bank.Bottom, scene.Y);
        Assert.Same(bank, scene.Parent);
        var properties = layout.Panel(PanelKeys.Properties)!;
        Assert.Equal(scene.Bottom, properties.Y);

        var transform = layout.Panel(PanelKeys.Transform)!;
        Assert.Equal(PanelAnchor.Right, transform.Anchor);
        Assert.Equal(1600 - FloatingLayout.Margin - transform.Width, transform.X);

        var timeline = layout.Panel(PanelKeys.Timeline)!;
        Assert.Equal(FloatingLayout.BottomWidth(1600), timeline.Width);
        Assert.Equal(1000 - FloatingLayout.Margin - timeline.Height, timeline.Y);

        // All eleven rigging panels (MATERIAL included) are present, and none is off-screen.
        Assert.Equal(11, layout.Panels.Count);
        Assert.All(layout.Panels, state =>
        {
            Assert.True(state.X >= -state.Width + 60, $"{state.Key} X={state.X}");
            Assert.True(state.Y >= 0 && state.Y <= 1000 - FloatingLayout.HeaderHeight, $"{state.Key} Y={state.Y} H={state.Height}");
        });
    }

    [Fact]
    public void Layout_MovesAPanelAndKeepsItsTitleBarReachable()
    {
        var layout = Empty("Rigging");
        var bank = layout.Panel(PanelKeys.Bank)!;

        Assert.True(layout.Move(PanelKeys.Bank, 640, 300));
        Assert.Equal(PanelAnchor.Free, bank.Anchor);
        Assert.Equal(640, bank.X);
        Assert.Equal(300, bank.Y);

        // Dragged past the right and bottom edges: it snaps to the right side as a column there,
        // and its title bar stays above the bottom edge (a panel may run below the window, never
        // past a side).
        layout.Move(PanelKeys.Bank, 5000, 5000);
        Assert.Equal(PanelAnchor.Right, bank.Anchor);
        Assert.Equal(1600 - FloatingLayout.Margin - bank.Width, bank.X);
        Assert.True(bank.Y <= 1000 - FloatingLayout.HeaderHeight);

        // Never above the window or past the left edge, where it snaps to the left side.
        layout.Move(PanelKeys.Bank, -5000, -5000);
        Assert.Equal(PanelAnchor.Left, bank.Anchor);
        Assert.Equal(FloatingLayout.Margin, bank.X);
        Assert.True(bank.Y >= 0);
    }

    [Fact]
    public void Layout_FoldsAPanelToItsTitleBarAndBack()
    {
        var layout = Empty("Rigging");
        var bank = layout.Panel(PanelKeys.Bank)!;
        double height = bank.Height;

        Assert.True(layout.Toggle(PanelKeys.Bank));
        Assert.True(bank.Collapsed);
        Assert.Equal(FloatingLayout.HeaderHeight, bank.OuterHeight);
        Assert.Equal(height, bank.Height); // the open height is kept

        Assert.False(layout.Toggle(PanelKeys.Bank));
        Assert.False(bank.Collapsed);
        Assert.Equal(height, bank.OuterHeight);

        // A folded bottom panel becomes a strip at the bottom.
        var timeline = layout.Panel(PanelKeys.Timeline)!;
        layout.Toggle(PanelKeys.Timeline);
        Assert.Equal(1000 - FloatingLayout.Margin - FloatingLayout.HeaderHeight, timeline.Y);
    }

    [Fact]
    public void Layout_ResizesAPanelWithinTheWindow()
    {
        var layout = Empty("Rigging");
        var bank = layout.Panel(PanelKeys.Bank)!;

        layout.Resize(PanelKeys.Bank, 900, 400);
        Assert.Equal(900, bank.Width);
        Assert.Equal(400, bank.Height);

        layout.Resize(PanelKeys.Bank, 10, 10);
        Assert.Equal(FloatingLayout.MinimumWidth, bank.Width);
        Assert.Equal(FloatingLayout.MinimumHeight, bank.Height);

        layout.Resize(PanelKeys.Bank, 5000, 5000);
        Assert.Equal(1600 - 2 * FloatingLayout.Margin, bank.Width);
        Assert.Equal(1000 - FloatingLayout.Margin, bank.Height);
    }

    [Fact]
    public void Layout_SnapsAResizedPanelToTheWidthOfItsChain()
    {
        var layout = Empty("Rigging");
        var bank = layout.Panel(PanelKeys.Bank)!;
        var scene = layout.Panel(PanelKeys.Scene)!;
        Assert.Same(bank, scene.Parent);

        layout.Resize(PanelKeys.Bank, scene.Width + FloatingLayout.SnapDistance - 2, 300);
        Assert.Equal(scene.Width, bank.Width);
        layout.Resize(PanelKeys.Bank, scene.Width + FloatingLayout.SnapDistance + 40, 300);
        Assert.Equal(scene.Width + FloatingLayout.SnapDistance + 40, bank.Width);
    }

    [Fact]
    public void BottomPanel_CoversMostOfTheWidthCentred()
    {
        Assert.Equal(Math.Round((1600 - 2 * FloatingLayout.Margin) * FloatingLayout.BottomWidthFraction), FloatingLayout.BottomWidth(1600));
        Assert.Equal((1600 - FloatingLayout.BottomWidth(1600)) / 2, FloatingLayout.BottomX(1600), 0.5);
        Assert.Equal(FloatingLayout.MinimumWidth, FloatingLayout.BottomWidth(100));
        Assert.True(FloatingLayout.BottomX(100) >= FloatingLayout.Margin);
    }

    [Fact]
    public void Layout_BringsAPressedPanelToTheFront()
    {
        var layout = Empty("Rigging");
        var scene = layout.Panel(PanelKeys.Scene)!;
        int before = scene.Order;

        layout.BringToFront(PanelKeys.Scene);
        Assert.True(scene.Order > before);
        Assert.All(layout.Panels.Where(state => state.Key != PanelKeys.Scene),
            state => Assert.True(state.Order < scene.Order));
    }

    [Fact]
    public void Layout_ArrangesPinnedPanelsForANewWindowSize()
    {
        var layout = Empty("Rigging");
        var bank = layout.Panel(PanelKeys.Bank)!;
        var transform = layout.Panel(PanelKeys.Transform)!;
        var timeline = layout.Panel(PanelKeys.Timeline)!;

        layout.Arrange(1200, 700);
        Assert.Equal(FloatingLayout.Margin, bank.X);
        Assert.Equal(1200 - FloatingLayout.Margin - transform.Width, transform.X);
        Assert.Equal(FloatingLayout.BottomWidth(1200), timeline.Width);
        Assert.Equal(700 - FloatingLayout.Margin - timeline.OuterHeight, timeline.Y);

        // A panel the user dragged keeps its place when the window changes size.
        layout.Move(PanelKeys.Bank, 500, 200);
        layout.Arrange(1200, 700);
        Assert.Equal(500, bank.X);
        Assert.Equal(200, bank.Y);
    }

    [Fact]
    public void Layout_KeepsTheSideColumnsAboveTheBottomPanelWhenTheWindowShrinks()
    {
        if (TestFiles.Preferences is not { } path) return;
        var layout = FloatingLayout.FromDock(DockLayout.For(Preferences.Load(path), "Rigging"),
            PanelCatalogue.For("Rigging"), 1600, 1000);
        Assert.True(layout.Panel(PanelKeys.Properties)!.Bottom <=
                    layout.Panel(PanelKeys.Timeline)!.Y - FloatingLayout.Gap + 0.01);

        // A shorter window moves the bottom panel up; the saved columns are slid up with it instead of
        // running behind it, as far as the top margin (a column taller than the room left - the
        // three-panel right columns of Rigging - then runs past it, as the may).
        layout.Arrange(1600, 900);
        var timeline = layout.Panel(PanelKeys.Timeline)!;
        Assert.All(layout.Panels.Where(state => state.Anchor is PanelAnchor.Left or PanelAnchor.Right),
            state =>
            {
                var head = state;
                while (head.Parent != null) head = head.Parent;
                Assert.True(state.Bottom <= timeline.Y - FloatingLayout.Gap + 0.01 || head.Y <= FloatingLayout.Margin,
                    $"{state} overlaps the timeline at {timeline.Y} and its column is not at the top");
            });
    }

    [Fact]
    public void Layout_RestoresAnUndockedAndFoldedPanelFromTheSavedEntry()
    {
        if (TestFiles.Preferences is not { } path) return;
        var preferences = Preferences.Load(path);

        // The sample docks the Transform under the Stacker; a drag clears that link and writes the
        // place (the drag does the same, open-questions 28).
        var dock = DockLayout.For(preferences, "Animation");
        Assert.True(dock.SetFloating("CTransformDialog", 700, 500, 170, 1600, 1000));
        Assert.True(dock.SetFolded("CMixerDialog", true));

        var layout = FloatingLayout.FromDock(DockLayout.For(preferences, "Animation"),
            PanelCatalogue.For("Animation"), 1600, 1000);
        var transform = layout.Panel(PanelKeys.Transform)!;
        Assert.Equal(PanelAnchor.Free, transform.Anchor);
        Assert.InRange(transform.X, 699, 701);
        Assert.InRange(transform.Y, 499, 501);

        // A folded panel opens as its title bar alone and keeps the height it unfolds to.
        var mixer = layout.Panel(PanelKeys.Mixer)!;
        Assert.True(mixer.Collapsed);
        Assert.Equal(FloatingLayout.HeaderHeight, mixer.OuterHeight);
        Assert.True(mixer.Height > FloatingLayout.HeaderHeight);
    }

    [Fact]
    public void Layout_MapsTheBankToTheContextsDialogClass()
    {
        Assert.Equal(new[] { "CAnimBankDialog", "CCharacterBankDialog" }, PanelCatalogue.For("Animation")[0].Dialogs);
        Assert.Contains(PanelCatalogue.For("Rigging"), spec => spec.Key == PanelKeys.SkinWorkshop);
        Assert.DoesNotContain(PanelCatalogue.For("Rigging"), spec => spec.Key == PanelKeys.Mixer);
        Assert.DoesNotContain(PanelCatalogue.For("Animation"), spec => spec.Key == PanelKeys.SkinWorkshop);
        // The Blendshapes panel belongs to both contexts (setup in Rigging, values in Animation).
        Assert.Contains(PanelCatalogue.For("Rigging"), spec => spec.Key == PanelKeys.Blendshapes);
        Assert.DoesNotContain(PanelCatalogue.For("Animation"), spec => spec.Key == PanelKeys.Blendshapes);
        Assert.Contains(PanelCatalogue.For("Animation"), spec => spec.Key == PanelKeys.Stacker);
        Assert.Contains(PanelCatalogue.For("Animation"), spec => spec.Key == PanelKeys.Cyclemaker);
        Assert.DoesNotContain(PanelCatalogue.For("Rigging"), spec => spec.Key == PanelKeys.Stacker);
        Assert.DoesNotContain(PanelCatalogue.For("Rigging"), spec => spec.Key == PanelKeys.Cyclemaker);
    }
}
