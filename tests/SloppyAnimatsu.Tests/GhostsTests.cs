using SloppyAnimatsu.Core.Model;
using Xunit;

namespace SloppyAnimatsu.Tests;

/// <summary>The Stacker's ghost planner: which times get a ghost, their colour bands and opacity.</summary>
public class GhostsTests
{
    private static readonly float[] Keys = [0, 5, 10, 15, 20, 25, 30, 35, 40, 45];

    [Fact]
    public void Plan_SplitsTheKeysAroundThePlayhead_NearestFirst()
    {
        var plan = Ghosts.Plan(Keys, 20, previous: true, next: true, maxPrevious: 10, maxNext: 10);
        Assert.Equal(new float[] { 15, 10, 5, 0 }, plan.Where(g => g.Side == GhostSide.Previous).Select(g => g.Time));
        Assert.Equal(new float[] { 25, 30, 35, 40, 45 }, plan.Where(g => g.Side == GhostSide.Next).Select(g => g.Time));
        Assert.DoesNotContain(plan, g => g.Time == 20); // the key under the playhead has no ghost
        Assert.All(plan.GroupBy(g => g.Side), side => Assert.Equal(Enumerable.Range(1, side.Count()), side.Select(g => g.Distance)));
    }

    [Fact]
    public void Plan_HonoursTheTogglesAndTheCounts()
    {
        Assert.Empty(Ghosts.Plan(Keys, 20, previous: false, next: false, 10, 10));
        Assert.All(Ghosts.Plan(Keys, 20, previous: false, next: true, 10, 10), g => Assert.Equal(GhostSide.Next, g.Side));
        Assert.All(Ghosts.Plan(Keys, 20, previous: true, next: false, 10, 10), g => Assert.Equal(GhostSide.Previous, g.Side));
        var limited = Ghosts.Plan(Keys, 22, previous: true, next: true, maxPrevious: 2, maxNext: 3);
        Assert.Equal(new float[] { 20, 15, 25, 30, 35 }, limited.Select(g => g.Time));
    }

    [Fact]
    public void Colours_SwapEveryFourGhosts()
    {
        // Eight keys ahead: four in the next colour (red), then four in the previous colour (grey).
        var plan = Ghosts.Plan(Enumerable.Range(1, 8).Select(i => (float)i), 0, previous: false, next: true, 10, 10);
        Assert.Equal(new[] { true, true, true, true, false, false, false, false }, plan.Select(g => g.UsesNextColor));
        // And mirrored behind the playhead: grey first, then red.
        var behind = Ghosts.Plan(Enumerable.Range(1, 9).Select(i => (float)i), 10, previous: true, next: false, 10, 10);
        Assert.Equal(new[] { false, false, false, false, true, true, true, true, false }, behind.Select(g => g.UsesNextColor));
    }

    [Fact]
    public void Opacity_FadesWithDistance()
    {
        var plan = Ghosts.Plan(Keys, 0, previous: false, next: true, 10, 10);
        Assert.Equal(Ghosts.NearestOpacity, plan[0].Opacity, 4);
        Assert.Equal(Ghosts.FarthestOpacity, plan[^1].Opacity, 4);
        for (int i = 1; i < plan.Count; i++) Assert.True(plan[i].Opacity < plan[i - 1].Opacity);
        Assert.Equal(Ghosts.NearestOpacity, Ghosts.Plan([5f], 0, false, true, 10, 10)[0].Opacity, 4);
    }

    [Fact]
    public void Cycle_WrapsAroundTheLoop_AndSharesTooFewKeys()
    {
        float[] loop = [0, 5, 10, 15]; // loop key at 20 left out, period 20
        // Past the last key the next ghosts continue from the first key.
        var ahead = Ghosts.Plan(loop, 17, previous: false, next: true, 10, 10, period: 20);
        Assert.Equal(new float[] { 0, 5, 10, 15 }, ahead.Select(g => g.Time));
        // Both sides ask for 10, the loop has 4: two each, nearest first.
        var both = Ghosts.Plan(loop, 13, previous: true, next: true, 10, 10, period: 20);
        Assert.Equal(new float[] { 10, 5 }, both.Where(g => g.Side == GhostSide.Previous).Select(g => g.Time));
        Assert.Equal(new float[] { 15, 0 }, both.Where(g => g.Side == GhostSide.Next).Select(g => g.Time));
        // On a key that key is left out, and a playhead past the loop wraps into it.
        var onKey = Ghosts.Plan(loop, 25, previous: true, next: true, 1, 1, period: 20);
        Assert.Equal(new float[] { 0, 10 }, onKey.Select(g => g.Time));
        // A loop key is a ghost of its own past the last key, except on the first key, whose pose it repeats.
        float[] withLoopKey = [0, 5, 7, 11, 16, 20, 23, 26, 31];
        var run = Ghosts.Plan(withLoopKey, 13, previous: true, next: true, 10, 10, period: 31);
        Assert.Equal(new float[] { 11, 7, 5, 0 }, run.Where(g => g.Side == GhostSide.Previous).Select(g => g.Time));
        Assert.Equal(new float[] { 16, 20, 23, 26, 31 }, run.Where(g => g.Side == GhostSide.Next).Select(g => g.Time));
        Assert.DoesNotContain(Ghosts.Plan(withLoopKey, 0, true, true, 10, 10, period: 31), g => g.Time is 0 or 31);
        // Without a period the sides stop at the ends.
        Assert.Empty(Ghosts.Plan(loop, 17, previous: false, next: true, 10, 10));
    }

    [Fact]
    public void FrameGhosts_KeepOneColourPerSide()
    {
        var frames = Enumerable.Range(0, 30).Select(i => (float)i);
        var plan = Ghosts.Plan(frames, 15, previous: true, next: true, 10, 10, swapBands: false);
        Assert.All(plan, g => Assert.Equal(g.Side == GhostSide.Next, g.UsesNextColor));
        Assert.Equal(Enumerable.Range(16, 10).Select(i => (float)i), plan.Where(g => g.Side == GhostSide.Next).Select(g => g.Time));
    }

    [Fact]
    public void Opacity_StaysAboveTheFloor()
    {
        Assert.Equal(0.5f, Ghosts.NearestOpacity);
        Assert.Equal(0.1f, Ghosts.FarthestOpacity);
        Assert.All(Enumerable.Range(1, 10), d => Assert.InRange(Ghosts.OpacityAt(d, 10), 0.0999f, 0.5001f));
    }

    [Fact]
    public void Settings_FallBackToTheShippedValues()
    {
        var d = GhostSettings.Default;
        Assert.True(d.OnPlaying);
        Assert.Equal(10, d.MaxPrevious);
        Assert.Equal(10, d.MaxNext);
        Assert.False(d.PerFrame);
        Assert.False(d.Wire);
        Assert.Equal(((byte)0x68, (byte)0x68, (byte)0x68), d.PreviousColor);
        Assert.Equal(((byte)0xF2, (byte)0x36, (byte)0x29), d.NextColor);
    }
}
