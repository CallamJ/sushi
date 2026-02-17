namespace Sushi.Application.Commands;

internal static class BenchmarkMath
{
    public static double Percentile(double[] ordered, double percentile)
    {
        if (ordered.Length == 0)
        {
            return 0;
        }

        if (ordered.Length == 1)
        {
            return ordered[0];
        }

        var rawIndex = percentile * (ordered.Length - 1);
        var lower = (int)Math.Floor(rawIndex);
        var upper = (int)Math.Ceiling(rawIndex);
        if (lower == upper)
        {
            return ordered[lower];
        }

        var weight = rawIndex - lower;
        return ordered[lower] + ((ordered[upper] - ordered[lower]) * weight);
    }
}
