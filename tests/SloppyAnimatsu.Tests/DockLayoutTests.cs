using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The saved dock layout of preferences.akt
/// the columns and DockDown chains of both interface
/// contexts, the rectangles they resolve to, and writing a new height back.
/// </summary>
public class DockLayoutTests
{
    [Fact]
    public void Layout_ReadsTheRiggingColumns()
    {
        if (TestFiles.Preferences is not { } path) return;
        var layout = DockLayout.For(Preferences.Load(path), "Rigging");

        Assert.Equal("Rigging", layout.Context);
        Assert.Equal(3, layout.Columns.Count);

        var left = layout.Columns[0];
        Assert.Equal(DockAnchor.Left, left.Anchor);
        Assert.Equal(new[] { "CCharacterBankDialog", "CHierarchyDialog", "CPropertiesDialog" },
            left.Panels.Select(p => p.DialogClass));
        Assert.Equal(new[] { 173, 355, 150 }, left.Panels.Select(p => p.Height));
        Assert.Equal(0.123f, left.Panels[0].Top, 3);
        Assert.True(left.Panels.All(p => p.HasStoredHeight));
        Assert.Equal(0, left.Width); // the file stores no column width

        // The inner right column comes before the one at the right edge.
        var innerRight = layout.Columns[1];
        Assert.Equal(DockAnchor.Right, innerRight.Anchor);
        Assert.Equal(2, innerRight.Distance);
        Assert.Equal(new[] { "CPainterDialog", "CSkinWorkshopDialog", "CFaceDialog" },
            innerRight.Panels.Select(p => p.DialogClass));
        Assert.Equal(new[] { 0, 373, 235 }, innerRight.Panels.Select(p => p.Height));
        Assert.Equal("CSkinWorkshopDialog", innerRight.Panels[0].Down);

        var right = layout.Columns[2];
        Assert.Equal(1, right.Distance);
        Assert.Equal(new[] { "CSkeletonDialog", "CTransformDialog", "CPickerDialog" },
            right.Panels.Select(p => p.DialogClass));
        Assert.Equal(new[] { 0, 350, 273 }, right.Panels.Select(p => p.Height));
        Assert.False(right.Panels[0].HasStoredHeight);

        Assert.Null(layout.Bottom);
        Assert.NotNull(layout.Panel("CTransformDialog"));
        Assert.Null(layout.Panel("CAKNMImportDialog"));
    }

    [Fact]
    public void Layout_ReadsTheAnimationColumnsAndTheTimeline()
    {
        if (TestFiles.Preferences is not { } path) return;
        var layout = DockLayout.For(Preferences.Load(path), "Animation");

        Assert.Equal(3, layout.Columns.Count);
        Assert.Equal(new[] { "CAnimBankDialog", "CHierarchyDialog", "CPropertiesDialog" },
            layout.Columns[0].Panels.Select(p => p.DialogClass));
        Assert.Equal(new[] { 300, 320, 159 }, layout.Columns[0].Panels.Select(p => p.Height));

        Assert.Equal(new[] { "CMixerDialog", "CPickerDialog", "CCycleDialog" },
            layout.Columns[1].Panels.Select(p => p.DialogClass));
        Assert.Equal(2, layout.Columns[1].Distance);

        // The Stacker is the column at the right screen edge, the Transform docked below it.
        Assert.Equal(new[] { "CStackerDialog", "CTransformDialog" },
            layout.Columns[2].Panels.Select(p => p.DialogClass));
        Assert.Equal(1, layout.Columns[2].Distance);
        Assert.Equal(136, layout.Columns[2].Panels[0].Height);

        Assert.NotNull(layout.Bottom);
        Assert.Equal("CTimelineDialog", layout.Bottom!.DialogClass);
        Assert.Equal(0.838f, layout.Bottom.Top, 3);
        Assert.Equal(0, layout.Bottom.Height);
    }

    [Fact]
    public void Layout_ResolvesPanelsInsideTheWindow()
    {
        if (TestFiles.Preferences is not { } path) return;
        var layout = DockLayout.For(Preferences.Load(path), "Rigging");
        var rects = layout.Resolve(2560, 1439);
        var byClass = rects.ToDictionary(r => r.DialogClass);

        var bank = byClass["CCharacterBankDialog"];
        Assert.Equal(0, bank.X);
        Assert.InRange(bank.Y, 170, 185); // PosY x window height
        Assert.Equal(DockLayout.DefaultWidth, bank.Width);
        Assert.Equal(173 + DockLayout.HeaderHeight, bank.Height);

        // The chain stacks: the hierarchy starts after the bank and its own title bar.
        var hierarchy = byClass["CHierarchyDialog"];
        Assert.Equal(bank.Bottom, hierarchy.Y);
        Assert.Equal(355 + DockLayout.HeaderHeight, hierarchy.Height);

        var properties = byClass["CPropertiesDialog"];
        Assert.Equal(hierarchy.Bottom, properties.Y);
        Assert.Equal(150 + DockLayout.HeaderHeight, properties.Height);

        // The right columns: the edge column is the rightmost, the inner one to its left.
        var skeleton = byClass["CSkeletonDialog"];
        var transform = byClass["CTransformDialog"];
        var painter = byClass["CPainterDialog"];
        Assert.Equal(2560 - DockLayout.DefaultWidth, skeleton.X);
        Assert.Equal(2560 - 2 * DockLayout.DefaultWidth, painter.X);
        Assert.Equal(skeleton.X, transform.X);
        Assert.Equal(350 + DockLayout.HeaderHeight, transform.Height);

        // Every rectangle is inside the window and the side columns stop above the bottom panel.
        Assert.All(rects, r =>
        {
            Assert.True(r.X >= 0 && r.Y >= 0);
            Assert.True(r.Right <= 2560);
            Assert.True(r.Bottom <= 1439);
        });
    }

    [Fact]
    public void Layout_ResolvesTheTimelineAcrossTheBottom()
    {
        if (TestFiles.Preferences is not { } path) return;
        var layout = DockLayout.For(Preferences.Load(path), "Animation");
        var rects = layout.Resolve(1600, 900);
        var timeline = Assert.Single(rects, r => r.DialogClass == "CTimelineDialog");
        int top = (int)MathF.Round(0.838f * 900);
        Assert.Equal(0, timeline.X);
        Assert.Equal(1600, timeline.Width);
        Assert.Equal(top, timeline.Y);
        Assert.Equal(900 - top, timeline.Height);

        // The panels of the side columns never reach into the timeline.
        Assert.All(rects.Where(r => r.DialogClass != "CTimelineDialog"),
            r => Assert.True(r.Bottom <= timeline.Y, $"{r} overlaps the timeline at {timeline.Y}"));
    }

    [Fact]
    public void Layout_WritesHeightsAndTopsBack()
    {
        if (TestFiles.Preferences is not { } path) return;

        string first = TempFile("layout");
        string second = TempFile("layout");
        try
        {
            var preferences = Preferences.Load(path);
            var layout = DockLayout.For(preferences, "Animation");

            Assert.True(layout.SetHeight("CTransformDialog", 420));
            Assert.True(layout.SetTop("CStackerDialog", 200, 1000));
            Assert.False(layout.SetHeight("CNoSuchDialog", 100));
            Assert.False(layout.SetTop("CNoSuchDialog", 0, 1000));
            preferences.SaveAs(first);

            var reloaded = Preferences.Load(first);
            var transform = reloaded.DialogPosition("Animation", "CTransformDialog")!;
            Assert.Equal(new ushort[] { 420 }, transform.SizeBarPositions.ToArray());
            Assert.Equal(0.2f, reloaded.DialogPosition("Animation", "CStackerDialog")!.Y, 5);

            // Everything else keeps the saved values, and the file round-trips byte-exact.
            Assert.Equal(new ushort[] { 300 }, reloaded.DialogPosition("Animation", "CAnimBankDialog")!.SizeBarPositions.ToArray());
            Assert.Equal(new ushort[] { 350 }, reloaded.DialogPosition("Rigging", "CTransformDialog")!.SizeBarPositions.ToArray());
            reloaded.SaveAs(second);
            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public void Layout_IgnoresFloatingDialogsAndOtherContexts()
    {
        if (TestFiles.Preferences is not { } path) return;
        var rigging = DockLayout.For(Preferences.Load(path), "Rigging");

        // The root CSpinnerDialog has no context and is a floating dialog.
        Assert.Null(rigging.Panel("CSpinnerDialog"));
        // The Animation timeline is not part of the rigging context.
        Assert.Null(rigging.Panel("CTimelineDialog"));
        // The welcome/preferences/shortcut dialogs have no column: they are floating, with the place
        // the file stores, and Resolve gives them no dock rectangle.
        Assert.True(rigging.Panel("CWelcomeDialog")!.IsFloating);
        Assert.True(rigging.Panel("CShortcutDialog")!.IsFloating);
        Assert.Contains(rigging.Floating, p => p.DialogClass == "CWelcomeDialog");
        Assert.DoesNotContain(rigging.Resolve(2560, 1439), r => r.DialogClass == "CWelcomeDialog");
        Assert.Equal(3, rigging.Columns.Count);

        Assert.True(DockLayout.Empty("Rigging").IsEmpty);
        Assert.Empty(DockLayout.Empty("Rigging").Resolve(800, 600));
    }

    [Fact]
    public void Layout_ReadsAndWritesAFloatingPanelsPlaceAndFold()
    {
        if (TestFiles.Preferences is not { } path) return;
        string first = TempFile("floating");
        string second = TempFile("floating");
        try
        {
            // A dialog the file never docked keeps the place its PosX/PosY fractions mean.
            var preferences = Preferences.Load(path);
            var floating = DockLayout.For(preferences, "Animation")
                .Panel("CWelcomeDialog")!;
            Assert.True(floating.IsFloating);
            Assert.Equal(1085, DockLayout.FloatLeft(floating, 1920, 200));
            Assert.Equal(350, DockLayout.FloatTop(floating, 1000));

            // An undocked panel's place round-trips through the fractions the app writes: the write
            // clears the chain link of the panel above it and the reader restores the same pixels.
            var layout = DockLayout.For(preferences, "Animation");
            Assert.True(layout.SetFloating("CTransformDialog", 1050, 646, 168, 1600, 1000));
            Assert.Null(layout.Panel("CStackerDialog")!.Down);
            var reread = DockLayout.For(preferences, "Animation").Panel("CTransformDialog")!;
            Assert.True(reread.IsFloating);
            Assert.InRange(DockLayout.FloatLeft(reread, 1600, 168), 1049, 1051);
            Assert.InRange(DockLayout.FloatTop(reread, 1000), 645, 647);

            // Flags bit 0 is the folded state.
            var foldLayout = DockLayout.For(preferences, "Animation");
            Assert.False(foldLayout.Panel("CMixerDialog")!.Folded);
            Assert.True(foldLayout.SetFolded("CMixerDialog", true));
            Assert.True(DockLayout.For(preferences, "Animation").Panel("CMixerDialog")!.Folded);
            Assert.False(foldLayout.SetFolded("CMixerDialog", true)); // already folded
            Assert.True(foldLayout.SetFolded("CMixerDialog", false));
            Assert.False(DockLayout.For(preferences, "Animation").Panel("CMixerDialog")!.Folded);

            // The written file round-trips byte-exact.
            preferences.SaveAs(first);
            Preferences.Load(first).SaveAs(second);
            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public void Layout_WritesAColumnWidthBack()
    {
        if (TestFiles.Preferences is not { } path) return;

        string first = TempFile("width");
        string second = TempFile("width");
        try
        {
            var preferences = Preferences.Load(path);
            var layout = DockLayout.For(preferences, "Rigging");
            var left = layout.Columns[0];

            // A splitter drag between the viewport and a column stores the column's width as the
            // second SizeBarPositions value on every panel of the column, and Resolve uses it.
            Assert.True(layout.SetWidth(left, 330));
            Assert.All(left.Panels, p => Assert.Equal(new[] { (ushort)p.Height, (ushort)330 },
                p.Placement.SizeBarPositions.ToArray()));
            var rects = layout.Resolve(2560, 1439).ToDictionary(r => r.DialogClass);
            Assert.Equal(330, rects["CCharacterBankDialog"].Width);

            preferences.SaveAs(first);
            var reloaded = Preferences.Load(first);
            var reread = DockLayout.For(reloaded, "Rigging");
            Assert.Equal(330, reread.Columns[0].Width);
            Assert.Equal(330, reread.Resolve(2560, 1439)
                .First(r => r.DialogClass == "CHierarchyDialog").Width);

            // A width below the minimum is clamped (a splitter cannot close a column).
            Assert.True(reread.SetWidth(reread.Columns[0], 10));
            Assert.Equal(new[] { (ushort)173, (ushort)DockLayout.MinimumColumnWidth },
                reread.Panel("CCharacterBankDialog")!.Placement.SizeBarPositions.ToArray());

            // A file that carries two SizeBarPositions values round-trips byte-exact.
            var same = Preferences.Load(first);
            same.SaveAs(second);
            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public void Layout_WritesOnePanelsWidthBack()
    {
        if (TestFiles.Preferences is not { } path) return;

        string first = TempFile("onewidth");
        string second = TempFile("onewidth");
        try
        {
            var preferences = Preferences.Load(path);
            var layout = DockLayout.For(preferences, "Rigging");

            // A panel that was dragged out of its dock has no column to write: the resize writes the
            // second SizeBarPositions value on that one entry (the floating panels' grip).
            Assert.True(layout.SetWidth("CTransformDialog", 305));
            Assert.Equal(new[] { (ushort)350, (ushort)305 },
                layout.Panel("CTransformDialog")!.Placement.SizeBarPositions.ToArray());

            // The dock reader takes a column's width as the largest second entry, so the column the
            // panel still belongs to in the file resolves to the written width.
            var reread = DockLayout.For(preferences, "Rigging");
            Assert.Equal(305, reread.Columns[2].Width);
            Assert.Equal(305, reread.Resolve(2560, 1439)
                .First(r => r.DialogClass == "CTransformDialog").Width);

            // The two-entry file round-trips byte-exact.
            preferences.SaveAs(first);
            var same = Preferences.Load(first);
            same.SaveAs(second);
            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    private static string TempFile(string purpose) =>
        Path.Combine(Path.GetTempPath(), $"sloppyanimatsu-{purpose}-{Guid.NewGuid():N}.akt");
}
