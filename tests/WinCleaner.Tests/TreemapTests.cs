using WinCleaner.Core;

namespace WinCleaner.Tests;

public class TreemapTests
{
    private static readonly RectD Bounds = new(10, 20, 800, 500);

    private static void AssertValid(double[] values, RectD[] rects, RectD bounds)
    {
        const double eps = 1e-6;
        double total = values.Where(v => v > 0).Sum();
        for (int i = 0; i < values.Length; i++)
        {
            var r = rects[i];
            if (values[i] <= 0) { Assert.Equal(0, r.Area); continue; }
            // inside the bounds
            Assert.True(r.X >= bounds.X - eps && r.Y >= bounds.Y - eps, $"rect {i} starts outside");
            Assert.True(r.Right <= bounds.Right + eps && r.Bottom <= bounds.Bottom + eps, $"rect {i} ends outside");
            // area proportional to the value
            Assert.Equal(values[i] / total * bounds.Area, r.Area, 3);
        }
        // together they fill the whole area, and no two overlap
        Assert.Equal(bounds.Area, rects.Sum(r => r.Area), 3);
        for (int i = 0; i < rects.Length; i++)
            for (int j = i + 1; j < rects.Length; j++)
            {
                double w = Math.Min(rects[i].Right, rects[j].Right) - Math.Max(rects[i].X, rects[j].X);
                double h = Math.Min(rects[i].Bottom, rects[j].Bottom) - Math.Max(rects[i].Y, rects[j].Y);
                Assert.False(w > 1e-6 && h > 1e-6, $"rects {i} and {j} overlap");
            }
    }

    [Fact]
    public void Single_value_fills_everything()
    {
        var r = Treemap.Layout(new double[] { 5 }, Bounds);
        Assert.Equal(Bounds, r[0]);
    }

    [Fact]
    public void Areas_are_proportional_inside_bounds_and_do_not_overlap()
    {
        var v = new double[] { 60, 25, 10, 3, 1.5, 0.5 };
        AssertValid(v, Treemap.Layout(v, Bounds), Bounds);
    }

    [Fact]
    public void Works_for_random_inputs_and_unsorted_order()
    {
        var rng = new Random(42);
        for (int round = 0; round < 50; round++)
        {
            var v = Enumerable.Range(0, rng.Next(1, 60)).Select(_ => Math.Pow(rng.NextDouble(), 3) * 1000 + 0.01).ToArray();
            var bounds = new RectD(0, 0, 100 + rng.Next(900), 100 + rng.Next(600));
            AssertValid(v, Treemap.Layout(v, bounds), bounds);
        }
    }

    [Fact]
    public void Zero_and_negative_values_get_empty_rectangles_and_the_rest_still_fill_the_area()
    {
        var v = new double[] { 10, 0, 30, -5, 20 };
        var r = Treemap.Layout(v, Bounds);
        Assert.Equal(0, r[1].Area);
        Assert.Equal(0, r[3].Area);
        AssertValid(v, r, Bounds);
    }

    [Fact]
    public void Empty_input_or_empty_bounds_gives_no_area()
    {
        Assert.Empty(Treemap.Layout(Array.Empty<double>(), Bounds));
        Assert.All(Treemap.Layout(new double[] { 1, 2 }, new RectD(0, 0, 0, 100)), r => Assert.Equal(0, r.Area));
        Assert.All(Treemap.Layout(new double[] { 0, 0 }, Bounds), r => Assert.Equal(0, r.Area));
    }

    [Fact]
    public void Blocks_stay_reasonably_square()
    {
        var v = Enumerable.Range(1, 30).Select(i => 1000.0 / i).ToArray();
        var rects = Treemap.Layout(v, new RectD(0, 0, 800, 500));
        // the biggest blocks (the ones you read) should not be thin slivers
        foreach (var r in rects.Take(10))
            Assert.InRange(Math.Max(r.W / r.H, r.H / r.W), 1, 5);
    }
}
