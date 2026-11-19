using SloppyAnimatsu.Core.Model;
using SloppyAnimatsu.Rendering;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>
/// The timeline's time bar: considered keys, the key and play ranges, the loop key, the play wrap and
/// the range bar's gestures.
/// </summary>
public class TimeBarTests
{
    /// <summary>A new scene's new animation with keys at 0, 10 and 20 (plus its end or loop key).</summary>
    private static (PoseEditSession Session, Animation Animation, AnimStack Stack) Keyed(bool cycle)
    {
        var model = SceneFactory.NewSceneModel(60);
        var session = new PoseEditSession(new ScenePose(model), model.Scene.Characters.First());
        var animation = session.CreateAnimation();
        var stack = Assert.Single(animation.Stacks);
        if (stack.IsCycle != cycle) session.ToggleStackFlag(stack, AnimationBankEditor.CycleFlag);
        session.CreateKey(stack, 10);
        session.CreateKey(stack, 20);
        Assert.Equal(cycle, stack.IsCycle);
        return (session, animation, stack);
    }

    [Fact]
    public void NonCycle_TheLastStoredKeyIsAnEndMarker_NotCounted()
    {
        var (_, animation, stack) = Keyed(cycle: false);
        int n = stack.Keys.Count;
        Assert.True(n >= 3);
        Assert.Equal(n - 1, stack.ConsideredKeyCount);
        Assert.Equal((stack.Keys[0].Time, stack.Keys[n - 2].Time), animation.KeyRange);
        Assert.Equal(animation.KeyRange, animation.PlayRange);
    }

    [Fact]
    public void Cycle_CountsTheLoopKey_AndPlaysOneFrameShortOfIt()
    {
        var (_, animation, stack) = Keyed(cycle: true);
        int n = stack.Keys.Count;
        Assert.Equal(n, stack.ConsideredKeyCount);
        int loop = stack.Keys[n - 1].Time;
        Assert.Equal((stack.Keys[0].Time, loop), animation.KeyRange);
        Assert.Equal((stack.Keys[0].Time, loop - 1), animation.PlayRange);
    }

    [Fact]
    public void MoveKeys_MovesTheLoopKey_AndKeepsItAfterThePreviousKey()
    {
        var (session, animation, stack) = Keyed(cycle: true);
        int n = stack.Keys.Count;
        int loop = stack.Keys[n - 1].Time, before = stack.Keys[n - 2].Time;

        session.Editor.MoveKeys(stack, [n - 1], 7);
        Assert.Equal(n, stack.Keys.Count);
        Assert.Equal(loop + 7, stack.Keys[n - 1].Time);
        Assert.Equal(loop + 7, animation.KeyRange.End);

        session.Editor.MoveKeys(stack, [n - 1], -1000);
        Assert.Equal(before + 1, stack.Keys[n - 1].Time);
    }

    [Theory]
    [InlineData(5f, 0, 10, 5f)]
    [InlineData(10.5f, 0, 10, 10.5f)]
    [InlineData(11f, 0, 10, 0f)]
    [InlineData(12.5f, 0, 10, 1.5f)]
    [InlineData(25f, 5, 9, 5f)]
    [InlineData(3f, 5, 9, 8f)]
    public void Wrap_LoopsOverEndMinusStartPlusOneFrames(float time, int start, int end, float expected) =>
        Assert.Equal(expected, TimeBar.Wrap(time, start, end), 3);

    private static TimeBar Bar(int min, int max, int start, int length)
    {
        var bar = new TimeBar();
        bar.Set(min, max, start, length);
        return bar;
    }

    [Fact]
    public void Page_MovesOneLengthTowardTheClick_ClampedToTheBar()
    {
        var bar = Bar(0, 100, 20, 30);
        bar.Page(90);
        Assert.Equal(50, bar.ViewStart);
        bar.Page(90);
        Assert.Equal(70, bar.ViewStart); // clamped to Max - Length
        bar.Page(0);
        Assert.Equal(40, bar.ViewStart);
    }

    [Fact]
    public void Slide_IsClampedToTheBar()
    {
        var bar = Bar(0, 100, 20, 30);
        bar.Slide(20, 15);
        Assert.Equal(35, bar.ViewStart);
        bar.Slide(20, -50);
        Assert.Equal(0, bar.ViewStart);
        Assert.Equal(30, bar.ViewLength);
    }

    [Fact]
    public void DragLeft_ShrinksToOneFrame_AndGrowsQuadraticallyPastTheStart()
    {
        var bar = Bar(0, 100, 20, 30);
        bar.DragLeft(20, 30, 0, 10);
        Assert.Equal((30, 20, 0), (bar.ViewStart, bar.ViewLength, bar.Min));
        bar.DragLeft(20, 30, 0, 100);
        Assert.Equal((49, 1), (bar.ViewStart, bar.ViewLength));
        // 4 frames before Min: 4*4/2 = 8 more, and Min follows.
        bar.DragLeft(20, 30, 0, -24);
        Assert.Equal((-12, 62, -12), (bar.ViewStart, bar.ViewLength, bar.Min));
    }

    [Fact]
    public void DragRight_GrowsQuadraticallyPastTheEnd()
    {
        var bar = Bar(0, 100, 20, 30);
        bar.DragRight(20, 30, 100, -10);
        Assert.Equal((20, 20, 100), (bar.ViewStart, bar.ViewLength, bar.Max));
        // End at 106: 6 past Max, 6*6/2 = 18 more.
        bar.DragRight(20, 30, 100, 56);
        Assert.Equal((86 + 18, 124), (bar.ViewLength, bar.Max));
    }

    [Fact]
    public void Zoom_ScalesByOnePointTwoPerNotch_AboutThePointer()
    {
        var bar = Bar(0, 1000, 100, 120);
        bar.Zoom(160, 1);
        Assert.Equal(100, bar.ViewLength);
        Assert.Equal(110, bar.ViewStart); // the frame under the pointer stays put
        bar.Zoom(160, -1);
        Assert.Equal(120, bar.ViewLength);

        var small = Bar(0, 100, 10, 3);
        small.Zoom(11, 1);
        Assert.Equal(2, small.ViewLength);
        small.Zoom(11, 5);
        Assert.Equal(1, small.ViewLength);
    }

    [Fact]
    public void FitAll_SpansEveryConsideredKeyAndTheTime()
    {
        var (_, animation, _) = Keyed(cycle: false);
        var (start, end) = animation.KeyRange;
        var bar = new TimeBar();
        bar.FitAll(animation, 0);
        Assert.Equal((start, end, start, end - start), (bar.Min, bar.Max, bar.ViewStart, bar.ViewLength));
        bar.FitAll(animation, end + 30);
        Assert.Equal(end + 30, bar.ViewEnd);
        bar.FitAll(null, 7);
        Assert.Equal((7, 100), (bar.ViewStart, bar.ViewLength));
    }

    [Fact]
    public void FitTimes_SetsTheWindow_AndRefusesASingleFrame()
    {
        var bar = Bar(0, 100, 0, 100);
        Assert.True(bar.FitTimes([10, 40, 25]));
        Assert.Equal((10, 30, 0, 100), (bar.ViewStart, bar.ViewLength, bar.Min, bar.Max));
        Assert.False(bar.FitTimes([5, 5]));
        Assert.Equal(10, bar.ViewStart);
        Assert.True(bar.FitTimes([90, 150]));
        Assert.Equal(150, bar.Max);
    }
}
