using System.Numerics;
using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The timeline's pointer geometry: a press picks the
/// key, audio clip or fade handle drawn under it, the ruler is not a row, and the frame under an x
/// is the frame drawn there. The viewer draws from the same <see cref="TimelineLayout"/>, so these
/// rules no longer need a window to be checked.
/// </summary>
public class TimelineLayoutTests
{
    private const double Width = 12000;

    [Fact]
    public void WithoutAnAnimation_ThereIsOneEmptyRowAndTheRulerIsNotARow()
    {
        var layout = new TimelineLayout { Width = 800, Height = 60 };
        Assert.Empty(layout.Stacks);
        Assert.Equal((0, 1), layout.Range);
        Assert.True(layout.FrameWidth > 0);
        Assert.Equal(-1, layout.RowAt(5));
        // No stack means no row either: a press in the ruler or below it hits nothing.
        Assert.Equal((-1, -1), layout.KeyAt(new Vector2(400, 5)));
        Assert.Equal((-1, -1), layout.KeyAt(new Vector2(400, 40)));
        Assert.Equal(0, layout.SlotAt(-100));
        Assert.Equal(0, layout.SlotAt(100));
        Assert.Equal(TimelineLayout.RulerHeight + TimelineLayout.RowHeight + 2, layout.TotalHeight, 3);
    }

    [Fact]
    public void RowsAndSlots_AreTheStackRowsAndTheGapsBetweenThem()
    {
        var layout = new TimelineLayout { Width = Width, Height = 200 };
        Assert.Equal(TimelineLayout.RulerHeight, TimelineLayout.RowTop(0), 3);
        Assert.Equal(-1, layout.RowAt(TimelineLayout.RulerHeight - 1));
        Assert.Equal(0, layout.RowAt(TimelineLayout.RulerHeight + 1));
        Assert.Equal(1, layout.RowAt(TimelineLayout.RowTop(1) + TimelineLayout.RowHeight - 1));
    }

    [SceneTheory, SceneSamples]
    public void Keys_ArePickedAtTheFrameTheyAreDrawnAt(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (AnimationWithKeys(model) is not { } pair) return;
        var animation = pair.Animation;
        var layout = new TimelineLayout { Width = Width, Height = 400, Animation = animation };
        var stacks = animation.Stacks;
        for (int row = 0; row < stacks.Count; row++)
        {
            var keys = stacks[row].Keys;
            // Only the drawn keys: a non-cycle stack's end marker past its last key is not drawn.
            for (int k = 0; k < layout.KeyCountAt(row); k++)
            {
                var at = new Vector2((float)layout.X(keys[k].Time), (float)(TimelineLayout.RowTop(row) + TimelineLayout.RowHeight / 2));
                var (hitRow, hitKey) = layout.KeyAt(at);
                Assert.Equal(row, hitRow);
                Assert.InRange(hitKey, 0, keys.Count - 1);
                // Another key drawn at the same frame wins the tie, so compare the frame, not the index.
                Assert.Equal(keys[hitKey].Time, keys[k].Time);
            }
        }
    }

    [SceneTheory, SceneSamples]
    public void A_PressLeftOfTheGutterAndBelowTheRows_HitsNoKey(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (AnimationWithKeys(model) is not { } pair) return;
        var animation = pair.Animation;
        var layout = new TimelineLayout { Width = Width, Height = 400, Animation = animation };
        int keys = animation.Stacks[0].Keys.Count;

        Assert.Equal((0, -1), layout.KeyAt(new Vector2((float)TimelineLayout.Gutter / 2, (float)TimelineLayout.RowTop(0) + 2)));
        Assert.Equal((-1, -1), layout.KeyAt(new Vector2((float)TimelineLayout.Gutter + 50, (float)TimelineLayout.RowTop(animation.Stacks.Count) + 4)));
        Assert.Equal((0, -1), layout.KeyAt(new Vector2(-500, (float)TimelineLayout.RowTop(0) + 2)));
        Assert.True(keys > 0);
    }

    [SceneTheory, SceneSamples]
    public void TheFrameUnderAnX_IsTheFrameDrawnThere(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (AnimationWithKeys(model) is not { } pair) return;
        var animation = pair.Animation;
        var layout = new TimelineLayout { Width = Width, Height = 400, Animation = animation };
        var (start, end) = layout.Range;
        foreach (int frame in new[] { start, start + (end - start) / 2, end })
        {
            Assert.Equal(frame, layout.FrameUnder(layout.X(frame)));
            Assert.Equal(frame, (int)MathF.Round(layout.FrameAt(layout.X(frame))));
        }
        // A press left of the ruler area is clamped to the first frame instead of going negative.
        Assert.Equal(start, layout.FrameUnder(TimelineLayout.Gutter - 500));
    }

    [SceneTheory, SceneSamples]
    public void FadeHandles_ArePickedAtTheRampEndOnlyWhereAKeyHasAFade(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (AnimationWithKeys(model) is not { } pair) return;
        var animation = pair.Animation;
        var layout = new TimelineLayout { Width = Width, Height = 400, Animation = animation };
        int fades = 0;
        for (int row = 0; row < animation.Stacks.Count; row++)
        {
            var keys = animation.Stacks[row].Keys;
            for (int k = 0; k < keys.Count; k++)
            {
                var handle = keys[k].FadeOut != 0 ? layout.HandleRect(row, keys[k], false)
                    : keys[k].FadeIn != 0 ? layout.HandleRect(row, keys[k], true) : default;
                bool fadeIn = keys[k].FadeOut == 0;
                if (keys[k].FadeIn == 0 && keys[k].FadeOut == 0) continue;
                fades++;
                var centre = new Vector2((float)(handle.X + handle.Width / 2), (float)(handle.Y + handle.Height / 2));
                var (hitRow, hitKey, hitIn) = layout.FadeHandleAt(centre);
                Assert.Equal(row, hitRow);
                Assert.Equal(k, hitKey);
                Assert.Equal(fadeIn, hitIn);
            }
        }
        // Rows without a fade handle have nothing to grab at the key's own frame.
        for (int row = 0; row < animation.Stacks.Count; row++)
            foreach (var key in animation.Stacks[row].Keys.Where(k => k.FadeIn == 0 && k.FadeOut == 0))
                Assert.Equal(-1, layout.FadeHandleAt(new Vector2((float)layout.X(key.Time), (float)(TimelineLayout.RowTop(row) + TimelineLayout.RowHeight / 2))).Row);
        _ = fades;
    }

    [SceneTheory, SceneSamples]
    public void AudioClips_AreOneClickableBlockFromStartToDuration(string path)
    {
        var model = TestFiles.LoadScene(path);
        if (AnimationWithKeys(model) is not { } pair) return;
        var audio = pair.Animation.Stacks.OfType<AudioStack>().FirstOrDefault(s => s.Events.Count > 0);
        if (audio == null) return;
        int row = pair.Animation.Stacks.ToList().IndexOf(audio);
        var layout = new TimelineLayout { Width = Width, Height = 400, Animation = pair.Animation };
        var clip = layout.AudioClip(audio.Events[0]);
        double y = TimelineLayout.RowTop(row) + TimelineLayout.RowHeight / 2;

        Assert.Equal((row, 0), layout.KeyAt(new Vector2((float)(clip.X0 + 1), (float)y)));
        Assert.Equal((row, 0), layout.KeyAt(new Vector2((float)((clip.X0 + clip.X1) / 2), (float)y)));
        Assert.Equal((row, 0), layout.KeyAt(new Vector2((float)clip.X1, (float)y)));
        Assert.True(clip.X1 > clip.X0);
    }

    private static (Animation Animation, int Stacks)? AnimationWithKeys(SceneModel model)
    {
        foreach (var character in model.Scene.Characters)
            foreach (var animation in character.Animations)
                if (animation.Stacks.Count > 0 && animation.Stacks.Any(s => s.Keys.Count > 2))
                    return (animation, animation.Stacks.Count);
        return null;
    }

    [Fact]
    public void GapMark_DropsTheNumberThenTheArrowsThenTheLineAsTheGapShrinks()
    {
        const double label = 10;
        var seen = new List<GapMarkKind>();
        for (double gap = 60; gap >= 0; gap -= 0.5)
        {
            var kind = TimelineLayout.GapMark(gap, label);
            if (seen.Count == 0 || seen[^1] != kind) seen.Add(kind);
        }
        Assert.Equal([GapMarkKind.Labelled, GapMarkKind.Arrows, GapMarkKind.Line, GapMarkKind.None], seen);
        // The number only appears with room for it and an arrowhead plus a shaft on each side.
        Assert.Equal(GapMarkKind.Arrows, TimelineLayout.GapMark(label + 2 * TimelineLayout.GapArrowHead, label));
        Assert.Equal(GapMarkKind.Labelled, TimelineLayout.GapMark(40, label));
        Assert.Equal(GapMarkKind.None, TimelineLayout.GapMark(1, label));
        // A wider number needs a wider gap.
        Assert.Equal(GapMarkKind.Arrows, TimelineLayout.GapMark(30, 25));
    }

    [Theory]
    [InlineData(0f, 12f, "12")]
    [InlineData(3f, 4f, "1")]
    [InlineData(2f, 4.5f, "2.5")]
    [InlineData(1f, 1.333f, "0.33")]
    public void FrameDistanceText_IsWholeFramesPlainElseTwoDecimals(float from, float to, string expected) =>
        Assert.Equal(expected, TimelineLayout.FrameDistanceText(from, to));
}
