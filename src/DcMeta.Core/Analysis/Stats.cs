namespace DcMeta.Core.Analysis;

public static class Stats
{
    /// <summary>Sample standard deviation (n − 1). NaN for fewer than 2 values.</summary>
    public static double Sd(IEnumerable<double> xs)
    {
        var a = xs as IList<double> ?? xs.ToList();
        if (a.Count < 2) return double.NaN;
        double m = a.Average();
        return Math.Sqrt(a.Sum(x => (x - m) * (x - m)) / (a.Count - 1));
    }

    public static double Median(IEnumerable<double> xs)
    {
        var a = xs.OrderBy(x => x).ToList();
        if (a.Count == 0) return double.NaN;
        int n = a.Count;
        return n % 2 == 1 ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) / 2.0;
    }

    /// <summary>Welch's t statistic for two independent samples. NaN if either side has &lt; 2 values.</summary>
    public static double WelchT(IList<double> a, IList<double> b)
    {
        if (a.Count < 2 || b.Count < 2) return double.NaN;
        double va = Math.Pow(Sd(a), 2) / a.Count, vb = Math.Pow(Sd(b), 2) / b.Count;
        double se = Math.Sqrt(va + vb);
        return se == 0 ? double.NaN : (a.Average() - b.Average()) / se;
    }

    /// <summary>
    /// Optimal 1-D two-cluster split (exact 2-means). Returns the threshold t such that
    /// values ≥ t form the upper cluster, minimizing total within-cluster sum of squares.
    /// </summary>
    public static double OneDimTwoMeansThreshold(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count < 2) return v.Count == 1 ? v[0] : 0;
        double best = double.MaxValue, threshold = v[^1];
        for (int i = 1; i < v.Count; i++)
        {
            if (v[i] == v[i - 1]) continue; // cut only between distinct values
            var lo = v.Take(i).ToList();
            var hi = v.Skip(i).ToList();
            double ss = Sse(lo) + Sse(hi);
            if (ss < best) { best = ss; threshold = v[i]; }
        }
        return threshold;

        static double Sse(List<double> xs) { double m = xs.Average(); return xs.Sum(x => (x - m) * (x - m)); }
    }

    public static int[] KMeans(IList<double[]> points, int[] initLabels, int k, int maxIter = 100)
    {
        var labels = (int[])initLabels.Clone();
        int dim = points[0].Length;
        for (int iter = 0; iter < maxIter; iter++)
        {
            var centroids = Enumerable.Range(0, k).Select(c =>
            {
                var members = Enumerable.Range(0, points.Count).Where(i => labels[i] == c).ToList();
                return members.Count == 0 ? null
                    : Enumerable.Range(0, dim).Select(d => members.Average(i => points[i][d])).ToArray();
            }).ToArray();
            bool changed = false;
            for (int i = 0; i < points.Count; i++)
            {
                int best = labels[i]; double bestD = double.MaxValue;
                for (int c = 0; c < k; c++)
                {
                    if (centroids[c] is null) continue;
                    double dd = points[i].Zip(centroids[c]!, (p, q) => (p - q) * (p - q)).Sum();
                    if (dd < bestD) { bestD = dd; best = c; }
                }
                if (best != labels[i]) { labels[i] = best; changed = true; }
            }
            if (!changed) break;
        }
        return labels;
    }
}
