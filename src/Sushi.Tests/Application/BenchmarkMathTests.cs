namespace Sushi.Tests.Application;

using System;
using Sushi.Application.Commands;
using Xunit;

public class BenchmarkMathTests
{
    [Fact]
    public void Percentile_EmptyArray_ReturnsZero()
    {
        var value = BenchmarkMath.Percentile(Array.Empty<double>(), 0.95);
        Assert.Equal(0, value);
    }

    [Fact]
    public void Percentile_SingleValue_ReturnsSameValue()
    {
        var value = BenchmarkMath.Percentile(new[] { 42.0 }, 0.5);
        Assert.Equal(42.0, value);
    }

    [Fact]
    public void Percentile_InterpolatesBetweenNeighbors()
    {
        var ordered = new[] { 10.0, 20.0, 30.0, 40.0 };
        var p95 = BenchmarkMath.Percentile(ordered, 0.95);
        Assert.Equal(38.5, p95, 6);
    }
}
