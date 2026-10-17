using Kright.Services;
using Xunit;

namespace Kright.Tests;

public class ScrollPhysicsTests
{
    private static int Run(ScrollPhysics p, ScrollFeel feel, double ms, double frame = 7)
    {
        int total = 0;
        for (double t = 0; t < ms && p.Active; t += frame) total += p.Step(frame, feel);
        return total;
    }

    [Fact]
    public void OneClick_DeliversExactlyOneClick_AndStops()
    {
        var p = new ScrollPhysics();
        p.AddClick(-120, 0, ScrollFeel.Standard);
        int total = Run(p, ScrollFeel.Standard, 2000);
        Assert.Equal(-120, total);
        Assert.False(p.Active);
    }

    [Fact]
    public void OneClick_IsSpreadOverManyFrames()
    {
        var p = new ScrollPhysics();
        p.AddClick(120, 0, ScrollFeel.Standard);
        int first = p.Step(7, ScrollFeel.Standard);
        Assert.InRange(first, 1, 40); // a fraction of the click, not all of it
    }

    [Fact]
    public void MostOfTheDistance_IsCoveredWithinTheDuration()
    {
        var feel = ScrollFeel.Standard;
        var p = new ScrollPhysics();
        p.AddClick(120, 0, feel);
        int within = Run(p, feel, feel.DurationMs);
        Assert.InRange(within, 110, 120);
    }

    [Fact]
    public void StepFactor_ScalesTheDistance()
    {
        var feel = new ScrollFeel(1.5, 300, 1);
        var p = new ScrollPhysics();
        p.AddClick(120, 0, feel);
        Assert.Equal(180, Run(p, feel, 3000));
    }

    [Fact]
    public void FastSeries_IsAccelerated_SlowSeriesIsNot()
    {
        var feel = new ScrollFeel(1, 300, 2);

        var slow = new ScrollPhysics();
        for (int i = 0; i < 5; i++) slow.AddClick(120, i * 200, feel);
        int slowTotal = Run(slow, feel, 5000);

        var fast = new ScrollPhysics();
        for (int i = 0; i < 5; i++) fast.AddClick(120, i * 20, feel);
        int fastTotal = Run(fast, feel, 5000);

        Assert.Equal(600, slowTotal);
        Assert.True(fastTotal > slowTotal, $"fast {fastTotal} should beat slow {slowTotal}");
        Assert.True(fastTotal <= 1200, "acceleration is capped at the multiplier");
    }

    [Fact]
    public void Reversing_DropsTheRest_AndGoesTheNewWay()
    {
        var feel = ScrollFeel.Standard;
        var p = new ScrollPhysics();
        p.AddClick(120 * 5, 0, feel);
        p.Step(7, feel);
        p.AddClick(-120, 500, feel);
        int total = Run(p, feel, 3000);
        Assert.Equal(-120, total);
    }

    [Fact]
    public void Cancel_StopsAtOnce()
    {
        var p = new ScrollPhysics();
        p.AddClick(120, 0, ScrollFeel.Standard);
        p.Cancel();
        Assert.False(p.Active);
        Assert.Equal(0, p.Step(7, ScrollFeel.Standard));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Presets_AreDistinct(int preset)
    {
        var f = ScrollFeel.ForPreset(preset, new ScrollFeel(9, 9, 9));
        Assert.NotEqual(9, f.StepFactor);
    }
}
