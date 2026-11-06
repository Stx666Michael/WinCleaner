namespace WinCleaner.Core;

public readonly record struct RectD(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double Area => W * H;
}

/// <summary>Squarified treemap layout (Bruls, Huijing, van Wijk): blocks stay as close to square as possible.</summary>
public static class Treemap
{
    /// <summary>
    /// Splits <paramref name="bounds"/> into one rectangle per value, each with an area proportional to its value.
    /// Values that are zero or negative get an empty rectangle. The result is in the same order as the input.
    /// </summary>
    public static RectD[] Layout(IReadOnlyList<double> values, RectD bounds)
    {
        var result = new RectD[values.Count];
        if (values.Count == 0 || bounds.W <= 0 || bounds.H <= 0) return result;

        // Indices of positive values, largest first (the algorithm needs descending order).
        var idx = Enumerable.Range(0, values.Count).Where(i => values[i] > 0).OrderByDescending(i => values[i]).ToArray();
        if (idx.Length == 0) return result;

        double total = idx.Sum(i => values[i]);
        double scale = bounds.W * bounds.H / total;
        var areas = idx.Select(i => values[i] * scale).ToArray();

        var rest = bounds;
        int start = 0;
        while (start < areas.Length)
        {
            double side = Math.Min(rest.W, rest.H);
            int end = start + 1;
            double sum = areas[start], min = areas[start], max = areas[start];
            double worst = Worst(sum, min, max, side);

            // Keep adding blocks to the current row while that does not make the shapes worse.
            while (end < areas.Length)
            {
                double a = areas[end];
                double s2 = sum + a, mn = Math.Min(min, a), mx = Math.Max(max, a);
                double w2 = Worst(s2, mn, mx, side);
                if (w2 > worst) break;
                sum = s2; min = mn; max = mx; worst = w2; end++;
            }

            if (rest.W >= rest.H)
            {
                // Wide area: the row is a column down the left edge.
                double colW = Math.Min(sum / rest.H, rest.W);
                double y = rest.Y;
                for (int k = start; k < end; k++)
                {
                    double h = areas[k] / colW;
                    result[idx[k]] = new RectD(rest.X, y, colW, h);
                    y += h;
                }
                rest = new RectD(rest.X + colW, rest.Y, Math.Max(0, rest.W - colW), rest.H);
            }
            else
            {
                // Tall area: the row runs along the top edge.
                double rowH = Math.Min(sum / rest.W, rest.H);
                double x = rest.X;
                for (int k = start; k < end; k++)
                {
                    double w = areas[k] / rowH;
                    result[idx[k]] = new RectD(x, rest.Y, w, rowH);
                    x += w;
                }
                rest = new RectD(rest.X, rest.Y + rowH, rest.W, Math.Max(0, rest.H - rowH));
            }
            start = end;
        }
        return result;
    }

    /// <summary>The worst (largest) aspect ratio in a row of the given areas laid along a side of the given length.</summary>
    private static double Worst(double sum, double min, double max, double side)
    {
        double s2 = sum * sum, w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }
}
